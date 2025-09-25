using Mpv.NET.Player;
using System;
using System.ComponentModel;
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
        private MpvPlayer? _mpvPlayer;
        private bool _isInitialized;
        private bool _isDisposed;
        private readonly DispatcherQueue _dispatcherQueue;

        public event PropertyChangedEventHandler? PropertyChanged;
        public event EventHandler<string>? ErrorOccurred;
        public event EventHandler? PlaybackStarted;
        public event EventHandler? PlaybackPaused;
        public event EventHandler? PlaybackStopped;
        public event EventHandler? PlaybackEnded;

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

            try
            {
                await Task.Run(() =>
                {
                    // Create mpv player instance with the window handle
                    _mpvPlayer = new MpvPlayer(windowHandle)
                    {
                        // Set basic properties
                        Volume = 100,
                        Loop = false
                    };
                });

                _isInitialized = true;
                return true;
            }
            catch (Exception ex)
            {
                ErrorOccurred?.Invoke(this, $"Failed to initialize video player: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Load and optionally play a video file or stream URL
        /// </summary>
        public async Task<bool> LoadFileAsync(string url, bool autoPlay = false)
        {
            if (!_isInitialized || _mpvPlayer == null || _isDisposed)
                return false;

            try
            {
                CurrentUrl = url;
                
                await Task.Run(() =>
                {
                    _mpvPlayer.Load(url, autoPlay);
                });

                if (autoPlay)
                {
                    IsPlaying = true;
                    PlaybackStarted?.Invoke(this, EventArgs.Empty);
                }

                return true;
            }
            catch (Exception ex)
            {
                ErrorOccurred?.Invoke(this, $"Failed to load file: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Start or resume playback
        /// </summary>
        public async Task<bool> PlayAsync()
        {
            if (!_isInitialized || _mpvPlayer == null || _isDisposed)
                return false;

            try
            {
                await Task.Run(() => _mpvPlayer.Resume());
                IsPlaying = true;
                PlaybackStarted?.Invoke(this, EventArgs.Empty);
                return true;
            }
            catch (Exception ex)
            {
                ErrorOccurred?.Invoke(this, $"Failed to play: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Pause playback
        /// </summary>
        public async Task<bool> PauseAsync()
        {
            if (!_isInitialized || _mpvPlayer == null || _isDisposed)
                return false;

            try
            {
                await Task.Run(() => _mpvPlayer.Pause());
                IsPlaying = false;
                PlaybackPaused?.Invoke(this, EventArgs.Empty);
                return true;
            }
            catch (Exception ex)
            {
                ErrorOccurred?.Invoke(this, $"Failed to pause: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Stop playback
        /// </summary>
        public async Task<bool> StopAsync()
        {
            if (!_isInitialized || _mpvPlayer == null || _isDisposed)
                return false;

            try
            {
                await Task.Run(() => _mpvPlayer.Stop());
                IsPlaying = false;
                CurrentUrl = string.Empty;
                PlaybackStopped?.Invoke(this, EventArgs.Empty);
                return true;
            }
            catch (Exception ex)
            {
                ErrorOccurred?.Invoke(this, $"Failed to stop: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Set volume (0-100)
        /// </summary>
        public async Task<bool> SetVolumeAsync(int volume)
        {
            if (!_isInitialized || _mpvPlayer == null || _isDisposed)
                return false;

            try
            {
                var clampedVolume = Math.Clamp(volume, 0, 100);
                await Task.Run(() => _mpvPlayer.Volume = clampedVolume);
                return true;
            }
            catch (Exception ex)
            {
                ErrorOccurred?.Invoke(this, $"Failed to set volume: {ex.Message}");
                return false;
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
    }
}