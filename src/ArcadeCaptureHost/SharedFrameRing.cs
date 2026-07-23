using System;
using System.IO.MemoryMappedFiles;
using System.Runtime.CompilerServices;
using System.Threading;

namespace ArcadeCaptureHost
{
    // SharedFrameRing is the WRITER of the BGRA frame ring the Go worker reads. The protocol is defined
    // identically here and in the worker (pkg/worker/caged/capture/wincapture.go). Little-endian; this
    // is x64 only, so native int/long writes are already little-endian.
    //
    // Layout (a named shared-memory section, created by THIS process, opened by the worker):
    //
    //   Header  64 bytes @ 0:
    //     0  u32 magic        = 'MTWC' (0x4357544D read as LE u32)
    //     4  u32 version      = 1
    //     8  u32 width        (packed frame width, pixels)
    //     12 u32 height       (packed frame height, rows)
    //     16 u32 stride       (= width*4, packed BGRA)
    //     20 u32 slotCount    = 3
    //     24 u32 slotSize     (page-aligned pixel capacity of ONE slot, bytes)
    //     28 u32 generation   (bumped when width/height change)
    //     32 i32 latestSlot   (index of the most-recently-published slot; -1 until first frame)
    //     36 (pad)
    //     40 u64 frameSeq     (monotonic; bumps every published frame)
    //     48 u64 qpcTimestamp (QueryPerformanceCounter ticks at publish)
    //     56 (reserved)
    //
    //   Then slotCount slots, each (8 + slotSize) bytes, slot i @ 64 + i*(8+slotSize):
    //     +0 u64 seq   (seqlock: odd while the slot is being written, even when complete)
    //     +8 pixels    (stride*height BGRA bytes, top-down)
    //
    // Writer publishes a frame into a slot that is NOT latestSlot (round-robin), using the per-slot
    // seqlock so the reader can detect a torn copy and retry, then publishes latestSlot/frameSeq/qpc
    // and signals the frame event.
    internal sealed unsafe class SharedFrameRing : IDisposable
    {
        public const uint Magic = 0x4357544D; // 'M','T','W','C' as a LE u32
        public const uint Version = 1;
        public const int SlotCount = 3;
        private const int HeaderSize = 64;
        private const int PageSize = 4096;

        // Header field byte offsets.
        private const int OffMagic = 0, OffVersion = 4, OffWidth = 8, OffHeight = 12, OffStride = 16;
        private const int OffSlotCount = 20, OffSlotSize = 24, OffGeneration = 28, OffLatestSlot = 32;
        private const int OffFrameSeq = 40, OffQpc = 48;

        private readonly MemoryMappedFile _mmf;
        private readonly MemoryMappedViewAccessor _view;
        private readonly EventWaitHandle _frameEvent;
        private byte* _base;

        private int _width, _height, _stride, _slotSize, _perSlot;
        private uint _generation;
        private int _nextSlot;
        private ulong _frameSeq;

        public int Width => _width;
        public int Height => _height;

        public SharedFrameRing(string shmName, string eventName, int width, int height)
        {
            _frameEvent = new EventWaitHandle(false, EventResetMode.AutoReset, eventName);

            Layout(width, height);
            long total = HeaderSize + (long)SlotCount * _perSlot;
            _mmf = MemoryMappedFile.CreateNew(shmName, total, MemoryMappedFileAccess.ReadWrite);
            _view = _mmf.CreateViewAccessor(0, total, MemoryMappedFileAccess.ReadWrite);
            byte* p = null;
            _view.SafeMemoryMappedViewHandle.AcquirePointer(ref p);
            _base = p;

            WriteHeaderStatic();
            SetInt(OffLatestSlot, -1);
        }

        private void Layout(int width, int height)
        {
            _width = width;
            _height = height;
            _stride = width * 4;
            int pixels = _stride * height;
            _slotSize = (pixels + PageSize - 1) / PageSize * PageSize; // page-aligned pixel capacity
            _perSlot = 8 + _slotSize;
        }

        private void WriteHeaderStatic()
        {
            SetU32(OffMagic, Magic);
            SetU32(OffVersion, Version);
            SetU32(OffWidth, (uint)_width);
            SetU32(OffHeight, (uint)_height);
            SetU32(OffStride, (uint)_stride);
            SetU32(OffSlotCount, SlotCount);
            SetU32(OffSlotSize, (uint)_slotSize);
            SetU32(OffGeneration, _generation);
        }

        // Publish copies one frame into the next free slot and advances the header. srcRowPtr points at
        // the top-left of the source (mapped D3D staging texture); srcRowPitch is its row pitch (>= stride,
        // may include padding). Rows are copied packed to `stride`. Content narrower/shorter than the ring
        // dimensions is clamped and the remainder zero-filled, so the ring never overflows and the worker
        // always sees fixed width*height (it builds a fixed-geometry pipeline). qpc = QPC ticks.
        public void Publish(byte* srcRowPtr, int srcRowPitch, int srcWidth, int srcHeight, long qpc)
        {
            int slot = _nextSlot;
            if (slot == GetInt(OffLatestSlot)) slot = (slot + 1) % SlotCount;

            long seqOff = HeaderSize + (long)slot * _perSlot;
            long pixOff = seqOff + 8;

            // seqlock: mark odd (writing).
            ulong seq = GetU64AtRaw(seqOff);
            SetU64Volatile(seqOff, seq + 1);

            int copyRowBytes = Math.Min(srcWidth * 4, _stride);
            int copyRows = Math.Min(srcHeight, _height);
            byte* dst = _base + pixOff;
            if (copyRowBytes == _stride && srcRowPitch == _stride)
            {
                Buffer.MemoryCopy(srcRowPtr, dst, (long)_stride * copyRows, (long)_stride * copyRows);
            }
            else
            {
                for (int y = 0; y < copyRows; y++)
                {
                    Buffer.MemoryCopy(srcRowPtr + (long)y * srcRowPitch, dst + (long)y * _stride, _stride, copyRowBytes);
                    if (copyRowBytes < _stride)
                        Unsafe.InitBlockUnaligned(dst + (long)y * _stride + copyRowBytes, 0, (uint)(_stride - copyRowBytes));
                }
            }
            // Zero any rows the source didn't fill (e.g. a transient smaller content size).
            for (int y = copyRows; y < _height; y++)
                Unsafe.InitBlockUnaligned(dst + (long)y * _stride, 0, (uint)_stride);

            // seqlock: mark even (complete). Full fence so the pixel writes are visible first.
            Thread.MemoryBarrier();
            SetU64Volatile(seqOff, seq + 2);

            // Publish header last.
            _frameSeq++;
            SetU64Volatile(OffQpc, (ulong)qpc);
            SetU64Volatile(OffFrameSeq, _frameSeq);
            SetIntVolatile(OffLatestSlot, slot); // this is what the reader keys on
            _nextSlot = (slot + 1) % SlotCount;

            _frameEvent.Set();
        }

        // BlackenAndSignal publishes an all-zero frame (used to nudge the reader / prove liveness). Not
        // used in normal flow (the worker owns black-on-exit), but handy for tests.
        public void PublishBlack(long qpc)
        {
            int slot = _nextSlot;
            if (slot == GetInt(OffLatestSlot)) slot = (slot + 1) % SlotCount;
            long seqOff = HeaderSize + (long)slot * _perSlot;
            ulong seq = GetU64AtRaw(seqOff);
            SetU64Volatile(seqOff, seq + 1);
            Unsafe.InitBlockUnaligned(_base + seqOff + 8, 0, (uint)(_stride * _height));
            Thread.MemoryBarrier();
            SetU64Volatile(seqOff, seq + 2);
            _frameSeq++;
            SetU64Volatile(OffQpc, (ulong)qpc);
            SetU64Volatile(OffFrameSeq, _frameSeq);
            SetIntVolatile(OffLatestSlot, slot);
            _nextSlot = (slot + 1) % SlotCount;
            _frameEvent.Set();
        }

        // ── raw accessors (x64, little-endian) ───────────────────────────────────────────────────────
        private void SetU32(int off, uint v) => *(uint*)(_base + off) = v;
        private void SetInt(int off, int v) => *(int*)(_base + off) = v;
        private int GetInt(int off) => *(int*)(_base + off);
        private ulong GetU64AtRaw(long off) => *(ulong*)(_base + off);
        private void SetIntVolatile(int off, int v) => Volatile.Write(ref Unsafe.AsRef<int>(_base + off), v);
        private void SetU64Volatile(long off, ulong v) => Volatile.Write(ref Unsafe.AsRef<ulong>(_base + off), v);

        public void Dispose()
        {
            try { if (_base != null) _view.SafeMemoryMappedViewHandle.ReleasePointer(); } catch { }
            _base = null;
            _view?.Dispose();
            _mmf?.Dispose();
            _frameEvent?.Dispose();
        }
    }
}
