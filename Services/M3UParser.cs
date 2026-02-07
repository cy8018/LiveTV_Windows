using System.IO;
using System.Net.Http;
using System.Text.RegularExpressions;
using IPTVPlayer.Models;

namespace IPTVPlayer.Services;

/// <summary>
/// Parses M3U and M3U8 playlist files
/// </summary>
public partial class M3UParser
{
    private static readonly Regex ExtInfRegex = GenerateExtInfRegex();
    private static readonly Regex AttributeRegex = GenerateAttributeRegex();

    /// <summary>
    /// Parse an M3U playlist from a file path
    /// </summary>
    public async Task<List<Channel>> ParseFileAsync(string filePath)
    {
        if (!File.Exists(filePath))
            throw new FileNotFoundException("Playlist file not found", filePath);

        var content = await File.ReadAllTextAsync(filePath);
        return Parse(content);
    }

    /// <summary>
    /// Parse an M3U playlist from a URL
    /// </summary>
    public async Task<List<Channel>> ParseUrlAsync(string url)
    {
        using var httpClient = new HttpClient();
        httpClient.Timeout = TimeSpan.FromSeconds(30);
        var content = await httpClient.GetStringAsync(url);
        return Parse(content);
    }

    /// <summary>
    /// Parse M3U content string
    /// </summary>
    public List<Channel> Parse(string content)
    {
        var rawChannels = new List<Channel>();
        var lines = content.Split('\n', StringSplitOptions.RemoveEmptyEntries)
                          .Select(l => l.Trim())
                          .ToArray();

        if (lines.Length == 0)
            return rawChannels;

        // Skip #EXTM3U header if present
        int startIndex = lines[0].StartsWith("#EXTM3U", StringComparison.OrdinalIgnoreCase) ? 1 : 0;

        for (int i = startIndex; i < lines.Length; i++)
        {
            var line = lines[i];

            if (line.StartsWith("#EXTINF:", StringComparison.OrdinalIgnoreCase))
            {
                var channel = ParseExtInf(line);

                // Find the URL (next non-comment line)
                for (int j = i + 1; j < lines.Length; j++)
                {
                    var nextLine = lines[j];
                    if (!nextLine.StartsWith("#"))
                    {
                        // Add as a source
                        channel.Sources.Add(new ChannelSource { Url = nextLine });
                        i = j; // Skip to URL line
                        break;
                    }
                    // Handle additional tags like #EXTGRP
                    else if (nextLine.StartsWith("#EXTGRP:", StringComparison.OrdinalIgnoreCase))
                    {
                        channel.Group ??= nextLine.Substring(8).Trim();
                    }
                }

                if (channel.Sources.Count > 0)
                {
                    rawChannels.Add(channel);
                }
            }
        }

        // Combine channels with the same name
        return CombineChannels(rawChannels);
    }

    /// <summary>
    /// Combine channels with the same name into single entries with multiple sources
    /// </summary>
    private List<Channel> CombineChannels(List<Channel> rawChannels)
    {
        var combinedDict = new Dictionary<string, Channel>(StringComparer.OrdinalIgnoreCase);
        
        foreach (var channel in rawChannels)
        {
            if (combinedDict.TryGetValue(channel.Name, out var existing))
            {
                // Add this channel's sources to existing channel
                foreach (var source in channel.Sources)
                {
                    // Avoid duplicate URLs
                    if (!existing.Sources.Any(s => s.Url.Equals(source.Url, StringComparison.OrdinalIgnoreCase)))
                    {
                        existing.Sources.Add(source);
                    }
                }
                
                // Update logo if existing doesn't have one
                existing.Logo ??= channel.Logo;
                existing.Group ??= channel.Group;
                existing.TvgId ??= channel.TvgId;
            }
            else
            {
                // New channel
                combinedDict[channel.Name] = channel;
            }
        }
        
        // Assign IDs and return as list
        var result = combinedDict.Values.ToList();
        for (int i = 0; i < result.Count; i++)
        {
            result[i].Id = i + 1;
        }
        
        return result;
    }

    /// <summary>
    /// Group channels by their group attribute
    /// </summary>
    public List<ChannelGroup> GroupChannels(List<Channel> channels)
    {
        var groups = channels
            .GroupBy(c => c.Group ?? "Ungrouped")
            .Select(g => new ChannelGroup
            {
                Name = g.Key,
                Channels = g.ToList()
            })
            .OrderBy(g => g.Name == "Ungrouped" ? 1 : 0)
            .ThenBy(g => g.Name)
            .ToList();

        return groups;
    }

    private Channel ParseExtInf(string line)
    {
        var channel = new Channel();

        // Remove #EXTINF: prefix
        var content = line.Substring(8);

        // Extract attributes from the line
        var attributes = ExtractAttributes(content);

        foreach (var attr in attributes)
        {
            switch (attr.Key.ToLowerInvariant())
            {
                case "tvg-id":
                    channel.TvgId = attr.Value;
                    break;
                case "tvg-name":
                    channel.TvgName = attr.Value;
                    break;
                case "tvg-logo":
                    channel.Logo = attr.Value;
                    break;
                case "group-title":
                    channel.Group = attr.Value;
                    break;
                case "tvg-language":
                    channel.Language = attr.Value;
                    break;
                case "tvg-country":
                    channel.Country = attr.Value;
                    break;
                case "hidden":
                    channel.IsHidden = attr.Value.Equals("true", StringComparison.OrdinalIgnoreCase) ||
                                      attr.Value == "1";
                    break;
                default:
                    channel.ExtendedAttributes[attr.Key] = attr.Value;
                    break;
            }
        }

        // Extract channel name (text after the last comma)
        var match = ExtInfRegex.Match(content);
        if (match.Success)
        {
            channel.Name = match.Groups["name"].Value.Trim();
        }
        else
        {
            // Fallback: try to get name after comma
            var lastComma = content.LastIndexOf(',');
            if (lastComma >= 0)
            {
                channel.Name = content.Substring(lastComma + 1).Trim();
            }
        }

        // Use tvg-name as fallback for name
        if (string.IsNullOrEmpty(channel.Name) && !string.IsNullOrEmpty(channel.TvgName))
        {
            channel.Name = channel.TvgName;
        }

        return channel;
    }

    private Dictionary<string, string> ExtractAttributes(string content)
    {
        var attributes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var matches = AttributeRegex.Matches(content);

        foreach (Match match in matches)
        {
            var key = match.Groups["key"].Value;
            var value = match.Groups["value"].Value;
            attributes[key] = value;
        }

        return attributes;
    }

    [GeneratedRegex(@",\s*(?<name>[^,]+)$")]
    private static partial Regex GenerateExtInfRegex();

    [GeneratedRegex(@"(?<key>[\w-]+)=""(?<value>[^""]*)""")]
    private static partial Regex GenerateAttributeRegex();
}
