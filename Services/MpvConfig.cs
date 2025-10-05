using System;
using System.Collections.Generic;

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
        public static Dictionary<string, string> GetStreamingOptions()
        {
            var options = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                // Video / audio behaviour
                ["hwdec"] = "auto-safe",
                ["video-sync"] = "audio",
                ["keep-open"] = "no",
                ["force-window"] = "no",
                ["idle"] = "no",
                ["osc"] = "no",

                // Audio handling
                ["audio-channels"] = "auto",
                ["audio-samplerate"] = "48000",

                // Network resilience
                ["cache"] = "yes",
                ["cache-secs"] = "10",
                ["demuxer-seekable-cache"] = "yes",
                ["demuxer-readahead-secs"] = "10",
                ["stream-lavf-o"] = "reconnect=1,reconnect_at_eof=1,reconnect_streamed=1,reconnect_delay_max=5",
                ["network-timeout"] = "30",
                ["rtsp-transport"] = "tcp",

                // Logging
                ["msg-level"] = "status=warn",

                // Threading defaults
                ["vd-lavc-threads"] = "0",
                ["ad-lavc-threads"] = "0"
            };

            return options;
        }
    }
}