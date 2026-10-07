using System;
using System.Diagnostics;
using System.Globalization;

using System.Linq;
using Avalonia;
using Avalonia.Platform;
using Avalonia.Rendering.Composition;
using SharpDX;
using SharpDX.Direct3D;
using SharpDX.Direct3D11;
using SharpDX.DXGI;
using SharpDX.D3DCompiler;
using SharpDX.Mathematics.Interop;

using Buffer = SharpDX.Direct3D11.Buffer;
using DxgiFactory1 = SharpDX.DXGI.Factory1;
using Matrix = SharpDX.Matrix;
using D3DDevice = SharpDX.Direct3D11.Device;
using Rectangle = Avalonia.Controls.Shapes.Rectangle;

namespace GpuInterop.D3DDemo;

// public class D3D11DemoControl : DrawingSurfaceDemoBase
public class D3D11DemoControl : DrawingSurfaceDemoBase
{
    private D3DDevice? _device;
    private D3D11Swapchain? _swapchain;

    private Texture2D? _imageTexture;
    private ShaderResourceView? _imageView;

    private VertexShader? _vertexShader;
    private PixelShader? _pixelShader;
    private InputLayout? _inputLayout;
    private Buffer? _vertexBuffer;
    private SamplerState? _sampler;

    private PixelSize _lastSize;
    private PixelSize _lastRenderedSize;

    private int _imageWidth;
    private int _imageHeight;

    // --- Debounce state for swapchain resize -----------------------------
    // IMPORTANT: RenderFrame / InitializeGraphicsResources / FreeGraphicsResources
    // all run on Avalonia's dedicated render/compositor thread, NOT the UI
    // thread. Avalonia's DispatcherTimer is tied to the UI thread's
    // dispatcher and is NOT safe to Start()/Stop() from another thread, so
    // we use a plain Stopwatch checked inline on the render thread instead.
    //
    // SwapchainBase recreates its backing GPU image (texture + shared
    // handle + imported composition image) every time BeginDraw is called
    // with a size that differs from the current image's size. If we fed it
    // the live, unthrottled pixel size every frame, a window drag would
    // create dozens of brand-new full-size GPU images per second. So we
    // keep two sizes: the live size (for the viewport, so the quad still
    // stretches smoothly every frame) and a debounced size (fed to
    // BeginDraw, so the actual swapchain image is only recreated once
    // resizing has paused for ResizeDebounceMs).
    private readonly Stopwatch _resizeStopwatch = new();
    private const long ResizeDebounceMs = 100;
    private PixelSize _pendingSwapchainSize;
    private PixelSize _currentSwapchainSize;
    private bool _swapchainNeedsResize;

    // --- Latency measurement state ---------------------------------------
    // These are all touched only from RenderFrame on the render thread,
    // so no locking is needed.
    private readonly Stopwatch _frameStopwatch = new();

    // Rolling stats over the last N frames. Tune as you like.
    private const int StatsWindowSize = 120; // ~2 seconds at 60 fps
    private readonly double[] _frameTimeHistory = new double[StatsWindowSize];
    private int _frameTimeIndex;
    private int _frameTimeCount;

    // Aggregated stats printed every StatsPrintIntervalMs.
    private readonly Stopwatch _statsPrintStopwatch = new();
    private const long StatsPrintIntervalMs = 1000;
    private long _totalFramesSinceLastPrint;

    // Minimum / maximum seen since last print
    private double _minFrameTimeMs = double.MaxValue;
    private double _maxFrameTimeMs = double.MinValue;

    // Set to false to silence the periodic console output entirely.
    private const bool EnableLatencyLogging = true;

    protected override (
        bool success,
        string info)
        InitializeGraphicsResources(
            Compositor compositor,
            CompositionDrawingSurface surface,
            ICompositionGpuInterop interop)
    {
        if (interop.SupportedImageHandleTypes.Contains(
                KnownPlatformGraphicsExternalImageHandleTypes
                    .D3D11TextureGlobalSharedHandle) != true)
        {
            return (
                false,
                "DXGI shared handle import is not supported by the current graphics backend");
        }

        var factory = new DxgiFactory1();

        using var adapter = factory.GetAdapter1(0);

        _device = new D3DDevice(
            adapter,
            DeviceCreationFlags.None,
            new[]
            {
                FeatureLevel.Level_12_1,
                FeatureLevel.Level_12_0,
                FeatureLevel.Level_11_1,
                FeatureLevel.Level_11_0,
                FeatureLevel.Level_10_0,
                FeatureLevel.Level_9_3,
                FeatureLevel.Level_9_2,
                FeatureLevel.Level_9_1
            });

        _swapchain = new D3D11Swapchain(
            _device,
            interop,
            surface);

        CreateShaders();
        CreateQuad();
        CreateSampler();
        LoadImage();

        _swapchainNeedsResize = false;
        _resizeStopwatch.Reset();
        _currentSwapchainSize = default;
        _lastSize = default;

        // Reset latency counters on (re)initialization
        Array.Clear(_frameTimeHistory, 0, _frameTimeHistory.Length);
        _frameTimeIndex = 0;
        _frameTimeCount = 0;
        _totalFramesSinceLastPrint = 0;
        _minFrameTimeMs = double.MaxValue;
        _maxFrameTimeMs = double.MinValue;
        _statsPrintStopwatch.Restart();

        return (
            true,
            $"D3D11 ({_device.FeatureLevel}) {adapter.Description1.Description}");
    }

    private void CreateShaders()
    {
        const string vertexShaderCode = @"
struct VSInput
{
    float3 Position : POSITION;
    float2 TexCoord : TEXCOORD0;
};

struct VSOutput
{
    float4 Position : SV_POSITION;
    float2 TexCoord : TEXCOORD0;
};

VSOutput main(VSInput input)
{
    VSOutput output;

    output.Position = float4(input.Position, 1.0);
    output.TexCoord = input.TexCoord;

    return output;
}";

        const string pixelShaderCode = @"
Texture2D Image : register(t0);
SamplerState Sampler : register(s0);

struct PSInput
{
    float4 Position : SV_POSITION;
    float2 TexCoord : TEXCOORD0;
};

float4 main(PSInput input) : SV_TARGET
{
    return Image.Sample(Sampler, input.TexCoord);
}";

        using var vsByteCode = ShaderBytecode.Compile(
            vertexShaderCode,
            "main",
            "vs_5_0");

        using var psByteCode = ShaderBytecode.Compile(
            pixelShaderCode,
            "main",
            "ps_5_0");

        _vertexShader = new VertexShader(
            _device!,
            vsByteCode);

        _pixelShader = new PixelShader(
            _device!,
            psByteCode);

        _inputLayout = new InputLayout(
            _device!,
            ShaderSignature.GetInputSignature(vsByteCode),
            new[]
            {
                new InputElement(
                    "POSITION",
                    0,
                    Format.R32G32B32_Float,
                    0,
                    0),

                new InputElement(
                    "TEXCOORD",
                    0,
                    Format.R32G32_Float,
                    12,
                    0)
            });
    }

    private void CreateQuad()
    {
        /*
         * Fullscreen quad.
         *
         * Position is already in clip space:
         *
         * (-1,+1) -------- (+1,+1)
         *    |                |
         *    |                |
         * (-1,-1) -------- (+1,-1)
         */

        var vertices = new[]
        {
            // Position             UV

            -1.0f,  1.0f, 0.0f,     0.0f, 1.0f,
            1.0f,  1.0f, 0.0f,     1.0f, 1.0f,
            1.0f, -1.0f, 0.0f,     1.0f, 0.0f,

            -1.0f,  1.0f, 0.0f,     0.0f, 1.0f,
            1.0f, -1.0f, 0.0f,     1.0f, 0.0f,
            -1.0f, -1.0f, 0.0f,     0.0f, 0.0f
        };

        _vertexBuffer = Buffer.Create(
            _device!,
            BindFlags.VertexBuffer,
            vertices);
    }

    private void CreateSampler()
    {
        _sampler = new SamplerState(
            _device!,
            new SamplerStateDescription
            {
                Filter = Filter.MinMagMipLinear,

                AddressU = TextureAddressMode.Clamp,
                AddressV = TextureAddressMode.Clamp,
                AddressW = TextureAddressMode.Clamp,

                ComparisonFunction = Comparison.Never,

                MinimumLod = 0,
                MaximumLod = float.MaxValue
            });
    }

    private void LoadImage()
    {
        const string imagePath = "dev_img1.jpg";

        if (!System.IO.File.Exists(imagePath))
        {
            throw new System.IO.FileNotFoundException(
                $"Could not find {imagePath}.",
                imagePath);
        }

        using var factory = new SharpDX.WIC.ImagingFactory2();

        using var decoder = new SharpDX.WIC.BitmapDecoder(
            factory,
            imagePath,
            SharpDX.WIC.DecodeOptions.CacheOnLoad);

        using var frame = decoder.GetFrame(0);

        _imageWidth = frame.Size.Width;
        _imageHeight = frame.Size.Height;

        using var converter = new SharpDX.WIC.FormatConverter(factory);

        converter.Initialize(
            frame,
            SharpDX.WIC.PixelFormat.Format32bppBGRA,
            SharpDX.WIC.BitmapDitherType.None,
            null,
            0.0,
            SharpDX.WIC.BitmapPaletteType.Custom);

        var description = new Texture2DDescription
        {
            Width = _imageWidth,
            Height = _imageHeight,

            MipLevels = 1,
            ArraySize = 1,

            Format = Format.B8G8R8A8_UNorm,

            SampleDescription = new SampleDescription(1, 0),

            Usage = ResourceUsage.Immutable,

            BindFlags = BindFlags.ShaderResource,

            CpuAccessFlags = CpuAccessFlags.None,

            OptionFlags = ResourceOptionFlags.None
        };

        int stride = _imageWidth * 4;
        int bufferSize = stride * _imageHeight;

        using var pixels = new SharpDX.DataStream(
            bufferSize,
            true,
            true);

        converter.CopyPixels(
            stride,
            pixels);

        pixels.Position = 0;

        var data = new DataBox(
            pixels.DataPointer,
            stride,
            0);

        _imageTexture = new Texture2D(
            _device!,
            description,
            new[] { data });

        _imageView = new ShaderResourceView(
            _device!,
            _imageTexture);
    }

    protected override void FreeGraphicsResources()
    {
        _swapchainNeedsResize = false;
        _resizeStopwatch.Reset();
        _frameStopwatch.Reset();

        if (_swapchain is not null)
        {
            _swapchain.DisposeAsync()
                .GetAwaiter()
                .GetResult();

            _swapchain = null;
        }

        Utilities.Dispose(ref _sampler);
        Utilities.Dispose(ref _vertexBuffer);
        Utilities.Dispose(ref _inputLayout);

        Utilities.Dispose(ref _vertexShader);
        Utilities.Dispose(ref _pixelShader);

        Utilities.Dispose(ref _imageView);
        Utilities.Dispose(ref _imageTexture);

        Utilities.Dispose(ref _device);
    }

    protected override bool SupportsDisco => false;

    protected override void RenderFrame(PixelSize pixelSize)
    {
        if (pixelSize == default)
            return;

        if (_swapchain is null ||
            _device is null ||
            _imageView is null)
        {
            return;
        }

        // --- START FRAME TIMING ------------------------------------------
        // Measure from the very top of RenderFrame to the very bottom.
        // This is the CPU-side cost of recording and submitting the frame.
        // It does NOT include GPU execution time or compositor latency.
        _frameStopwatch.Restart();

        // Detect a size change and (re)start the debounce window. We do
        // NOT feed 'pixelSize' straight into BeginDraw — that would make
        // SwapchainBase recreate the GPU image on every single frame while
        // the user drags the window edge. Instead we only update
        // _currentSwapchainSize (what BeginDraw actually uses) once the
        // size has stopped changing for ResizeDebounceMs.
        if (pixelSize != _lastSize)
        {
            _lastSize = pixelSize;

            // First frame ever: no point debouncing, there's nothing to
            // stretch yet — apply immediately so BeginDraw has a valid size.
            if (_currentSwapchainSize == default)
                _currentSwapchainSize = pixelSize;
            else
                ScheduleSwapchainResize(pixelSize);
        }

        // Everything below runs on the render thread, so it's safe to just
        // check elapsed time here instead of using a UI-thread timer.
        ApplyPendingResizeIfSettled();

        // Draw using the debounced size, so the swapchain image is only
        // recreated once resizing has settled. The viewport below still
        // uses the *live* pixelSize every frame, so the compositor stretches
        // the output to fill the control in the meantime — same live-resize
        // feel, without recreating a full GPU image on every frame.
        using (_swapchain.BeginDraw(
                   _currentSwapchainSize,
                   out var renderView))
        {
            var context = _device.ImmediateContext;

            /*
             * Render target
             */
            context.OutputMerger.SetTargets(renderView);

            /*
             * Clear background.
             */
            context.ClearRenderTargetView(
                renderView,
                new RawColor4(
                    0.0f,
                    0.0f,
                    0.0f,
                    1.0f));

            /*
             * Viewport
             *
             * Use the *control's* pixel size here, not the swapchain's size.
             * This makes the fullscreen quad stretch to fill the new bounds
             * immediately, so the image resizes live.
             */
            context.Rasterizer.SetViewport(
                0,
                0,
                pixelSize.Width,
                pixelSize.Height,
                0.0f,
                1.0f);

            /*
             * Input layout
             */
            context.InputAssembler.InputLayout =
                _inputLayout;

            context.InputAssembler.PrimitiveTopology =
                SharpDX.Direct3D.PrimitiveTopology.TriangleList;

            context.InputAssembler.SetVertexBuffers(
                0,
                new VertexBufferBinding(
                    _vertexBuffer!,
                    sizeof(float) * 5,
                    0));

            /*
             * Shaders
             */
            context.VertexShader.Set(
                _vertexShader);

            context.PixelShader.Set(
                _pixelShader);

            /*
             * JPEG texture
             */
            context.PixelShader.SetShaderResource(
                0,
                _imageView);

            context.PixelShader.SetSampler(
                0,
                _sampler);

            /*
             * Draw fullscreen quad.
             */
            context.Draw(
                6,
                0);

            /*
             * Make sure commands have been submitted.
             */
            context.Flush();
        }

        _lastRenderedSize = pixelSize;

        // --- END FRAME TIMING --------------------------------------------
        _frameStopwatch.Stop();
        RecordFrameTime(_frameStopwatch.Elapsed.TotalMilliseconds);
    }

    /// <summary>
    /// Marks that a swapchain resize is pending and (re)starts the debounce
    /// window. Called from RenderFrame, so it's already on the render
    /// thread — no locking needed.
    /// </summary>
    private void ScheduleSwapchainResize(PixelSize newSize)
    {
        _pendingSwapchainSize = newSize;
        _swapchainNeedsResize = true;
        _resizeStopwatch.Restart();
    }

    /// <summary>
    /// Called once per frame from RenderFrame (render thread). Updates
    /// _currentSwapchainSize — the size actually passed to BeginDraw — only
    /// after the live size has stopped changing for ResizeDebounceMs.
    /// SwapchainBase takes care of disposing the old image and creating a
    /// new one the next time BeginDraw sees a different size; we're just
    /// controlling *when* that happens instead of doing it every frame.
    /// </summary>
    private void ApplyPendingResizeIfSettled()
    {
        if (!_swapchainNeedsResize)
            return;

        if (_resizeStopwatch.ElapsedMilliseconds < ResizeDebounceMs)
            return;

        _swapchainNeedsResize = false;
        _resizeStopwatch.Reset();

        var sizeToApply = _pendingSwapchainSize;

        if (sizeToApply.Width <= 0 || sizeToApply.Height <= 0)
            return;

        _currentSwapchainSize = sizeToApply;
        _lastRenderedSize = sizeToApply;
    }

    // ---------------------------------------------------------------------
    // Latency measurement helpers
    // ---------------------------------------------------------------------

    /// <summary>
    /// Stores a single frame's CPU time in the rolling window and, if the
    /// print interval has elapsed, emits aggregated statistics to the
    /// debugger console.
    /// </summary>
    private void RecordFrameTime(double frameTimeMs)
    {
        _frameTimeHistory[_frameTimeIndex] = frameTimeMs;
        _frameTimeIndex = (_frameTimeIndex + 1) % StatsWindowSize;

        if (_frameTimeCount < StatsWindowSize)
            _frameTimeCount++;

        _totalFramesSinceLastPrint++;

        if (frameTimeMs < _minFrameTimeMs)
            _minFrameTimeMs = frameTimeMs;

        if (frameTimeMs > _maxFrameTimeMs)
            _maxFrameTimeMs = frameTimeMs;

        if (!EnableLatencyLogging)
            return;

        if (_statsPrintStopwatch.ElapsedMilliseconds < StatsPrintIntervalMs)
            return;

        _statsPrintStopwatch.Restart();

        double sum = 0;
        double min = double.MaxValue;
        double max = double.MinValue;

        for (int i = 0; i < _frameTimeCount; i++)
        {
            double v = _frameTimeHistory[i];
            sum += v;

            if (v < min) min = v;
            if (v > max) max = v;
        }

        double avg = sum / _frameTimeCount;

        // Compute standard deviation for jitter insight.
        double varianceSum = 0;
        for (int i = 0; i < _frameTimeCount; i++)
        {
            double d = _frameTimeHistory[i] - avg;
            varianceSum += d * d;
        }

        double stdDev = Math.Sqrt(varianceSum / _frameTimeCount);

        double fps = avg > 0 ? 1000.0 / avg : 0;

        Debug.WriteLine(string.Format(
            CultureInfo.InvariantCulture,
            "[FrameLatency] frames={0} fps={1:F1} avg={2:F3}ms min={3:F3}ms max={4:F3}ms stddev={5:F3}ms window={6}",
            _totalFramesSinceLastPrint,
            fps,
            avg,
            min,
            max,
            stdDev,
            _frameTimeCount));
        Console.WriteLine(string.Format(
            CultureInfo.InvariantCulture,
            "[FrameLatency] frames={0} fps={1:F1} avg={2:F3}ms min={3:F3}ms max={4:F3}ms stddev={5:F3}ms window={6}",
            _totalFramesSinceLastPrint,
            fps,
            avg,
            min,
            max,
            stdDev,
            _frameTimeCount));

        // Reset per-interval counters.
        _totalFramesSinceLastPrint = 0;
        _minFrameTimeMs = double.MaxValue;
        _maxFrameTimeMs = double.MinValue;
    }
}