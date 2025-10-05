using Mpv.NET.API;
using Mpv.NET.Player;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using Microsoft.UI.Dispatching;

namespace HyggePlay.Services
{
    /// <summary>
    /// Simplified video player service using libmpv
    /// </summary>
    public sealed class VideoPlayerService : INotifyPropertyChanged, IAsyncDisposable
    {
        private const string DefaultUserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) HyggePlay/1.0";
        private static readonly string[] UnsupportedOptionsForLegacyMpv =
        {
            "tone-mapping",
            "tone-mapping-param",
            "hdr-compute-peak",
            "hdr-peak-percentile",
            "hdr-peak-percentile-type",
            "target-peak",
            "target-trc",
            "target-prim",
            "dither-size-fruit",
            "target-contrast",
            "glsl-shaders"
        };

        private MpvPlayer? _mpvPlayer;
        private bool _isInitialized;
        private bool _isDisposed;
        private readonly DispatcherQueue _dispatcherQueue;

        public event PropertyChangedEventHandler? PropertyChanged;
        public event EventHandler<string>? ErrorOccurred;
        public event EventHandler? PlaybackStarted;
        public event EventHandler? PlaybackPaused;
    public event EventHandler? PlaybackStopped;

        private bool _isPlaying;
        private string _currentUrl = string.Empty;

        public bool IsInitialized => _isInitialized;
        public bool IsPlaying
        {
            get => _isPlaying;
            private set => SetProperty(ref _isPlaying, value);
        }

        public string CurrentUrl
        {
            get => _currentUrl;
            private set => SetProperty(ref _currentUrl, value);
        }

        public VideoPlayerService()
        {
            _dispatcherQueue = DispatcherQueue.GetForCurrentThread();
        }

        /// <summary>
        /// Initialize the mpv player
        /// </summary>
        public async Task<bool> InitializeAsync(IntPtr windowHandle)
        {
            if (_isInitialized || _isDisposed)
                return _isInitialized;

            if (windowHandle == IntPtr.Zero)
            {
                ErrorOccurred?.Invoke(this, "Failed to initialize video player: Invalid window handle");
                await LogService.LogErrorAsync("VideoPlayerService.InitializeAsync failure", new InvalidOperationException("Invalid window handle"), new Dictionary<string, string>
                {
                    { "windowHandle", windowHandle.ToString() }
                });
                return false;
            }

            string? libMpvPath = ResolveLibMpvPath();
            if (string.IsNullOrWhiteSpace(libMpvPath))
            {
                var exception = new FileNotFoundException("libmpv not found", "mpv-1.dll");
                ErrorOccurred?.Invoke(this, "Failed to initialize video player: libmpv not found");
                await LogService.LogErrorAsync("VideoPlayerService.InitializeAsync failure", exception, new Dictionary<string, string>
                {
                    { "baseDirectory", AppContext.BaseDirectory }
                });
                return false;
            }

            try
            {
                _mpvPlayer = new MpvPlayer(windowHandle, libMpvPath)
                {
                    Volume = 100,
                    Loop = false
                };

                await ApplyDefaultOptionsAsync();

                _isInitialized = true;

                string? mpvVersion = null;
                try
                {
                    mpvVersion = _mpvPlayer.API.GetPropertyString("mpv-version");
                }
                catch
                {
                    // Ignore inability to query version.
                }

                await LogService.LogInfoAsync("VideoPlayerService.InitializeAsync success", new Dictionary<string, string>
                {
                    { "userAgent", DefaultUserAgent },
                    { "libMpvPath", libMpvPath },
                    { "mpvVersion", mpvVersion ?? "unknown" }
                });

                return true;
            }
            catch (MpvAPIException ex)
            {
                ErrorOccurred?.Invoke(this, $"Failed to initialize video player: {ex.Message}");
                await LogService.LogErrorAsync("VideoPlayerService.InitializeAsync exception", ex, new Dictionary<string, string>
                {
                    { "libMpvPath", libMpvPath }
                });
                return false;
            }
            catch (Exception ex)
            {
                ErrorOccurred?.Invoke(this, $"Failed to initialize video player: {ex.Message}");
                await LogService.LogErrorAsync("VideoPlayerService.InitializeAsync exception", ex, new Dictionary<string, string>
                {
                    { "libMpvPath", libMpvPath }
                });
                return false;
            }
        }

        /// <summary>
        /// Load and optionally play a video file or stream URL
        /// </summary>
        public Task<bool> LoadFileAsync(string url, bool autoPlay = false)
        {
            if (!_isInitialized || _mpvPlayer == null || _isDisposed)
                return Task.FromResult(false);

            if (string.IsNullOrWhiteSpace(url))
            {
                ErrorOccurred?.Invoke(this, "Failed to load file: URL is empty");
                return Task.FromResult(false);
            }

            try
            {
                CurrentUrl = url;

                _mpvPlayer.Load(url, autoPlay);

                try
                {
                    _mpvPlayer.API.SetPropertyString("pause", "no");
                }
                catch (MpvAPIException pauseException)
                {
                    _ = LogService.LogErrorAsync("VideoPlayerService.Load pause property error", pauseException, new Dictionary<string, string>
                    {
                        { "url", url }
                    });
                }

                if (autoPlay)
                {
                    IsPlaying = true;
                    PlaybackStarted?.Invoke(this, EventArgs.Empty);
                }

                return Task.FromResult(true);
            }
            catch (Exception ex)
            {
                ErrorOccurred?.Invoke(this, $"Failed to load file: {ex.Message}");
                return Task.FromResult(false);
            }
        }

        /// <summary>
        /// Start or resume playback
        /// </summary>
        public Task<bool> PlayAsync()
        {
            if (!_isInitialized || _mpvPlayer == null || _isDisposed)
                return Task.FromResult(false);

            try
            {
                _mpvPlayer.Resume();
                IsPlaying = true;
                PlaybackStarted?.Invoke(this, EventArgs.Empty);
                return Task.FromResult(true);
            }
            catch (Exception ex)
            {
                ErrorOccurred?.Invoke(this, $"Failed to play: {ex.Message}");
                return Task.FromResult(false);
            }
        }

        /// <summary>
        /// Pause playback
        /// </summary>
        public Task<bool> PauseAsync()
        {
            if (!_isInitialized || _mpvPlayer == null || _isDisposed)
                return Task.FromResult(false);

            try
            {
                _mpvPlayer.Pause();
                IsPlaying = false;
                PlaybackPaused?.Invoke(this, EventArgs.Empty);
                return Task.FromResult(true);
            }
            catch (Exception ex)
            {
                ErrorOccurred?.Invoke(this, $"Failed to pause: {ex.Message}");
                return Task.FromResult(false);
            }
        }

        /// <summary>
        /// Stop playback
        /// </summary>
        public Task<bool> StopAsync()
        {
            if (!_isInitialized || _mpvPlayer == null || _isDisposed)
                return Task.FromResult(false);

            try
            {
                _mpvPlayer.Stop();
                IsPlaying = false;
                CurrentUrl = string.Empty;
                PlaybackStopped?.Invoke(this, EventArgs.Empty);
                return Task.FromResult(true);
            }
            catch (Exception ex)
            {
                ErrorOccurred?.Invoke(this, $"Failed to stop: {ex.Message}");
                return Task.FromResult(false);
            }
        }

        /// <summary>
        /// Set volume (0-100)
        /// </summary>
        public Task<bool> SetVolumeAsync(int volume)
        {
            if (!_isInitialized || _mpvPlayer == null || _isDisposed)
                return Task.FromResult(false);

            try
            {
                var clampedVolume = Math.Clamp(volume, 0, 100);
                _mpvPlayer.Volume = clampedVolume;
                return Task.FromResult(true);
            }
            catch (Exception ex)
            {
                ErrorOccurred?.Invoke(this, $"Failed to set volume: {ex.Message}");
                return Task.FromResult(false);
            }
        }

        private bool SetProperty<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
        {
            if (!System.Collections.Generic.EqualityComparer<T>.Default.Equals(field, value))
            {
                field = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
                return true;
            }
            return false;
        }

        public async ValueTask DisposeAsync()
        {
            if (_isDisposed) return;

            try
            {
                if (_mpvPlayer != null)
                {
                    if (_isInitialized)
                    {
                        await StopAsync();
                    }

                    _mpvPlayer.Dispose();
                    _mpvPlayer = null;
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error disposing VideoPlayerService: {ex.Message}");
            }
            finally
            {
                _isInitialized = false;
                _isDisposed = true;
            }
        }

        private async Task ApplyDefaultOptionsAsync()
        {
            if (_mpvPlayer == null)
            {
                return;
            }

            try
            {
                Dictionary<string, string> options = MpvConfig.GetStreamingOptions();
                foreach ((string key, string value) in options)
                {
                    if (UnsupportedOptionsForLegacyMpv.Contains(key, StringComparer.OrdinalIgnoreCase))
                    {
                        await LogService.LogInfoAsync("VideoPlayerService.InitializeAsync option skipped", new Dictionary<string, string>
                        {
                            { "option", key }
                        });
                        continue;
                    }

                    try
                    {
                        _mpvPlayer.API.SetPropertyString(key, value);
                    }
                    catch (MpvAPIException optionException)
                    {
                        await LogService.LogErrorAsync("VideoPlayerService.InitializeAsync option error", optionException, new Dictionary<string, string>
                        {
                            { "option", key },
                            { "value", value }
                        });
                    }
                }

                try
                {
                    _mpvPlayer.API.SetPropertyString("user-agent", DefaultUserAgent);
                }
                catch (MpvAPIException userAgentException)
                {
                    await LogService.LogErrorAsync("VideoPlayerService.InitializeAsync user agent error", userAgentException, new Dictionary<string, string>
                    {
                        { "userAgent", DefaultUserAgent }
                    });
                }
            }
            catch (Exception ex)
            {
                ErrorOccurred?.Invoke(this, $"Failed to apply default mpv options: {ex.Message}");
                await LogService.LogErrorAsync("VideoPlayerService.InitializeAsync options failure", ex, null);
            }
        }

        private string? ResolveLibMpvPath()
        {
            string baseDirectory = AppContext.BaseDirectory;
            IEnumerable<string> candidatePaths = new[]
            {
                Path.Combine(baseDirectory, "mpv-1.dll"),
                Path.Combine(baseDirectory, "mpv.dll"),
                Path.Combine(baseDirectory, "libmpv-2.dll"),
                Path.Combine(baseDirectory, "win-x64", "mpv-1.dll"),
                Path.Combine(baseDirectory, "win-x64", "mpv.dll"),
                Path.Combine(baseDirectory, "NativeLibraries", "mpv-1.dll"),
                Path.Combine(baseDirectory, "NativeLibraries", "mpv.dll"),
            };

            foreach (string candidate in candidatePaths)
            {
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }

            string libFromEnv = Environment.GetEnvironmentVariable("LIBMPV_PATH") ?? string.Empty;
            if (!string.IsNullOrWhiteSpace(libFromEnv) && File.Exists(libFromEnv))
            {
                return libFromEnv;
            }

            return null;
        }
    }
}