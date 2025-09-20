using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Navigation;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices.WindowsRuntime;
using Windows.Foundation;
using Windows.Foundation.Collections;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

namespace HyggePlay;

/// <summary>
/// An empty window that can be used on its own or navigated to within a Frame.
/// </summary>
public sealed partial class MainWindow : Window
{
    private readonly IPTVService _iptvService;
    
    public MainWindow()
    {
        this.InitializeComponent();
        
        // Initialize IPTV service
        _iptvService = new IPTVService();
        
        // Set window properties for IPTV player
        this.Title = "HyggePlay IPTV Player";
        
        // Set minimum window size
        this.AppWindow.Resize(new Windows.Graphics.SizeInt32(800, 600));
        
        // Center the window on screen
        var displayArea = Microsoft.UI.Windowing.DisplayArea.GetFromWindowId(this.AppWindow.Id, Microsoft.UI.Windowing.DisplayAreaFallback.Nearest);
        if (displayArea != null)
        {
            var centeredPosition = new Windows.Graphics.PointInt32(
                (displayArea.WorkArea.Width - 800) / 2,
                (displayArea.WorkArea.Height - 600) / 2
            );
            this.AppWindow.Move(centeredPosition);
        }
        
        // Wire up the login button click event
        LoginButton.Click += LoginButton_Click;
    }
    
    private async void LoginButton_Click(object sender, RoutedEventArgs e)
    {
        // Get the login credentials from the form
        string serverUrl = ServerUrlTextBox.Text.Trim();
        string username = UsernameTextBox.Text.Trim();
        string password = PasswordBox.Password;
        
        // Validate input
        if (string.IsNullOrEmpty(serverUrl) || string.IsNullOrEmpty(username) || string.IsNullOrEmpty(password))
        {
            await ShowErrorDialog("Please fill in all fields.");
            return;
        }
        
        // Disable login button during authentication
        LoginButton.IsEnabled = false;
        LoginButton.Content = "Authenticating...";
        
        try
        {
            // Attempt to authenticate with Xtream Codes server
            AuthenticationResult result = await _iptvService.AuthenticateAsync(serverUrl, username, password);
            
            if (result.IsSuccess)
            {
                string successMessage = "✅ Xtream Codes Authentication Successful!";
                
                if (result.UserInfo != null)
                {
                    successMessage += $"\n\n👤 Username: {result.UserInfo.Username}";
                    successMessage += $"\n📊 Status: {result.UserInfo.Status}";
                    if (!string.IsNullOrEmpty(result.UserInfo.ExpirationDate))
                        successMessage += $"\n📅 Expires: {result.UserInfo.ExpirationDate}";
                    if (!string.IsNullOrEmpty(result.UserInfo.MaxConnections))
                        successMessage += $"\n🔗 Max Connections: {result.UserInfo.MaxConnections}";
                    if (!string.IsNullOrEmpty(result.UserInfo.ActiveConnections))
                        successMessage += $"\n🔴 Active: {result.UserInfo.ActiveConnections}";
                }
                
                // Add useful URLs
                successMessage += $"\n\n📺 M3U Playlist:\n{_iptvService.GetM3UPlaylistUrl()}";
                successMessage += $"\n\n📋 EPG Guide:\n{_iptvService.GetEPGUrl()}";
                
                await ShowSuccessDialog(successMessage);
                
                // TODO: Navigate to main IPTV interface
            }
            else
            {
                await ShowErrorDialog(result.ErrorMessage ?? "Authentication failed. Please check your credentials and server URL.");
            }
        }
        catch (Exception ex)
        {
            await ShowErrorDialog($"Unexpected error: {ex.Message}");
        }
        finally
        {
            // Re-enable login button
            LoginButton.IsEnabled = true;
            LoginButton.Content = "Login";
        }
    }
    private async Task ShowErrorDialog(string message)
    {
        ContentDialog dialog = new ContentDialog()
        {
            Title = "Error",
            Content = message,
            CloseButtonText = "OK",
            XamlRoot = this.Content.XamlRoot
        };
        
        await dialog.ShowAsync();
    }
    
    private async Task ShowSuccessDialog(string message)
    {
        ContentDialog dialog = new ContentDialog()
        {
            Title = "Success",
            Content = message,
            CloseButtonText = "OK",
            XamlRoot = this.Content.XamlRoot
        };
        
        await dialog.ShowAsync();
    }
}