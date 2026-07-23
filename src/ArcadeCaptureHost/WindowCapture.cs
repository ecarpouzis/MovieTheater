using System;
using System.Runtime.InteropServices;
using System.Threading;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Windows.Graphics;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX;
using Windows.Graphics.DirectX.Direct3D11;
using WinRT;

namespace ArcadeCaptureHost
{
    // WindowCapture wraps a WGC window-capture session on an own D3D11 device. Each arrived frame is
    // copied through a staging texture and handed to onFrame (mapped BGRA rows). Device-lost / item
    // Closed are surfaced via callbacks so the host can recover or exit.
    internal sealed unsafe class WindowCapture : IDisposable
    {
        // IGraphicsCaptureItemInterop — the classic Win32 interop that turns an HWND into a capture item.
        [ComImport, Guid("3628E81B-3CAC-4C60-B7F4-23CE0E0C3356"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IGraphicsCaptureItemInterop
        {
            IntPtr CreateForWindow([In] IntPtr window, [In] ref Guid iid);
            IntPtr CreateForMonitor([In] IntPtr monitor, [In] ref Guid iid);
        }

        // IDirect3DDxgiInterfaceAccess — pulls the ID3D11Texture2D out of a WGC frame's IDirect3DSurface.
        [ComImport, Guid("A9B3D012-3DF2-4EE3-B8D1-8695F457D3C1"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IDirect3DDxgiInterfaceAccess
        {
            IntPtr GetInterface([In] ref Guid iid);
        }

        [DllImport("d3d11.dll", EntryPoint = "CreateDirect3D11DeviceFromDXGIDevice", SetLastError = true)]
        private static extern int CreateDirect3D11DeviceFromDXGIDevice(IntPtr dxgiDevice, out IntPtr graphicsDevice);

        private static readonly Guid GraphicsCaptureItemIid = new("79C3F95B-31F7-4EC2-A464-632EF5D30760");
        private static readonly Guid ID3D11Texture2DIid = new("6f15aaf2-d208-4e89-9ab4-489535d34f9c");

        private readonly IntPtr _hwnd;
        private readonly int _fps;
        private readonly Action<IntPtr, int, int, int> _onFrame; // (rowPtr, rowPitch, width, height)
        private readonly Action<string> _onStatus;               // json status line (already serialized)
        private readonly Action<string> _onClosed;               // reason
        private readonly Action<string> _onError;                // detail

        private ID3D11Device _d3dDevice;
        private ID3D11DeviceContext _d3dContext;
        private IDirect3DDevice _rtDevice;
        private GraphicsCaptureItem _item;
        private Direct3D11CaptureFramePool _pool;
        private GraphicsCaptureSession _session;
        private ID3D11Texture2D _staging;
        private SizeInt32 _stagingSize;
        private SizeInt32 _lastContentSize;
        private uint _generation;
        private long _frames;
        private int _disposed;

        // Ring geometry the frames must come out at. When the capture item's physical size differs
        // (DPI-virtualized window on a scaled display — the console TV runs 300%), frames are
        // GPU-downscaled to this size instead of letting the ring crop the top-left corner.
        private int _targetW, _targetH;
        private VideoScaler _scaler;
        private bool _scalerBroken;

        public long Frames => Volatile.Read(ref _frames);
        public uint Generation => _generation;

        public WindowCapture(IntPtr hwnd, int fps, int targetWidth, int targetHeight,
            Action<IntPtr, int, int, int> onFrame,
            Action<string> onStatus, Action<string> onClosed, Action<string> onError)
        {
            _hwnd = hwnd;
            _fps = fps;
            _targetW = targetWidth;
            _targetH = targetHeight;
            _onFrame = onFrame;
            _onStatus = onStatus;
            _onClosed = onClosed;
            _onError = onError;
        }

        public SizeInt32 Start()
        {
            CreateDevice();

            var interop = GraphicsCaptureItem.As<IGraphicsCaptureItemInterop>();
            var iid = GraphicsCaptureItemIid;
            IntPtr itemPtr = interop.CreateForWindow(_hwnd, ref iid);
            _item = MarshalInterface<GraphicsCaptureItem>.FromAbi(itemPtr);
            Marshal.Release(itemPtr);

            _item.Closed += (s, e) => { if (Volatile.Read(ref _disposed) == 0) _onClosed("item-closed"); };

            var size = _item.Size;
            _lastContentSize = size;
            // No explicit ring geometry (standalone/testing): lock the ring to the first item size so
            // later mid-room resizes still come out at a fixed geometry.
            if (_targetW <= 0 || _targetH <= 0) { _targetW = size.Width; _targetH = size.Height; }
            _pool = Direct3D11CaptureFramePool.CreateFreeThreaded(
                _rtDevice, DirectXPixelFormat.B8G8R8A8UIntNormalized, 2, size);
            _pool.FrameArrived += OnFrameArrived;

            _session = _pool.CreateCaptureSession(_item);
            TryConfigureSession(_session);
            _session.StartCapture();
            return size;
        }

        private void CreateDevice()
        {
            var flags = DeviceCreationFlags.BgraSupport;
            var levels = new[] { FeatureLevel.Level_11_1, FeatureLevel.Level_11_0 };
            var res = D3D11.D3D11CreateDevice(null, DriverType.Hardware, flags, levels,
                out _d3dDevice, out _d3dContext);
            if (res.Failure)
            {
                // Retry on WARP so a headless/odd adapter state still yields a device.
                D3D11.D3D11CreateDevice(null, DriverType.Warp, flags, levels, out _d3dDevice, out _d3dContext).CheckError();
            }
            using var dxgi = _d3dDevice.QueryInterface<IDXGIDevice>();
            int hr = CreateDirect3D11DeviceFromDXGIDevice(dxgi.NativePointer, out IntPtr rtPtr);
            if (hr != 0) throw new InvalidOperationException($"CreateDirect3D11DeviceFromDXGIDevice hr=0x{hr:X8}");
            _rtDevice = MarshalInterface<IDirect3DDevice>.FromAbi(rtPtr);
            Marshal.Release(rtPtr);
        }

        // Best-effort: no cursor, no yellow capture border. Both properties are build-gated; failure is
        // non-fatal (the window is off-screen anyway, so a border nobody sees is harmless).
        private void TryConfigureSession(GraphicsCaptureSession s)
        {
            try { s.IsCursorCaptureEnabled = false; } catch { }
            try
            {
                if (ApiInformation_IsBorderPropertyPresent())
                    s.IsBorderRequired = false;
            }
            catch { }
        }

        private static bool ApiInformation_IsBorderPropertyPresent()
        {
            try
            {
                return Windows.Foundation.Metadata.ApiInformation.IsPropertyPresent(
                    "Windows.Graphics.Capture.GraphicsCaptureSession", "IsBorderRequired");
            }
            catch { return false; }
        }

        private void OnFrameArrived(Direct3D11CaptureFramePool sender, object args)
        {
            if (Volatile.Read(ref _disposed) != 0) return;
            Direct3D11CaptureFrame frame = null;
            try
            {
                frame = sender.TryGetNextFrame();
                if (frame == null) return;

                var content = frame.ContentSize;
                if (content.Width != _lastContentSize.Width || content.Height != _lastContentSize.Height)
                {
                    _lastContentSize = content;
                    _generation++;
                    _onStatus($"{{\"event\":\"resize\",\"width\":{content.Width},\"height\":{content.Height},\"generation\":{_generation}}}");
                    // Recreate the pool at the new content size so WGC keeps delivering.
                    try { sender.Recreate(_rtDevice, DirectXPixelFormat.B8G8R8A8UIntNormalized, 2, content); }
                    catch (Exception ex) { _onError("pool-recreate: " + ex.Message); }
                }

                using var surfaceTexture = GetTextureFromSurface(frame.Surface);
                var desc = surfaceTexture.Description;

                // A frame bigger/smaller than the ring (DPI-virtualized window) is GPU-downscaled to
                // ring size; a raw copy would let the ring keep only the top-left corner (the console
                // 300%-scaling black screen, 2026-07-23). On any scaler failure fall back to that crop
                // so a room still shows SOMETHING while the error is visible in the log.
                bool scaled = false;
                if (((int)desc.Width != _targetW || (int)desc.Height != _targetH) && !_scalerBroken)
                {
                    try
                    {
                        _scaler ??= new VideoScaler(_d3dDevice, _d3dContext);
                        if (_scaler.Ensure((int)desc.Width, (int)desc.Height, _targetW, _targetH))
                            _onStatus($"{{\"event\":\"scale\",\"srcWidth\":{desc.Width},\"srcHeight\":{desc.Height},\"dstWidth\":{_targetW},\"dstHeight\":{_targetH}}}");
                        _scaler.Process(surfaceTexture);
                        scaled = true;
                    }
                    catch (Exception ex)
                    {
                        uint shr = (uint)ex.HResult;
                        if (shr == 0x887A0005 /*DEVICE_REMOVED*/ || shr == 0x887A0006 /*DEVICE_HUNG*/)
                            throw; // outer handler recovers the whole capture
                        _scalerBroken = true;
                        _onError("scaler: " + ex.Message + " — falling back to top-left crop");
                    }
                }

                int outW = scaled ? _targetW : (int)desc.Width;
                int outH = scaled ? _targetH : (int)desc.Height;
                EnsureStaging(outW, outH);
                _d3dContext.CopyResource(_staging, scaled ? _scaler.Output : surfaceTexture);
                var map = _d3dContext.Map(_staging, 0, MapMode.Read, Vortice.Direct3D11.MapFlags.None);
                try
                {
                    _onFrame(map.DataPointer, (int)map.RowPitch, outW, outH);
                    Interlocked.Increment(ref _frames);
                }
                finally
                {
                    _d3dContext.Unmap(_staging, 0);
                }
            }
            catch (Exception ex)
            {
                // A device-lost surfaces here (DXGI_ERROR_DEVICE_REMOVED) — report and let the host recover.
                uint hr = (uint)ex.HResult;
                if (hr == 0x887A0005 /*DEVICE_REMOVED*/ || hr == 0x887A0006 /*DEVICE_HUNG*/)
                    _onClosed("device-lost");
                else
                    _onError("frame: " + ex.Message);
            }
            finally
            {
                frame?.Dispose();
            }
        }

        private ID3D11Texture2D GetTextureFromSurface(IDirect3DSurface surface)
        {
            var access = surface.As<IDirect3DDxgiInterfaceAccess>();
            var iid = ID3D11Texture2DIid;
            IntPtr ptr = access.GetInterface(ref iid);
            return new ID3D11Texture2D(ptr);
        }

        private void EnsureStaging(int w, int h)
        {
            if (_staging != null && _stagingSize.Width == w && _stagingSize.Height == h) return;
            _staging?.Dispose();
            _staging = _d3dDevice.CreateTexture2D(new Texture2DDescription
            {
                Width = (uint)w,
                Height = (uint)h,
                MipLevels = 1,
                ArraySize = 1,
                Format = Vortice.DXGI.Format.B8G8R8A8_UNorm,
                SampleDescription = new SampleDescription(1, 0),
                Usage = ResourceUsage.Staging,
                BindFlags = BindFlags.None,
                CPUAccessFlags = CpuAccessFlags.Read,
                MiscFlags = ResourceOptionFlags.None,
            });
            _stagingSize = new SizeInt32 { Width = w, Height = h };
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            try { _session?.Dispose(); } catch { }
            try { if (_pool != null) _pool.FrameArrived -= OnFrameArrived; } catch { }
            try { _pool?.Dispose(); } catch { }
            try { _scaler?.Dispose(); } catch { }
            try { _staging?.Dispose(); } catch { }
            try { _d3dContext?.Dispose(); } catch { }
            try { _d3dDevice?.Dispose(); } catch { }
        }
    }
}
