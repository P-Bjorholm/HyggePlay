namespace HyggePlay.Models;

public sealed class ChannelListItem
{
    public int ChannelId { get; init; }
    public string Name { get; init; } = string.Empty;
    public string GroupName { get; init; } = string.Empty;
    public string? StreamUrl { get; init; }
}
