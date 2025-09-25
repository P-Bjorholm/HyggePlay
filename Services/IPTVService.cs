using HyggePlay.Models;
using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;

namespace HyggePlay.Services;

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
    
    private void EnsureAuthenticated()
    {
        if (string.IsNullOrEmpty(_serverUrl) || string.IsNullOrEmpty(_username) || string.IsNullOrEmpty(_password))
        {
            throw new InvalidOperationException("The IPTV service has not been authenticated yet.");
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
    
    public async Task<List<ChannelGroupInfo>> GetLiveCategoriesAsync()
    {
        EnsureAuthenticated();

        List<ChannelGroupInfo> groups = new();
        try
        {
            string categoriesUrl = $"{_serverUrl}/player_api.php?username={_username}&password={_password}&action=get_live_categories";
            HttpResponseMessage response = await _httpClient.GetAsync(categoriesUrl);
            if (!response.IsSuccessStatusCode)
            {
                return groups;
            }

            string json = await response.Content.ReadAsStringAsync();
            using JsonDocument doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement element in doc.RootElement.EnumerateArray())
                {
                    string? id = element.TryGetProperty("category_id", out JsonElement idProp) ? idProp.GetString() : null;
                    if (string.IsNullOrEmpty(id))
                    {
                        continue;
                    }

                    string name = element.TryGetProperty("category_name", out JsonElement nameProp) ? nameProp.GetString() ?? string.Empty : string.Empty;
                    if (string.IsNullOrEmpty(name))
                    {
                        name = $"Group {id}";
                    }

                    groups.Add(new ChannelGroupInfo
                    {
                        GroupId = id,
                        Name = name
                    });
                }
            }
        }
        catch
        {
            // Swallow and return empty collection. Caller will handle surface errors separately.
        }

        return groups;
    }

    public async Task<List<ChannelInfo>> GetLiveChannelsAsync()
    {
        EnsureAuthenticated();

        List<ChannelInfo> channels = new();

        try
        {
            string streamsUrl = $"{_serverUrl}/player_api.php?username={_username}&password={_password}&action=get_live_streams";
            HttpResponseMessage response = await _httpClient.GetAsync(streamsUrl);
            if (!response.IsSuccessStatusCode)
            {
                return channels;
            }

            string json = await response.Content.ReadAsStringAsync();
            using JsonDocument doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement element in doc.RootElement.EnumerateArray())
                {
                    string groupId = element.TryGetProperty("category_id", out JsonElement categoryProp) ? categoryProp.GetString() ?? string.Empty : string.Empty;
                    if (string.IsNullOrEmpty(groupId))
                    {
                        groupId = "0";
                    }

                    int channelId = 0;
                    if (element.TryGetProperty("stream_id", out JsonElement streamIdProp))
                    {
                        if (streamIdProp.ValueKind == JsonValueKind.Number && streamIdProp.TryGetInt32(out int parsedId))
                        {
                            channelId = parsedId;
                        }
                        else if (streamIdProp.ValueKind == JsonValueKind.String && int.TryParse(streamIdProp.GetString(), out int parsedIdFromString))
                        {
                            channelId = parsedIdFromString;
                        }
                    }

                    if (channelId == 0)
                    {
                        continue;
                    }

                    string name = element.TryGetProperty("name", out JsonElement nameProp) ? nameProp.GetString() ?? $"Channel {channelId}" : $"Channel {channelId}";
                    string? streamIcon = element.TryGetProperty("stream_icon", out JsonElement iconProp) ? iconProp.GetString() : null;
                    string streamUrl = BuildStreamUrl(channelId);

                    channels.Add(new ChannelInfo
                    {
                        ChannelId = channelId,
                        Name = name,
                        GroupId = groupId,
                        StreamIcon = string.IsNullOrWhiteSpace(streamIcon) ? null : streamIcon,
                        StreamUrl = streamUrl
                    });
                }
            }
        }
        catch
        {
            // Ignore exceptions so UI can react accordingly.
        }

        return channels;
    }

    public string BuildStreamUrl(int streamId)
    {
        EnsureAuthenticated();
        return $"{_serverUrl}/live/{_username}/{_password}/{streamId}.ts";
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