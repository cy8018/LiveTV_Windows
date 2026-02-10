using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace IPTVPlayer.Models;

/// <summary>
/// Represents an IPTV channel source/URL
/// </summary>
public class ChannelSource
{
    public string Url { get; set; } = string.Empty;
    public string? Quality { get; set; }  // e.g., "HD", "SD", "4K"
    public bool IsWorking { get; set; } = true;
    
    public override string ToString() => Quality ?? Url;
}

/// <summary>
/// Represents an IPTV channel from an M3U playlist with multiple sources
/// </summary>
public class Channel : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;
    
    protected void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }

    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
    
    /// <summary>
    /// Primary URL (first source)
    /// </summary>
    public string Url => Sources.Count > 0 ? Sources[CurrentSourceIndex].Url : string.Empty;
    
    /// <summary>
    /// All available sources for this channel
    /// </summary>
    public List<ChannelSource> Sources { get; set; } = new();
    
    private int _currentSourceIndex = 0;
    /// <summary>
    /// Currently selected source index
    /// </summary>
    public int CurrentSourceIndex 
    { 
        get => _currentSourceIndex;
        set
        {
            if (_currentSourceIndex != value)
            {
                _currentSourceIndex = value;
                OnPropertyChanged();
            }
        }
    }
    
    /// <summary>
    /// Number of available sources
    /// </summary>
    public int SourceCount => Sources.Count;
    
    /// <summary>
    /// Display text for source count (e.g., "3 sources")
    /// </summary>
    public string SourceCountText => Sources.Count > 1 ? $"{Sources.Count} sources" : "";
    
    public string? Logo { get; set; }
    public string? Group { get; set; }
    public string? TvgId { get; set; }
    public string? TvgName { get; set; }
    public string? Language { get; set; }
    public string? Country { get; set; }
    public string? TvgUrl { get; set; }
    public Dictionary<string, string> ExtendedAttributes { get; set; } = new();

    private bool _isHidden;
    /// <summary>
    /// Whether this channel is hidden from the list
    /// </summary>
    public bool IsHidden
    {
        get => _isHidden;
        set
        {
            if (_isHidden != value)
            {
                _isHidden = value;
                OnPropertyChanged();
            }
        }
    }

    private int _sortOrder;
    /// <summary>
    /// Custom sort order for the channel (lower = higher in list)
    /// </summary>
    public int SortOrder
    {
        get => _sortOrder;
        set
        {
            if (_sortOrder != value)
            {
                _sortOrder = value;
                OnPropertyChanged();
            }
        }
    }

    /// <summary>
    /// Switch to the next available source, wrapping around to the first
    /// </summary>
    /// <returns>True if there are multiple sources to cycle through</returns>
    public bool TryNextSource()
    {
        if (Sources.Count <= 1) return false;
        CurrentSourceIndex = (CurrentSourceIndex + 1) % Sources.Count;
        return true;
    }
    
    /// <summary>
    /// Reset to the first source
    /// </summary>
    public void ResetSource()
    {
        CurrentSourceIndex = 0;
    }

    public override string ToString() => Name;
}
