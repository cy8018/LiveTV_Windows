namespace IPTVPlayer.Models;

/// <summary>
/// Represents a group of channels
/// </summary>
public class ChannelGroup
{
    public string Name { get; set; } = string.Empty;
    public List<Channel> Channels { get; set; } = new();
    public bool IsExpanded { get; set; } = true;

    public override string ToString() => $"{Name} ({Channels.Count})";
}
