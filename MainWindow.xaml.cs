using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using HyggePlay.Models;
using HyggePlay.Services;
using WinRT.Interop;
using Microsoft.UI.Xaml.Media;
using System.Runtime.InteropServices;
using Windows.Foundation;

namespace HyggePlay
{
    public sealed partial class MainWindow : Window
    {
        private readonly IPTVService _iptvService;
        private readonly DatabaseService _databaseService;
        private readonly ObservableCollection<UserProfile> _users;
        private readonly ObservableCollection<ChannelInfo> _channels;
        private readonly ObservableCollection<ChannelInfo> _filteredChannels;
        private readonly ObservableCollection<ChannelGroupOption> _channelGroups;
    private readonly SemaphoreSlim _dialogSemaphore = new(1, 1);
    private readonly SemaphoreSlim _playbackSemaphore = new(1, 1);
        private VideoPlayerService? _videoPlayerService;
        private int? _activeUserId;
    private int? _currentChannelId;
        private IntPtr _videoHostHwnd = IntPtr.Zero;
        private readonly TaskCompletionSource<bool> _videoHostReadyTcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private double _lastRasterizationScale = 1.0;
    private int _lastHostX = -1;
    private int _lastHostY = -1;
    private int _lastHostWidth = -1;
    private int _lastHostHeight = -1;

        public MainWindow()
        {
            this.InitializeComponent();
            this.Title = "HyggePlay IPTV Player";

            _iptvService = new IPTVService();
            _databaseService = new DatabaseService();
            _users = new ObservableCollection<UserProfile>();
            _channels = new ObservableCollection<ChannelInfo>();
            _filteredChannels = new ObservableCollection<ChannelInfo>();
            _channelGroups = new ObservableCollection<ChannelGroupOption>();

            // Initialize after window loads
            _ = InitializeAsync();

        this.SizeChanged += MainWindow_SizeChanged;
        this.Closed += MainWindow_Closed;
        VideoContainer.Loaded += VideoContainer_Loaded;
        VideoContainer.SizeChanged += VideoContainer_SizeChanged;
        VideoContainer.LayoutUpdated += VideoContainer_LayoutUpdated;
        }

        private async Task InitializeAsync()
        {
            try
            {
                // Initialize database
                await _databaseService.InitializeAsync();

                // Wire up events
                LoginButton.Click += LoginButton_Click;
                LoadUserButton.Click += LoadUserButton_Click;
                DeleteUserButton.Click += DeleteUserButton_Click;
                UserComboBox.SelectionChanged += UserComboBox_SelectionChanged;
                ChannelGroupComboBox.SelectionChanged += ChannelGroupComboBox_SelectionChanged;
                ChannelSearchTextBox.TextChanged += ChannelSearchTextBox_TextChanged;
                ChannelResultsListView.SelectionChanged += ChannelResultsListView_SelectionChanged;

                // Set up data sources
                UserComboBox.ItemsSource = _users;
                ChannelGroupComboBox.ItemsSource = _channelGroups;
                ChannelResultsListView.ItemsSource = _filteredChannels;

                // Load saved users on startup
                await LoadSavedUsers();
            }
            catch (System.Exception ex)
            {
                await ShowErrorDialog("Initialization Error", $"Failed to initialize application: {ex.Message}");
            }
        }

        private async Task LoadSavedUsers()
        {
            try
            {
                var users = await _databaseService.GetUsersAsync();
                _users.Clear();
                foreach (var user in users)
                {
                    _users.Add(user);
                }
            }
            catch (Exception ex)
            {
                await ShowErrorDialog("Database Error", $"Failed to load saved users: {ex.Message}");
            }
        }

        private async void LoginButton_Click(object sender, RoutedEventArgs e)
        {
            var serverUrl = ServerUrlTextBox.Text?.Trim();
            var username = UsernameTextBox.Text?.Trim();
            var password = PasswordBox.Password?.Trim();

            if (string.IsNullOrEmpty(serverUrl) || string.IsNullOrEmpty(username) || string.IsNullOrEmpty(password))
            {
                await ShowErrorDialog("Validation Error", "Please fill in all fields");
                return;
            }

            try
            {
                LoginButton.IsEnabled = false;
                LoginButton.Content = "Logging in...";

                var loginResult = await _iptvService.AuthenticateAsync(serverUrl, username, password);

                if (loginResult.IsSuccess)
                {
                    // Save user to database
                    var user = new UserProfile
                    {
                        Username = username,
                        ServerUrl = serverUrl,
                        Password = password, // In production, encrypt this
                        DisplayName = username
                    };

                    int userId = await _databaseService.UpsertUserAsync(user);
                    user.Id = userId;

                    await LoadSavedUsers();

                    var refreshedUser = _users.FirstOrDefault(u => u.Id == userId);
                    if (refreshedUser != null)
                    {
                        UserComboBox.SelectedItem = refreshedUser;
                        user = refreshedUser;
                    }

                    // Load channels for this user
                    await LoadChannelsForUser(user);

                    // Clear form
                    ServerUrlTextBox.Text = "";
                    UsernameTextBox.Text = "";
                    PasswordBox.Password = "";
                }
                else
                {
                    await ShowErrorDialog("Login Failed", loginResult.ErrorMessage ?? "Invalid credentials");
                }
            }
            catch (Exception ex)
            {
                await ShowErrorDialog("Login Error", $"An error occurred during login: {ex.Message}");
            }
            finally
            {
                LoginButton.IsEnabled = true;
                LoginButton.Content = "Login";
            }
        }

        private async void LoadUserButton_Click(object sender, RoutedEventArgs e)
        {
            var selectedUser = UserComboBox.SelectedItem as UserProfile;
            if (selectedUser == null)
            {
                await ShowErrorDialog("Selection Required", "Please select a user profile");
                return;
            }

            await LoadChannelsForUser(selectedUser);
        }

        private async void DeleteUserButton_Click(object sender, RoutedEventArgs e)
        {
            var selectedUser = UserComboBox.SelectedItem as UserProfile;
            if (selectedUser == null)
            {
                await ShowErrorDialog("Selection Required", "Please select a user profile to delete");
                return;
            }

            var dialog = new ContentDialog
            {
                Title = "Confirm Delete",
                Content = $"Are you sure you want to delete the profile for '{selectedUser.DisplayName}'?",
                PrimaryButtonText = "Delete",
                CloseButtonText = "Cancel"
            };
            dialog.XamlRoot = this.Content.XamlRoot;

            var result = await dialog.ShowAsync();
            if (result == ContentDialogResult.Primary)
            {
                try
                {
                    await _databaseService.DeleteUserAsync(selectedUser.Id);
                    await LoadSavedUsers();

                    // Clear channels if this was the active user
                    _channels.Clear();
                    _filteredChannels.Clear();
                    _channelGroups.Clear();
                    ChannelSearchPanel.Visibility = Visibility.Collapsed;
                }
                catch (Exception ex)
                {
                    await ShowErrorDialog("Delete Error", $"Failed to delete user: {ex.Message}");
                }
            }
        }

        private void UserComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            var hasSelection = UserComboBox.SelectedItem != null;
            LoadUserButton.IsEnabled = hasSelection;
            DeleteUserButton.IsEnabled = hasSelection;
        }

        private async Task LoadChannelsForUser(UserProfile user)
        {
            try
            {
                LoadUserButton.IsEnabled = false;
                LoadUserButton.Content = "Loading...";
                _activeUserId = null;

                if (user.Id <= 0)
                {
                    var allUsers = await _databaseService.GetUsersAsync();
                    UserProfile? matchedUser = allUsers.FirstOrDefault(u =>
                        u.ServerUrl.Equals(user.ServerUrl, StringComparison.OrdinalIgnoreCase) &&
                        u.Username.Equals(user.Username, StringComparison.OrdinalIgnoreCase));

                    if (matchedUser == null)
                    {
                        await ShowErrorDialog("Load Error", "Unable to resolve the selected profile. Please log in again.");
                        return;
                    }

                    user.Id = matchedUser.Id;
                }

                if (user.Id <= 0)
                {
                    await ShowErrorDialog("Load Error", "The selected profile is invalid. Please log in again.");
                    return;
                }

                _activeUserId = user.Id;

                // First try to get channels from database
                var savedChannels = await _databaseService.SearchChannelsAsync(user.Id, null, null);
                
                if (savedChannels.Any())
                {
                    // Load from database
                    _channels.Clear();
                    foreach (var channel in savedChannels)
                    {
                        _channels.Add(channel);
                    }
                }
                else
                {
                    // Fetch from server
                    var authResult = await _iptvService.AuthenticateAsync(user.ServerUrl, user.Username, user.Password);
                    if (!authResult.IsSuccess)
                    {
                        await ShowErrorDialog("Authentication Failed", "Failed to authenticate with the server");
                        return;
                    }

                    var groups = await _iptvService.GetLiveCategoriesAsync();
                    var channels = await _iptvService.GetLiveChannelsAsync();
                    
                    _channels.Clear();
                    foreach (var channel in channels)
                    {
                        _channels.Add(channel);
                    }
                    
                    // Save to database
                    await _databaseService.ReplaceChannelDataAsync(user.Id, groups, channels);
                }

                // Update UI
                await UpdateChannelGroupsAsync();
                FilterChannels();
                ChannelSearchPanel.Visibility = Visibility.Visible;
            }
            catch (Exception ex)
            {
                await ShowErrorDialog("Load Error", $"Failed to load channels: {ex.Message}");
            }
            finally
            {
                LoadUserButton.IsEnabled = true;
                LoadUserButton.Content = "Load Channels";
            }
        }

        private async Task UpdateChannelGroupsAsync()
        {
            _channelGroups.Clear();
            _channelGroups.Add(new ChannelGroupOption { Name = "All Groups", Value = "" });

            var groupedChannels = _channels
                .Where(c => !string.IsNullOrEmpty(c.GroupId))
                .GroupBy(c => c.GroupId)
                .Select(g => new
                {
                    GroupId = g.Key,
                    Count = g.Count(),
                    NameFromChannels = g.Select(ch => ch.GroupName)
                        .FirstOrDefault(name => !string.IsNullOrWhiteSpace(name))
                })
                .ToList();

            if (groupedChannels.Count == 0)
            {
                return;
            }

            Dictionary<string, string> displayNameLookup = groupedChannels
                .ToDictionary(g => g.GroupId, g => g.NameFromChannels ?? string.Empty);

            if (_activeUserId.HasValue)
            {
                try
                {
                    var storedGroups = await _databaseService.GetChannelGroupsAsync(_activeUserId.Value);
                    foreach (var group in storedGroups)
                    {
                        if (string.IsNullOrWhiteSpace(group.GroupId))
                        {
                            continue;
                        }

                        displayNameLookup[group.GroupId] = string.IsNullOrWhiteSpace(group.Name)
                            ? group.GroupId
                            : group.Name;
                    }
                }
                catch
                {
                    // Ignore database errors while resolving group names and fall back to channel data.
                }
            }

            foreach (var group in groupedChannels
                .OrderBy(g =>
                    displayNameLookup.TryGetValue(g.GroupId, out string? value) && !string.IsNullOrWhiteSpace(value)
                        ? value
                        : g.GroupId,
                    StringComparer.CurrentCultureIgnoreCase))
            {
                string displayName = displayNameLookup.TryGetValue(group.GroupId, out string? lookupName) && !string.IsNullOrWhiteSpace(lookupName)
                    ? lookupName
                    : group.GroupId;

                _channelGroups.Add(new ChannelGroupOption
                {
                    Name = $"{displayName} ({group.Count})",
                    Value = group.GroupId
                });
            }
        }

        private void ChannelGroupComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            FilterChannels();
        }

        private void ChannelSearchTextBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            FilterChannels();
        }

        private void FilterChannels()
        {
            _filteredChannels.Clear();

            var searchText = ChannelSearchTextBox.Text?.ToLower() ?? "";
            var selectedGroup = (ChannelGroupComboBox.SelectedItem as ChannelGroupOption)?.Value ?? "";

            var filtered = _channels.AsEnumerable();

            // Filter by group
            if (!string.IsNullOrEmpty(selectedGroup))
            {
                filtered = filtered.Where(c => c.GroupId == selectedGroup);
            }

            // Filter by search text
            if (!string.IsNullOrEmpty(searchText))
            {
                filtered = filtered.Where(c => 
                    c.Name?.ToLower().Contains(searchText) == true ||
                    c.GroupId?.ToLower().Contains(searchText) == true ||
                    c.GroupName?.ToLower().Contains(searchText) == true);
            }

            foreach (var channel in filtered)
            {
                _filteredChannels.Add(channel);
            }
        }

        private void VideoContainer_Loaded(object sender, RoutedEventArgs e)
        {
            EnsureVideoHost();
            if (VideoContainer.XamlRoot is XamlRoot xamlRoot)
            {
                _lastRasterizationScale = xamlRoot.RasterizationScale;
                xamlRoot.Changed += XamlRoot_Changed;
            }

            _videoHostReadyTcs.TrySetResult(true);
        }

        private void VideoContainer_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            UpdateVideoHostPlacement();
        }

        private void VideoContainer_LayoutUpdated(object? sender, object e)
        {
            UpdateVideoHostPlacement();
        }

        private void MainWindow_SizeChanged(object sender, WindowSizeChangedEventArgs e)
        {
            UpdateVideoHostPlacement();
        }

        private void XamlRoot_Changed(XamlRoot sender, XamlRootChangedEventArgs args)
        {
            _lastRasterizationScale = sender.RasterizationScale;
            UpdateVideoHostPlacement();
        }

        private void MainWindow_Closed(object sender, WindowEventArgs args)
        {
            if (_videoHostHwnd != IntPtr.Zero)
            {
                NativeMethods.DestroyWindow(_videoHostHwnd);
                _videoHostHwnd = IntPtr.Zero;
                _lastHostX = _lastHostY = _lastHostWidth = _lastHostHeight = -1;
            }

            if (VideoContainer.XamlRoot is XamlRoot xamlRoot)
            {
                xamlRoot.Changed -= XamlRoot_Changed;
            }

            VideoContainer.Loaded -= VideoContainer_Loaded;
            VideoContainer.SizeChanged -= VideoContainer_SizeChanged;
            VideoContainer.LayoutUpdated -= VideoContainer_LayoutUpdated;
            this.SizeChanged -= MainWindow_SizeChanged;
        }

        private void EnsureVideoHost()
        {
            if (_videoHostHwnd != IntPtr.Zero)
            {
                UpdateVideoHostPlacement();
                return;
            }

            if (VideoContainer.ActualWidth <= 0 || VideoContainer.ActualHeight <= 0)
            {
                return;
            }

            IntPtr mainWindowHandle = WindowNative.GetWindowHandle(this);
            if (mainWindowHandle == IntPtr.Zero)
            {
                return;
            }

            double scale = VideoContainer.XamlRoot?.RasterizationScale ?? _lastRasterizationScale;
            if (scale <= 0)
            {
                scale = 1.0;
            }

            try
            {
                GeneralTransform transform = VideoContainer.TransformToVisual(RootGrid);
                Point offset = transform.TransformPoint(new Point(0, 0));

                int x = (int)Math.Round(offset.X * scale);
                int y = (int)Math.Round(offset.Y * scale);
                int width = Math.Max(1, (int)Math.Round(VideoContainer.ActualWidth * scale));
                int height = Math.Max(1, (int)Math.Round(VideoContainer.ActualHeight * scale));

                IntPtr moduleHandle = NativeMethods.GetModuleHandle(null);
                _videoHostHwnd = NativeMethods.CreateWindowExW(
                    NativeMethods.WS_EX_NOREDIRECTIONBITMAP,
                    "STATIC",
                    string.Empty,
                    NativeMethods.WS_CHILD | NativeMethods.WS_VISIBLE | NativeMethods.WS_CLIPSIBLINGS | NativeMethods.WS_CLIPCHILDREN,
                    x,
                    y,
                    width,
                    height,
                    mainWindowHandle,
                    IntPtr.Zero,
                    moduleHandle,
                    IntPtr.Zero);

                if (_videoHostHwnd != IntPtr.Zero)
                {
                    NativeMethods.SetWindowPos(_videoHostHwnd, IntPtr.Zero, x, y, width, height, NativeMethods.SWP_NOZORDER | NativeMethods.SWP_NOACTIVATE | NativeMethods.SWP_SHOWWINDOW);
                    _lastHostX = x;
                    _lastHostY = y;
                    _lastHostWidth = width;
                    _lastHostHeight = height;
                    _ = LogService.LogInfoAsync("VideoHost created", new Dictionary<string, string>
                    {
                        { "hwnd", _videoHostHwnd.ToString("X") },
                        { "x", x.ToString() },
                        { "y", y.ToString() },
                        { "width", width.ToString() },
                        { "height", height.ToString() },
                        { "scale", scale.ToString("F2") }
                    });
                }
                else
                {
                    _ = LogService.LogErrorAsync("VideoHost creation failure", new InvalidOperationException("Failed to create video host window"), null);
                }
            }
            catch (Exception ex)
            {
                _ = LogService.LogErrorAsync("VideoHost creation exception", ex, null);
            }
        }

        private void UpdateVideoHostPlacement()
        {
            if (VideoContainer.ActualWidth <= 0 || VideoContainer.ActualHeight <= 0)
            {
                return;
            }

            double scale = VideoContainer.XamlRoot?.RasterizationScale ?? _lastRasterizationScale;
            if (scale <= 0)
            {
                scale = 1.0;
            }

            try
            {
                GeneralTransform transform = VideoContainer.TransformToVisual(RootGrid);
                Point offset = transform.TransformPoint(new Point(0, 0));

                int x = (int)Math.Round(offset.X * scale);
                int y = (int)Math.Round(offset.Y * scale);
                int width = Math.Max(1, (int)Math.Round(VideoContainer.ActualWidth * scale));
                int height = Math.Max(1, (int)Math.Round(VideoContainer.ActualHeight * scale));

                if (_videoHostHwnd != IntPtr.Zero)
                {
                    bool changed = x != _lastHostX || y != _lastHostY || width != _lastHostWidth || height != _lastHostHeight;
                    if (changed)
                    {
                        NativeMethods.SetWindowPos(_videoHostHwnd, IntPtr.Zero, x, y, width, height, NativeMethods.SWP_NOZORDER | NativeMethods.SWP_NOACTIVATE | NativeMethods.SWP_SHOWWINDOW);
                        _lastHostX = x;
                        _lastHostY = y;
                        _lastHostWidth = width;
                        _lastHostHeight = height;
                        _ = LogService.LogInfoAsync("VideoHost repositioned", new Dictionary<string, string>
                        {
                            { "hwnd", _videoHostHwnd.ToString("X") },
                            { "x", x.ToString() },
                            { "y", y.ToString() },
                            { "width", width.ToString() },
                            { "height", height.ToString() },
                            { "scale", scale.ToString("F2") }
                        });
                    }
                }
            }
            catch (Exception ex)
            {
                _ = LogService.LogErrorAsync("VideoHost placement exception", ex, null);
            }
        }

        private async void ChannelResultsListView_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            var selectedChannel = e.AddedItems?.OfType<ChannelInfo>().FirstOrDefault();
            if (selectedChannel != null)
            {
                await PlayChannelAsync(selectedChannel);
            }
        }

        private async Task PlayChannelAsync(ChannelInfo channel)
        {
            if (string.IsNullOrEmpty(channel.StreamUrl))
            {
                await ShowErrorDialog("Channel Error", $"Channel '{channel.Name}' has no URL configured.");
                return;
            }

            string streamUrl = channel.StreamUrl!;

            await _playbackSemaphore.WaitAsync();
            try
            {
                if (_currentChannelId == channel.ChannelId &&
                    string.Equals(_videoPlayerService?.CurrentUrl, streamUrl, StringComparison.OrdinalIgnoreCase))
                {
                    return;
                }

                _currentChannelId = channel.ChannelId;

                await LogService.LogInfoAsync("PlayChannelAsync start", new Dictionary<string, string>
                {
                    { "channelId", channel.ChannelId.ToString() },
                    { "channelName", channel.Name },
                    { "groupId", channel.GroupId },
                    { "streamUrl", streamUrl }
                });

                await InitializeVideoPlayerAsync();
                if (_videoPlayerService == null || !_videoPlayerService.IsInitialized)
                {
                    _currentChannelId = null;
                    await LogService.LogErrorAsync("PlayChannelAsync initialization failure", new InvalidOperationException("Video player service not available"), new Dictionary<string, string>
                    {
                        { "channelId", channel.ChannelId.ToString() },
                        { "channelName", channel.Name }
                    });
                    await ShowErrorDialog("Playback Error", "Video player failed to initialize. Please try again.");
                    return;
                }

                try
                {
                    CurrentChannelText.Text = $"Loading: {channel.Name}";

                    // Stop any current playback first
                    await _videoPlayerService.StopAsync();

                    var success = await _videoPlayerService.LoadFileAsync(streamUrl, autoPlay: true);
                    if (success)
                    {
                        CurrentChannelText.Text = $"Playing: {channel.Name}";
                        await LogService.LogInfoAsync("PlayChannelAsync success", new Dictionary<string, string>
                        {
                            { "channelId", channel.ChannelId.ToString() },
                            { "channelName", channel.Name }
                        });
                    }
                    else
                    {
                        _currentChannelId = null;
                        CurrentChannelText.Text = "Failed to load channel";
                        await LogService.LogErrorAsync("PlayChannelAsync load failure", new InvalidOperationException("LoadFileAsync returned false"), new Dictionary<string, string>
                        {
                            { "channelId", channel.ChannelId.ToString() },
                            { "channelName", channel.Name },
                            { "streamUrl", streamUrl }
                        });
                        await ShowErrorDialog("Playback Error", $"Failed to load channel '{channel.Name}'. The stream may be unavailable.");
                    }
                }
                catch (Exception ex)
                {
                    _currentChannelId = null;
                    CurrentChannelText.Text = "Error loading channel";
                    await LogService.LogErrorAsync("PlayChannelAsync exception", ex, new Dictionary<string, string>
                    {
                        { "channelId", channel.ChannelId.ToString() },
                        { "channelName", channel.Name },
                        { "streamUrl", streamUrl }
                    });
                    await ShowErrorDialog("Playback Error", $"An error occurred while loading channel '{channel.Name}': {ex.Message}");
                }
            }
            finally
            {
                _playbackSemaphore.Release();
            }
        }

        private async Task ShowErrorDialog(string title, string message)
        {
            await _dialogSemaphore.WaitAsync();
            try
            {
                var dialog = new ContentDialog
                {
                    Title = title,
                    Content = message,
                    CloseButtonText = "OK"
                };
                dialog.XamlRoot = this.Content.XamlRoot;
                await dialog.ShowAsync();
            }
            finally
            {
                _dialogSemaphore.Release();
            }
        }

        // Video Player Methods
        private async Task InitializeVideoPlayerAsync()
        {
            if (_videoPlayerService != null)
            {
                if (_videoPlayerService.IsInitialized)
                    return;

                await _videoPlayerService.DisposeAsync();
                _videoPlayerService = null;
            }

            try
            {
                var playerService = new VideoPlayerService();

                await _videoHostReadyTcs.Task;
                EnsureVideoHost();
                UpdateVideoHostPlacement();

                if (_videoHostHwnd == IntPtr.Zero)
                {
                    await playerService.DisposeAsync();
                    await LogService.LogErrorAsync("InitializeVideoPlayerAsync failure", new InvalidOperationException("Video host window not available"));
                    await ShowErrorDialog("Video Player Error", "Video surface could not be created. Please try resizing the window and retry.");
                    return;
                }

                var success = await playerService.InitializeAsync(_videoHostHwnd);
                if (!success)
                {
                    await playerService.DisposeAsync();
                    await LogService.LogErrorAsync("InitializeVideoPlayerAsync failure", new InvalidOperationException("Video player initialization returned false"));
                    await ShowErrorDialog("Video Player Error", "Failed to initialize video player.");
                    return;
                }

                _videoPlayerService = playerService;

                // Subscribe to events
                _videoPlayerService.ErrorOccurred += OnVideoPlayerError;
                _videoPlayerService.PlaybackStarted += OnVideoPlayerPlaybackStarted;
                _videoPlayerService.PlaybackPaused += OnVideoPlayerPlaybackPaused;
                _videoPlayerService.PlaybackStopped += OnVideoPlayerPlaybackStopped;

                await LogService.LogInfoAsync("InitializeVideoPlayerAsync success", new Dictionary<string, string>
                {
                    { "playerCreated", "true" }
                });
            }
            catch (Exception ex)
            {
                if (_videoPlayerService != null)
                {
                    await _videoPlayerService.DisposeAsync();
                    _videoPlayerService = null;
                }

                await LogService.LogErrorAsync("InitializeVideoPlayerAsync exception", ex, null);
                await ShowErrorDialog("Video Player Error", $"Failed to initialize video player: {ex.Message}");
            }
        }

        // Video Player Event Handlers
        private async void OnPlayButtonClick(object sender, RoutedEventArgs e)
        {
            await InitializeVideoPlayerAsync();
            if (_videoPlayerService != null)
            {
                await _videoPlayerService.PlayAsync();
            }
        }

        private async void OnPauseButtonClick(object sender, RoutedEventArgs e)
        {
            if (_videoPlayerService != null)
            {
                await _videoPlayerService.PauseAsync();
            }
        }

        private async void OnStopButtonClick(object sender, RoutedEventArgs e)
        {
            if (_videoPlayerService != null)
            {
                await _videoPlayerService.StopAsync();
                CurrentChannelText.Text = "No media loaded";
            }
        }

        private async void OnLoadTestStreamButtonClick(object sender, RoutedEventArgs e)
        {
            var url = TestStreamUrlTextBox.Text?.Trim();
            if (string.IsNullOrEmpty(url))
            {
                await ShowErrorDialog("Input Error", "Please enter a valid URL or file path.");
                return;
            }

            await InitializeVideoPlayerAsync();
            if (_videoPlayerService != null)
            {
                try
                {
                    CurrentChannelText.Text = $"Loading: {url}";
                    var success = await _videoPlayerService.LoadFileAsync(url, autoPlay: true);
                    if (success)
                    {
                        CurrentChannelText.Text = $"Playing: {url}";
                    }
                    else
                    {
                        CurrentChannelText.Text = "Failed to load media";
                        await ShowErrorDialog("Playback Error", "Failed to load the media. Please check the URL and try again.");
                    }
                }
                catch (Exception ex)
                {
                    CurrentChannelText.Text = "Error loading media";
                    await ShowErrorDialog("Playback Error", $"An error occurred while loading the media: {ex.Message}");
                }
            }
        }

        private async void OnVolumeSliderValueChanged(object sender, Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e)
        {
            if (_videoPlayerService != null)
            {
                await _videoPlayerService.SetVolumeAsync((int)e.NewValue);
            }
        }

        // Video Player Event Callbacks
        private void OnVideoPlayerError(object? sender, string errorMessage)
        {
            DispatcherQueue.TryEnqueue(async () =>
            {
                await LogService.LogErrorAsync("VideoPlayerService error", new InvalidOperationException(errorMessage), new Dictionary<string, string>
                {
                    { "currentUrl", _videoPlayerService?.CurrentUrl ?? string.Empty },
                    { "isPlaying", (_videoPlayerService?.IsPlaying ?? false).ToString() }
                });
                await ShowErrorDialog("Video Player Error", errorMessage);
            });
        }

        private void OnVideoPlayerPlaybackStarted(object? sender, EventArgs e)
        {
            DispatcherQueue.TryEnqueue(() =>
            {
                // Update UI state for playing
                PlayButton.IsEnabled = false;
                PauseButton.IsEnabled = true;
                StopButton.IsEnabled = true;
            });
        }

        private void OnVideoPlayerPlaybackPaused(object? sender, EventArgs e)
        {
            DispatcherQueue.TryEnqueue(() =>
            {
                // Update UI state for paused
                PlayButton.IsEnabled = true;
                PauseButton.IsEnabled = false;
                StopButton.IsEnabled = true;
            });
        }

        private void OnVideoPlayerPlaybackStopped(object? sender, EventArgs e)
        {
            DispatcherQueue.TryEnqueue(() =>
            {
                // Update UI state for stopped
                PlayButton.IsEnabled = true;
                PauseButton.IsEnabled = false;
                StopButton.IsEnabled = false;
            });
        }
    }

    internal static class NativeMethods
    {
        internal const int WS_CHILD = 0x40000000;
        internal const int WS_VISIBLE = 0x10000000;
        internal const int WS_CLIPSIBLINGS = 0x04000000;
        internal const int WS_CLIPCHILDREN = 0x02000000;
        internal const int WS_EX_NOREDIRECTIONBITMAP = 0x00200000;
        internal const uint SWP_NOZORDER = 0x0004;
        internal const uint SWP_NOACTIVATE = 0x0010;
        internal const uint SWP_SHOWWINDOW = 0x0040;

        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        internal static extern IntPtr CreateWindowExW(
            int dwExStyle,
            string lpClassName,
            string lpWindowName,
            int dwStyle,
            int X,
            int Y,
            int nWidth,
            int nHeight,
            IntPtr hWndParent,
            IntPtr hMenu,
            IntPtr hInstance,
            IntPtr lpParam);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool SetWindowPos(
            IntPtr hWnd,
            IntPtr hWndInsertAfter,
            int X,
            int Y,
            int cx,
            int cy,
            uint uFlags);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool DestroyWindow(IntPtr hWnd);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        internal static extern IntPtr GetModuleHandle(string? lpModuleName);
    }

    public class ChannelGroupOption
    {
        public string Name { get; set; } = string.Empty;
        public string Value { get; set; } = string.Empty;

        public override string ToString() => Name;
    }
}
