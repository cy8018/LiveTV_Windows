using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LibVLCSharp.Shared;
using IPTVPlayer.Models;
using IPTVPlayer.Services;
using OpenFileDialog = Microsoft.Win32.OpenFileDialog;
using MessageBox = System.Windows.MessageBox;
using Application = System.Windows.Application;

namespace IPTVPlayer.ViewModels;

public partial class MainViewModel : ObservableObject
{
    private readonly M3UParser _parser = new();
    private readonly SettingsService _settings = new();
    private LibVLC? _libVLC;
    private MediaPlayer? _mediaPlayer;
    private DispatcherTimer? _statsTimer;
    private long _lastBytesRead;
    private DateTime _lastStatsTime;
    private readonly EpgService _epgService = new();
    private DispatcherTimer? _epgTimer;
    private CancellationTokenSource? _epgCts;

    [ObservableProperty]
    private ObservableCollection<Channel> _channels = new();

    [ObservableProperty]
    private ObservableCollection<Channel> _filteredChannels = new();

    [ObservableProperty]
    private Channel? _selectedChannel;

    [ObservableProperty]
    private Channel? _currentChannel;

    [ObservableProperty]
    private string _searchText = string.Empty;

    [ObservableProperty]
    private string _statusMessage = "No playlist loaded";

    [ObservableProperty]
    private bool _isPlaying;

    [ObservableProperty]
    private bool _isLoading;

    [ObservableProperty]
    private string _loadingText = "Loading...";

    [ObservableProperty]
    private bool _isPaused;

    [ObservableProperty]
    private bool _hasEverPlayed;

    [ObservableProperty]
    private int _volume = 75;

    [ObservableProperty]
    private bool _isMuted;

    [ObservableProperty]
    private string _playPauseIcon = "\uE768";  // Play icon

    [ObservableProperty]
    private string _volumeIcon = "\uE767";  // Volume icon

    [ObservableProperty]
    private bool _isEpgVisible;

    [ObservableProperty]
    private EpgProgram? _currentEpgProgram;

    [ObservableProperty]
    private ObservableCollection<EpgProgram> _upcomingEpgPrograms = new();

    [ObservableProperty]
    private bool _isEpgLoading;

    [ObservableProperty]
    private bool _hasUpcomingPrograms;

    [ObservableProperty]
    private double _epgProgressPercent;

    public MediaPlayer? MediaPlayer => _mediaPlayer;

    public async Task InitializeAsync()
    {
        Debug.WriteLine("[IPTV] Initializing LibVLC...");
        
        try
        {
            // Run heavy native library loading AND LibVLC construction on a background thread
            // to keep the UI responsive during startup (LibVLC plugin scanning can take seconds)
            var (libVLC, mediaPlayer) = await Task.Run(() =>
            {
                Core.Initialize();
                
                var vlc = new LibVLC(
                    "--network-caching=3000",
                    "--live-caching=3000",
                    "--file-caching=3000",
                    "--clock-jitter=0",
                    "--clock-synchro=0",
                    "--no-video-title-show"
                );
                
                var player = new MediaPlayer(vlc);
                return (vlc, player);
            });

            _libVLC = libVLC;
            _mediaPlayer = mediaPlayer;
            
            // Setup stats timer for showing download speed
            _statsTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromSeconds(1)
            };
            _statsTimer.Tick += StatsTimer_Tick;
            
            // Setup EPG refresh timer
            _epgTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromSeconds(30)
            };
            _epgTimer.Tick += (s, e) =>
            {
                if (IsEpgVisible) UpdateEpgInfo();
            };
            _epgTimer.Start();
            
            // Event handlers — use BeginInvoke (non-blocking) to avoid deadlocks
            // between VLC threads and the UI thread
            _mediaPlayer.Playing += (s, e) => Application.Current.Dispatcher.BeginInvoke(() =>
            {
                IsPlaying = true;
                HasEverPlayed = true;
                IsPaused = false;
                IsLoading = false;
                PlayPauseIcon = "\uE769";  // Pause icon
                StatusMessage = $"Now playing: {CurrentChannel?.Name}";
                StartStatsTimer();
                if (IsEpgVisible) UpdateEpgInfo();
            });
            
            _mediaPlayer.Paused += (s, e) => Application.Current.Dispatcher.BeginInvoke(() =>
            {
                IsPaused = true;
                PlayPauseIcon = "\uE768";  // Play icon
                StatusMessage = $"Paused: {CurrentChannel?.Name}";
                StopStatsTimer();
            });
            
            _mediaPlayer.Stopped += (s, e) => Application.Current.Dispatcher.BeginInvoke(() =>
            {
                IsPlaying = false;
                IsPaused = false;
                IsLoading = false;
                PlayPauseIcon = "\uE768";  // Play icon
                StopStatsTimer();
            });
            
            _mediaPlayer.Buffering += (s, e) => Application.Current.Dispatcher.BeginInvoke(() =>
            {
                if (e.Cache < 100)
                {
                    IsLoading = true;
                    LoadingText = $"Buffering {e.Cache:F0}%";
                    StatusMessage = $"Buffering: {CurrentChannel?.Name} ({e.Cache:F0}%)";
                }
                else
                {
                    IsLoading = false;
                    if (IsPlaying)
                    {
                        StatusMessage = $"Now playing: {CurrentChannel?.Name}";
                    }
                }
            });
            
            _mediaPlayer.EncounteredError += (s, e) => Application.Current.Dispatcher.BeginInvoke(async () =>
            {
                Debug.WriteLine($"[IPTV] LibVLC Error");
                IsLoading = false;
                StatusMessage = $"Error playing: {CurrentChannel?.Name}";
                
                // Try next source automatically after a short delay
                if (CurrentChannel != null && CurrentChannel.SourceCount > 1)
                {
                    await Task.Delay(500); // Small delay to prevent rapid retries
                    TryNextSource();
                }
            });

            // Load saved volume
            Volume = _settings.Settings.Volume;
            _mediaPlayer.Volume = Volume;
            
            Debug.WriteLine("[IPTV] LibVLC initialization complete");
            OnPropertyChanged(nameof(MediaPlayer));
            
            // Auto-load last playlist
            LoadLastPlaylistAsync();
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[IPTV] LibVLC initialization failed: {ex.Message}");
            StatusMessage = $"Error initializing player: {ex.Message}";
            MessageBox.Show($"Failed to initialize video player:\n{ex.Message}", 
                "Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async void LoadLastPlaylistAsync()
    {
        var lastPath = _settings.Settings.LastPlaylistPath;
        if (!string.IsNullOrEmpty(lastPath))
        {
            if (lastPath.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                lastPath.StartsWith("https://", StringComparison.OrdinalIgnoreCase) ||
                System.IO.File.Exists(lastPath))
            {
                Debug.WriteLine($"[IPTV] Auto-loading last playlist: {lastPath}");
                await LoadPlaylistAsync(lastPath);
            }
        }
    }

    public void Cleanup()
    {
        Debug.WriteLine("[IPTV] Cleanup called");
        StopStatsTimer();
        _statsTimer = null;
        _epgTimer?.Stop();
        _epgTimer = null;
        _epgCts?.Cancel();
        _epgCts?.Dispose();
        _epgCts = null;
        
        // Run cleanup on background thread to prevent UI hang
        var mediaPlayer = _mediaPlayer;
        var libVLC = _libVLC;
        _mediaPlayer = null;
        _libVLC = null;
        
        Task.Run(() =>
        {
            try
            {
                mediaPlayer?.Stop();
                mediaPlayer?.Dispose();
                libVLC?.Dispose();
            }
            catch { }
        });
    }

    private void StartStatsTimer()
    {
        _lastBytesRead = 0;
        _lastStatsTime = DateTime.Now;
        _statsTimer?.Start();
    }

    private void StopStatsTimer()
    {
        _statsTimer?.Stop();
    }

    private void StatsTimer_Tick(object? sender, EventArgs e)
    {
        if (_mediaPlayer == null || CurrentChannel == null) return;

        try
        {
            var media = _mediaPlayer.Media;
            if (media == null) return;

            // Get current stats - use DemuxReadBytes which works better for streams
            var stats = media.Statistics;
            long currentBytes = stats.DemuxReadBytes;
            if (currentBytes == 0) currentBytes = stats.ReadBytes; // Fallback
            var currentTime = DateTime.Now;

            // Calculate speed
            var timeDiff = (currentTime - _lastStatsTime).TotalSeconds;
            if (timeDiff >= 0.5) // Only update if enough time has passed
            {
                string speedText = "";
                
                if (_lastBytesRead > 0 && currentBytes > _lastBytesRead)
                {
                    var bytesDiff = currentBytes - _lastBytesRead;
                    var bytesPerSecond = bytesDiff / timeDiff;

                    if (bytesPerSecond >= 1024 * 1024)
                    {
                        speedText = $"{bytesPerSecond / (1024 * 1024):F1} MB/s";
                    }
                    else if (bytesPerSecond >= 1024)
                    {
                        speedText = $"{bytesPerSecond / 1024:F0} KB/s";
                    }
                    else if (bytesPerSecond > 0)
                    {
                        speedText = $"{bytesPerSecond:F0} B/s";
                    }
                }

                // Use DemuxBitrate as fallback - it's in kbit/s
                var demuxBitrate = stats.DemuxBitrate;
                if (string.IsNullOrEmpty(speedText) && demuxBitrate > 0)
                {
                    var kbps = demuxBitrate * 8; // Convert to kbps
                    if (kbps >= 1000)
                    {
                        speedText = $"{kbps / 1000:F1} Mbps";
                    }
                    else
                    {
                        speedText = $"{kbps:F0} kbps";
                    }
                }

                if (!string.IsNullOrEmpty(speedText))
                {
                    StatusMessage = $"Playing: {CurrentChannel.Name} | {speedText}";
                }
                else
                {
                    StatusMessage = $"Playing: {CurrentChannel.Name}";
                }

                _lastBytesRead = currentBytes;
                _lastStatsTime = currentTime;
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[IPTV] Stats error: {ex.Message}");
        }
    }

    partial void OnSearchTextChanged(string value)
    {
        FilterChannels();
    }

    partial void OnVolumeChanged(int value)
    {
        if (_mediaPlayer != null)
        {
            _mediaPlayer.Volume = value;
            if (value == 0)
            {
                IsMuted = true;
                VolumeIcon = "\uE74F";  // Mute icon
            }
            else
            {
                IsMuted = false;
                VolumeIcon = value > 50 ? "\uE767" : "\uE993";  // Volume high/low
            }
            
            // Save volume setting
            _settings.Settings.Volume = value;
            _settings.Save();
        }
    }

    private void FilterChannels()
    {
        if (string.IsNullOrWhiteSpace(SearchText))
        {
            // Filter out hidden channels and sort by SortOrder
            var visible = Channels
                .Where(c => !c.IsHidden)
                .OrderBy(c => c.SortOrder)
                .ThenBy(c => c.Id)
                .ToList();
            FilteredChannels = new ObservableCollection<Channel>(visible);
        }
        else
        {
            var filtered = Channels
                .Where(c => !c.IsHidden &&
                           (c.Name.Contains(SearchText, StringComparison.OrdinalIgnoreCase) ||
                           (c.Group?.Contains(SearchText, StringComparison.OrdinalIgnoreCase) ?? false)))
                .OrderBy(c => c.SortOrder)
                .ThenBy(c => c.Id)
                .ToList();
            FilteredChannels = new ObservableCollection<Channel>(filtered);
        }
    }

    [RelayCommand]
    private async Task OpenPlaylist()
    {
        var dialog = new OpenFileDialog
        {
            Title = "Open M3U Playlist",
            Filter = "M3U Playlist Files (*.m3u;*.m3u8)|*.m3u;*.m3u8|All Files (*.*)|*.*",
            FilterIndex = 1
        };

        if (dialog.ShowDialog() == true)
        {
            await LoadPlaylistAsync(dialog.FileName);
        }
    }

    [RelayCommand]
    private void EditChannels()
    {
        // Handled in code-behind for window manipulation
    }

    /// <summary>
    /// Apply saved channel customizations after loading
    /// </summary>
    private void ApplyChannelCustomizations()
    {
        foreach (var channel in Channels)
        {
            var key = GetChannelKey(channel);
            if (_settings.Settings.ChannelCustomizations.TryGetValue(key, out var customization))
            {
                channel.IsHidden = customization.IsHidden;
                channel.SortOrder = customization.SortOrder;
                if (customization.LastSourceIndex > 0 && customization.LastSourceIndex < channel.Sources.Count)
                {
                    channel.CurrentSourceIndex = customization.LastSourceIndex;
                }
            }
            else
            {
                // Default sort order to channel ID
                channel.SortOrder = channel.Id;
            }
        }
    }

    /// <summary>
    /// Save channel customizations to settings
    /// </summary>
    public void SaveChannelCustomizations()
    {
        _settings.Settings.ChannelCustomizations.Clear();
        
        foreach (var channel in Channels)
        {
            // Only save if customized (hidden, custom order, or non-default source)
            if (channel.IsHidden || channel.SortOrder != channel.Id || channel.CurrentSourceIndex != 0)
            {
                var key = GetChannelKey(channel);
                _settings.Settings.ChannelCustomizations[key] = new ChannelCustomization
                {
                    IsHidden = channel.IsHidden,
                    SortOrder = channel.SortOrder,
                    LastSourceIndex = channel.CurrentSourceIndex
                };
            }
        }
        
        _settings.Save();
        FilterChannels(); // Refresh the filtered list
    }

    /// <summary>
    /// Get a unique key for a channel (name + first URL)
    /// </summary>
    private static string GetChannelKey(Channel channel)
    {
        var urlPart = channel.Sources.Count > 0 ? channel.Sources[0].Url : "";
        return $"{channel.Name}|{urlPart}";
    }

    /// <summary>
    /// Save the current source index for a channel to settings
    /// </summary>
    private void SaveChannelSourceIndex(Channel channel)
    {
        var key = GetChannelKey(channel);
        if (_settings.Settings.ChannelCustomizations.TryGetValue(key, out var customization))
        {
            customization.LastSourceIndex = channel.CurrentSourceIndex;
        }
        else
        {
            _settings.Settings.ChannelCustomizations[key] = new ChannelCustomization
            {
                IsHidden = channel.IsHidden,
                SortOrder = channel.SortOrder,
                LastSourceIndex = channel.CurrentSourceIndex
            };
        }
        _settings.Save();
    }

    private async Task LoadPlaylistAsync(string path)
    {
        try
        {
            IsLoading = true;
            LoadingText = "Loading playlist...";
            StatusMessage = "Loading playlist...";

            // Stop current playback
            _mediaPlayer?.Stop();
            CurrentChannel = null;
            SelectedChannel = null;
            IsPlaying = false;
            IsPaused = false;
            PlayPauseIcon = "\uE768";

            // Clear all saved customizations (order, hidden, source index)
            _settings.Settings.ChannelCustomizations.Clear();
            _settings.Save();

            // Clear EPG data so it reloads fresh for the new playlist
            _epgCts?.Cancel();
            _epgCts?.Dispose();
            _epgCts = null;
            _epgService.Clear();
            CurrentEpgProgram = null;
            UpcomingEpgPrograms = new ObservableCollection<EpgProgram>();
            HasUpcomingPrograms = false;
            EpgProgressPercent = 0;

            List<Channel> channels;
            
            if (path.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                path.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            {
                channels = await _parser.ParseUrlAsync(path);
            }
            else
            {
                channels = await _parser.ParseFileAsync(path);
            }

            Channels = new ObservableCollection<Channel>(channels);
            
            // Assign default sort order (no saved customizations to apply)
            foreach (var channel in Channels)
            {
                channel.SortOrder = channel.Id;
            }
            FilterChannels();

            StatusMessage = $"Loaded {channels.Count} channels";
            
            // Save last playlist path
            _settings.Settings.LastPlaylistPath = path;
            _settings.Save();

            // Load EPG data in background
            if (_parser.EpgUrls.Count > 0)
            {
                _ = LoadEpgDataAsync(_parser.EpgUrls.ToList());
            }
        }
        catch (Exception ex)
        {
            StatusMessage = $"Error: {ex.Message}";
            MessageBox.Show($"Failed to load playlist:\n{ex.Message}", "Error", 
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            IsLoading = false;
        }
    }

    public async void PlaySelectedChannel()
    {
        if (SelectedChannel != null)
        {
            await PlayChannelAsync(SelectedChannel);
        }
    }

    private async Task PlayChannelAsync(Channel channel)
    {
        Debug.WriteLine($"[IPTV] PlayChannel called: {channel.Name}");
        Debug.WriteLine($"[IPTV] URL: {channel.Url}");
        Debug.WriteLine($"[IPTV] Source {channel.CurrentSourceIndex + 1} of {channel.SourceCount}");

        if (_mediaPlayer == null || _libVLC == null)
        {
            Debug.WriteLine("[IPTV] ERROR: MediaPlayer or LibVLC is null!");
            StatusMessage = "Error: Player not initialized";
            return;
        }

        // Show loading immediately
        IsLoading = true;
        LoadingText = "Connecting...";
        var sourceInfo = channel.SourceCount > 1 ? $" (Source {channel.CurrentSourceIndex + 1}/{channel.SourceCount})" : "";
        StatusMessage = $"Loading: {channel.Name}{sourceInfo}";
        CurrentChannel = channel;

        // Push a render frame so WPF paints the loading indicator before Stop() blocks the UI
        await Application.Current.Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.Render);

        try
        {
            // Stop must run on UI thread (LibVLC D3D11 surface is thread-bound)
            _mediaPlayer.Stop();

            // Create and play media on background thread
            await Task.Run(() =>
            {
                var media = new Media(_libVLC, channel.Url, FromType.FromLocation);
                Application.Current.Dispatcher.Invoke(() =>
                {
                    _mediaPlayer.Play(media);
                });
            });
            
            Debug.WriteLine($"[IPTV] Playing via LibVLC: {channel.Url}");
            
            // Remember the source index for this channel
            SaveChannelSourceIndex(channel);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[IPTV] ERROR: {ex.Message}");
            StatusMessage = $"Error playing channel: {ex.Message}";
            IsLoading = false;
            
            // Try next source if available
            TryNextSource();
        }
    }

    // Sync wrapper for commands that need to call PlayChannel
    private async void PlayChannel(Channel channel)
    {
        await PlayChannelAsync(channel);
    }

    [RelayCommand]
    private void TryNextSource()
    {
        if (CurrentChannel != null && CurrentChannel.TryNextSource())
        {
            Debug.WriteLine($"[IPTV] Trying next source: {CurrentChannel.CurrentSourceIndex + 1} of {CurrentChannel.SourceCount}");
            PlayChannel(CurrentChannel);
        }
        else
        {
            // All sources exhausted
            Debug.WriteLine("[IPTV] All sources exhausted for this channel");
            IsLoading = false;
            StatusMessage = $"Failed to play: {CurrentChannel?.Name} (no working sources)";
        }
    }

    [RelayCommand]
    private void TryPreviousSource()
    {
        if (CurrentChannel != null && CurrentChannel.SourceCount > 1)
        {
            CurrentChannel.CurrentSourceIndex = (CurrentChannel.CurrentSourceIndex - 1 + CurrentChannel.SourceCount) % CurrentChannel.SourceCount;
            Debug.WriteLine($"[IPTV] Trying previous source: {CurrentChannel.CurrentSourceIndex + 1} of {CurrentChannel.SourceCount}");
            PlayChannel(CurrentChannel);
        }
    }

    [RelayCommand]
    private void PlayPause()
    {
        if (_mediaPlayer == null)
            return;

        if (_mediaPlayer.IsPlaying)
        {
            _mediaPlayer.Pause();
        }
        else if (IsPaused)
        {
            _mediaPlayer.Play();
        }
        else if (CurrentChannel != null)
        {
            PlayChannel(CurrentChannel);
        }
        else if (SelectedChannel != null)
        {
            PlayChannel(SelectedChannel);
        }
    }

    [RelayCommand]
    private void Stop()
    {
        _mediaPlayer?.Stop();
        CurrentChannel = null;
        IsPlaying = false;
        IsPaused = false;
        PlayPauseIcon = "\uE768";  // Play icon
        StatusMessage = Channels.Count > 0 ? $"{Channels.Count} channels available" : "No playlist loaded";
    }

    [RelayCommand]
    private void Mute()
    {
        if (_mediaPlayer == null)
            return;

        IsMuted = !IsMuted;
        _mediaPlayer.Mute = IsMuted;
        VolumeIcon = IsMuted ? "\uE74F" : (Volume > 50 ? "\uE767" : "\uE993");
    }

    [RelayCommand]
    private void NextChannel()
    {
        if (FilteredChannels.Count == 0)
            return;

        int currentIndex = CurrentChannel != null 
            ? FilteredChannels.IndexOf(CurrentChannel) 
            : -1;

        int nextIndex = (currentIndex + 1) % FilteredChannels.Count;
        SelectedChannel = FilteredChannels[nextIndex];
        PlayChannel(FilteredChannels[nextIndex]);
    }

    [RelayCommand]
    private void PreviousChannel()
    {
        if (FilteredChannels.Count == 0)
            return;

        int currentIndex = CurrentChannel != null 
            ? FilteredChannels.IndexOf(CurrentChannel) 
            : 0;

        int prevIndex = currentIndex <= 0 
            ? FilteredChannels.Count - 1 
            : currentIndex - 1;

        SelectedChannel = FilteredChannels[prevIndex];
        PlayChannel(FilteredChannels[prevIndex]);
    }

    [RelayCommand]
    private void Fullscreen()
    {
        // Handled in code-behind for window manipulation
    }

    [RelayCommand]
    private void ToggleEpg()
    {
        IsEpgVisible = !IsEpgVisible;
        if (IsEpgVisible)
        {
            UpdateEpgInfo();
        }
    }

    partial void OnCurrentChannelChanged(Channel? value)
    {
        if (IsEpgVisible)
        {
            UpdateEpgInfo();
        }
    }

    private void UpdateEpgInfo()
    {
        if (CurrentChannel == null)
        {
            CurrentEpgProgram = null;
            UpcomingEpgPrograms = new ObservableCollection<EpgProgram>();
            HasUpcomingPrograms = false;
            EpgProgressPercent = 0;
            return;
        }

        Debug.WriteLine($"[EPG] UpdateEpgInfo for channel: Name='{CurrentChannel.Name}', TvgId='{CurrentChannel.TvgId}'");

        CurrentEpgProgram = _epgService.GetCurrentProgram(CurrentChannel.TvgId, CurrentChannel.Name);

        var upcoming = _epgService.GetUpcomingPrograms(CurrentChannel.TvgId, 5, CurrentChannel.Name);

        // If no current programme but upcoming ones exist, show the first upcoming as "current"
        // (EPG data may not have started yet or has a gap between programmes)
        if (CurrentEpgProgram == null && upcoming.Count > 0)
        {
            Debug.WriteLine($"[EPG] No current programme, promoting first upcoming: '{upcoming[0].Title}' at {upcoming[0].Start}");
            CurrentEpgProgram = upcoming[0];
            upcoming = upcoming.Skip(1).ToList();
        }

        UpcomingEpgPrograms = new ObservableCollection<EpgProgram>(upcoming);
        HasUpcomingPrograms = upcoming.Count > 0;

        Debug.WriteLine($"[EPG] Result: Current='{CurrentEpgProgram?.Title}', Upcoming={UpcomingEpgPrograms.Count}");

        if (CurrentEpgProgram != null)
        {
            var total = (CurrentEpgProgram.Stop - CurrentEpgProgram.Start).TotalSeconds;
            var elapsed = (DateTime.Now - CurrentEpgProgram.Start).TotalSeconds;
            EpgProgressPercent = total > 0 ? Math.Min(1.0, Math.Max(0, elapsed / total)) : 0;
        }
        else
        {
            EpgProgressPercent = 0;
        }
    }

    private async Task LoadEpgDataAsync(List<string> epgUrls)
    {
        try
        {
            _epgCts?.Cancel();
            _epgCts?.Dispose();
            _epgCts = new CancellationTokenSource();
            var token = _epgCts.Token;

            IsEpgLoading = true;
            Debug.WriteLine($"[EPG] Starting EPG load for {epgUrls.Count} URL(s)");
            await _epgService.LoadEpgAsync(epgUrls, token);
            IsEpgLoading = false;
            Debug.WriteLine("[EPG] EPG data loaded successfully");

            if (IsEpgVisible)
            {
                UpdateEpgInfo();
            }
        }
        catch (OperationCanceledException)
        {
            Debug.WriteLine("[EPG] EPG loading was cancelled");
            IsEpgLoading = false;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[EPG] Failed to load EPG data: {ex.Message}");
            IsEpgLoading = false;
        }
    }
}
