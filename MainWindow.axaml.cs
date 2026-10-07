using System;
using System.Diagnostics;
using System.Threading;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Rendering;
using Avalonia.Rendering.Composition;
using Avalonia.VisualTree;
using GpuInterop.test;
using SharpDX.Direct3D11;

namespace GpuInterop
{
    public partial  class MainWindow : Window
    {
        
        
        string fileToPlay = @"M:\movie\Kung.Fu.Panda.3.2016.720p.WEBRip.x264.AAC-ETRG.mp4";


    test.FFmpeg ffmpeg;  

  
        Thread threadPlay;          // Simulates FPS
        
        private volatile bool is_running = true;
        
        
        private readonly object _d3dLock = new object();
        public MainWindow()
        {
            InitializeComponent();
            
            this.AttachDevTools();
            // RendererDiagnostics.DebugOverlays = RendererDebugOverlays.Fps;
            RendererDiagnostics.DebugOverlays = RendererDebugOverlays.RenderTimeGraph;
            
            
            Loaded += OnLoaded;
        
            
            // Console.WriteLine(my_render);
            
            // AvaloniaInteropRenderer.Instance.Initialized += OnInitialized;
            
            
            // my_render.Initialized += OnInitialized;
            
            my_render.Initialized += OnInitialized;
            
            Closing += (_, _) => my_render.Dispose();  
            
            Closed += OnClosed;
            
        }

        private void OnClosed(object? sender, EventArgs e)
        {
            is_running = false;
            
            threadPlay?.Join(1000);
        }


        private void OnInitialized(object? sender, EventArgs e)
        {

            Console.WriteLine("gpuintrop inititalized!");
            
            // my_render.DisplayImage("dev_img1.jpg");
            
            // my_render.Play_video();
            Play_video();
            
        }

        private void OnLoaded(object? sender, RoutedEventArgs e)
        {
            // my_render.DisplayImage("dev_img1.jpg");
            
            
            // if (my_render.IsInitialized)
            //     my_render.DisplayImage("dev_img1.jpg");
            // else
            //     my_render.Initialized += (object? sender, EventArgs e) => my_render.DisplayImage("dev_img1.jpg");
            
        }

        //
        // private void Play_video()
        // {
        //     
        //     
        //     
        //     
        //      try
        // {
        //     ffmpeg = new test.FFmpeg();
        //     // directX = new DirectX();
        //     // directX.Initialize(handle.Handle);
        //
        //     if (!ffmpeg.InitHWAccel(my_render.my_Device))
        //     {
        //         Console.WriteLine("Failed to Initialize FFmpeg's HW Acceleration");
        //         return;
        //     }
        //
        //     if (!ffmpeg.Open(fileToPlay))
        //     {
        //         Console.WriteLine("FFmpeg failed to open input");
        //         return;
        //     }
        //
        //     // NOTE: No SizeChanged handler here on purpose.
        //     // Avalonia reports DIPs (not physical pixels) and may fire before the HWND
        //     // is actually resized. DirectX_2.PresentFrame() now syncs the swap chain
        //     // with the real client size of the HWND every frame.
        //
        //     threadPlay = new Thread(() =>
        //     {
        //         try
        //         {
        //             var swDecode  = new Stopwatch();
        //             var swPresent = new Stopwatch();
        //             var swTotal   = new Stopwatch();
        //
        //             double sumDecode  = 0;
        //             double sumPresent = 0;
        //             double sumTotal   = 0;
        //             int    samples    = 0;
        //             int    frameCount = 0;
        //             const int ReportEvery = 60;
        //
        //             double minTotal = double.MaxValue;
        //             double maxTotal = 0;
        //
        //             while (is_running)
        //             {
        //                 swTotal.Restart();
        //
        //                 Texture2D? textureHW;
        //                 long       decodeStamp;
        //
        //                 lock (my_render._d3dLock)
        //                 {
        //                     swDecode.Restart();
        //                     textureHW = ffmpeg.GetFrame();
        //                     swDecode.Stop();
        //
        //                     decodeStamp = my_render.NowTicks;
        //                 }
        //
        //                 if (textureHW == null)
        //                 {
        //                     Thread.Sleep(1);
        //                     continue;   // now outside the lock — good
        //                 }
        //
        //                 swPresent.Restart();
        //                 try
        //                 {
        //                     my_render.PresentFrame(textureHW, decodeStamp);
        //                 }
        //                 catch (Exception ex)
        //                 {
        //                     Console.WriteLine($"[Play] PresentFrame threw: {ex.GetType().Name}: {ex.Message}");
        //                 }
        //                 swPresent.Stop();
        //
        //                 swTotal.Stop();
        //
        //                 double decodeMs  = swDecode.Elapsed.TotalMilliseconds;
        //                 double presentMs = swPresent.Elapsed.TotalMilliseconds;
        //                 double totalMs   = swTotal.Elapsed.TotalMilliseconds;
        //
        //                 sumDecode  += decodeMs;
        //                 sumPresent += presentMs;
        //                 sumTotal   += totalMs;
        //                 samples++;
        //
        //                 if (totalMs < minTotal) minTotal = totalMs;
        //                 if (totalMs > maxTotal) maxTotal = totalMs;
        //
        //                 if (++frameCount >= ReportEvery)
        //                 {
        //                     frameCount = 0;
        //
        //                     double avgDecode  = sumDecode  / samples;
        //                     double avgPresent = sumPresent / samples;
        //                     double avgTotal   = sumTotal   / samples;
        //                     double fps        = avgTotal > 0 ? 1000.0 / avgTotal : 0;
        //
        //                     Console.WriteLine(
        //                         $"[latency] decode={avgDecode:F2}ms  " +
        //                         $"present={avgPresent:F2}ms  " +
        //                         $"total={avgTotal:F2}ms  " +
        //                         $"min={minTotal:F2}ms  " +
        //                         $"max={maxTotal:F2}ms  " +
        //                         $"fps={fps:F1}");
        //
        //                     sumDecode = 0;
        //                     sumPresent = 0;
        //                     sumTotal = 0;
        //                     samples = 0;
        //                     minTotal = double.MaxValue;
        //                     maxTotal = 0;
        //                 }
        //
        //                 // Pace to ~60 fps
        //                 double remaining = 16.67 - swTotal.Elapsed.TotalMilliseconds;
        //                 if (remaining > 1)
        //                     Thread.Sleep((int)remaining);
        //             }
        //         }
        //         catch (Exception ex)
        //         {
        //             Console.WriteLine($"Thread error: {ex.Message}");
        //             Console.WriteLine($"Stack trace: {ex.StackTrace}");
        //         }
        //     });
        //
        //     threadPlay.SetApartmentState(ApartmentState.STA);
        //     threadPlay.Start();
        // }
        // catch (Exception ex)
        // {
        //     Console.WriteLine($"Initialization error: {ex.Message}");
        //     Console.WriteLine($"Stack trace: {ex.StackTrace}");
        // }
        //     
        //     
        //     
        //     
        // }
        //
        //
        
        
        
        
        
        
        
    public void Play_video()
    {
        try
        {
            ffmpeg = new test.FFmpeg();

            // if (!ffmpeg.InitHWAccel(my_render.my_Device!))
            // {
            //     Console.WriteLine("Failed to Initialize FFmpeg's HW Acceleration");
            //     return;
            // }
            
            
            if (!ffmpeg.InitHWAccel(my_render.my_Device!))
            {
                // Same as my_AV_win.cs: log it, then continue with software decode.
                Console.WriteLine("[FFMPEG] HW accel unavailable — using software fallback");
            }

            if (!ffmpeg.Open(fileToPlay))
            {
                Console.WriteLine("FFmpeg failed to open input");
                return;
            }

            threadPlay = new Thread(() =>
            {
                try
                {
                    var swDecode  = new Stopwatch();
                    var swPresent = new Stopwatch();
                    var swTotal   = new Stopwatch();

                    double sumDecode  = 0;
                    double sumPresent = 0;
                    double sumTotal   = 0;
                    int    samples    = 0;
                    int    frameCount = 0;
                    const int ReportEvery = 60;

                    double minTotal = double.MaxValue;
                    double maxTotal = 0;

                    while (is_running)
                    {
                        swTotal.Restart();

                        Texture2D? textureHW;
                        long       decodeStamp;
                        lock (my_render._d3dLock)
                        {
                            swDecode.Restart();
                            textureHW = ffmpeg.GetFrame();
                            swDecode.Stop();

                         
                            
                            
                            
                            
                            
                            // Stamp the moment decode finished, still inside
                            // the lock so it's as close as possible to the
                            // actual decode-complete instant.
                            // decodeStamp = _globalClock.ElapsedTicks;
                            decodeStamp = my_render.NowTicks;
                            
                        }

                        if (textureHW == null)
                        {
                            Thread.Sleep(1);
                            continue;
                        }

                        swPresent.Restart();
                        my_render.PresentFrame(textureHW, decodeStamp);
                        swPresent.Stop();
                        
                        
                        
                        
                           
                        // Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                        // {
                        //     if (this.GetVisualRoot() is Avalonia.Rendering.IRenderRoot root)
                        //     {
                        //         // This is the snippet you quoted. It's a UI-thread-only call.
                        //         root.Renderer.Paint(new Rect(root.ClientSize));
                        //     }
                        // }, Avalonia.Threading.DispatcherPriority.Render);

                        
                        

                        swTotal.Stop();

                        double decodeMs  = swDecode.Elapsed.TotalMilliseconds;
                        double presentMs = swPresent.Elapsed.TotalMilliseconds;
                        double totalMs   = swTotal.Elapsed.TotalMilliseconds;

                        sumDecode  += decodeMs;
                        sumPresent += presentMs;
                        sumTotal   += totalMs;
                        samples++;

                        if (totalMs < minTotal) minTotal = totalMs;
                        if (totalMs > maxTotal) maxTotal = totalMs;

                        if (++frameCount >= ReportEvery)
                        {
                            frameCount = 0;

                           

                            sumDecode  = 0;
                            sumPresent = 0;
                            sumTotal   = 0;
                            samples    = 0;
                            minTotal   = double.MaxValue;
                            maxTotal   = 0;
                        }

                        // double remaining = 16.67 - swTotal.Elapsed.TotalMilliseconds;
                        double remaining = 33 - swTotal.Elapsed.TotalMilliseconds;
                        if (remaining > 1)
                            Thread.Sleep((int)remaining);
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Thread error: {ex.Message}");
                    Console.WriteLine($"Stack trace: {ex.StackTrace}");
                }
            });

            threadPlay.SetApartmentState(ApartmentState.STA);
            threadPlay.Start();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Initialization error: {ex.Message}");
            Console.WriteLine($"Stack trace: {ex.StackTrace}");
        }
    }
        
        
        
        
        
        
        
        
        
        
        
        
        
        

        
    }
}
