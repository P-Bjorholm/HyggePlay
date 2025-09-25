namespace HyggePlay.Models;

public sealed class ChannelInfo
{
    public int ChannelId { get; set; }
    public string Name { get; set; } = string.Empty;
    public string GroupId { get; set; } = string.Empty;
    public string? StreamIcon { get; set; }
    public string? StreamUrl { get; set; }
}
