//
//
// using System;
// using System.IO;
// using System.Runtime.InteropServices;
// using System.Runtime.ExceptionServices;
// using System.Security;
//
// using FFmpeg.AutoGen;
// using SharpDX;
// using static FFmpeg.AutoGen.ffmpeg;
// using static FFmpeg.AutoGen.AVMediaType;
// using static FFmpeg.AutoGen.AVPixelFormat;
//
// using SharpDX.Direct3D11;
// using SharpDX.DXGI;
// using Device = SharpDX.Direct3D11.Device;
//
//
// namespace GpuInterop.test;
//
// public unsafe class FFmpeg
//     {
//         #region Declaration
//
//         // FFmpeg Basic Setup
//         AVFormatContext*        fmtCtx;
//         AVCodecContext*         vCodecCtx;
//         AVStream*               vStream;
//
//         // HW Acceleration
//         const int               AV_CODEC_HW_CONFIG_METHOD_HW_DEVICE_CTX = 0x01;
//         const AVHWDeviceType    HW_DEVICE       = AVHWDeviceType.AV_HWDEVICE_TYPE_D3D11VA;
//         const AVPixelFormat     HW_PIX_FMT      = AVPixelFormat.AV_PIX_FMT_D3D11;
//
//         Device                  device;             // Direct3D 11 Device (We use it for HW Rendering and FFmpeg for HW Decoding)
//         AVBufferRef*            hw_device_ctx;      // FFmpeg's HW Device Context (To setup our video codec while opening it)
//         Texture2DDescription    textDescHW;         // HW Texture2D Description
//         Texture2D               textureHW;          // HW Texture2D
//         Texture2D               textureFFmpeg;      // HW Texture2D Array (FFmpeg's pool)
//
//         public FFmpeg()
//         {
//             RegisterFFmpegBinaries();
//             var current = Environment.CurrentDirectory;
//
//             // Console.WriteLine(current);
//             
//             // RootPath = current;
//             av_log_set_level(ffmpeg.AV_LOG_ERROR);
//         }
//
//         #endregion
//
//         // Creates FFmpeg's HW Device Context based on our rendering Direct3D 11 Device
//         public bool InitHWAccel(Device device)
//         {
//             int ret;
//
//             
//             if (hw_device_ctx != null) return false;
//
//             hw_device_ctx  = av_hwdevice_ctx_alloc(HW_DEVICE);
//
//             AVHWDeviceContext* device_ctx = (AVHWDeviceContext*) hw_device_ctx->data;
//             AVD3D11VADeviceContext* d3d11va_device_ctx = (AVD3D11VADeviceContext*) device_ctx->hwctx;
//             d3d11va_device_ctx->device = (ID3D11Device*) device.NativePointer;
//
//             ret = av_hwdevice_ctx_init(hw_device_ctx);
//
//             if (ret != 0)
//             {
//                 Log($"[ERROR-1]{ErrorCodeToMsg(ret)} ({ret})");
//                 
//                 fixed(AVBufferRef** ptr = &hw_device_ctx) av_buffer_unref(ptr);
//                 hw_device_ctx = null;
//                 return false;
//             }
//
//             this.device = device;
//             return true;
//         }
//
//         // Ensures that the current Video Codec is supported from our GPU (for HW Decoding)
//         private bool CheckHWAccelCodecSupport(AVCodec* codec)
//         {
//             for (int i = 0; ; i++)
//             {
//                 AVCodecHWConfig* config = avcodec_get_hw_config(codec, i);
//                 if (config == null) break;
//                 if ((config->methods & AV_CODEC_HW_CONFIG_METHOD_HW_DEVICE_CTX) == 0 || config->pix_fmt == AVPixelFormat.AV_PIX_FMT_NONE) continue;
//
//                 if (config->device_type == HW_DEVICE && config->pix_fmt == HW_PIX_FMT) return true;
//             }
//
//             return false;
//         }
//
//         /* 1. Format Context Open & Setup
//          * 2. Streams Setup
//          * 3. Codecs Setup
//          * 4. HWDevice Setup (FFmpeg will do the GPU Video Decoding)
//          */
//         public bool Open(string url)
//         {
//             int ret = -1;
//
//             Initialize();
//
//             // Format Context Open & Setup
//             fmtCtx = avformat_alloc_context();
//             if (fmtCtx == null) return false;
//
//             AVFormatContext* fmtCtxPtr = fmtCtx;
//             ret = avformat_open_input(&fmtCtxPtr, url, null, null);
//             if (ret < 0) { OpenFailed(ret, false); return false; }
//
//             // Video Stream Setup
//             ret = avformat_find_stream_info(fmtCtx, null);
//             if (ret < 0) { OpenFailed(ret); return false; }
//
//             ret = av_find_best_stream(fmtCtx, AVMEDIA_TYPE_VIDEO,   -1, -1, null, 0);
//             if (ret < 0) { OpenFailed(ret); return false; }
//             vStream = fmtCtx->streams[ret];
//
//             // We use only Video for this Project (Discards All Streams except Video)
//             for (int i=0; i<fmtCtx->nb_streams; i++)
//                 if (i != vStream->index) fmtCtx->streams[i]->discard = AVDiscard.AVDISCARD_ALL;
//
//             SetupVideo();
//
//             return true;
//         }
//         private bool SetupVideo()
//         {
//             int ret;
//
//             AVCodec* codec = avcodec_find_decoder(vStream->codecpar->codec_id);
//             if (codec == null)
//                 { Log($"[CodecOpen] [ERROR-1] No suitable codec found"); return false; }
//
//             if (!CheckHWAccelCodecSupport(codec))
//                 { Log($"[CodecOpen] [ERROR-1] HW Acceleration not supported for this codec"); return false; }
//
//             vCodecCtx = avcodec_alloc_context3(null);
//             if (vCodecCtx == null)
//                 { Log($"[CodecOpen] [ERROR-2] Failed to allocate context3"); return false; }
//
//             ret = avcodec_parameters_to_context(vCodecCtx, vStream->codecpar);
//             if (ret < 0)
//                 { Log($"[CodecOpen] [ERROR-3] {ErrorCodeToMsg(ret)} ({ret})"); return false; }
//
//             vCodecCtx->pkt_timebase  = vStream->time_base;
//             vCodecCtx->codec_id      = codec->id;
//
//             vCodecCtx->thread_count = Math.Min(Environment.ProcessorCount, vCodecCtx->codec_id == AVCodecID.AV_CODEC_ID_HEVC ? 32 : 16);
//             vCodecCtx->thread_type  = 0;
//
//             // vCodecCtx->thread_safe_callbacks = 1;
//
//             vCodecCtx->hw_device_ctx = av_buffer_ref(hw_device_ctx);
//
//             textDescHW = new Texture2DDescription()
//             {
// 	            Usage               = ResourceUsage.Default,
//
//                 Width               = vCodecCtx->width,
//                 Height              = vCodecCtx->height,
//
//                 BindFlags           = BindFlags.Decoder,
// 	            CpuAccessFlags      = CpuAccessFlags.None,
// 	            OptionFlags         = ResourceOptionFlags.None,
//
// 	            SampleDescription   = new SampleDescription(1, 0),
// 	            ArraySize           = 1,
// 	            MipLevels           = 1
//             };
//
//             Console.WriteLine($"{vCodecCtx->width}, {vCodecCtx->height}");
//             ret = avcodec_open2(vCodecCtx, codec, null);
//             if (ret == 0) return true;
//
//             return false;
//         }
//
//         // Demuxing and HW Decoding
//         private AVFrame* DecodeFrame(out int ret)
//         {
//             ret = 0;
//
//             AVPacket* avpacket  = av_packet_alloc();
//             AVFrame*  avframe   = av_frame_alloc();
//             av_init_packet(avpacket);
//
//             ret = av_read_frame(fmtCtx, avpacket);
//             if (ret < 0)
//             {
//                 if (ret == AVERROR_EOF || avio_feof(fmtCtx->pb) != 0)
//                 {
//                     // EOF Handling & Draining
//                     av_packet_unref(avpacket);
//                     return null;
//                 }
//
//                 av_packet_unref(avpacket); ret = -1; return null;
//             }
//
//             if (avpacket->stream_index == vStream->index)
//             {
//                 ret = avcodec_send_packet(vCodecCtx, avpacket);
//                 if (ret != 0)
//                     { av_packet_unref(avpacket); ret = 0; return null; }
//
//                 ret = avcodec_receive_frame(vCodecCtx, avframe);
//                 if (ret == AVERROR_EOF || ret == AVERROR(EAGAIN)) { av_packet_unref(avpacket); ret = 0; return null; }
//                 if (ret != 0) { if (avframe != null) av_frame_free(&avframe);
//                     av_packet_unref(avpacket); ret = 0; return null; }
//             }
//
//             av_packet_unref(avpacket);
//             return avframe;
//         }
//
//         /* FFmpeg HW Decode and return Texture2D for rendering
//          * 
//          * FFmpeg source code -> https://github.com/FFmpeg/FFmpeg/blob/master/libavutil/hwcontext_d3d11va.c
//          *     frame->data[0] = (uint8_t *)desc->texture;
//          *     frame->data[1] = (uint8_t *)desc->index;
//          *     
//          * 1. Casting ID3D11Texture2D (d3d11.h) to Texture2D (SharpDX.Direct3D11) from avframe->data.ToArray()[0]
//          * 2. Creating new Texture2D based on FFmpeg TextureDescription (eg. NV12 width/height etc)
//          * 3. Copy Subresource from FFmpeg's Array Texture to our Texture and return it
//          */
//         // public Texture2D GetFrame()
//         // {
//         //     AVFrame* avframe;
//         //     int ret = 0;
//         //     do
//         //     {
//         //         // Demux & Decode Video Frame (FFmpeg HW decodes with the supplied threads)
//         //         avframe = DecodeFrame(out ret);
//         //
//         //         if (avframe != null)
//         //         {
//         //             if (avframe->best_effort_timestamp != AV_NOPTS_VALUE)
//         //             {
//         //                 textureFFmpeg       = new Texture2D((IntPtr) avframe->data.ToArray()[0]);
//         //                 textDescHW.Format   = textureFFmpeg.Description.Format;
//         //                 textureHW           = new Texture2D(device, textDescHW);
//         //                 
//         //                 //lock (device)
//         //                 device.ImmediateContext.CopySubresourceRegion(textureFFmpeg, (int) avframe->data.ToArray()[1], new ResourceRegion(0,0,0,textureHW.Description.Width,textureHW.Description.Height,1), textureHW,0);
//         //
//         //                 av_frame_free(&avframe);
//         //
//         //                 return textureHW;
//         //             }
//         //         }
//         //
//         //     } while (ret != -1);
//         //
//         //     return null;
//         // }
//         
//         
//         
//         
//         public Texture2D GetFrame()
//         {
//             AVFrame* avframe;
//             int ret = 0;
//             do
//             {
//                 avframe = DecodeFrame(out ret);
//         
//                 if (avframe != null)
//                 {
//                     if (avframe->best_effort_timestamp != AV_NOPTS_VALUE)
//                     {
//                         // Wrap FFmpeg's decoder texture, use it, and release the
//                         // wrapper as soon as the copy is done. FFmpeg returns the
//                         // underlying surface to its pool on the next receive_frame,
//                         // so we must not hold a stale pointer.
//                         using (var textureFFmpeg =
//                                new Texture2D((IntPtr)avframe->data.ToArray()[0]))
//                         {
//                             textDescHW.Format = textureFFmpeg.Description.Format;
//         
//                             // Always create a fresh destination; never reuse one the
//                             // renderer might still be holding.
//                             Utilities.Dispose(ref textureHW);
//                             textureHW = new Texture2D(device, textDescHW);
//         
//                             device.ImmediateContext.CopySubresourceRegion(
//                                 textureFFmpeg,
//                                 (int)avframe->data.ToArray()[1],
//                                 new ResourceRegion(
//                                     0, 0, 0,
//                                     textureHW.Description.Width,
//                                     textureHW.Description.Height,
//                                     1),
//                                 textureHW,
//                                 0);
//                         }
//         
//                         av_frame_free(&avframe);
//                         return textureHW;
//                     }
//                     else
//                     {
//                         av_frame_free(&avframe);
//                     }
//                 }
//         
//             } while (ret != -1);
//         
//             return null;
//         }
//         
//         
//         
//
//         #region Misc
//         [HandleProcessCorruptedStateExceptions]
//         [SecurityCritical]
//         private void Initialize()
//         {
//             AVCodecContext* codecCtxPtr;
//             try
//             {
//                 if (vStream != null)
//                 {
//                     codecCtxPtr = vCodecCtx;
//                     avcodec_free_context(&codecCtxPtr);
//                 }
//             }
//             catch (Exception e)
//             {
//                 Log("Error[" + (-1).ToString("D4") + "], Msg: " + e.Message + "\n" + e.StackTrace);
//             }
//
//             try
//             {
//                 if (fmtCtx != null)
//                 {
//                     AVFormatContext* fmtCtxPtr = fmtCtx;
//                     avformat_close_input(&fmtCtxPtr);
//                 }
//
//                 fmtCtx = null;
//                 vStream = null;
//             }
//             catch (Exception e)
//             {
//                 Log("Error[" + (-1).ToString("D4") + "], Msg: " + e.Message + "\n" + e.StackTrace);
//             }
//         }
//         private void OpenFailed(int ret, bool opened = true)
//         {
//             Log(ErrorCodeToMsg(ret));
//             AVFormatContext* fmtCtxPtr = fmtCtx;
//             if (fmtCtx != null && opened) avformat_close_input(&fmtCtxPtr);
//             av_freep(fmtCtx);
//         }
//         private static string ErrorCodeToMsg(int error)
//         {
//             byte* buffer = stackalloc byte[1024];
//             ffmpeg.av_strerror(error, buffer, 1024);
//             return Marshal.PtrToStringAnsi((IntPtr)buffer);
//         }
//
//         public static bool alreadyRegister = false;
//         private void RegisterFFmpegBinaries()
//         {
//             if (alreadyRegister) 
//                 return;
//             alreadyRegister = true;
//
//             // var current = Environment.CurrentDirectory;
//             var current = @"c:\";
//             var probe = Path.Combine("deps", Environment.Is64BitProcess ? "x64" : "x32");
//             
//             string ffmpegPath = Path.Combine(
//                 Directory.GetCurrentDirectory(),
//                 "deps",
//                 Environment.Is64BitProcess ? "x64" : "x32");
//             
//
//             if (!Directory.Exists(ffmpegPath))
//             {
//                 throw new DirectoryNotFoundException(
//                     $"FFmpeg directory not found: {ffmpegPath}");
//             }
//
//             Console.WriteLine(ffmpegPath);
//             // ffmpeg.RootPath = ffmpegPath;
//             ffmpeg.RootPath = ffmpegPath;
//
//             while (current != null)
//             {
//                 var ffmpegBinaryPath = Path.Combine(current, probe);
//                 // Console.WriteLine(RootPath);
//                 if (Directory.Exists(ffmpegBinaryPath))
//                 {
//                     Console.WriteLine("path exists");
//                     RootPath = ffmpegBinaryPath;
//
//                     Console.WriteLine("111111");
//                     uint ver = ffmpeg.avformat_version();
//                     Log($"[Version: {ver >> 16}.{ver >> 8 & 255}.{ver & 255}] [Location: {ffmpegBinaryPath}]");
//
//                     return;
//                 }
//                 current = Directory.GetParent(current)?.FullName;
//             }
//         }
//         private void Log(string msg) { Console.WriteLine($"[FFMPEG] {msg}"); }
//         #endregion
//     
// }
//
//







using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.ExceptionServices;
using System.Security;

using FFmpeg.AutoGen;
using SharpDX;
using static FFmpeg.AutoGen.ffmpeg;
using static FFmpeg.AutoGen.AVMediaType;
using static FFmpeg.AutoGen.AVPixelFormat;

using SharpDX.Direct3D11;
using SharpDX.DXGI;
using Device = SharpDX.Direct3D11.Device;


namespace GpuInterop.test;

public unsafe class FFmpeg
{
    #region Declaration

    // FFmpeg Basic Setup
    AVFormatContext*        fmtCtx;
    AVCodecContext*         vCodecCtx;
    AVStream*               vStream;

    // HW Acceleration
    const int               AV_CODEC_HW_CONFIG_METHOD_HW_DEVICE_CTX = 0x01;
    const AVHWDeviceType    HW_DEVICE       = AVHWDeviceType.AV_HWDEVICE_TYPE_D3D11VA;
    const AVPixelFormat     HW_PIX_FMT      = AVPixelFormat.AV_PIX_FMT_D3D11;

    Device                  device;
    AVBufferRef*            hw_device_ctx;
    Texture2DDescription    textDescHW;
    Texture2D               textureHW;
    Texture2D               textureFFmpeg;

    // ---- Fallback state (same as my_AV_win.cs) ----
    bool hwInitialized       = false;
    bool useSoftwareFallback = false;

    public FFmpeg()
    {
        RegisterFFmpegBinaries();
        var current = Environment.CurrentDirectory;

        av_log_set_level(ffmpeg.AV_LOG_ERROR);
    }

    #endregion

    // Creates FFmpeg's HW Device Context based on our rendering Direct3D 11 Device.
    // Mirrors my_AV_win.cs: on failure it just marks software fallback and returns,
    // it does NOT signal a fatal error to the caller.
   
    public bool InitHWAccel(Device device)
    {
        if (hw_device_ctx != null) return false;
    
        // Same as my_AV_win.cs: store the device first, unconditionally.
        // The software fallback path still needs it to create BGRA textures.
        this.device = device;
    
        try
        {
            hw_device_ctx = av_hwdevice_ctx_alloc(HW_DEVICE);
    
            if (hw_device_ctx == null)
            {
                Log("Failed to allocate D3D11VA device, using software fallback");
                useSoftwareFallback = true;
                return false;
            }
    
            AVHWDeviceContext* device_ctx = (AVHWDeviceContext*) hw_device_ctx->data;
            AVD3D11VADeviceContext* d3d11va_device_ctx =
                (AVD3D11VADeviceContext*) device_ctx->hwctx;
    
            d3d11va_device_ctx->device = (ID3D11Device*) device.NativePointer;
    
            int ret = av_hwdevice_ctx_init(hw_device_ctx);
    
            if (ret != 0)
            {
                Log($"[ERROR-1]{ErrorCodeToMsg(ret)} ({ret}) - using software fallback");
    
                fixed (AVBufferRef** ptr = &hw_device_ctx)
                    av_buffer_unref(ptr);
    
                hw_device_ctx = null;
                useSoftwareFallback = true;
                return false;
            }
    
            hwInitialized = true;
            useSoftwareFallback = false;
            return true;
        }
        catch (Exception ex)
        {
            Log($"HW acceleration setup failed: {ex.Message}, using software fallback");
    
            if (hw_device_ctx != null)
            {
                fixed (AVBufferRef** ptr = &hw_device_ctx)
                    av_buffer_unref(ptr);
                hw_device_ctx = null;
            }
    
            useSoftwareFallback = true;
            return false;
        }
    }

    // Kept for reference; no longer used to hard-fail SetupVideo.
    private bool CheckHWAccelCodecSupport(AVCodec* codec)
    {
        for (int i = 0; ; i++)
        {
            AVCodecHWConfig* config = avcodec_get_hw_config(codec, i);
            if (config == null) break;
            if ((config->methods & AV_CODEC_HW_CONFIG_METHOD_HW_DEVICE_CTX) == 0 ||
                config->pix_fmt == AVPixelFormat.AV_PIX_FMT_NONE) continue;

            if (config->device_type == HW_DEVICE && config->pix_fmt == HW_PIX_FMT)
                return true;
        }

        return false;
    }

    public bool Open(string url)
    {
        int ret = -1;

        Initialize();

        fmtCtx = avformat_alloc_context();
        if (fmtCtx == null) return false;

        AVFormatContext* fmtCtxPtr = fmtCtx;
        ret = avformat_open_input(&fmtCtxPtr, url, null, null);
        if (ret < 0) { OpenFailed(ret, false); return false; }

        ret = avformat_find_stream_info(fmtCtx, null);
        if (ret < 0) { OpenFailed(ret); return false; }

        ret = av_find_best_stream(fmtCtx, AVMEDIA_TYPE_VIDEO, -1, -1, null, 0);
        if (ret < 0) { OpenFailed(ret); return false; }
        vStream = fmtCtx->streams[ret];

        for (int i = 0; i < fmtCtx->nb_streams; i++)
            if (i != vStream->index) fmtCtx->streams[i]->discard = AVDiscard.AVDISCARD_ALL;

        SetupVideo();

        return true;
    }

    private bool SetupVideo()
    {
        int ret;

        AVCodec* codec = avcodec_find_decoder(vStream->codecpar->codec_id);
        if (codec == null)
        {
            Log($"[CodecOpen] [ERROR-1] No suitable codec found");
            return false;
        }

        // my_AV_win.cs does NOT hard-fail when HW isn't supported for this codec.
        // It just proceeds without attaching a hw_device_ctx to the codec.
        if (!CheckHWAccelCodecSupport(codec))
        {
            Log($"[CodecOpen] HW Acceleration not supported for this codec, using software fallback");
            useSoftwareFallback = true;
            hwInitialized = false;

            if (hw_device_ctx != null)
            {
                fixed (AVBufferRef** ptr = &hw_device_ctx)
                    av_buffer_unref(ptr);
                hw_device_ctx = null;
            }
        }

        vCodecCtx = avcodec_alloc_context3(null);
        if (vCodecCtx == null)
        {
            Log($"[CodecOpen] [ERROR-2] Failed to allocate context3");
            return false;
        }

        ret = avcodec_parameters_to_context(vCodecCtx, vStream->codecpar);
        if (ret < 0)
        {
            Log($"[CodecOpen] [ERROR-3] {ErrorCodeToMsg(ret)} ({ret})");
            return false;
        }

        vCodecCtx->pkt_timebase = vStream->time_base;
        vCodecCtx->codec_id     = codec->id;

        vCodecCtx->thread_count = Math.Min(
            Environment.ProcessorCount,
            vCodecCtx->codec_id == AVCodecID.AV_CODEC_ID_HEVC ? 32 : 16);

        vCodecCtx->thread_type = 0;

        // Only attach HW device context if we actually have a working one.
        // This is exactly what my_AV_win.cs does.
        if (hwInitialized && hw_device_ctx != null)
        {
            vCodecCtx->hw_device_ctx = av_buffer_ref(hw_device_ctx);
        }

        textDescHW = new Texture2DDescription()
        {
            Usage             = ResourceUsage.Default,
            Width             = vCodecCtx->width,
            Height            = vCodecCtx->height,
            BindFlags         = BindFlags.Decoder,
            CpuAccessFlags    = CpuAccessFlags.None,
            OptionFlags       = ResourceOptionFlags.None,
            SampleDescription = new SampleDescription(1, 0),
            ArraySize         = 1,
            MipLevels         = 1
        };

        Console.WriteLine($"{vCodecCtx->width}, {vCodecCtx->height}");

        ret = avcodec_open2(vCodecCtx, codec, null);
        if (ret == 0)
        {
            Log($"Decoder initialized. HW: {hwInitialized}, SW Fallback: {useSoftwareFallback}");
            return true;
        }

        return false;
    }

    private AVFrame* DecodeFrame(out int ret)
    {
        ret = 0;

        AVPacket* avpacket = av_packet_alloc();
        AVFrame*  avframe  = av_frame_alloc();
        av_init_packet(avpacket);

        ret = av_read_frame(fmtCtx, avpacket);
        if (ret < 0)
        {
            if (ret == AVERROR_EOF || avio_feof(fmtCtx->pb) != 0)
            {
                av_packet_unref(avpacket);
                return null;
            }

            av_packet_unref(avpacket);
            ret = -1;
            return null;
        }

        if (avpacket->stream_index == vStream->index)
        {
            ret = avcodec_send_packet(vCodecCtx, avpacket);
            if (ret != 0)
            {
                av_packet_unref(avpacket);
                ret = 0;
                return null;
            }

            ret = avcodec_receive_frame(vCodecCtx, avframe);
            if (ret == AVERROR_EOF || ret == AVERROR(EAGAIN))
            {
                av_packet_unref(avpacket);
                ret = 0;
                return null;
            }
            if (ret != 0)
            {
                if (avframe != null) av_frame_free(&avframe);
                av_packet_unref(avpacket);
                ret = 0;
                return null;
            }
        }

        av_packet_unref(avpacket);
        return avframe;
    }

    /* Same return contract as my_AV_win.cs:
     *   - HW path -> copy of the decoded D3D11 frame into a fresh Texture2D
     *   - SW path -> frame converted to BGRA and uploaded to a fresh Texture2D
     * Caller owns the returned texture.
     */
    public Texture2D GetFrame()
    {
        AVFrame* avframe;
        int ret = 0;

        do
        {
            avframe = DecodeFrame(out ret);

            if (avframe != null)
            {
                if (avframe->best_effort_timestamp != AV_NOPTS_VALUE)
                {
                    Texture2D result = null;

                    bool isD3D11Frame =
                        avframe->format == (int)AVPixelFormat.AV_PIX_FMT_D3D11;

                    if (hwInitialized && isD3D11Frame && avframe->data[0] != null)
                    {
                        result = ConvertD3D11FrameToTexture(avframe);
                    }
                    else
                    {
                        result = ConvertFrameToTexture(avframe);
                    }

                    av_frame_free(&avframe);
                    return result;
                }

                av_frame_free(&avframe);
            }

        } while (ret != -1);

        return null;
    }

    // =============================================================
    // D3D11 FRAME -> SHARPDX TEXTURE (mirrors my_AV_win.cs)
    // =============================================================
    private Texture2D ConvertD3D11FrameToTexture(AVFrame* frame)
    {
        if (frame == null)
            return null;

        Texture2D result = null;

        try
        {
            IntPtr ptr     = (IntPtr)frame->data[0];
            int arrayIndex = (int)frame->data[1];

            textureFFmpeg = new Texture2D(ptr);
            if (textureFFmpeg == null)
                throw new Exception("Failed to wrap texture");

            int videoWidth  = frame->width;
            int videoHeight = frame->height;

            if (videoWidth <= 0 || videoHeight <= 0)
                return null;

            if (textureHW == null ||
                textureHW.Description.Width  != videoWidth  ||
                textureHW.Description.Height != videoHeight ||
                textureHW.Description.Format != textureFFmpeg.Description.Format)
            {
                Console.WriteLine($"[FFmpeg] Recreate hwTexture: {videoWidth}x{videoHeight}");

                textureHW?.Dispose();

                textDescHW.Width     = videoWidth;
                textDescHW.Height    = videoHeight;
                textDescHW.Format    = textureFFmpeg.Description.Format;
                textDescHW.BindFlags = BindFlags.ShaderResource | BindFlags.RenderTarget;

                textureHW = new Texture2D(device, textDescHW);
            }

            var ctx = device.ImmediateContext;

            ctx.CopySubresourceRegion(
                textureFFmpeg,
                arrayIndex,
                new ResourceRegion(0, 0, 0, videoWidth, videoHeight, 1),
                textureHW,
                0);

            result = new Texture2D(device, textureHW.Description);
            ctx.CopyResource(textureHW, result);
            ctx.Flush();

            return result;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"GPU path failed: {ex.Message}");

            result?.Dispose();
            result = null;

            try
            {
                return ConvertFrameToTexture(frame);
            }
            catch (Exception fallbackEx)
            {
                Console.WriteLine($"CPU fallback after GPU failure failed: {fallbackEx.Message}");
                return null;
            }
        }
        finally
        {
            // Do NOT dispose — FFmpeg owns this texture.
            textureFFmpeg = null;
        }
    }

    // =============================================================
    // CPU FALLBACK (mirrors my_AV_win.cs)
    // =============================================================
    private Texture2D ConvertFrameToTexture(AVFrame* frame)
    {
        if (frame == null || frame->width <= 0 || frame->height <= 0)
            return null;

        AVFrame* swFrame = null;
        AVFrame* frameToConvert = frame;

        if (frame->format == (int)AVPixelFormat.AV_PIX_FMT_D3D11)
        {
            swFrame = av_frame_alloc();
            if (swFrame == null)
                return null;

            int ret = av_hwframe_transfer_data(swFrame, frame, 0);
            if (ret < 0)
            {
                av_frame_free(&swFrame);
                return null;
            }

            frameToConvert = swFrame;
        }

        byte_ptrArray4 dst_data    = new byte_ptrArray4();
        int_array4    dst_linesize = new int_array4();

        SwsContext* sws_ctx = null;

        try
        {
            sws_ctx = sws_getContext(
                frameToConvert->width,
                frameToConvert->height,
                (AVPixelFormat)frameToConvert->format,

                frameToConvert->width,
                frameToConvert->height,
                AVPixelFormat.AV_PIX_FMT_BGRA,

                SWS_POINT,

                null, null, null);

            if (sws_ctx == null)
                return null;

            int dst_bufsize = av_image_alloc(
                ref dst_data,
                ref dst_linesize,
                frameToConvert->width,
                frameToConvert->height,
                AVPixelFormat.AV_PIX_FMT_BGRA,
                1);

            if (dst_bufsize < 0)
                return null;

            int result = sws_scale(
                sws_ctx,
                frameToConvert->data,
                frameToConvert->linesize,
                0,
                frameToConvert->height,
                dst_data,
                dst_linesize);

            if (result <= 0)
                return null;

            var textureDesc = new Texture2DDescription
            {
                Width  = frameToConvert->width,
                Height = frameToConvert->height,

                MipLevels   = 1,
                ArraySize   = 1,

                Format      = Format.B8G8R8A8_UNorm,
                SampleDescription = new SampleDescription(1, 0),

                Usage       = ResourceUsage.Default,
                BindFlags   = BindFlags.ShaderResource | BindFlags.RenderTarget,
                CpuAccessFlags = CpuAccessFlags.None,
                OptionFlags    = ResourceOptionFlags.None
            };

            var dataBox = new DataRectangle(
                (IntPtr)dst_data[0],
                dst_linesize[0]);

            return new Texture2D(device, textureDesc, new[] { dataBox });
        }
        finally
        {
            if (dst_data[0] != null)
            {
                byte* ptr = dst_data[0];
                av_freep(&ptr);
            }

            if (sws_ctx != null)
                sws_freeContext(sws_ctx);

            if (swFrame != null)
                av_frame_free(&swFrame);
        }
    }

    #region Misc
    [HandleProcessCorruptedStateExceptions]
    [SecurityCritical]
    private void Initialize()
    {
        AVCodecContext* codecCtxPtr;
        try
        {
            if (vStream != null)
            {
                codecCtxPtr = vCodecCtx;
                avcodec_free_context(&codecCtxPtr);
            }
        }
        catch (Exception e)
        {
            Log("Error[" + (-1).ToString("D4") + "], Msg: " + e.Message + "\n" + e.StackTrace);
        }

        try
        {
            if (fmtCtx != null)
            {
                AVFormatContext* fmtCtxPtr = fmtCtx;
                avformat_close_input(&fmtCtxPtr);
            }

            fmtCtx  = null;
            vStream = null;
        }
        catch (Exception e)
        {
            Log("Error[" + (-1).ToString("D4") + "], Msg: " + e.Message + "\n" + e.StackTrace);
        }
    }

    private void OpenFailed(int ret, bool opened = true)
    {
        Log(ErrorCodeToMsg(ret));
        AVFormatContext* fmtCtxPtr = fmtCtx;
        if (fmtCtx != null && opened) avformat_close_input(&fmtCtxPtr);
        av_freep(fmtCtx);
    }

    private static string ErrorCodeToMsg(int error)
    {
        byte* buffer = stackalloc byte[1024];
        ffmpeg.av_strerror(error, buffer, 1024);
        return Marshal.PtrToStringAnsi((IntPtr)buffer);
    }

    public static bool alreadyRegister = false;

    private void RegisterFFmpegBinaries()
    {
        if (alreadyRegister)
            return;
        alreadyRegister = true;

        var current = @"c:\";
        var probe = Path.Combine("deps", Environment.Is64BitProcess ? "x64" : "x32");

        string ffmpegPath = Path.Combine(
            Directory.GetCurrentDirectory(),
            "deps",
            Environment.Is64BitProcess ? "x64" : "x32");

        if (!Directory.Exists(ffmpegPath))
        {
            throw new DirectoryNotFoundException(
                $"FFmpeg directory not found: {ffmpegPath}");
        }

        Console.WriteLine(ffmpegPath);
        ffmpeg.RootPath = ffmpegPath;

        while (current != null)
        {
            var ffmpegBinaryPath = Path.Combine(current, probe);
            if (Directory.Exists(ffmpegBinaryPath))
            {
                Console.WriteLine("path exists");
                RootPath = ffmpegBinaryPath;

                Console.WriteLine("111111");
                uint ver = ffmpeg.avformat_version();
                Log($"[Version: {ver >> 16}.{ver >> 8 & 255}.{ver & 255}] [Location: {ffmpegBinaryPath}]");

                return;
            }
            current = Directory.GetParent(current)?.FullName;
        }
    }

    private void Log(string msg) { Console.WriteLine($"[FFMPEG] {msg}"); }
    #endregion
}