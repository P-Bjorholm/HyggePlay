namespace HyggePlay.Models;

public sealed class UserProfile
{
    public int Id { get; set; }
    public string DisplayName { get; set; } = string.Empty;
    public string ServerUrl { get; set; } = string.Empty;
    public string Username { get; set; } = string.Empty;
    public string Password { get; set; } = string.Empty;
}
