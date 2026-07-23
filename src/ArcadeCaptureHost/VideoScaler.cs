using System;
using Vortice;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Vortice.Mathematics;

namespace ArcadeCaptureHost
{
    // VideoScaler is a GPU scaling blit (D3D11 video processor) that bridges a WGC capture item whose
    // physical size differs from the shm ring. The load-bearing case: a DPI-unaware game window on a
    // scaled display (the console QN90 runs 300%) is DWM-virtualized, so WGC delivers scale× the
    // window's logical size — 5760x3240 for a 1920x1080 window — and a raw copy into the ring would
    // keep only the top-left corner. The blit preserves aspect: content is fitted centered onto a
    // black background.
    //
    // One instance per WindowCapture; Ensure() rebuilds the per-size objects whenever the source or
    // destination geometry changes (cheap, happens once per room in practice).
    internal sealed class VideoScaler : IDisposable
    {
        private readonly ID3D11Device _device;
        private readonly ID3D11DeviceContext _context;
        private readonly ID3D11VideoDevice _videoDevice;   // throws in ctor if unsupported → caller falls back
        private readonly ID3D11VideoContext _videoContext;

        private ID3D11VideoProcessorEnumerator _enum;
        private ID3D11VideoProcessor _proc;
        private ID3D11Texture2D _src;          // default-usage copy of the capture frame (input-view target)
        private ID3D11VideoProcessorInputView _srcView;
        private ID3D11Texture2D _out;          // ring-sized render target the caller reads back
        private ID3D11VideoProcessorOutputView _outView;
        private ID3D11RenderTargetView _outRtv;
        private int _srcW, _srcH, _dstW, _dstH;

        public ID3D11Texture2D Output => _out;

        public VideoScaler(ID3D11Device device, ID3D11DeviceContext context)
        {
            _device = device;
            _context = context;
            _videoDevice = device.QueryInterface<ID3D11VideoDevice>();
            _videoContext = context.QueryInterface<ID3D11VideoContext>();
        }

        // Ensure (re)builds processor + textures + views for the given geometry. Returns true when the
        // geometry changed (first use included) so the caller can log it once.
        public bool Ensure(int srcW, int srcH, int dstW, int dstH)
        {
            if (srcW == _srcW && srcH == _srcH && dstW == _dstW && dstH == _dstH && _proc != null)
                return false;
            ReleaseSized();
            _srcW = srcW; _srcH = srcH; _dstW = dstW; _dstH = dstH;

            var desc = new VideoProcessorContentDescription
            {
                InputFrameFormat = VideoFrameFormat.Progressive,
                InputFrameRate = new Rational(60u, 1u),
                InputWidth = (uint)srcW,
                InputHeight = (uint)srcH,
                OutputFrameRate = new Rational(60u, 1u),
                OutputWidth = (uint)dstW,
                OutputHeight = (uint)dstH,
                Usage = VideoUsage.PlaybackNormal,
            };
            _enum = _videoDevice.CreateVideoProcessorEnumerator(desc);
            _proc = _videoDevice.CreateVideoProcessor(_enum, 0);

            _src = _device.CreateTexture2D(new Texture2DDescription
            {
                Width = (uint)srcW,
                Height = (uint)srcH,
                MipLevels = 1,
                ArraySize = 1,
                Format = Format.B8G8R8A8_UNorm,
                SampleDescription = new SampleDescription(1, 0),
                Usage = ResourceUsage.Default,
                BindFlags = BindFlags.RenderTarget | BindFlags.ShaderResource,
            });
            _out = _device.CreateTexture2D(new Texture2DDescription
            {
                Width = (uint)dstW,
                Height = (uint)dstH,
                MipLevels = 1,
                ArraySize = 1,
                Format = Format.B8G8R8A8_UNorm,
                SampleDescription = new SampleDescription(1, 0),
                Usage = ResourceUsage.Default,
                BindFlags = BindFlags.RenderTarget,
            });
            _outRtv = _device.CreateRenderTargetView(_out);

            _srcView = _videoDevice.CreateVideoProcessorInputView(_src, _enum, new VideoProcessorInputViewDescription
            {
                FourCC = 0,
                ViewDimension = VideoProcessorInputViewDimension.Texture2D,
                Texture2D = new Texture2DVideoProcessorInputView { MipSlice = 0, ArraySlice = 0 },
            });
            _outView = _videoDevice.CreateVideoProcessorOutputView(_out, _enum, new VideoProcessorOutputViewDescription
            {
                ViewDimension = VideoProcessorOutputViewDimension.Texture2D,
                Texture2D = new Texture2DVideoProcessorOutputView { MipSlice = 0 },
            });

            var fit = Fit(srcW, srcH, dstW, dstH);
            _videoContext.VideoProcessorSetStreamFrameFormat(_proc, 0, VideoFrameFormat.Progressive);
            _videoContext.VideoProcessorSetStreamAutoProcessingMode(_proc, 0, false); // no driver "enhancement"
            _videoContext.VideoProcessorSetStreamSourceRect(_proc, 0, true, new RawRect(0, 0, srcW, srcH));
            _videoContext.VideoProcessorSetStreamDestRect(_proc, 0, true, fit);
            _videoContext.VideoProcessorSetOutputTargetRect(_proc, true, new RawRect(0, 0, dstW, dstH));
            return true;
        }

        // Process blits one captured frame (must match the Ensure'd source size) into Output.
        public void Process(ID3D11Texture2D frameTexture)
        {
            _context.CopyResource(_src, frameTexture);
            // Clear first so letterbox bars (and anything a driver leaves outside the dest rect) are black.
            _context.ClearRenderTargetView(_outRtv, new Color4(0f, 0f, 0f, 1f));
            var stream = new VideoProcessorStream
            {
                Enable = true,
                OutputIndex = 0,
                InputFrameOrField = 0,
                InputSurface = _srcView,
            };
            _videoContext.VideoProcessorBlt(_proc, _outView, 0, 1, new[] { stream });
        }

        // Fit computes the centered aspect-preserving destination rectangle.
        private static RawRect Fit(int sw, int sh, int dw, int dh)
        {
            int w, h;
            if ((long)sw * dh >= (long)sh * dw) { w = dw; h = Math.Max(1, (int)((long)sh * dw / sw)); }
            else { h = dh; w = Math.Max(1, (int)((long)sw * dh / sh)); }
            int x = (dw - w) / 2, y = (dh - h) / 2;
            return new RawRect(x, y, x + w, y + h);
        }

        private void ReleaseSized()
        {
            _outRtv?.Dispose(); _outRtv = null;
            _outView?.Dispose(); _outView = null;
            _srcView?.Dispose(); _srcView = null;
            _out?.Dispose(); _out = null;
            _src?.Dispose(); _src = null;
            _proc?.Dispose(); _proc = null;
            _enum?.Dispose(); _enum = null;
        }

        public void Dispose()
        {
            ReleaseSized();
            _videoContext?.Dispose();
            _videoDevice?.Dispose();
        }
    }
}
