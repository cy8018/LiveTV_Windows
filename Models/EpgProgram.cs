namespace IPTVPlayer.Models;

/// <summary>
/// Represents a single EPG (Electronic Program Guide) program entry
/// </summary>
public class EpgProgram
{
    public string ChannelId { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string? Description { get; set; }
    public DateTime Start { get; set; }
    public DateTime Stop { get; set; }
    public string? Category { get; set; }

    /// <summary>
    /// Whether this program is currently airing
    /// </summary>
    public bool IsCurrent => DateTime.Now >= Start && DateTime.Now < Stop;

    /// <summary>
    /// Whether this program is in the future
    /// </summary>
    public bool IsFuture => DateTime.Now < Start;

    /// <summary>
    /// Formatted time range (e.g., "14:00 - 15:30")
    /// </summary>
    public string TimeRange => $"{Start:HH:mm} - {Stop:HH:mm}";

    /// <summary>
    /// Formatted start time (e.g., "14:00")
    /// </summary>
    public string StartTime => Start.ToString("HH:mm");

    /// <summary>
    /// Progress percentage (0.0 to 1.0) for the current program
    /// </summary>
    public double Progress
    {
        get
        {
            if (!IsCurrent) return 0;
            var total = (Stop - Start).TotalSeconds;
            if (total <= 0) return 0;
            var elapsed = (DateTime.Now - Start).TotalSeconds;
            return Math.Min(1.0, Math.Max(0, elapsed / total));
        }
    }
}
