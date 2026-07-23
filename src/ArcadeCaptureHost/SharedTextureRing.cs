using System;
using System.IO.MemoryMappedFiles;
using System.Runtime.CompilerServices;
using System.Threading;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace ArcadeCaptureHost
{
    // SharedTextureRing is the WRITER of the capture-lane-v2 GPU frame path: a ring of keyed-mutex
    // NT-handle SHARED D3D11 textures the Go worker opens (by NT-handle duplication — see below) and
    // GPU-copies straight into nvautogpu*enc, with NO system-memory readback. It reuses the same shm
    // header/event as SharedFrameRing.cs as a CONTROL channel (latestSlot + frameSeq only — slotSize=0,
    // no pixels).
    //
    // TRANSPORT = NT-handle DUPLICATION, not open-by-name. Proven on Ziggy 2026-07-23:
    // CreateSharedHandle(name)+OpenSharedResourceByName is process-local on this box (cross-process
    // E_INVALIDARG). So each texture's shared handle is created UNNAMED and this process publishes its
    // pid + the raw handle values + its adapter LUID (in the ready JSON, Program.cs); the worker
    // OpenProcess(PROCESS_DUP_HANDLE)+DuplicateHandle+OpenSharedResource1's them into itself.
    //
    // Per frame the writer picks the next slot, AcquireSync(key 0) its keyed mutex, GPU-copies the WGC
    // frame (or the VideoScaler output) into the slot texture, ReleaseSync(0), then publishes
    // latestSlot/frameSeq and sets the event. The keyed mutex gives the consumer GPU-level ordering.
    internal sealed unsafe class SharedTextureRing : IDisposable
    {
        public const uint Magic = 0x4357544D; // 'MTWC'
        public const uint Version = 1;
        private const int HeaderSize = 64;
        private const int OffMagic = 0, OffVersion = 4, OffWidth = 8, OffHeight = 12, OffStride = 16;
        private const int OffSlotCount = 20, OffSlotSize = 24, OffGeneration = 28, OffLatestSlot = 32;
        private const int OffFrameSeq = 40, OffQpc = 48;

        [System.Runtime.InteropServices.DllImport("kernel32.dll")]
        private static extern bool QueryPerformanceCounter(out long value);

        private readonly ID3D11Device _device;
        private readonly ID3D11DeviceContext _ctx;
        private readonly int _width, _height, _count;

        private readonly MemoryMappedFile _mmf;
        private readonly MemoryMappedViewAccessor _view;
        private readonly EventWaitHandle _event;
        private byte* _base;

        private readonly ID3D11Texture2D[] _tex;
        private readonly IDXGIKeyedMutex[] _mutex;
        private readonly IntPtr[] _handles;
        private int _nextSlot;
        private ulong _frameSeq;

        public long Luid { get; }
        public int Count => _count;
        public int Width => _width;
        public int Height => _height;
        // Raw NT-handle values (valid in THIS process) the worker duplicates via our pid.
        public long[] Handles
        {
            get { var a = new long[_count]; for (int i = 0; i < _count; i++) a[i] = _handles[i].ToInt64(); return a; }
        }

        public SharedTextureRing(ID3D11Device device, ID3D11DeviceContext ctx,
            string shmName, string eventName, int width, int height, int slotCount)
        {
            _device = device;
            _ctx = ctx;
            _width = width;
            _height = height;
            _count = slotCount;
            _tex = new ID3D11Texture2D[slotCount];
            _mutex = new IDXGIKeyedMutex[slotCount];
            _handles = new IntPtr[slotCount];

            using (var dxgiDev = device.QueryInterface<IDXGIDevice>())
            using (var adapter = dxgiDev.GetAdapter())
                Luid = adapter.Description.Luid;

            for (int i = 0; i < slotCount; i++)
            {
                var tex = device.CreateTexture2D(new Texture2DDescription
                {
                    Width = (uint)width,
                    Height = (uint)height,
                    MipLevels = 1,
                    ArraySize = 1,
                    Format = Format.B8G8R8A8_UNorm,
                    SampleDescription = new SampleDescription(1, 0),
                    Usage = ResourceUsage.Default,
                    BindFlags = BindFlags.ShaderResource | BindFlags.RenderTarget,
                    MiscFlags = ResourceOptionFlags.SharedNTHandle | ResourceOptionFlags.SharedKeyedMutex,
                });
                using (var res1 = tex.QueryInterface<IDXGIResource1>())
                    _handles[i] = res1.CreateSharedHandle(null,
                        Vortice.DXGI.SharedResourceFlags.Read | Vortice.DXGI.SharedResourceFlags.Write, null);
                _tex[i] = tex;
                _mutex[i] = tex.QueryInterface<IDXGIKeyedMutex>();
            }

            // Control shm: same header as SharedFrameRing but slotSize=0 (no pixels; slot/seq only).
            _event = new EventWaitHandle(false, EventResetMode.AutoReset, eventName);
            long total = HeaderSize + (long)slotCount * 8; // 8-byte per-slot seq region kept (unused in v2)
            _mmf = MemoryMappedFile.CreateNew(shmName, total, MemoryMappedFileAccess.ReadWrite);
            _view = _mmf.CreateViewAccessor(0, total, MemoryMappedFileAccess.ReadWrite);
            byte* p = null;
            _view.SafeMemoryMappedViewHandle.AcquirePointer(ref p);
            _base = p;

            SetU32(OffMagic, Magic);
            SetU32(OffVersion, Version);
            SetU32(OffWidth, (uint)width);
            SetU32(OffHeight, (uint)height);
            SetU32(OffStride, (uint)(width * 4));
            SetU32(OffSlotCount, (uint)slotCount);
            SetU32(OffSlotSize, 0); // v2: no pixel bytes — the frame is the shared TEXTURE at latestSlot
            SetU32(OffGeneration, 0);
            SetInt(OffLatestSlot, -1);
        }

        // Publish GPU-copies src into the next slot texture under its keyed mutex, then publishes the slot
        // index + frame seq. src must be BGRA; if its size differs from the ring it is copied clamped
        // top-left (a broken-scaler fallback — normally src is already the ring size).
        public void Publish(ID3D11Texture2D src, int srcW, int srcH)
        {
            int slot = _nextSlot;
            var km = _mutex[slot];
            try { km.AcquireSync(0, 1000); }
            catch { return; } // worker holding the slot too long (rare) — skip this frame
            try
            {
                if (srcW == _width && srcH == _height)
                    _ctx.CopyResource(_tex[slot], src);
                else
                {
                    int cw = Math.Min(srcW, _width), ch = Math.Min(srcH, _height);
                    _ctx.CopySubresourceRegion(_tex[slot], 0, 0, 0, 0, src, 0,
                        new Vortice.Mathematics.Box(0, 0, 0, cw, ch, 1));
                }
                _ctx.Flush();
            }
            finally { km.ReleaseSync(0); }

            QueryPerformanceCounter(out long qpc);
            _frameSeq++;
            SetU64Volatile(OffQpc, (ulong)qpc);
            SetIntVolatile(OffLatestSlot, slot);
            SetU64Volatile(OffFrameSeq, _frameSeq); // published last: the reader keys on this changing
            _nextSlot = (slot + 1) % _count;
            _event.Set();
        }

        private void SetU32(int off, uint v) => *(uint*)(_base + off) = v;
        private void SetInt(int off, int v) => *(int*)(_base + off) = v;
        private void SetIntVolatile(int off, int v) => Volatile.Write(ref Unsafe.AsRef<int>(_base + off), v);
        private void SetU64Volatile(long off, ulong v) => Volatile.Write(ref Unsafe.AsRef<ulong>(_base + off), v);

        public void Dispose()
        {
            for (int i = 0; i < _count; i++)
            {
                try { _mutex[i]?.Dispose(); } catch { }
                try { _tex[i]?.Dispose(); } catch { }
                // The NT handles are closed when the process exits; closing them here would invalidate a
                // worker mid-open. The ring is disposed only on shutdown, so leaving them is correct.
            }
            try { if (_base != null) _view.SafeMemoryMappedViewHandle.ReleasePointer(); } catch { }
            _base = null;
            _view?.Dispose();
            _mmf?.Dispose();
            _event?.Dispose();
        }
    }
}
