//
//
//
// using System;
// using System.Threading.Tasks;
// using Avalonia;
// using Avalonia.Controls;
// using Avalonia.LogicalTree;
// using Avalonia.Rendering.Composition;
// using Avalonia.VisualTree;
//
// namespace GpuInterop;
//
// public abstract class DrawingSurfaceDemoBase : Control
// {
//     private CompositionSurfaceVisual? _visual;
//     private Compositor? _compositor;
//     private readonly Action _update;
//     private string _info = string.Empty;
//     private bool _updateQueued;
//     private bool _initialized;
//
//     protected CompositionDrawingSurface? Surface { get; private set; }
//
//     public DrawingSurfaceDemoBase()
//     {
//         _update = UpdateFrame;
//     }
//
//     protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
//     {
//         base.OnAttachedToVisualTree(e);
//         Initialize();
//     }
//
//     protected override void OnDetachedFromLogicalTree(LogicalTreeAttachmentEventArgs e)
//     {
//         if (_initialized)
//             FreeGraphicsResources();
//         _initialized = false;
//         base.OnDetachedFromLogicalTree(e);
//     }
//
//     async void Initialize()
//     {
//         try
//         {
//             var selfVisual = ElementComposition.GetElementVisual(this)!;
//             _compositor = selfVisual.Compositor;
//
//             Surface = _compositor.CreateDrawingSurface();
//             _visual = _compositor.CreateSurfaceVisual();
//             _visual.Size = new(Bounds.Width, Bounds.Height);
//             _visual.Surface = Surface;
//             ElementComposition.SetElementChildVisual(this, _visual);
//
//             var (res, info) = await DoInitialize(_compositor, Surface);
//             _info = info;
//          
//             _initialized = res;
//             QueueNextFrame();
//         }
//         catch (Exception e)
//         {
//             
//             Console.WriteLine(e.ToString());
//         }
//     }
//
//     void UpdateFrame()
//     {
//         _updateQueued = false;
//         var root = this.GetVisualRoot();
//         if (root == null)
//             return;
//
//         // _visual is null once we have fallen back to software rendering.
//         if (_visual != null)
//             _visual.Size = new(Bounds.Width, Bounds.Height);
//
//         var size = PixelSize.FromSize(Bounds.Size, root.RenderScaling);
//         RenderFrame(size);
//         if ((SupportsDisco && Disco > 0) || RunContinuously)
//             QueueNextFrame();
//     }
//
//     void QueueNextFrame()
//     {
//         if (_initialized && !_updateQueued && _compositor != null)
//         {
//             _updateQueued = true;
//             _compositor?.RequestCompositionUpdate(_update);
//         }
//     }
//
//     protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
//     {
//         if (change.Property == BoundsProperty)
//             QueueNextFrame();
//         base.OnPropertyChanged(change);
//     }
//
//     async Task<(bool success, string info)> DoInitialize(Compositor compositor,
//         CompositionDrawingSurface compositionDrawingSurface)
//     {
//         try
//         {
//             var interop = await compositor.TryGetCompositionGpuInterop();
//             if (interop == null)
//                 return FallbackToSoftware("Compositor doesn't support GPU interop for the current backend");
//
//             var result = InitializeGraphicsResources(compositor, compositionDrawingSurface, interop);
//             if (!result.success)
//                 return FallbackToSoftware(result.info);
//
//             return result;
//         }
//         catch (Exception ex)
//         {
//             return FallbackToSoftware(ex.Message);
//         }
//     }
//
//     private (bool success, string info) FallbackToSoftware(string reason)
//     {
//         // Remove the (empty) GPU surface so it doesn't cover what Render() draws.
//         try { ElementComposition.SetElementChildVisual(this, null); } catch { }
//         _visual = null;
//         Surface = null;
//         return InitializeSoftwareFallback(reason);
//     }
//
//     /// <summary>
//     /// Called when the GPU path is unavailable. Return success = true to keep
//     /// running in software mode (the derived control then draws in Render()).
//     /// </summary>
//     protected virtual (bool success, string info) InitializeSoftwareFallback(string reason)
//         => (false, reason);
//
//     protected abstract (bool success, string info) InitializeGraphicsResources(Compositor compositor,
//         CompositionDrawingSurface compositionDrawingSurface, ICompositionGpuInterop gpuInterop);
//
//     protected abstract void FreeGraphicsResources();
//
//     protected abstract void RenderFrame(PixelSize pixelSize);
//     protected virtual bool SupportsDisco => false;
//
//     /// <summary>
//     /// When true, UpdateFrame re-arms RequestCompositionUpdate on every call,
//     /// so RenderFrame keeps firing continuously instead of only when Bounds
//     /// changes. Use for content that updates on its own (e.g. video playback).
//     /// </summary>
//     protected virtual bool RunContinuously => false;
//
//    
//     
//     
//     
//     public void Update(float yaw, float pitch, float roll, float disco)
//     {
//         Yaw = yaw;
//         Pitch = pitch;
//         Roll = roll;
//         Disco = disco;
//         QueueNextFrame();
//     }
//     
//     
//     
//     
//
//     public float Disco { get; private set; }
//
//     public float Roll { get; private set; }
//
//     public float Pitch { get; private set; }
//
//     public float Yaw { get; private set; }
// }













using System;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.LogicalTree;
using Avalonia.Rendering.Composition;
using Avalonia.VisualTree;

namespace GpuInterop;

public abstract class DrawingSurfaceDemoBase : Control
{
    private CompositionSurfaceVisual? _visual;
    private Compositor? _compositor;
    private readonly Action _update;
    private string _info = string.Empty;
    private bool _updateQueued;
    private bool _initialized;

    protected CompositionDrawingSurface? Surface { get; private set; }

    /// <summary>
    /// The compositor we obtained in Initialize(). Null until attached.
    /// </summary>
    protected Compositor? Compositor => _compositor;

    /// <summary>
    /// The surface visual that hosts <see cref="Surface"/>. Null if we
    /// fell back to software or never initialized.
    /// </summary>
    protected CompositionSurfaceVisual? SurfaceVisual => _visual;

    /// <summary>
    /// Set this to true from InitializeGraphicsResources when the derived
    /// class is going to drive <see cref="Surface"/> directly (e.g. CPU
    /// interop). When true, UpdateFrame keeps the surface visual sized to
    /// the control but does NOT call RenderFrame, and Render() is expected
    /// to draw nothing (or only letterbox bars).
    /// </summary>
    protected bool DrivesSurfaceDirectly { get; set; }

    public DrawingSurfaceDemoBase()
    {
        _update = UpdateFrame;
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        Initialize();
    }

    protected override void OnDetachedFromLogicalTree(LogicalTreeAttachmentEventArgs e)
    {
        if (_initialized)
            FreeGraphicsResources();
        _initialized = false;
        base.OnDetachedFromLogicalTree(e);
    }

    async void Initialize()
    {
        try
        {
            var selfVisual = ElementComposition.GetElementVisual(this)!;
            _compositor = selfVisual.Compositor;

            Surface = _compositor.CreateDrawingSurface();
            _visual = _compositor.CreateSurfaceVisual();
            _visual.Size = new(Bounds.Width, Bounds.Height);
            _visual.Surface = Surface;
            ElementComposition.SetElementChildVisual(this, _visual);

            var (res, info) = await DoInitialize(_compositor, Surface);
            _info = info;

            _initialized = res;
            QueueNextFrame();
        }
        catch (Exception e)
        {
            Console.WriteLine(e.ToString());
        }
    }

    void UpdateFrame()
    {
        _updateQueued = false;
        var root = this.GetVisualRoot();
        if (root == null)
            return;

        // _visual is null once we have fallen back to software rendering.
        if (_visual != null)
            _visual.Size = new(Bounds.Width, Bounds.Height);

        if (DrivesSurfaceDirectly)
        {
            // The compositor presents the surface. We still want to re-arm
            // if the derived class asked for continuous updates.
            if ((SupportsDisco && Disco > 0) || RunContinuously)
                QueueNextFrame();
            return;
        }

        var size = PixelSize.FromSize(Bounds.Size, root.RenderScaling);
        RenderFrame(size);
        if ((SupportsDisco && Disco > 0) || RunContinuously)
            QueueNextFrame();
    }

    void QueueNextFrame()
    {
        if (_initialized && !_updateQueued && _compositor != null)
        {
            _updateQueued = true;
            _compositor?.RequestCompositionUpdate(_update);
        }
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        if (change.Property == BoundsProperty)
            QueueNextFrame();
        base.OnPropertyChanged(change);
    }

    async Task<(bool success, string info)> DoInitialize(Compositor compositor,
        CompositionDrawingSurface compositionDrawingSurface)
    {
        try
        {
            var interop = await compositor.TryGetCompositionGpuInterop();
            if (interop == null)
                return FallbackToSoftware("Compositor doesn't support GPU interop for the current backend");

            var result = InitializeGraphicsResources(compositor, compositionDrawingSurface, interop);
            if (!result.success)
                return FallbackToSoftware(result.info);

            return result;
        }
        catch (Exception ex)
        {
            return FallbackToSoftware(ex.Message);
        }
    }

    private (bool success, string info) FallbackToSoftware(string reason)
    {
        // Remove the (empty) GPU surface so it doesn't cover what Render() draws.
        try { ElementComposition.SetElementChildVisual(this, null); } catch { }
        _visual = null;
        Surface = null;
        DrivesSurfaceDirectly = false;
        return InitializeSoftwareFallback(reason);
    }

    /// <summary>
    /// Called when the GPU path is unavailable. Return success = true to keep
    /// running in software mode (the derived control then draws in Render()).
    /// </summary>
    protected virtual (bool success, string info) InitializeSoftwareFallback(string reason)
        => (false, reason);

    protected abstract (bool success, string info) InitializeGraphicsResources(Compositor compositor,
        CompositionDrawingSurface compositionDrawingSurface, ICompositionGpuInterop gpuInterop);

    protected abstract void FreeGraphicsResources();

    protected abstract void RenderFrame(PixelSize pixelSize);
    protected virtual bool SupportsDisco => false;

    /// <summary>
    /// When true, UpdateFrame re-arms RequestCompositionUpdate on every call,
    /// so RenderFrame keeps firing continuously instead of only when Bounds
    /// changes. Use for content that updates on its own (e.g. video playback).
    /// </summary>
    protected virtual bool RunContinuously => false;

    public void Update(float yaw, float pitch, float roll, float disco)
    {
        Yaw = yaw;
        Pitch = pitch;
        Roll = roll;
        Disco = disco;
        QueueNextFrame();
    }

    public float Disco { get; private set; }
    public float Roll { get; private set; }
    public float Pitch { get; private set; }
    public float Yaw { get; private set; }
}