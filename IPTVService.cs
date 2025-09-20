using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;

namespace HyggePlay;

/// <summary>
/// Barebones Xtream Codes IPTV service
/// </summary>
public class IPTVService
{
    private readonly HttpClient _httpClient;
    private string? _serverUrl;
    private string? _username;
    private string? _password;
    
    public IPTVService()
    {
        _httpClient = new HttpClient();
        _httpClient.Timeout = TimeSpan.FromSeconds(30);
    }
    
    /// <summary>
    /// Authenticate with Xtream Codes server
    /// </summary>
    public async Task<AuthenticationResult> AuthenticateAsync(string serverUrl, string username, string password)
    {
        try
        {
            _serverUrl = NormalizeServerUrl(serverUrl);
            _username = username;
            _password = password;
            
            // Xtream Codes authentication endpoint
            string authUrl = $"{_serverUrl}/player_api.php?username={username}&password={password}";
            
            HttpResponseMessage response = await _httpClient.GetAsync(authUrl);
            
            if (response.IsSuccessStatusCode)
            {
                string responseContent = await response.Content.ReadAsStringAsync();
                
                using JsonDocument doc = JsonDocument.Parse(responseContent);
                JsonElement root = doc.RootElement;
                
                // Check if we got user_info (successful authentication)
                if (root.TryGetProperty("user_info", out JsonElement userInfo))
                {
                    // Check if user is active
                    if (userInfo.TryGetProperty("status", out JsonElement status))
                    {
                        string statusValue = status.GetString() ?? "";
                        if (statusValue.Equals("Active", StringComparison.OrdinalIgnoreCase))
                        {
                            return new AuthenticationResult
                            {
                                IsSuccess = true,
                                UserInfo = ParseUserInfo(userInfo)
                            };
                        }
                        else
                        {
                            return new AuthenticationResult
                            {
                                IsSuccess = false,
                                ErrorMessage = $"Account status: {statusValue}"
                            };
                        }
                    }
                }
                
                // Check for error message
                if (root.TryGetProperty("message", out JsonElement message))
                {
                    return new AuthenticationResult
                    {
                        IsSuccess = false,
                        ErrorMessage = message.GetString() ?? "Authentication failed"
                    };
                }
            }
            
            return new AuthenticationResult 
            { 
                IsSuccess = false, 
                ErrorMessage = "Invalid credentials or server error" 
            };
        }
        catch (Exception ex)
        {
            return new AuthenticationResult 
            { 
                IsSuccess = false, 
                ErrorMessage = $"Connection error: {ex.Message}" 
            };
        }
    }
    
    private UserInfo ParseUserInfo(JsonElement userInfo)
    {
        return new UserInfo
        {
            Username = userInfo.TryGetProperty("username", out JsonElement username) ? username.GetString() : null,
            Status = userInfo.TryGetProperty("status", out JsonElement status) ? status.GetString() : null,
            ExpirationDate = userInfo.TryGetProperty("exp_date", out JsonElement expDate) ? expDate.GetString() : null,
            MaxConnections = userInfo.TryGetProperty("max_connections", out JsonElement maxConn) ? maxConn.GetString() : "0",
            ActiveConnections = userInfo.TryGetProperty("active_cons", out JsonElement activeCons) ? activeCons.GetString() : "0"
        };
    }
    
    private string NormalizeServerUrl(string serverUrl)
    {
        serverUrl = serverUrl.Trim();
        
        if (!serverUrl.StartsWith("http://") && !serverUrl.StartsWith("https://"))
        {
            serverUrl = "http://" + serverUrl;
        }
        
        return serverUrl.TrimEnd('/');
    }
    
    /// <summary>
    /// Get M3U playlist URL
    /// </summary>
    public string GetM3UPlaylistUrl()
    {
        if (string.IsNullOrEmpty(_serverUrl) || string.IsNullOrEmpty(_username) || string.IsNullOrEmpty(_password))
            return string.Empty;
            
        return $"{_serverUrl}/get.php?username={_username}&password={_password}&type=m3u_plus&output=ts";
    }
    
    /// <summary>
    /// Get EPG URL
    /// </summary>
    public string GetEPGUrl()
    {
        if (string.IsNullOrEmpty(_serverUrl) || string.IsNullOrEmpty(_username) || string.IsNullOrEmpty(_password))
            return string.Empty;
            
        return $"{_serverUrl}/xmltv.php?username={_username}&password={_password}";
    }
    
    public void Dispose()
    {
        _httpClient?.Dispose();
    }
}

/// <summary>
/// Authentication result
/// </summary>
public class AuthenticationResult
{
    public bool IsSuccess { get; set; }
    public string? ErrorMessage { get; set; }
    public UserInfo? UserInfo { get; set; }
}

/// <summary>
/// User information from Xtream Codes server
/// </summary>
public class UserInfo
{
    public string? Username { get; set; }
    public string? Status { get; set; }
    public string? ExpirationDate { get; set; }
    public string? MaxConnections { get; set; }
    public string? ActiveConnections { get; set; }
}