using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace HyggePlay.Services
{
    /// <summary>
    /// Configuration class for mpv with high-quality playback settings
    /// </summary>
    public static class MpvConfig
    {
        /// <summary>
        /// Gets the high-quality mpv configuration options
        /// </summary>
        public static Dictionary<string, string> GetHighQualityOptions()
        {
            var options = new Dictionary<string, string>
            {
                // Video Output - Use GPU acceleration with modern backends
                ["vo"] = "gpu-next",
                ["gpu-api"] = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "d3d11" : "vulkan",
                ["gpu-context"] = "winvk",
                
                // High-quality scaling
                ["scale"] = "ewa_lanczossharp",
                ["cscale"] = "ewa_lanczossharp",
                ["dscale"] = "mitchell",
                ["correct-downscaling"] = "yes",
                
                // Debanding for better quality
                ["deband"] = "yes",
                ["deband-iterations"] = "2",
                ["deband-threshold"] = "48",
                ["deband-range"] = "16",
                ["deband-grain"] = "48",
                
                // Dithering for smooth gradients
                ["dither-depth"] = "auto",
                ["dither"] = "error-diffusion",
                
                // HDR and tone mapping
                ["tone-mapping"] = "bt.2446a",
                ["tone-mapping-param"] = "1.0",
                ["hdr-compute-peak"] = "yes",
                ["hdr-peak-percentile"] = "99.995",
                
                // Temporal interpolation for smooth motion
                ["video-sync"] = "display-resample",
                ["interpolation"] = "yes",
                ["tscale"] = "oversample",
                
                // Audio settings - High quality audio
                ["audio-device"] = "auto",
                ["audio-exclusive"] = "yes",
                ["audio-format"] = "s32",
                ["audio-samplerate"] = "48000",
                ["audio-channels"] = "auto-safe",
                
                // Passthrough for high-quality audio formats
                ["ad-lavc-ac3drc"] = "0",
                ["ad-lavc-downmix"] = "no",
                ["audio-spdif"] = "ac3,eac3,dts,dts-hd,truehd",
                
                // Performance and quality balance
                ["hwdec"] = "auto-safe",
                ["hwdec-codecs"] = "all",
                
                // Cache settings for streaming
                ["cache"] = "yes",
                ["demuxer-max-bytes"] = "150MiB",
                ["demuxer-readahead-secs"] = "20",
                
                // Window and display
                ["keep-open"] = "always",
                ["force-window"] = "immediate",
                ["idle"] = "once",
                
                // Logging (can be adjusted for production)
                ["msg-level"] = "all=warn",
                
                // Performance optimizations
                ["vd-lavc-threads"] = "0", // Auto-detect CPU cores
                ["ad-lavc-threads"] = "0",
                ["vd-lavc-skiploopfilter"] = "none",
                
                // Color management
                ["icc-profile-auto"] = "yes",
                ["target-prim"] = "auto",
                ["target-trc"] = "auto"
            };

            return options;
        }

        /// <summary>
        /// Gets streaming-optimized configuration options
        /// </summary>
        public static Dictionary<string, string> GetStreamingOptions()
        {
            var options = GetHighQualityOptions();
            
            // Adjust for streaming scenarios
            options["cache-secs"] = "30";
            options["cache-pause-initial"] = "yes";
            options["cache-pause-wait"] = "3";
            options["demuxer-seekable-cache"] = "yes";
            options["stream-lavf-o"] = "reconnect=1,reconnect_at_eof=1,reconnect_streamed=1,reconnect_delay_max=5";
            
            // Network timeout settings for IPTV
            options["network-timeout"] = "30";
            options["rtsp-transport"] = "tcp";
            options["user-agent"] = "HyggePlay/1.0 (Windows NT 10.0; Win64; x64)";
            
            return options;
        }

        /// <summary>
        /// Gets configuration for low-latency playback (useful for live streams)
        /// </summary>
        public static Dictionary<string, string> GetLowLatencyOptions()
        {
            var options = GetStreamingOptions();
            
            // Low latency adjustments
            options["cache-secs"] = "5";
            options["demuxer-readahead-secs"] = "5";
            options["video-sync"] = "audio";
            options["framedrop"] = "vo";
            options["untimed"] = "yes";
            
            return options;
        }
    }
}