using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Xml;
using IPTVPlayer.Models;

namespace IPTVPlayer.Services;

/// <summary>
/// Service for downloading, parsing, and querying EPG (Electronic Program Guide) data
/// from XMLTV format files (optionally gzip-compressed).
/// </summary>
public class EpgService
{
    private readonly HttpClient _httpClient;
    private readonly Dictionary<string, List<EpgProgram>> _programsByChannel = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _loadedUrls = new(StringComparer.OrdinalIgnoreCase);
    
    // Maps display-names / aliases to XMLTV channel IDs for flexible matching
    private readonly Dictionary<string, string> _displayNameToChannelId = new(StringComparer.OrdinalIgnoreCase);

    public bool IsLoaded => _programsByChannel.Count > 0;

    public EpgService()
    {
        _httpClient = new HttpClient();
        _httpClient.Timeout = TimeSpan.FromSeconds(60);
        _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("IPTVPlayer/1.0");
    }

    /// <summary>
    /// Download and parse EPG data from one or more XMLTV URLs
    /// </summary>
    public async Task LoadEpgAsync(IEnumerable<string> urls, CancellationToken cancellationToken = default)
    {
        // Materialize the list to avoid "Collection was modified" if the source list changes
        var urlList = urls.Distinct(StringComparer.OrdinalIgnoreCase).ToList();

        foreach (var url in urlList)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (_loadedUrls.Contains(url)) continue;

            try
            {
                Debug.WriteLine($"[EPG] Loading EPG from: {url}");
                var xmlContent = await DownloadAndDecompressAsync(url, cancellationToken);
                Debug.WriteLine($"[EPG] Downloaded {xmlContent.Length} chars from {url}");
                ParseXmlTv(xmlContent);
                _loadedUrls.Add(url);
                Debug.WriteLine($"[EPG] Successfully loaded EPG. Total channels with data: {_programsByChannel.Count}");
            }
            catch (OperationCanceledException)
            {
                Debug.WriteLine("[EPG] EPG loading cancelled");
                throw;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[EPG] Failed to load EPG from {url}: {ex.Message}");
            }
        }
    }

    /// <summary>
    /// Download content from URL, automatically decompressing if gzipped
    /// </summary>
    private async Task<string> DownloadAndDecompressAsync(string url, CancellationToken cancellationToken = default)
    {
        var response = await _httpClient.GetAsync(url, cancellationToken);
        response.EnsureSuccessStatusCode();

        var bytes = await response.Content.ReadAsByteArrayAsync();

        // Detect gzip by URL extension or magic bytes (1F 8B)
        if (url.EndsWith(".gz", StringComparison.OrdinalIgnoreCase) ||
            (bytes.Length >= 2 && bytes[0] == 0x1F && bytes[1] == 0x8B))
        {
            using var compressedStream = new MemoryStream(bytes);
            using var gzipStream = new GZipStream(compressedStream, CompressionMode.Decompress);
            using var reader = new StreamReader(gzipStream);
            return await reader.ReadToEndAsync();
        }

        return System.Text.Encoding.UTF8.GetString(bytes);
    }

    /// <summary>
    /// Parse XMLTV format content into program entries
    /// </summary>
    private void ParseXmlTv(string xmlContent)
    {
        try
        {
            Debug.WriteLine($"[EPG] Parsing XMLTV content, length: {xmlContent.Length} chars");

            var settings = new XmlReaderSettings
            {
                DtdProcessing = DtdProcessing.Ignore,
                IgnoreComments = true,
                IgnoreWhitespace = true
            };

            int channelCount = 0;
            int programmeCount = 0;
            int beforeChannels = _programsByChannel.Count;

            using var stringReader = new StringReader(xmlContent);
            using var reader = XmlReader.Create(stringReader, settings);

            while (reader.Read())
            {
                if (reader.NodeType == XmlNodeType.Element)
                {
                    if (reader.Name == "channel")
                    {
                        ParseChannel(reader);
                        channelCount++;
                    }
                    else if (reader.Name == "programme")
                    {
                        ParseProgramme(reader);
                        programmeCount++;
                    }
                }
            }

            // Sort each channel's programs by start time
            foreach (var programs in _programsByChannel.Values)
            {
                programs.Sort((a, b) => a.Start.CompareTo(b.Start));
            }

            Debug.WriteLine($"[EPG] Parsed {channelCount} <channel> elements, {programmeCount} <programme> elements. " +
                $"Channels in dict: {beforeChannels} -> {_programsByChannel.Count}");

            // Dump first few channel IDs for diagnosis
            if (_programsByChannel.Count > 0 && _programsByChannel.Count != beforeChannels)
            {
                var sampleIds = _programsByChannel.Keys.Take(10);
                Debug.WriteLine($"[EPG] Sample channel IDs: {string.Join(", ", sampleIds)}");
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[EPG] Error parsing XMLTV: {ex.Message}");
        }
    }

    /// <summary>
    /// Parse a &lt;channel&gt; element to extract display-names for flexible matching
    /// </summary>
    private void ParseChannel(XmlReader reader)
    {
        var channelId = reader.GetAttribute("id");
        if (string.IsNullOrEmpty(channelId)) return;

        // Use ReadSubtree to prevent reading past </channel> into sibling elements
        using var subReader = reader.ReadSubtree();
        subReader.Read(); // Move into the <channel> element

        while (subReader.Read())
        {
            if (subReader.NodeType == XmlNodeType.Element && subReader.Name == "display-name")
            {
                var displayName = subReader.ReadElementContentAsString().Trim();
                if (!string.IsNullOrEmpty(displayName))
                {
                    _displayNameToChannelId.TryAdd(displayName, channelId);

                    // Also store the first token (before space) as an alias
                    // e.g. "CCTV-13 新闻" → also register "CCTV-13"
                    var spaceIdx = displayName.IndexOf(' ');
                    if (spaceIdx > 0)
                    {
                        var prefix = displayName.Substring(0, spaceIdx);
                        _displayNameToChannelId.TryAdd(prefix, channelId);
                    }
                }
            }
        }
    }

    private void ParseProgramme(XmlReader reader)
    {
        var channelId = reader.GetAttribute("channel");
        var startStr = reader.GetAttribute("start");
        var stopStr = reader.GetAttribute("stop");

        if (string.IsNullOrEmpty(channelId) || string.IsNullOrEmpty(startStr) || string.IsNullOrEmpty(stopStr))
            return;

        var start = ParseXmlTvDateTime(startStr);
        var stop = ParseXmlTvDateTime(stopStr);

        if (start == DateTime.MinValue || stop == DateTime.MinValue)
            return;

        string? title = null;
        string? description = null;
        string? category = null;

        // Use ReadSubtree to prevent reading past </programme> into sibling elements
        using var subReader = reader.ReadSubtree();
        subReader.Read(); // Move into the <programme> element

        while (subReader.Read())
        {
            if (subReader.NodeType == XmlNodeType.Element)
            {
                switch (subReader.Name)
                {
                    case "title":
                        title = subReader.ReadElementContentAsString();
                        break;
                    case "desc":
                        description = subReader.ReadElementContentAsString();
                        break;
                    case "category":
                        category = subReader.ReadElementContentAsString();
                        break;
                }
            }
        }

        var epgProgram = new EpgProgram
        {
            ChannelId = channelId,
            Title = title ?? "Unknown",
            Description = description,
            Start = start,
            Stop = stop,
            Category = category
        };

        if (!_programsByChannel.TryGetValue(channelId, out var programs))
        {
            programs = new List<EpgProgram>();
            _programsByChannel[channelId] = programs;
        }

        programs.Add(epgProgram);
    }

    /// <summary>
    /// Parse XMLTV datetime format: "20230101120000 +0800" or "20230101120000"
    /// </summary>
    private static DateTime ParseXmlTvDateTime(string dateTimeStr)
    {
        try
        {
            dateTimeStr = dateTimeStr.Trim();

            // Extract the numeric datetime part (first 14 chars: yyyyMMddHHmmss)
            if (dateTimeStr.Length < 14) return DateTime.MinValue;

            var dtPart = dateTimeStr.Substring(0, 14);

            if (!DateTime.TryParseExact(dtPart, "yyyyMMddHHmmss",
                CultureInfo.InvariantCulture, DateTimeStyles.None, out var dt))
            {
                return DateTime.MinValue;
            }

            // Check for timezone offset (e.g., "+0800", "-0500")
            var remaining = dateTimeStr.Substring(14).Trim();
            if (remaining.Length >= 5 && (remaining[0] == '+' || remaining[0] == '-'))
            {
                var sign = remaining[0] == '+' ? 1 : -1;
                var tzStr = remaining.Substring(1).TrimEnd();

                if (tzStr.Length >= 4 &&
                    int.TryParse(tzStr.Substring(0, 2), out var hours) &&
                    int.TryParse(tzStr.Substring(2, 2), out var minutes))
                {
                    var offset = new TimeSpan(sign * hours, sign * minutes, 0);
                    var dto = new DateTimeOffset(dt, offset);
                    return dto.LocalDateTime;
                }
            }

            // No timezone - treat as local time
            return dt;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[EPG] Error parsing datetime '{dateTimeStr}': {ex.Message}");
            return DateTime.MinValue;
        }
    }

    /// <summary>
    /// Resolve the XMLTV channel ID by trying multiple match strategies:
    /// 1. Direct tvg-id match
    /// 2. tvg-id matches a display-name
    /// 3. Channel name matches a display-name
    /// 4. Channel name matches an XMLTV channel ID
    /// </summary>
    private string? ResolveChannelId(string? tvgId, string? channelName)
    {
        // 1. Direct match on tvg-id
        if (!string.IsNullOrEmpty(tvgId) && _programsByChannel.ContainsKey(tvgId))
            return tvgId;

        // 2. tvg-id matches a display-name (or its prefix) in the XMLTV
        if (!string.IsNullOrEmpty(tvgId) && _displayNameToChannelId.TryGetValue(tvgId, out var resolved))
        {
            if (_programsByChannel.ContainsKey(resolved))
                return resolved;
        }

        // 3. Channel name matches a display-name (or its prefix)
        if (!string.IsNullOrEmpty(channelName) && _displayNameToChannelId.TryGetValue(channelName, out var resolvedByName))
        {
            if (_programsByChannel.ContainsKey(resolvedByName))
                return resolvedByName;
        }

        // 4. Channel name directly matches an XMLTV channel ID
        if (!string.IsNullOrEmpty(channelName) && _programsByChannel.ContainsKey(channelName))
            return channelName;

        // 5. Fuzzy: channel name is contained in a display-name (e.g. "CCTV-13" in "CCTV-13新闻")
        if (!string.IsNullOrEmpty(channelName))
        {
            foreach (var kvp in _displayNameToChannelId)
            {
                // Display-name starts with the channel name
                if (kvp.Key.StartsWith(channelName, StringComparison.OrdinalIgnoreCase) &&
                    _programsByChannel.ContainsKey(kvp.Value))
                {
                    Debug.WriteLine($"[EPG] Fuzzy matched channel '{channelName}' → display-name '{kvp.Key}' → id '{kvp.Value}'");
                    return kvp.Value;
                }
            }
        }

        // 6. Normalized match: strip hyphens/spaces and compare
        if (!string.IsNullOrEmpty(channelName))
        {
            var normalizedName = NormalizeName(channelName);
            foreach (var kvp in _displayNameToChannelId)
            {
                if (NormalizeName(kvp.Key) == normalizedName && _programsByChannel.ContainsKey(kvp.Value))
                {
                    Debug.WriteLine($"[EPG] Normalized matched channel '{channelName}' → display-name '{kvp.Key}' → id '{kvp.Value}'");
                    return kvp.Value;
                }
            }
            // Also try normalized against XMLTV channel IDs
            foreach (var key in _programsByChannel.Keys)
            {
                if (NormalizeName(key) == normalizedName)
                {
                    Debug.WriteLine($"[EPG] Normalized matched channel '{channelName}' → xmltv-id '{key}'");
                    return key;
                }
            }
        }

        Debug.WriteLine($"[EPG] Could not resolve channel: tvgId='{tvgId}', name='{channelName}'");
        return null;
    }

    /// <summary>
    /// Normalize a channel name by lowercasing and stripping hyphens, spaces, dots
    /// e.g. "CCTV-13" and "CCTV13" both become "cctv13"
    /// </summary>
    private static string NormalizeName(string name)
    {
        return name.ToLowerInvariant()
            .Replace("-", "")
            .Replace(" ", "")
            .Replace(".", "")
            .Replace("_", "");
    }

    /// <summary>
    /// Get the currently airing program for a channel
    /// </summary>
    public EpgProgram? GetCurrentProgram(string? tvgId, string? channelName = null)
    {
        var resolvedId = ResolveChannelId(tvgId, channelName);
        if (resolvedId == null) return null;

        var programs = _programsByChannel[resolvedId];
        var now = DateTime.Now;
        var current = programs.FirstOrDefault(p => now >= p.Start && now < p.Stop);

        if (current == null && programs.Count > 0)
        {
            var first = programs[0];
            var last = programs[^1];
            Debug.WriteLine($"[EPG] No current programme for channel '{resolvedId}' at {now:HH:mm:ss}. " +
                $"EPG range: {first.Start:yyyy-MM-dd HH:mm} to {last.Stop:yyyy-MM-dd HH:mm} ({programs.Count} programmes)");
        }

        return current;
    }

    /// <summary>
    /// Get upcoming programs for a channel
    /// </summary>
    public List<EpgProgram> GetUpcomingPrograms(string? tvgId, int count = 5, string? channelName = null)
    {
        var resolvedId = ResolveChannelId(tvgId, channelName);
        if (resolvedId == null) return new List<EpgProgram>();

        var programs = _programsByChannel[resolvedId];
        var now = DateTime.Now;
        return programs
            .Where(p => p.Start > now)
            .OrderBy(p => p.Start)
            .Take(count)
            .ToList();
    }

    /// <summary>
    /// Clear all loaded EPG data
    /// </summary>
    public void Clear()
    {
        _programsByChannel.Clear();
        _displayNameToChannelId.Clear();
        _loadedUrls.Clear();
    }
}
