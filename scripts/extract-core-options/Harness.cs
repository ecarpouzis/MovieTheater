using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Threading;

namespace MovieTheater.Tools.ExtractCoreOptions
{
    /// <summary>
    /// The runtime harness: LoadLibrary ONE core, call <c>retro_set_environment</c> with our own
    /// <c>retro_environment_t</c>, and capture whichever option-registration command the core uses.
    ///
    /// <para>Per the libretro API most cores register their options INSIDE retro_set_environment
    /// (RetroArch calls it before retro_init precisely so the option list exists early). A few older
    /// cores only register from retro_init, so if set_environment yielded nothing we install no-op
    /// audio/video/input callbacks and call retro_init as a second attempt — under a watchdog, and
    /// with the outcome recorded either way.</para>
    ///
    /// <para>⚠ A crash is NEVER "no options". The process simply dies; the driver records
    /// <c>crashed</c> for that DLL. To keep a late crash from destroying good data, the output file is
    /// rewritten the moment options are captured, before control returns to the core.</para>
    /// </summary>
    internal static class Harness
    {
        // ── libretro environment commands (libretro.h). RETRO_ENVIRONMENT_EXPERIMENTAL is masked off. ──
        private const uint EXPERIMENTAL = 0x10000;
        private const uint GET_OVERSCAN = 2;
        private const uint GET_CAN_DUPE = 3;
        private const uint GET_SYSTEM_DIRECTORY = 9;
        private const uint GET_VARIABLE = 15;
        private const uint SET_VARIABLES = 16;
        private const uint GET_VARIABLE_UPDATE = 17;
        private const uint GET_LIBRETRO_PATH = 19;
        private const uint GET_CORE_ASSETS_DIRECTORY = 30;
        private const uint GET_SAVE_DIRECTORY = 31;
        private const uint GET_INPUT_DEVICE_CAPABILITIES = 24;
        private const uint GET_LANGUAGE = 39;
        private const uint GET_INPUT_BITMASKS = 51;
        private const uint GET_CORE_OPTIONS_VERSION = 52;
        private const uint SET_CORE_OPTIONS = 53;
        private const uint SET_CORE_OPTIONS_INTL = 54;
        private const uint GET_PREFERRED_HW_RENDER = 56;
        private const uint GET_DISK_CONTROL_INTERFACE_VERSION = 57;
        private const uint GET_MESSAGE_INTERFACE_VERSION = 59;
        private const uint GET_INPUT_MAX_USERS = 61;
        private const uint SET_CORE_OPTIONS_V2 = 67;
        private const uint SET_CORE_OPTIONS_V2_INTL = 68;
        private const uint GET_SAVESTATE_CONTEXT = 72;
        private const uint GET_JIT_CAPABLE = 74;

        // Struct strides (libretro.h, x64). RETRO_NUM_CORE_OPTION_VALUES_MAX = 128; retro_core_option_value
        // is {const char *value; const char *label;} = 16 bytes.
        private const int VALUES_MAX = 128;
        private const int VALUE_STRIDE = 16;
        // retro_core_option_definition: key, desc, info, values[128], default_value
        private const int DEF_V1_STRIDE = 3 * 8 + VALUES_MAX * VALUE_STRIDE + 8;   // 2080
        private const int DEF_V1_VALUES_OFF = 3 * 8;
        // retro_core_option_v2_definition: key, desc, desc_categorized, info, info_categorized,
        //                                  category_key, values[128], default_value
        private const int DEF_V2_STRIDE = 6 * 8 + VALUES_MAX * VALUE_STRIDE + 8;   // 2104
        private const int DEF_V2_VALUES_OFF = 6 * 8;
        // retro_core_option_v2_category: key, desc, info
        private const int CAT_V2_STRIDE = 24;

        [DllImport("kernel32", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr LoadLibraryExW(string lpLibFileName, IntPtr hFile, uint dwFlags);
        private const uint LOAD_WITH_ALTERED_SEARCH_PATH = 0x00000008;

        [DllImport("kernel32", CharSet = CharSet.Ansi, SetLastError = true, ExactSpelling = true)]
        private static extern IntPtr GetProcAddress(IntPtr hModule, string procName);

        [DllImport("kernel32", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SetDllDirectoryW(string path);

        [DllImport("kernel32")]
        private static extern uint SetErrorMode(uint uMode);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        [return: MarshalAs(UnmanagedType.U1)]
        private delegate bool EnvironmentCb(uint cmd, IntPtr data);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate void SetEnvironmentFn(IntPtr cb);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate void VoidFn();

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate void SetPtrFn(IntPtr fn);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate uint GetApiVersionFn();

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate void GetSystemInfoFn(IntPtr info);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate void VideoRefreshCb(IntPtr data, uint w, uint h, UIntPtr pitch);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate void AudioSampleCb(short l, short r);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate UIntPtr AudioSampleBatchCb(IntPtr data, UIntPtr frames);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate void InputPollCb();
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate short InputStateCb(uint port, uint device, uint index, uint id);

        // Kept alive for the lifetime of the process — a collected delegate is a call into freed memory.
        private static EnvironmentCb _env;
        private static VideoRefreshCb _video;
        private static AudioSampleCb _audio;
        private static AudioSampleBatchCb _audioBatch;
        private static InputPollCb _poll;
        private static InputStateCb _state;

        private static string _outPath;
        private static Result _result;
        private static IntPtr _tmpDirUtf8, _dllPathUtf8;

        internal sealed class ExValue
        {
            public string token { get; set; }
            public string label { get; set; }
        }

        internal sealed class ExOption
        {
            public string key { get; set; }
            public string desc { get; set; }
            /// <summary>v2's <c>desc_categorized</c> — the SHORT label a frontend that groups by category
            /// shows ("Renderer" where <c>desc</c> is "Video &gt; Renderer"). Our config UI groups by
            /// category, so this is the label to prefer when the core supplies one.</summary>
            public string descCategorized { get; set; }
            public string info { get; set; }
            public string categoryKey { get; set; }
            public string @default { get; set; }
            public List<ExValue> values { get; set; } = new();
        }

        internal sealed class ExCategory
        {
            public string key { get; set; }
            public string desc { get; set; }
        }

        internal sealed class Result
        {
            public string file { get; set; }
            public string dll { get; set; }
            public string coreKey { get; set; }
            public bool custom { get; set; }
            public string libraryName { get; set; }
            public string libraryVersion { get; set; }
            public uint apiVersion { get; set; }
            public string outcome { get; set; } = "no-options";
            public string source { get; set; }
            public bool afterRetroInit { get; set; }
            public List<string> notes { get; set; } = new();
            /// <summary>Every RETRO_ENVIRONMENT command the core issued, in order, with a hit count. This is
            /// the diagnostic that tells a "registered nothing" core apart from one whose registration we
            /// failed to answer far enough to reach.</summary>
            public List<string> envTrace { get; set; } = new();
            public List<ExCategory> categories { get; set; } = new();
            public List<ExOption> options { get; set; } = new();
        }

        // Capture buckets, best source wins: v2 > v1 > variables.
        private static readonly Dictionary<string, List<ExOption>> _bySource = new(StringComparer.Ordinal);
        private static readonly Dictionary<string, List<ExCategory>> _catsBySource = new(StringComparer.Ordinal);
        private static readonly string[] SourcePrecedence =
            { "SET_CORE_OPTIONS_V2_INTL", "SET_CORE_OPTIONS_V2", "SET_CORE_OPTIONS_INTL", "SET_CORE_OPTIONS", "SET_VARIABLES" };

        internal static int Run(Args a)
        {
            var dll = a.GetPath("dll");
            _outPath = a.GetPath("out");
            var timeoutSec = a.GetInt("timeout", 60);
            var tmp = a.Get("tmp", Path.Combine(Path.GetTempPath(), "arcade-core-option-harness"));
            Directory.CreateDirectory(tmp);
            Directory.CreateDirectory(Path.GetDirectoryName(_outPath) ?? ".");

            // No WER dialogs / critical-error popups: a crashing core must die quietly so the driver can
            // record it and move on, not block the loop on a modal window.
            SetErrorMode(0x0001 /*FAILCRITICALERRORS*/ | 0x0002 /*NOGPFAULTERRORBOX*/ | 0x8000 /*NOOPENFILEERRORBOX*/);

            var file = Path.GetFileName(dll);
            _result = new Result
            {
                file = file,
                dll = dll,
                coreKey = CoreKeyFor(file),
                custom = file.Contains("_custom", StringComparison.OrdinalIgnoreCase),
            };
            WriteOut();   // a stub exists from the very first moment, so even an instant crash leaves a record

            _tmpDirUtf8 = Utf8(tmp);
            _dllPathUtf8 = Utf8(dll);

            // Watchdog: a wedged core must not stall the driver's loop.
            var watchdog = new Thread(() =>
            {
                Thread.Sleep(TimeSpan.FromSeconds(timeoutSec));
                _result.outcome = _result.options.Count > 0 ? "timeout-after-capture" : "timeout";
                _result.notes.Add($"watchdog fired after {timeoutSec}s");
                try { WriteOut(); } catch { /* best effort */ }
                Environment.Exit(3);
            })
            { IsBackground = true, Name = "watchdog" };
            watchdog.Start();

            // Some deployed cores are MSYS2/UCRT64 builds and import libwinpthread-1.dll, which lives with
            // the MSYS2 toolchain rather than next to the DLL — the worker finds it via its own environment.
            // Without this they fail with win32 126 (ERROR_MOD_NOT_FOUND), which would masquerade as "core
            // could not be read" (mupen64plus_next / parallel_n64 / kronos, 2026-08-02).
            var depDirs = a.Get("dep-dirs", "").Split(';', StringSplitOptions.RemoveEmptyEntries);
            if (depDirs.Length > 0)
            {
                Environment.SetEnvironmentVariable("PATH",
                    string.Join(";", depDirs) + ";" + Environment.GetEnvironmentVariable("PATH"));
                _result.notes.Add("dep dirs on PATH: " + string.Join(";", depDirs));
            }

            SetDllDirectoryW(Path.GetDirectoryName(dll));
            var h = LoadLibraryExW(dll, IntPtr.Zero, LOAD_WITH_ALTERED_SEARCH_PATH);
            if (h == IntPtr.Zero)
            {
                _result.outcome = "load-failed";
                _result.notes.Add("LoadLibraryEx failed, win32 error " + Marshal.GetLastWin32Error());
                WriteOut();
                Console.Error.WriteLine($"{file}: load-failed ({Marshal.GetLastWin32Error()})");
                return 4;
            }

            ReadSystemInfo(h);

            var setEnvPtr = GetProcAddress(h, "retro_set_environment");
            if (setEnvPtr == IntPtr.Zero)
            {
                _result.outcome = "not-a-core";
                _result.notes.Add("retro_set_environment not exported");
                WriteOut();
                return 5;
            }

            _env = EnvCallback;
            var setEnv = Marshal.GetDelegateForFunctionPointer<SetEnvironmentFn>(setEnvPtr);
            setEnv(Marshal.GetFunctionPointerForDelegate(_env));

            if (Best() == null)
            {
                // Nothing registered in retro_set_environment. Second attempt: the frontend contract in full
                // (no-op av/input callbacks) + retro_init, which is where a handful of older cores register.
                _result.notes.Add("no options from retro_set_environment; trying retro_init");
                TryRetroInit(h);
                if (Best() == null)
                {
                    // Last resort: hand the core the callback a second time. A few cores populate their
                    // option list lazily and re-publish it on the next set_environment.
                    try { setEnv(Marshal.GetFunctionPointerForDelegate(_env)); } catch { }
                    if (Best() != null) _result.notes.Add("options only appeared on a SECOND retro_set_environment");
                }
                if (Best() != null) _result.afterRetroInit = true;
                else
                {
                    // These cores build their option table from the loaded content (fbneo's per-driver
                    // dipswitches) or later still. We deliberately do NOT load content — that needs ROMs,
                    // BIOS and a GPU context, and the deployed ROM tree is off-limits to this tool. Say so
                    // precisely, so the builder carries the previous catalog block over instead of
                    // concluding the core lost its options.
                    _result.outcome = "no-options-before-content";
                }
            }

            Finish();
            WriteOut();
            Console.Error.WriteLine($"{file}: {_result.outcome} source={_result.source ?? "-"} options={_result.options.Count}");
            return 0;
        }

        /// <summary>Catalog core key for a deployed DLL file name. Strips the <c>_libretro</c> suffix and the
        /// <c>_custom</c> build marker; <c>mednafen_psx_hw</c> is the site's <c>beetle_psx_hw</c> (the DLL
        /// carries mednafen's name, the option keys carry Beetle's).</summary>
        internal static string CoreKeyFor(string fileName)
        {
            var n = Path.GetFileNameWithoutExtension(fileName);
            if (n.EndsWith("_libretro", StringComparison.OrdinalIgnoreCase)) n = n.Substring(0, n.Length - "_libretro".Length);
            if (n.EndsWith("_custom", StringComparison.OrdinalIgnoreCase)) n = n.Substring(0, n.Length - "_custom".Length);
            return n switch
            {
                "mednafen_psx_hw" => "beetle_psx_hw",
                _ => n,
            };
        }

        private static void ReadSystemInfo(IntPtr h)
        {
            try
            {
                var apiPtr = GetProcAddress(h, "retro_api_version");
                if (apiPtr != IntPtr.Zero)
                    _result.apiVersion = Marshal.GetDelegateForFunctionPointer<GetApiVersionFn>(apiPtr)();

                var infoPtr = GetProcAddress(h, "retro_get_system_info");
                if (infoPtr == IntPtr.Zero) return;
                // struct retro_system_info { const char *library_name, *library_version, *valid_extensions;
                //                            bool need_fullpath, block_extract; }
                var buf = Marshal.AllocHGlobal(64);
                try
                {
                    for (var i = 0; i < 64; i += 8) Marshal.WriteIntPtr(buf, i, IntPtr.Zero);
                    Marshal.GetDelegateForFunctionPointer<GetSystemInfoFn>(infoPtr)(buf);
                    _result.libraryName = Str(Marshal.ReadIntPtr(buf, 0));
                    _result.libraryVersion = Str(Marshal.ReadIntPtr(buf, 8));
                }
                finally { Marshal.FreeHGlobal(buf); }
            }
            catch (Exception ex) { _result.notes.Add("retro_get_system_info failed: " + ex.Message); }
        }

        private static void TryRetroInit(IntPtr h)
        {
            try
            {
                void Set(string name, Delegate d)
                {
                    var p = GetProcAddress(h, name);
                    if (p == IntPtr.Zero) return;
                    Marshal.GetDelegateForFunctionPointer<SetPtrFn>(p)(Marshal.GetFunctionPointerForDelegate(d));
                }

                _video = (d, w, hh, p) => { };
                _audio = (l, r) => { };
                _audioBatch = (d, f) => f;
                _poll = () => { };
                _state = (a, b, c, d) => 0;
                Set("retro_set_video_refresh", _video);
                Set("retro_set_audio_sample", _audio);
                Set("retro_set_audio_sample_batch", _audioBatch);
                Set("retro_set_input_poll", _poll);
                Set("retro_set_input_state", _state);

                var init = GetProcAddress(h, "retro_init");
                if (init == IntPtr.Zero) { _result.notes.Add("retro_init not exported"); return; }
                Marshal.GetDelegateForFunctionPointer<VoidFn>(init)();
                // Deliberately NO retro_deinit: this process is about to exit anyway and a deinit on a core
                // that never loaded content is a needless extra chance to crash after the data is already safe.
            }
            catch (Exception ex) { _result.notes.Add("retro_init attempt failed: " + ex.Message); }
        }

        private static void Finish()
        {
            _result.envTrace = _trace.GroupBy(c => c)
                                     .OrderBy(g => g.Key)
                                     .Select(g => g.Count() == 1 ? g.Key.ToString() : $"{g.Key}x{g.Count()}")
                                     .ToList();
            var best = Best();
            if (best == null)
            {
                if (_result.outcome == "no-options") _result.notes.Add("core registered no options at all");
                return;
            }
            _result.source = best;
            _result.options = _bySource[best];
            _result.categories = _catsBySource.TryGetValue(best, out var c) ? c : new List<ExCategory>();
            _result.outcome = _result.afterRetroInit ? "ok-after-retro_init" : "ok";
        }

        private static string Best()
        {
            foreach (var s in SourcePrecedence)
                if (_bySource.TryGetValue(s, out var l) && l.Count > 0) return s;
            return null;
        }

        // ── The environment callback ─────────────────────────────────────────────────────────────────
        private static readonly List<uint> _trace = new();

        private static bool EnvCallback(uint cmdRaw, IntPtr data)
        {
            var cmd = cmdRaw & ~EXPERIMENTAL;
            if (_trace.Count < 4096) _trace.Add(cmd);
            try
            {
                switch (cmd)
                {
                    case GET_CORE_OPTIONS_VERSION:
                        if (data == IntPtr.Zero) return false;
                        Marshal.WriteInt32(data, 2);           // we speak v2 (categories + per-option info)
                        return true;

                    case SET_VARIABLES:
                        return Capture("SET_VARIABLES", () => ReadVariables(data));
                    case SET_CORE_OPTIONS:
                        return Capture("SET_CORE_OPTIONS", () => ReadV1(data));
                    case SET_CORE_OPTIONS_INTL:
                        // struct retro_core_options_intl { definition *us; definition *local; } — us is English.
                        return Capture("SET_CORE_OPTIONS_INTL", () =>
                            data == IntPtr.Zero ? null : ReadV1(Marshal.ReadIntPtr(data, 0)));
                    case SET_CORE_OPTIONS_V2:
                        return Capture("SET_CORE_OPTIONS_V2", () => ReadV2(data));
                    case SET_CORE_OPTIONS_V2_INTL:
                        // struct retro_core_options_v2_intl { retro_core_options_v2 *us; *local; }
                        return Capture("SET_CORE_OPTIONS_V2_INTL", () =>
                            data == IntPtr.Zero ? null : ReadV2(Marshal.ReadIntPtr(data, 0)));

                    // ── Queries answered so the core gets far enough to register its options ──
                    case GET_CAN_DUPE:
                    case GET_JIT_CAPABLE:
                    case GET_INPUT_BITMASKS:
                        if (data == IntPtr.Zero) return true;
                        Marshal.WriteByte(data, 1);
                        return true;
                    case GET_OVERSCAN:
                    case GET_VARIABLE_UPDATE:
                        if (data == IntPtr.Zero) return true;
                        Marshal.WriteByte(data, 0);
                        return true;
                    case GET_LANGUAGE:                 // RETRO_LANGUAGE_ENGLISH
                    case GET_PREFERRED_HW_RENDER:      // RETRO_HW_CONTEXT_NONE — we host no renderer
                    case GET_SAVESTATE_CONTEXT:
                        if (data == IntPtr.Zero) return true;
                        Marshal.WriteInt32(data, 0);
                        return true;
                    case GET_DISK_CONTROL_INTERFACE_VERSION:
                    case GET_MESSAGE_INTERFACE_VERSION:
                        if (data == IntPtr.Zero) return true;
                        Marshal.WriteInt32(data, 1);
                        return true;
                    case GET_INPUT_MAX_USERS:
                        if (data == IntPtr.Zero) return true;
                        Marshal.WriteInt32(data, 4);
                        return true;
                    case GET_INPUT_DEVICE_CAPABILITIES:
                        if (data == IntPtr.Zero) return true;
                        Marshal.WriteInt64(data, 0);
                        return true;
                    case GET_SYSTEM_DIRECTORY:
                    case GET_CORE_ASSETS_DIRECTORY:
                    case GET_SAVE_DIRECTORY:
                        if (data == IntPtr.Zero) return false;
                        Marshal.WriteIntPtr(data, _tmpDirUtf8);   // a scratch dir, never the deployed cores dir
                        return true;
                    case GET_LIBRETRO_PATH:
                        if (data == IntPtr.Zero) return false;
                        Marshal.WriteIntPtr(data, _dllPathUtf8);
                        return true;
                    case GET_VARIABLE:
                        // struct retro_variable { const char *key; const char *value; } — no value set.
                        if (data != IntPtr.Zero) Marshal.WriteIntPtr(data, 8, IntPtr.Zero);
                        return false;

                    default:
                        // Accept the harmless SETTERs (input descriptors, controller info, geometry, …) so a
                        // core that treats a false as fatal keeps going; refuse everything that would hand
                        // back an interface we cannot honour (log/perf/camera/hw-render).
                        return cmd switch
                        {
                            1 or 6 or 8 or 10 or 11 or 12 or 13 or 18 or 21 or 32 or 33 or 34 or 35 or 36 or 37
                                or 44 or 55 or 58 or 60 or 62 or 63 or 64 or 65 or 69 or 70 => true,
                            _ => false,
                        };
                }
            }
            catch (Exception ex)
            {
                _result.notes.Add($"env cmd {cmd} threw: {ex.Message}");
                return false;
            }
        }

        private static bool Capture(string source, Func<(List<ExOption>, List<ExCategory>)?> read)
        {
            try
            {
                var got = read();
                if (got == null) return true;
                var (opts, cats) = got.Value;
                if (opts != null && opts.Count > 0)
                {
                    _bySource[source] = opts;
                    _catsBySource[source] = cats ?? new List<ExCategory>();
                    // Persist IMMEDIATELY: if the core crashes after this point the data is already on disk.
                    Finish();
                    WriteOut();
                }
            }
            catch (Exception ex) { _result.notes.Add($"{source} read failed: {ex.Message}"); }
            return true;
        }

        // struct retro_variable { const char *key; const char *value; }   value = "Description; a|b|c"
        private static (List<ExOption>, List<ExCategory>)? ReadVariables(IntPtr p)
        {
            if (p == IntPtr.Zero) return null;
            var list = new List<ExOption>();
            for (var i = 0; i < 4096; i++)
            {
                var key = Str(Marshal.ReadIntPtr(p, i * 16));
                if (key == null) break;
                var raw = Str(Marshal.ReadIntPtr(p, i * 16 + 8)) ?? "";
                var semi = raw.IndexOf(';');
                var desc = semi >= 0 ? raw.Substring(0, semi).Trim() : raw.Trim();
                var tokens = semi >= 0 ? raw.Substring(semi + 1) : "";
                var o = new ExOption { key = key, desc = desc };
                foreach (var t in tokens.Split('|'))
                {
                    var tok = t.Trim();
                    if (tok.Length == 0) continue;
                    o.values.Add(new ExValue { token = tok, label = tok });
                }
                o.@default = o.values.Count > 0 ? o.values[0].token : null;
                list.Add(o);
            }
            return (list, new List<ExCategory>());
        }

        private static (List<ExOption>, List<ExCategory>)? ReadV1(IntPtr p)
        {
            if (p == IntPtr.Zero) return null;
            var list = new List<ExOption>();
            for (var i = 0; i < 4096; i++)
            {
                var b = p + i * DEF_V1_STRIDE;
                var key = Str(Marshal.ReadIntPtr(b, 0));
                if (key == null) break;
                var o = new ExOption
                {
                    key = key,
                    desc = Str(Marshal.ReadIntPtr(b, 8)),
                    info = Str(Marshal.ReadIntPtr(b, 16)),
                };
                ReadValues(b + DEF_V1_VALUES_OFF, o);
                o.@default = Str(Marshal.ReadIntPtr(b, DEF_V1_VALUES_OFF + VALUES_MAX * VALUE_STRIDE))
                             ?? (o.values.Count > 0 ? o.values[0].token : null);
                list.Add(o);
            }
            return (list, new List<ExCategory>());
        }

        // struct retro_core_options_v2 { retro_core_option_v2_category *categories; *definitions; }
        private static (List<ExOption>, List<ExCategory>)? ReadV2(IntPtr p)
        {
            if (p == IntPtr.Zero) return null;
            var catsPtr = Marshal.ReadIntPtr(p, 0);
            var defsPtr = Marshal.ReadIntPtr(p, 8);

            var cats = new List<ExCategory>();
            if (catsPtr != IntPtr.Zero)
                for (var i = 0; i < 512; i++)
                {
                    var b = catsPtr + i * CAT_V2_STRIDE;
                    var key = Str(Marshal.ReadIntPtr(b, 0));
                    if (key == null) break;
                    cats.Add(new ExCategory { key = key, desc = Str(Marshal.ReadIntPtr(b, 8)) });
                }

            var list = new List<ExOption>();
            if (defsPtr != IntPtr.Zero)
                for (var i = 0; i < 4096; i++)
                {
                    var b = defsPtr + i * DEF_V2_STRIDE;
                    var key = Str(Marshal.ReadIntPtr(b, 0));
                    if (key == null) break;
                    var o = new ExOption
                    {
                        key = key,
                        desc = Str(Marshal.ReadIntPtr(b, 8)),
                        descCategorized = Str(Marshal.ReadIntPtr(b, 16)),
                        info = Str(Marshal.ReadIntPtr(b, 24)),
                        categoryKey = Str(Marshal.ReadIntPtr(b, 40)),
                    };
                    ReadValues(b + DEF_V2_VALUES_OFF, o);
                    o.@default = Str(Marshal.ReadIntPtr(b, DEF_V2_VALUES_OFF + VALUES_MAX * VALUE_STRIDE))
                                 ?? (o.values.Count > 0 ? o.values[0].token : null);
                    list.Add(o);
                }
            return (list, cats);
        }

        private static void ReadValues(IntPtr valuesBase, ExOption o)
        {
            for (var v = 0; v < VALUES_MAX; v++)
            {
                var token = Str(Marshal.ReadIntPtr(valuesBase, v * VALUE_STRIDE));
                if (token == null) break;
                var label = Str(Marshal.ReadIntPtr(valuesBase, v * VALUE_STRIDE + 8));
                o.values.Add(new ExValue { token = token, label = string.IsNullOrWhiteSpace(label) ? token : label });
            }
        }

        private static string Str(IntPtr p) => p == IntPtr.Zero ? null : Marshal.PtrToStringUTF8(p);

        private static IntPtr Utf8(string s)
        {
            var bytes = Encoding.UTF8.GetBytes(s + "\0");
            var p = Marshal.AllocHGlobal(bytes.Length);
            Marshal.Copy(bytes, 0, p, bytes.Length);
            return p;
        }

        private static readonly JsonSerializerOptions Json = new()
        {
            WriteIndented = true,
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
        };

        private static readonly object _writeLock = new();

        private static void WriteOut()
        {
            lock (_writeLock)
            {
                var tmp = _outPath + ".tmp";
                File.WriteAllText(tmp, JsonSerializer.Serialize(_result, Json), new UTF8Encoding(false));
                File.Move(tmp, _outPath, overwrite: true);
            }
        }
    }
}
