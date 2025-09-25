using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using HyggePlay.Models;
using HyggePlay.Services;

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

                // First try to get channels from database
                var savedChannels = await _databaseService.SearchChannelsAsync(user.Id, null, null, 1000);
                
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
                UpdateChannelGroups();
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

        private void UpdateChannelGroups()
        {
            _channelGroups.Clear();
            _channelGroups.Add(new ChannelGroupOption { Name = "All Groups", Value = "" });

            var groups = _channels
                .Where(c => !string.IsNullOrEmpty(c.GroupId))
                .GroupBy(c => c.GroupId)
                .Select(g => new ChannelGroupOption { Name = $"Group {g.Key} ({g.Count()})", Value = g.Key })
                .OrderBy(g => g.Value);

            foreach (var group in groups)
            {
                _channelGroups.Add(group);
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
                    c.GroupId?.ToLower().Contains(searchText) == true);
            }

            foreach (var channel in filtered.Take(100)) // Limit results for performance
            {
                _filteredChannels.Add(channel);
            }
        }

        private async Task ShowErrorDialog(string title, string message)
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
    }

    public class ChannelGroupOption
    {
        public string Name { get; set; } = string.Empty;
        public string Value { get; set; } = string.Empty;
    }
}
