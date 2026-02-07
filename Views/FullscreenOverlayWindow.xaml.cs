using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using IPTVPlayer.Models;
using IPTVPlayer.ViewModels;

namespace IPTVPlayer.Views;

public partial class FullscreenOverlayWindow : Window
{
    private MainViewModel? _viewModel;
    private DispatcherTimer? _hideTimer;
    private DispatcherTimer? _mouseCheckTimer;
    private DispatcherTimer? _clockTimer;
    private System.Windows.Point _lastMousePosition;
    private const int AutoHideDelaySeconds = 5;
    private bool _overlaysVisible = true;
    private DateTime _lastHideTime = DateTime.MinValue;
    private const int HideCooldownMs = 500; // Ignore mouse events for 500ms after hiding

    // Circular scrolling
    private const int CircularCopies = 3; // Number of copies of the channel list
    private List<Channel> _circularItems = new();
    private bool _isRecentering = false;
    private bool _suppressSelectionChanged = false;

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
            string icon = status.PowerLineStatus == System.Windows.Forms.PowerLineStatus.Online
                ? "\uEA93"  // Charging icon
                : percent > 75 ? "\uE83F"
                : percent > 50 ? "\uE83E"
                : percent > 25 ? "\uE83D"
                : percent > 10 ? "\uE83C"
                : "\uE850"; // Low battery

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
        
        // Listen for channel list changes to rebuild circular list
        viewModel.FilteredChannels.CollectionChanged += (s, e) =>
        {
            Dispatcher.BeginInvoke(DispatcherPriority.Background, BuildCircularList);
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

    public void ShowOverlays()
    {
        ControlBarPanel.Visibility = Visibility.Visible;
        SidebarTransform.X = 0; // Show sidebar
        _overlaysVisible = true;
        ResetHideTimer();
    }

    public void HideOverlays()
    {
        ControlBarPanel.Visibility = Visibility.Collapsed;
        SidebarTransform.X = -320; // Hide sidebar
        _overlaysVisible = false;
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

    private void ShowChannels_Click(object sender, RoutedEventArgs e)
    {
        // Sidebar is always shown with overlays, just reset the timer
        ResetHideTimer();
    }

    private void CloseSidebar_Click(object sender, RoutedEventArgs e)
    {
        // Hide everything
        HideOverlays();
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
        if (e.AddedItems.Count > 0 && e.AddedItems[0] is Channel channel)
        {
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
        if (_isRecentering || _viewModel == null) return;
        var channels = _viewModel.FilteredChannels;
        if (channels.Count == 0) return;

        var scrollViewer = FindScrollViewer(ChannelList);
        if (scrollViewer == null || scrollViewer.ScrollableHeight <= 0) return;

        // Calculate the scroll offset for one full copy of the list
        double oneCopyHeight = scrollViewer.ScrollableHeight / (CircularCopies - 1);
        double middleStart = oneCopyHeight; // Start of middle copy

        // If scrolled into the first copy or third copy, re-center to the equivalent position in the middle copy
        if (scrollViewer.VerticalOffset < oneCopyHeight * 0.3 || 
            scrollViewer.VerticalOffset > oneCopyHeight * 1.7)
        {
            _isRecentering = true;
            double currentOffset = scrollViewer.VerticalOffset;
            // Map to position within middle copy
            double newOffset = middleStart + (currentOffset % oneCopyHeight);
            
            // Preserve selection
            var selectedChannel = ChannelList.SelectedItem as Channel;
            int selectedIndexInCopy = -1;
            if (selectedChannel != null)
            {
                selectedIndexInCopy = channels.IndexOf(selectedChannel) >= 0 
                    ? channels.IndexOf(selectedChannel)
                    : ChannelList.SelectedIndex % channels.Count;
            }

            scrollViewer.ScrollToVerticalOffset(newOffset);

            // Re-select the equivalent item in the middle copy
            if (selectedIndexInCopy >= 0)
            {
                int middleCopyIndex = channels.Count + selectedIndexInCopy;
                _suppressSelectionChanged = true;
                ChannelList.SelectedIndex = middleCopyIndex;
                _suppressSelectionChanged = false;
            }

            _isRecentering = false;
        }
    }

    private void BuildCircularList()
    {
        if (_viewModel == null) return;
        var channels = _viewModel.FilteredChannels;
        
        _circularItems.Clear();
        if (channels.Count == 0)
        {
            ChannelList.ItemsSource = null;
            return;
        }

        // Build list with N copies for seamless circular scrolling
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

        // Scroll to middle copy after layout
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () =>
        {
            var scrollViewer = FindScrollViewer(ChannelList);
            if (scrollViewer != null && scrollViewer.ScrollableHeight > 0)
            {
                double oneCopyHeight = scrollViewer.ScrollableHeight / (CircularCopies - 1);
                scrollViewer.ScrollToVerticalOffset(oneCopyHeight);
            }

            // Select the current channel in the middle copy
            if (_viewModel.SelectedChannel != null)
            {
                int idx = _viewModel.FilteredChannels.IndexOf(_viewModel.SelectedChannel);
                if (idx >= 0)
                {
                    int middleIndex = channels.Count + idx;
                    _suppressSelectionChanged = true;
                    ChannelList.SelectedIndex = middleIndex;
                    ChannelList.ScrollIntoView(ChannelList.SelectedItem);
                    _suppressSelectionChanged = false;
                }
            }
        });
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

    protected override void OnMouseDown(MouseButtonEventArgs e)
    {
        base.OnMouseDown(e);
        MouseActivity?.Invoke(this, EventArgs.Empty);

        // Check if clicking on video area (transparent part)
        var element = e.OriginalSource as DependencyObject;
        if (IsClickOnTransparentArea(element))
        {
            // Single click/tap on video: just reset the idle timer (keep overlay visible)
            if (_overlaysVisible)
            {
                ResetHideTimer();
            }
            else
            {
                ShowOverlays();
            }
        }
        // Don't reset timer when clicking on overlay elements - let it auto-hide
    }

    protected override void OnTouchDown(TouchEventArgs e)
    {
        base.OnTouchDown(e);
        // Only show overlays if hidden, don't reset timer
        if (!_overlaysVisible)
        {
            ShowOverlays();
        }
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
