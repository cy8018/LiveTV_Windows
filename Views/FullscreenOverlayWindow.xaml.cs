using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using IPTVPlayer.Models;
using IPTVPlayer.Services;
using IPTVPlayer.ViewModels;

namespace IPTVPlayer.Views;

public partial class FullscreenOverlayWindow : Window
{
    private MainViewModel? _viewModel;
    private SettingsService? _settingsService;
    private DispatcherTimer? _hideTimer;
    private DispatcherTimer? _mouseCheckTimer;
    private DispatcherTimer? _clockTimer;
    private System.Windows.Point _lastMousePosition;
    private const int AutoHideDelaySeconds = 5;
    private bool _overlaysVisible = true;
    private DateTime _lastHideTime = DateTime.MinValue;
    private const int HideCooldownMs = 500; // Ignore mouse events for 500ms after hiding

    // Circular scrolling
    private const int CircularCopies = 5; // Number of copies of the channel list
    private const int MiddleCopy = CircularCopies / 2; // copy index 2 of 0-4
    private List<Channel> _circularItems = new();
    private bool _isRecentering = false;
    private bool _suppressSelectionChanged = false;
    private bool _selectionFromOverlayClick = false; // true when user clicked directly on the channel list
    private bool _isCircularMode = false;
    private int _manualSelectedIndex = -1; // Manually tracked selected index (avoids WPF duplicate-item bugs)

    public event EventHandler? ExitFullscreenRequested;
    public event EventHandler? MouseActivity;
    public event EventHandler<Channel>? ChannelSelected;
    public event EventHandler? CloseAppRequested;
    
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetCursorPos(out POINT lpPoint);
    
    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int X;
        public int Y;
    }

    public FullscreenOverlayWindow()
    {
        InitializeComponent();

        _hideTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(AutoHideDelaySeconds)
        };
        _hideTimer.Tick += HideTimer_Tick;
        
        // Timer to check mouse position (since transparent windows don't get mouse events reliably)
        _mouseCheckTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(100)
        };
        _mouseCheckTimer.Tick += MouseCheckTimer_Tick;

        // Clock + battery timer - update every second
        _clockTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(1)
        };
        _clockTimer.Tick += (s, e) => UpdateStatusInfo();
        UpdateStatusInfo();
        _clockTimer.Start();
    }

    private void UpdateStatusInfo()
    {
        ClockText.Text = DateTime.Now.ToString("H:mm");
        UpdateBattery();
    }

    private void UpdateBattery()
    {
        try
        {
            var status = System.Windows.Forms.SystemInformation.PowerStatus;
            if (status.BatteryChargeStatus == System.Windows.Forms.BatteryChargeStatus.NoSystemBattery)
            {
                BatteryIcon.Visibility = Visibility.Collapsed;
                BatteryPercent.Visibility = Visibility.Collapsed;
                return;
            }

            int percent = (int)(status.BatteryLifePercent * 100);
            bool isCharging = status.PowerLineStatus == System.Windows.Forms.PowerLineStatus.Online;

            string icon;
            if (isCharging)
            {
                // BatteryCharging0-10: E85A-E862, E83E, EA93
                icon = percent > 90 ? "\uEA93"   // BatteryCharging10
                     : percent > 70 ? "\uE862"   // BatteryCharging8
                     : percent > 50 ? "\uE860"   // BatteryCharging6
                     : percent > 30 ? "\uE85E"   // BatteryCharging4
                     : percent > 10 ? "\uE85C"   // BatteryCharging2
                     : "\uE85A";                  // BatteryCharging0
            }
            else
            {
                // Battery0-10: E850-E859, E83F
                icon = percent > 90 ? "\uE83F"   // Battery10
                     : percent > 70 ? "\uE859"   // Battery9
                     : percent > 50 ? "\uE857"   // Battery7
                     : percent > 30 ? "\uE855"   // Battery5
                     : percent > 10 ? "\uE853"   // Battery3
                     : "\uE850";                  // Battery0
            }

            BatteryIcon.Text = icon;
            BatteryIcon.Visibility = Visibility.Visible;
            BatteryPercent.Text = $"{percent}%";
            BatteryPercent.Visibility = Visibility.Visible;
        }
        catch
        {
            BatteryIcon.Visibility = Visibility.Collapsed;
            BatteryPercent.Visibility = Visibility.Collapsed;
        }
    }
    
    private void MouseCheckTimer_Tick(object? sender, EventArgs e)
    {
        // Don't show overlays during cooldown period after hiding
        if (IsInHideCooldown())
            return;
            
        if (GetCursorPos(out POINT pt))
        {
            var currentPos = new System.Windows.Point(pt.X, pt.Y);
            
            // Check if mouse has moved significantly (threshold of 10 pixels to avoid jitter)
            if (Math.Abs(currentPos.X - _lastMousePosition.X) > 10 || 
                Math.Abs(currentPos.Y - _lastMousePosition.Y) > 10)
            {
                _lastMousePosition = currentPos;
                
                if (!_overlaysVisible)
                {
                    ShowOverlays();
                }
            }
        }
    }
    
    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        // Start mouse tracking when window is ready
        if (GetCursorPos(out POINT pt))
        {
            _lastMousePosition = new System.Windows.Point(pt.X, pt.Y);
        }
        _mouseCheckTimer?.Start();
    }
    
    protected override void OnClosed(EventArgs e)
    {
        _mouseCheckTimer?.Stop();
        _hideTimer?.Stop();
        base.OnClosed(e);
    }
    
    public void SetViewModel(MainViewModel viewModel)
    {
        _viewModel = viewModel;
        DataContext = viewModel;
        
        // Load sidebar position preference
        _settingsService = new SettingsService();
        ApplySidebarPosition(_settingsService.Settings.ChannelListOnRight);
        
        // Listen for channel list changes to rebuild circular list.
        // FilterChannels() replaces the entire ObservableCollection, so we must
        // watch the property change (not CollectionChanged on a single instance).
        viewModel.PropertyChanged += (s, e) =>
        {
            if (e.PropertyName == nameof(MainViewModel.FilteredChannels))
            {
                Dispatcher.BeginInvoke(DispatcherPriority.Background, BuildCircularList);
            }
            else if (e.PropertyName == nameof(MainViewModel.SelectedChannel))
            {
                Dispatcher.BeginInvoke(DispatcherPriority.Background, SyncSelectedChannel);
            }
        };
    }
    
    public void UpdateFromViewModel()
    {
        BuildCircularList();
        ShowOverlays();
        _mouseCheckTimer?.Start();
    }
    
    private void Window_MouseMove(object sender, System.Windows.Input.MouseEventArgs e)
    {
        // Only show overlays if they're hidden and not in cooldown
        if (!_overlaysVisible && !IsInHideCooldown())
        {
            ShowOverlays();
        }
        MouseActivity?.Invoke(this, EventArgs.Empty);
    }

    private void Window_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (!_overlaysVisible && !IsInHideCooldown())
        {
            ShowOverlays();
        }
        else if (_overlaysVisible)
        {
            ResetHideTimer();
        }
        MouseActivity?.Invoke(this, EventArgs.Empty);
    }
    
    private void Window_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        // Only show overlays if they're hidden, don't reset timer on every key
        if (!_overlaysVisible)
        {
            ShowOverlays();
        }
        
        switch (e.Key)
        {
            case Key.Escape:
                ExitFullscreenRequested?.Invoke(this, EventArgs.Empty);
                e.Handled = true;
                break;
            case Key.Space:
                _viewModel?.PlayPauseCommand.Execute(null);
                e.Handled = true;
                break;
            case Key.M:
                _viewModel?.MuteCommand.Execute(null);
                e.Handled = true;
                break;
            case Key.C:
                // Toggle overlay visibility
                if (_overlaysVisible)
                    HideOverlays();
                else
                    ShowOverlays();
                e.Handled = true;
                break;
            case Key.G:
                _viewModel?.ToggleEpgCommand.Execute(null);
                e.Handled = true;
                break;
            case Key.PageUp:
                _viewModel?.PreviousChannelCommand.Execute(null);
                e.Handled = true;
                break;
            case Key.PageDown:
                _viewModel?.NextChannelCommand.Execute(null);
                e.Handled = true;
                break;
        }
    }

    public void SetBounds(double left, double top, double width, double height)
    {
        Left = left;
        Top = top;
        Width = width;
        Height = height;
    }

    private DateTime _lastShowTime = DateTime.MinValue;
    private const int ShowCooldownMs = 500; // Ignore channel selections for 500ms after showing

    public void ShowOverlays()
    {
        ControlBarPanel.Visibility = Visibility.Visible;
        if (_sidebarVisible)
        {
            SidebarTransform.X = 0; // Show sidebar
        }
        // Re-establish EPG binding (HideOverlays breaks it by setting a local value)
        RestoreEpgOverlayBinding();
        _overlaysVisible = true;
        _lastShowTime = DateTime.Now;
        ResetHideTimer();
    }

    private void RestoreEpgOverlayBinding()
    {
        var epgBinding = new System.Windows.Data.Binding("IsEpgVisible")
        {
            Converter = (System.Windows.Data.IValueConverter)FindResource("BoolToVisibilityConverter")
        };
        EpgOverlayPanel.SetBinding(UIElement.VisibilityProperty, epgBinding);
    }

    public void HideOverlays()
    {
        ControlBarPanel.Visibility = Visibility.Collapsed;
        bool onRight = _settingsService?.Settings.ChannelListOnRight ?? false;
        SidebarTransform.X = onRight ? 320 : -320; // Hide sidebar off-screen
        EpgOverlayPanel.Visibility = Visibility.Collapsed; // Hide EPG with overlays
        _overlaysVisible = false;
        // Note: _sidebarVisible is NOT reset here — it's a persistent user preference
        _lastHideTime = DateTime.Now; // Record when we hid, to prevent immediate re-show
        
        // Update last mouse position so the mouse check timer doesn't immediately show overlays again
        if (GetCursorPos(out POINT pt))
        {
            _lastMousePosition = new System.Windows.Point(pt.X, pt.Y);
        }
    }
    
    private bool IsInHideCooldown()
    {
        return (DateTime.Now - _lastHideTime).TotalMilliseconds < HideCooldownMs;
    }

    public bool IsOverlaysVisible => _overlaysVisible;

    private void ResetHideTimer()
    {
        _hideTimer?.Stop();
        _hideTimer?.Start();
    }

    private void HideTimer_Tick(object? sender, EventArgs e)
    {
        _hideTimer?.Stop();
        HideOverlays();
    }

    private bool _sidebarVisible = true;

    private void ShowChannels_Click(object sender, RoutedEventArgs e)
    {
        if (_sidebarVisible)
        {
            // Hide sidebar
            bool onRight = _settingsService?.Settings.ChannelListOnRight ?? false;
            SidebarTransform.X = onRight ? 320 : -320;
            _sidebarVisible = false;
        }
        else
        {
            // Show sidebar
            SidebarTransform.X = 0;
            _sidebarVisible = true;
        }
        ResetHideTimer();
    }

    private void ToggleSidebarPosition_Click(object sender, RoutedEventArgs e)
    {
        if (_settingsService == null) return;
        bool newValue = !_settingsService.Settings.ChannelListOnRight;
        _settingsService.Settings.ChannelListOnRight = newValue;
        _settingsService.Save();
        ApplySidebarPosition(newValue);
        ResetHideTimer();
    }

    private void ApplySidebarPosition(bool onRight)
    {
        if (onRight)
        {
            SidebarPanel.HorizontalAlignment = System.Windows.HorizontalAlignment.Right;
            SidebarPanel.CornerRadius = new CornerRadius(12, 0, 0, 12);
            // Arrow pointing left (move to left)
            ToggleSidebarIcon.Text = "\uE72B";
            ToggleSidebarPositionButton.ToolTip = "Move channel list to left side";
            // Move EPG to opposite side
            EpgOverlayPanel.HorizontalAlignment = System.Windows.HorizontalAlignment.Left;
            EpgOverlayPanel.Margin = new Thickness(20, 0, 0, 90);
        }
        else
        {
            SidebarPanel.HorizontalAlignment = System.Windows.HorizontalAlignment.Left;
            SidebarPanel.CornerRadius = new CornerRadius(0, 12, 12, 0);
            // Arrow pointing right (move to right)
            ToggleSidebarIcon.Text = "\uE72A";
            ToggleSidebarPositionButton.ToolTip = "Move channel list to right side";
            // Move EPG to opposite side
            EpgOverlayPanel.HorizontalAlignment = System.Windows.HorizontalAlignment.Right;
            EpgOverlayPanel.Margin = new Thickness(0, 0, 20, 90);
        }
    }

    private void CloseSidebar_Click(object sender, RoutedEventArgs e)
    {
        // Hide everything
        HideOverlays();
    }

    private void Overlay_TouchActivity(object sender, TouchEventArgs e)
    {
        ResetHideTimer();
    }

    private void ExitFullscreen_Click(object sender, RoutedEventArgs e)
    {
        ExitFullscreenRequested?.Invoke(this, EventArgs.Empty);
    }

    private void CloseApp_Click(object sender, RoutedEventArgs e)
    {
        CloseAppRequested?.Invoke(this, EventArgs.Empty);
    }

    private void ChannelList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressSelectionChanged) return;
        // Suppress channel switching during the cooldown after overlay was just shown
        if ((DateTime.Now - _lastShowTime).TotalMilliseconds < ShowCooldownMs)
        {
            // Revert visual selection back to the previously selected index
            _suppressSelectionChanged = true;
            if (_manualSelectedIndex >= 0)
                ChannelList.SelectedIndex = _manualSelectedIndex;
            _suppressSelectionChanged = false;
            return;
        }
        if (e.AddedItems.Count > 0 && e.AddedItems[0] is Channel channel)
        {
            // Track the actual clicked index
            _manualSelectedIndex = ChannelList.SelectedIndex;
            // Mark that selection came from the overlay list click,
            // so SyncSelectedChannel won't scroll the list
            _selectionFromOverlayClick = true;
            ChannelSelected?.Invoke(this, channel);
            ResetHideTimer();
        }
    }

    private void ChannelList_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        // Let normal scrolling happen — circular re-centering is handled in ScrollChanged
    }

    private void ChannelList_PreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        // Let normal key navigation happen — circular re-centering handles wrapping
    }

    private void ChannelList_ScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        // Any scroll counts as activity — keep overlay visible
        if (e.VerticalChange != 0 || e.HorizontalChange != 0)
        {
            ResetHideTimer();
        }

        if (!_isCircularMode || _isRecentering || _viewModel == null) return;
        var channels = _viewModel.FilteredChannels;
        if (channels.Count == 0) return;

        var scrollViewer = FindScrollViewer(ChannelList);
        if (scrollViewer == null || scrollViewer.ScrollableHeight <= 0) return;

        double oneCopyHeight = scrollViewer.ScrollableHeight / (CircularCopies - 1);

        if (scrollViewer.VerticalOffset < oneCopyHeight * 0.5 || 
            scrollViewer.VerticalOffset > oneCopyHeight * (CircularCopies - 1.5))
        {
            _isRecentering = true;
            double newOffset = MiddleCopy * oneCopyHeight + (scrollViewer.VerticalOffset % oneCopyHeight);
            scrollViewer.ScrollToVerticalOffset(newOffset);

            int selectedIndexInCopy = -1;
            if (_manualSelectedIndex >= 0 && channels.Count > 0)
            {
                selectedIndexInCopy = _manualSelectedIndex % channels.Count;
            }
            if (selectedIndexInCopy >= 0)
            {
                int middleCopyIndex = MiddleCopy * channels.Count + selectedIndexInCopy;
                SetManualSelection(middleCopyIndex);
            }

            _isRecentering = false;
        }
    }

    private void BuildCircularList()
    {
        if (_viewModel == null) return;
        var channels = _viewModel.FilteredChannels;
        
        _circularItems.Clear();
        _isCircularMode = false;
        if (channels.Count == 0)
        {
            ChannelList.ItemsSource = null;
            return;
        }

        // First, set a single copy to measure if it fills the viewport
        _suppressSelectionChanged = true;
        ChannelList.ItemsSource = channels;
        _suppressSelectionChanged = false;

        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () =>
        {
            var scrollViewer = FindScrollViewer(ChannelList);
            bool needsCircular = scrollViewer != null && scrollViewer.ScrollableHeight > 0;

            if (!needsCircular)
            {
                _isCircularMode = false;
                SyncSelectedChannel();
                return;
            }

            // Build list with N copies for seamless circular scrolling
            _circularItems.Clear();
            for (int copy = 0; copy < CircularCopies; copy++)
            {
                foreach (var ch in channels)
                {
                    _circularItems.Add(ch);
                }
            }

            _suppressSelectionChanged = true;
            ChannelList.ItemsSource = _circularItems;
            _suppressSelectionChanged = false;
            _isCircularMode = true;

            // Scroll to middle copy after layout
            Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () =>
            {
                var sv = FindScrollViewer(ChannelList);
                if (sv != null && sv.ScrollableHeight > 0)
                {
                    double oneCopyHeight = sv.ScrollableHeight / (CircularCopies - 1);
                    sv.ScrollToVerticalOffset(MiddleCopy * oneCopyHeight);
                }

                // Select the current channel in the middle copy
                SyncSelectedChannel();
            });
        });
    }

    /// <summary>
    /// Sync the overlay channel list selection to match the ViewModel's SelectedChannel.
    /// If the selection came from a direct click on the overlay list, don't scroll.
    /// If it came from prev/next buttons, scroll to center the selected channel.
    /// </summary>
    private void SyncSelectedChannel()
    {
        if (_viewModel == null) return;
        var channels = _viewModel.FilteredChannels;
        var selected = _viewModel.SelectedChannel;
        if (selected == null || channels.Count == 0) return;

        if (_selectionFromOverlayClick)
        {
            // User clicked directly on the list — don't scroll, but still
            // highlight all copies of the selected channel.
            _selectionFromOverlayClick = false;
            if (_isCircularMode)
            {
                int clickIdx = channels.IndexOf(selected);
                if (clickIdx >= 0)
                {
                    int middleIndex = MiddleCopy * channels.Count + clickIdx;
                    SetManualSelection(middleIndex);
                }
            }
            return;
        }

        int idx = channels.IndexOf(selected);
        if (idx < 0) return;

        if (_isCircularMode)
        {
            int middleIndex = MiddleCopy * channels.Count + idx;
            SetManualSelection(middleIndex);

            // Prev/next button or external change — scroll to center the item
            ScrollToCenter(middleIndex);
        }
        else
        {
            _suppressSelectionChanged = true;
            ChannelList.SelectedIndex = idx;
            ChannelList.ScrollIntoView(ChannelList.SelectedItem);
            _suppressSelectionChanged = false;
        }
    }

    /// <summary>
    /// Manually set the selected item by index, working around WPF's inability
    /// to reliably select duplicate items in a ListBox.
    /// Directly sets IsSelected on the target ListBoxItem container.
    /// </summary>
    private void SetManualSelection(int index)
    {
        _suppressSelectionChanged = true;
        var channels = _viewModel?.FilteredChannels;
        int channelCount = channels?.Count ?? 0;

        // Clear all copies of the previously selected channel
        if (_manualSelectedIndex >= 0 && _manualSelectedIndex < _circularItems.Count && channelCount > 0)
        {
            int oldBase = _manualSelectedIndex % channelCount;
            for (int copy = 0; copy < CircularCopies; copy++)
            {
                int i = oldBase + copy * channelCount;
                if (i < _circularItems.Count &&
                    ChannelList.ItemContainerGenerator.ContainerFromIndex(i) is ListBoxItem oldItem)
                {
                    oldItem.IsSelected = false;
                }
            }
        }

        // Also clear any WPF-managed selection
        ChannelList.SelectedIndex = -1;

        // Apply new selection to all copies
        _manualSelectedIndex = index;
        if (index >= 0 && index < _circularItems.Count && channelCount > 0)
        {
            int newBase = index % channelCount;
            for (int copy = 0; copy < CircularCopies; copy++)
            {
                int i = newBase + copy * channelCount;
                if (i < _circularItems.Count &&
                    ChannelList.ItemContainerGenerator.ContainerFromIndex(i) is ListBoxItem newItem)
                {
                    newItem.IsSelected = true;
                }
            }
        }

        _suppressSelectionChanged = false;
    }

    /// <summary>
    /// Scrolls the channel list so that the item at the given index is centered vertically.
    /// </summary>
    private void ScrollToCenter(int index)
    {
        var scrollViewer = FindScrollViewer(ChannelList);
        if (scrollViewer == null || scrollViewer.ScrollableHeight <= 0) return;

        // Each item should have the same height; estimate from total
        double itemHeight = scrollViewer.ExtentHeight / _circularItems.Count;
        double itemTop = index * itemHeight;
        double centeredOffset = itemTop - (scrollViewer.ViewportHeight / 2) + (itemHeight / 2);
        centeredOffset = Math.Max(0, Math.Min(centeredOffset, scrollViewer.ScrollableHeight));
        scrollViewer.ScrollToVerticalOffset(centeredOffset);
    }

    private static ScrollViewer? FindScrollViewer(DependencyObject parent)
    {
        for (int i = 0; i < System.Windows.Media.VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = System.Windows.Media.VisualTreeHelper.GetChild(parent, i);
            if (child is ScrollViewer sv) return sv;
            var result = FindScrollViewer(child);
            if (result != null) return result;
        }
        return null;
    }

    protected override void OnMouseMove(System.Windows.Input.MouseEventArgs e)
    {
        base.OnMouseMove(e);
        // Only show overlays if they're hidden and not in cooldown
        if (!_overlaysVisible && !IsInHideCooldown())
        {
            ShowOverlays();
        }
        MouseActivity?.Invoke(this, EventArgs.Empty);
    }

    protected override void OnPreviewMouseDown(MouseButtonEventArgs e)
    {
        // If overlays are hidden, the first click should only wake the overlay, not perform actions
        if (!_overlaysVisible)
        {
            ShowOverlays();
            MouseActivity?.Invoke(this, EventArgs.Empty);
            e.Handled = true; // Swallow so channel list doesn't receive the click
            return;
        }
        base.OnPreviewMouseDown(e);
    }

    protected override void OnPreviewTouchDown(TouchEventArgs e)
    {
        // If overlays are hidden, the first touch should only wake the overlay, not perform actions
        if (!_overlaysVisible)
        {
            ShowOverlays();
            MouseActivity?.Invoke(this, EventArgs.Empty);
            e.Handled = true; // Swallow so channel list doesn't receive the touch
            return;
        }
        base.OnPreviewTouchDown(e);
    }

    protected override void OnMouseDown(MouseButtonEventArgs e)
    {
        base.OnMouseDown(e);
        MouseActivity?.Invoke(this, EventArgs.Empty);

        // Check if clicking on video area (transparent part)
        var element = e.OriginalSource as DependencyObject;
        if (IsClickOnTransparentArea(element))
        {
            // Single click/tap on video: just reset the idle timer (keep overlay visible)
            ResetHideTimer();
        }
        // Don't reset timer when clicking on overlay elements - let it auto-hide
    }

    protected override void OnTouchDown(TouchEventArgs e)
    {
        base.OnTouchDown(e);
        ResetHideTimer();
        MouseActivity?.Invoke(this, EventArgs.Empty);
    }

    protected override void OnKeyDown(System.Windows.Input.KeyEventArgs e)
    {
        base.OnKeyDown(e);

        // Only show overlays if hidden, don't reset timer on every key
        if (!_overlaysVisible)
        {
            ShowOverlays();
        }

        switch (e.Key)
        {
            case Key.Escape:
                ExitFullscreenRequested?.Invoke(this, EventArgs.Empty);
                e.Handled = true;
                break;
            case Key.C:
                // Toggle overlay visibility
                if (_overlaysVisible)
                    HideOverlays();
                else
                    ShowOverlays();
                e.Handled = true;
                break;
        }
    }

    private bool IsClickOnTransparentArea(DependencyObject? element)
    {
        while (element != null)
        {
            if (element == ControlBarPanel || element == SidebarPanel)
                return false;
            if (element is System.Windows.Controls.Button)
                return false;
            element = System.Windows.Media.VisualTreeHelper.GetParent(element);
        }
        return true;
    }

    public void StopTimer()
    {
        _hideTimer?.Stop();
        _mouseCheckTimer?.Stop();
    }
    
    public void StartTracking()
    {
        if (GetCursorPos(out POINT pt))
        {
            _lastMousePosition = new System.Windows.Point(pt.X, pt.Y);
        }
        _mouseCheckTimer?.Start();
        ShowOverlays();
    }
}
