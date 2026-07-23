using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;

namespace ArcadeCaptureHost
{
    // ArcadeCaptureHost.exe --hwnd <decimal> --shm <name> --event <name> [--fps 60]
    //
    // Captures the given window via WGC and publishes packed BGRA frames into a named shared-memory ring
    // (created here) the Go worker opens. Emits one JSON status object per line on stdout the worker parses.
    // Exits when its target window is destroyed or when stdin closes (the worker holds stdin open, so the
    // helper dies with the worker).
    internal static class Program
    {
        [DllImport("user32.dll")] private static extern bool IsWindow(IntPtr hWnd);
        [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr hWnd);
        [DllImport("kernel32.dll")] private static extern bool QueryPerformanceCounter(out long value);

        private static readonly object _outLock = new();

        private static int Main(string[] rawArgs)
        {
            var args = ParseArgs(rawArgs);
            if (!args.TryGetValue("hwnd", out var hwndStr) ||
                !args.TryGetValue("shm", out var shmName) ||
                !args.TryGetValue("event", out var eventName))
            {
                Console.Error.WriteLine("usage: ArcadeCaptureHost --hwnd <decimal> --shm <name> --event <name> [--fps 60]");
                return 2;
            }
            IntPtr hwnd = (IntPtr)long.Parse(hwndStr, CultureInfo.InvariantCulture);
            int fps = args.TryGetValue("fps", out var f) ? int.Parse(f, CultureInfo.InvariantCulture) : 60;
            int width = args.TryGetValue("width", out var w) ? int.Parse(w, CultureInfo.InvariantCulture) : 0;
            int height = args.TryGetValue("height", out var h) ? int.Parse(h, CultureInfo.InvariantCulture) : 0;

            if (!IsWindow(hwnd))
            {
                Emit($"{{\"event\":\"error\",\"detail\":\"hwnd {hwnd} is not a window at start\"}}");
                return 3;
            }

            // The ring geometry is fixed for the room (the worker builds a fixed-geometry encode pipeline).
            // If width/height weren't passed, size to the window's first captured content (probed below).
            SharedFrameRing ring = null;
            var stopReason = new StopSignal();

            // stdin watcher: EOF (worker died / closed the pipe) → shut down. Skipped with --ignore-stdin
            // for standalone testing (no parent holding the pipe open).
            bool ignoreStdin = args.ContainsKey("ignore-stdin");
            // Capture lane v2: share the frame as keyed-mutex GPU textures instead of a pixel readback.
            // The worker passes --texshare only when windowZeroCopy is on; an old worker never does, so we
            // stay on v1. If ring creation fails we fall back to v1 and do NOT advertise texShare.
            bool texShare = args.ContainsKey("texshare");
            var stdinThread = new Thread(() =>
            {
                try
                {
                    var s = Console.OpenStandardInput();
                    var buf = new byte[64];
                    while (true)
                    {
                        int n = s.Read(buf, 0, buf.Length);
                        if (n == 0) break; // EOF
                    }
                }
                catch { }
                stopReason.Signal("stdin-closed");
            })
            { IsBackground = true, Name = "stdin-watch" };
            if (!ignoreStdin) stdinThread.Start();

            // window-liveness watcher: exit when the target window is destroyed.
            var winThread = new Thread(() =>
            {
                while (!stopReason.IsSet)
                {
                    if (!IsWindow(hwnd)) { stopReason.Signal("window-destroyed"); break; }
                    Thread.Sleep(250);
                }
            })
            { IsBackground = true, Name = "win-watch" };
            winThread.Start();

            int attempt = 0;
            try
            {
                while (!stopReason.IsSet)
                {
                    WindowCapture cap = null;
                    var recover = new StopSignal();
                    try
                    {
                        SharedFrameRing localRing = null;
                        Action<IntPtr, int, int, int> onFrame = (ptr, pitch, cw, ch) =>
                        {
                            var r = Volatile.Read(ref ring);
                            if (r == null) return;
                            QueryPerformanceCounter(out long qpc);
                            unsafe { r.Publish((byte*)ptr, pitch, cw, ch, qpc); }
                        };
                        cap = new WindowCapture(hwnd, fps, width, height, onFrame,
                            onStatus: Emit,
                            onClosed: reason => recover.Signal(reason),
                            onError: detail => Emit($"{{\"event\":\"error\",\"detail\":\"{Escape(detail)}\"}}"),
                            texShare: texShare, shmName: shmName, eventName: eventName);

                        var size = cap.Start();
                        int rw = width > 0 ? width : size.Width;
                        int rh = height > 0 ? height : size.Height;
                        if (rw <= 0 || rh <= 0) { rw = 1920; rh = 1080; }

                        if (cap.TexShareActive)
                        {
                            // v2: the frame ring is GPU SHARED TEXTURES (owned by cap). Publish pid + the
                            // raw NT-handle values + the adapter LUID so the worker duplicates+opens them.
                            string handlesJson = string.Join(",",
                                Array.ConvertAll(cap.TexHandles, h => h.ToString(CultureInfo.InvariantCulture)));
                            Emit($"{{\"event\":\"ready\",\"width\":{rw},\"height\":{rh},\"itemWidth\":{size.Width},\"itemHeight\":{size.Height},\"fps\":{fps}," +
                                 $"\"texShare\":true,\"texCount\":{cap.TexCount},\"luid\":{cap.TexLuid},\"pid\":{Environment.ProcessId},\"texHandles\":[{handlesJson}]}}");
                        }
                        else
                        {
                            if (ring == null)
                            {
                                localRing = new SharedFrameRing(shmName, eventName, rw, rh);
                                Volatile.Write(ref ring, localRing);
                            }
                            Emit($"{{\"event\":\"ready\",\"width\":{ring.Width},\"height\":{ring.Height},\"itemWidth\":{size.Width},\"itemHeight\":{size.Height},\"fps\":{fps}}}");
                        }
                        attempt = 0;

                        // Run until a recover/stop reason. Log a periodic heartbeat with frame count + minimize state.
                        long lastFrames = 0;
                        int quietTicks = 0;
                        while (!recover.IsSet && !stopReason.IsSet)
                        {
                            Thread.Sleep(1000);
                            long fr = cap.Frames;
                            bool iconic = IsIconic(hwnd);
                            if (fr == lastFrames)
                            {
                                quietTicks++;
                                // WGC legitimately pauses on a minimized window; not an error.
                                Emit($"{{\"event\":\"tick\",\"frames\":{fr},\"minimized\":{(iconic ? "true" : "false")},\"quiet\":{quietTicks}}}");
                            }
                            else
                            {
                                quietTicks = 0;
                                Emit($"{{\"event\":\"tick\",\"frames\":{fr},\"minimized\":{(iconic ? "true" : "false")}}}");
                            }
                            lastFrames = fr;
                        }
                    }
                    catch (Exception ex)
                    {
                        Emit($"{{\"event\":\"error\",\"detail\":\"{Escape(ex.Message)}\"}}");
                        recover.Signal("exception");
                    }
                    finally
                    {
                        cap?.Dispose();
                    }

                    if (stopReason.IsSet) break;

                    // recover: window still alive → back off and rebuild (device-lost / console<->RDP item close).
                    if (!IsWindow(hwnd)) { stopReason.Signal("window-destroyed"); break; }
                    attempt++;
                    int backoff = Math.Min(2000, 200 * attempt);
                    Emit($"{{\"event\":\"recover\",\"reason\":\"{Escape(recover.Reason)}\",\"attempt\":{attempt},\"backoffMs\":{backoff}}}");
                    Thread.Sleep(backoff);
                }
            }
            finally
            {
                Volatile.Read(ref ring)?.Dispose();
            }

            Emit($"{{\"event\":\"stopped\",\"reason\":\"{Escape(stopReason.Reason ?? "unknown")}\"}}");
            return 0;
        }

        private static Dictionary<string, string> ParseArgs(string[] a)
        {
            var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < a.Length; i++)
            {
                if (a[i].StartsWith("--"))
                {
                    string key = a[i].Substring(2);
                    string val = (i + 1 < a.Length && !a[i + 1].StartsWith("--")) ? a[++i] : "true";
                    d[key] = val;
                }
            }
            return d;
        }

        private static void Emit(string jsonLine)
        {
            lock (_outLock)
            {
                Console.Out.WriteLine(jsonLine);
                Console.Out.Flush();
            }
        }

        private static string Escape(string s) =>
            (s ?? "").Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\r", " ").Replace("\n", " ");
    }

    // StopSignal is a one-shot reason-carrying flag.
    internal sealed class StopSignal
    {
        private int _set;
        public string Reason { get; private set; }
        public bool IsSet => Volatile.Read(ref _set) != 0;
        public void Signal(string reason)
        {
            if (Interlocked.Exchange(ref _set, 1) == 0) Reason = reason;
        }
    }
}
