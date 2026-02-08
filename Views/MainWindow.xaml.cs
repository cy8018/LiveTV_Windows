using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using System.Windows.Forms.Integration;
using IPTVPlayer.Models;
using IPTVPlayer.ViewModels;

namespace IPTVPlayer.Views;

public partial class MainWindow : Window
{
    private readonly MainViewModel _viewModel;
    private MainViewModel ViewModel => _viewModel;
    private bool _isFullscreen;
    private WindowState _previousWindowState;
    private WindowStyle _previousWindowStyle;
    private ResizeMode _previousResizeMode;
    private Rect _previousWindowRect;
    
    // Fullscreen overlay window
    private FullscreenOverlayWindow? _overlayWindow;
    private DispatcherTimer? _mouseIdleTimer;
    private bool _overlayVisible;
    
    // Double-click detection
    private DateTime _lastClickTime = DateTime.MinValue;
    private const int DoubleClickTimeMs = 400;

    // Windows API for dark title bar
    [DllImport("dwmapi.dll", PreserveSig = true)]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int attrValue, int attrSize);

    private const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;

    public MainWindow()
    {
        Debug.WriteLine("[IPTV] MainWindow constructor starting");
        
        // Create ViewModel
        _viewModel = new MainViewModel();
        
        Debug.WriteLine("[IPTV] Calling InitializeComponent");
        InitializeComponent();
        
        // Set DataContext
        DataContext = _viewModel;
        
        // Enable dark title bar
        SourceInitialized += MainWindow_SourceInitialized;
        
        // Setup mouse idle timer for fullscreen
        _mouseIdleTimer = new DispatcherTimer();
        _mouseIdleTimer.Interval = TimeSpan.FromSeconds(5);
        _mouseIdleTimer.Tick += MouseIdleTimer_Tick;
        
        Debug.WriteLine("[IPTV] MainWindow constructor completed");
    }
    
    private void MouseIdleTimer_Tick(object? sender, EventArgs e)
    {
        _mouseIdleTimer?.Stop();
        if (_isFullscreen && _overlayVisible)
        {
            HideOverlay();
        }
    }

    private void MainWindow_SourceInitialized(object? sender, EventArgs e)
    {
        // Apply dark title bar
        var hwnd = new WindowInteropHelper(this).Handle;
        int value = 1; // Enable dark mode
        DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE, ref value, sizeof(int));
    }

    private async void Window_Loaded(object sender, RoutedEventArgs e)
    {
        Debug.WriteLine("[IPTV] Window_Loaded fired");
        
        // Initialize ViewModel (LibVLC loads on background thread)
        await _viewModel.InitializeAsync();
        
        // Set MediaPlayer on VideoView after initialization
        if (_viewModel.MediaPlayer != null)
        {
            VideoView.MediaPlayer = _viewModel.MediaPlayer;
        }
        
        // Set VideoView background to black (affects letterbox areas)
        VideoView.Background = new SolidColorBrush(Colors.Black);
        
        // Set the internal WinForms control background to black
        SetVideoViewBackgroundColor();
        
        // Track MediaPlayer property changes
        _viewModel.PropertyChanged += (s, args) =>
        {
            if (args.PropertyName == nameof(MainViewModel.MediaPlayer) && _viewModel.MediaPlayer != null)
            {
                Dispatcher.Invoke(() =>
                {
                    VideoView.MediaPlayer = _viewModel.MediaPlayer;
                });
            }
        };
        
        // Setup mouse tracking for video area
        VideoView.MouseMove += VideoView_MouseMove;
        VideoView.MouseDoubleClick += VideoView_MouseDoubleClick;
        VideoView.MouseLeftButtonDown += VideoView_MouseLeftButtonDown;
        
        // Hook into the WinForms control for mouse events (since WPF overlay can't sit on top)
        HookVideoViewMouseEvents();
    }

    private void HookVideoViewMouseEvents()
    {
        // Wait for the VideoView to be fully loaded
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() =>
        {
            try
            {
                var wfh = FindVisualChild<WindowsFormsHost>(VideoView);
                if (wfh?.Child != null)
                {
                    wfh.Child.MouseDown += WinFormsChild_MouseDown;
                    wfh.Child.MouseMove += WinFormsChild_MouseMove;
                    Debug.WriteLine("[IPTV] Hooked WinForms mouse events");
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[IPTV] Failed to hook mouse events: {ex.Message}");
            }
        }));
    }

    private void WinFormsChild_MouseDown(object? sender, System.Windows.Forms.MouseEventArgs e)
    {
        if (e.Button == System.Windows.Forms.MouseButtons.Left)
        {
            Dispatcher.Invoke(() =>
            {
                var now = DateTime.Now;
                var elapsed = (now - _lastClickTime).TotalMilliseconds;
                
                if (elapsed < DoubleClickTimeMs)
                {
                    // Double-click detected
                    ToggleFullscreen();
                    _lastClickTime = DateTime.MinValue;
                }
                else
                {
                    _lastClickTime = now;
                    
                    // Single click - allow dragging when not in fullscreen
                    if (!_isFullscreen)
                    {
                        try
                        {
                            DragMove();
                        }
                        catch
                        {
                            // DragMove can throw if button is released quickly
                        }
                    }
                }
            });
        }
    }

    private void WinFormsChild_MouseMove(object? sender, System.Windows.Forms.MouseEventArgs e)
    {
        if (_isFullscreen)
        {
            Dispatcher.Invoke(() =>
            {
                if (!_overlayVisible)
                {
                    ShowOverlay();
                }
                else
                {
                    ResetMouseIdleTimer();
                }
            });
        }
    }

    private void SetVideoViewBackgroundColor()
    {
        // Find the WindowsFormsHost inside VideoView and set background to black
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() =>
        {
            try
            {
                // Find WindowsFormsHost in VideoView's visual tree
                var wfh = FindVisualChild<WindowsFormsHost>(VideoView);
                if (wfh?.Child != null)
                {
                    wfh.Child.BackColor = System.Drawing.Color.Black;
                    Debug.WriteLine("[IPTV] Set WinForms control background to black");
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[IPTV] Failed to set video background: {ex.Message}");
            }
        }));
    }

    private static T? FindVisualChild<T>(DependencyObject parent) where T : DependencyObject
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T found)
                return found;
            var result = FindVisualChild<T>(child);
            if (result != null)
                return result;
        }
        return null;
    }

    private bool _isClosingConfirmed = false;

    private void Window_Closing(object sender, System.ComponentModel.CancelEventArgs e)
    {
        if (!_isClosingConfirmed)
        {
            e.Cancel = true;
            Dispatcher.BeginInvoke(new Action(() => ShowCloseConfirmation()));
            return;
        }

        _mouseIdleTimer?.Stop();
        _overlayWindow?.Close();
        ViewModel.Cleanup();
    }

    private void ShowCloseConfirmation()
    {
        var result = ConfirmDialog.Show(this, "Confirm Exit", "Are you sure you want to exit Live TV?");

        if (result)
        {
            _isClosingConfirmed = true;
            Close();
        }
    }

    private void MinimizeButton_Click(object sender, RoutedEventArgs e)
    {
        WindowState = WindowState.Minimized;
    }

    private void MaximizeButton_Click(object sender, RoutedEventArgs e)
    {
        if (WindowState == WindowState.Maximized)
        {
            WindowState = WindowState.Normal;
        }
        else
        {
            WindowState = WindowState.Maximized;
        }
    }

    private void Window_StateChanged(object sender, EventArgs e)
    {
        // Update maximize/restore icon
        if (WindowState == WindowState.Maximized)
        {
            MaximizeIcon.Text = "\uE923"; // Restore icon
        }
        else
        {
            MaximizeIcon.Text = "\uE739"; // Maximize icon
        }
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }

    private void ChannelList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        PlaySelectedFromSource(e.OriginalSource as DependencyObject);
    }

    private void PlaySelectedFromSource(DependencyObject? source)
    {
        var element = source;
        while (element != null && element is not ListBoxItem)
        {
            element = VisualTreeHelper.GetParent(element);
        }

        if (element is ListBoxItem listBoxItem && listBoxItem.DataContext is Channel channel)
        {
            ViewModel.SelectedChannel = channel;
            ViewModel.PlaySelectedChannel();
        }
    }

    private void ChannelList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        // Single click to play
        if (ViewModel.SelectedChannel != null)
        {
            ViewModel.PlaySelectedChannel();
        }
    }

    private void Window_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        // Any key press counts as activity in fullscreen
        if (_isFullscreen)
        {
            if (!_overlayVisible)
                ShowOverlay();
            else
                ResetMouseIdleTimer();
        }

        switch (e.Key)
        {
            case Key.Space:
                ViewModel.PlayPauseCommand.Execute(null);
                e.Handled = true;
                break;
            case Key.Enter:
                if (ViewModel.SelectedChannel != null)
                {
                    ViewModel.PlaySelectedChannel();
                    e.Handled = true;
                }
                break;
            case Key.F:
            case Key.F11:
                ToggleFullscreen();
                e.Handled = true;
                break;
            case Key.Escape:
                if (_isFullscreen)
                {
                    ExitFullscreen();
                    e.Handled = true;
                }
                break;
            case Key.M:
                ViewModel.MuteCommand.Execute(null);
                e.Handled = true;
                break;
            case Key.PageUp:
                ViewModel.PreviousChannelCommand.Execute(null);
                e.Handled = true;
                break;
            case Key.PageDown:
                ViewModel.NextChannelCommand.Execute(null);
                e.Handled = true;
                break;
            case Key.Up:
                if (Keyboard.Modifiers == ModifierKeys.Control)
                {
                    ViewModel.Volume = Math.Min(100, ViewModel.Volume + 5);
                    e.Handled = true;
                }
                break;
            case Key.Down:
                if (Keyboard.Modifiers == ModifierKeys.Control)
                {
                    ViewModel.Volume = Math.Max(0, ViewModel.Volume - 5);
                    e.Handled = true;
                }
                break;
        }
    }

    private void Fullscreen_Click(object sender, RoutedEventArgs e)
    {
        ToggleFullscreen();
    }

    private void EditChannels_Click(object sender, RoutedEventArgs e)
    {
        if (_viewModel.Channels.Count == 0)
        {
            System.Windows.MessageBox.Show("No channels loaded. Please open a playlist first.", 
                "No Channels", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var editorWindow = new ChannelEditorWindow(_viewModel.Channels)
        {
            Owner = this
        };

        editorWindow.ShowDialog();

        if (editorWindow.SaveChanges)
        {
            _viewModel.SaveChannelCustomizations();
        }
    }

    private void ToggleFullscreen()
    {
        if (_isFullscreen)
        {
            ExitFullscreen();
        }
        else
        {
            EnterFullscreen();
        }
    }

    private void EnterFullscreen()
    {
        _previousWindowState = WindowState;
        _previousWindowStyle = WindowStyle;
        _previousResizeMode = ResizeMode;
        _previousWindowRect = new Rect(Left, Top, Width, Height);
        
        // Hide normal UI
        Sidebar.Visibility = Visibility.Collapsed;
        SidebarColumn.Width = new GridLength(0);
        ControlBar.Visibility = Visibility.Collapsed;
        ControlBarRow.Height = new GridLength(0);
        
        // Hide the custom title bar by setting row height to 0
        if (MainGrid.Parent is Grid outerGrid && outerGrid.RowDefinitions.Count > 0)
        {
            outerGrid.RowDefinitions[0].Height = new GridLength(0);
        }
        
        // Set window to true fullscreen (covering taskbar)
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        Topmost = true;
        
        // First set to normal to ensure position changes take effect
        WindowState = WindowState.Normal;
        
        // Position window to cover entire screen including taskbar
        Left = 0;
        Top = 0;
        Width = SystemParameters.PrimaryScreenWidth;
        Height = SystemParameters.PrimaryScreenHeight;

        _isFullscreen = true;
        
        // Update fullscreen icon
        FullscreenIcon.Text = "\uE73F"; // Exit fullscreen icon
        
        // Delay overlay creation until window is positioned
        Dispatcher.BeginInvoke(new Action(() =>
        {
            CreateOverlayWindow();
            
            // Update overlay position to match maximized window
            if (_overlayWindow != null)
            {
                _overlayWindow.Left = 0;
                _overlayWindow.Top = 0;
                _overlayWindow.Width = SystemParameters.PrimaryScreenWidth;
                _overlayWindow.Height = SystemParameters.PrimaryScreenHeight;
            }
            
            ShowOverlay();
        }), DispatcherPriority.Loaded);
        
        Focus();
    }

    private void ExitFullscreen()
    {
        // Hide and close overlay window
        _mouseIdleTimer?.Stop();
        _overlayWindow?.Hide();
        
        // Show normal UI
        Sidebar.Visibility = Visibility.Visible;
        SidebarColumn.Width = new GridLength(300);
        ControlBar.Visibility = Visibility.Visible;
        ControlBarRow.Height = GridLength.Auto;
        
        // Restore the custom title bar
        if (MainGrid.Parent is Grid outerGrid && outerGrid.RowDefinitions.Count > 0)
        {
            outerGrid.RowDefinitions[0].Height = new GridLength(48);
        }
        
        // Restore window state
        Topmost = false;
        WindowStyle = _previousWindowStyle;
        ResizeMode = _previousResizeMode;
        
        // Restore position and size
        Left = _previousWindowRect.Left;
        Top = _previousWindowRect.Top;
        Width = _previousWindowRect.Width;
        Height = _previousWindowRect.Height;
        WindowState = _previousWindowState;

        _isFullscreen = false;
        
        // Update fullscreen icon
        FullscreenIcon.Text = "\uE740"; // Enter fullscreen icon
    }
    
    private void CreateOverlayWindow()
    {
        if (_overlayWindow == null)
        {
            _overlayWindow = new FullscreenOverlayWindow();
            _overlayWindow.SetViewModel(_viewModel);
            _overlayWindow.Owner = this;
            _overlayWindow.ExitFullscreenRequested += (s, e) => ExitFullscreen();
            _overlayWindow.MouseActivity += (s, e) => ResetMouseIdleTimer();
            _overlayWindow.ChannelSelected += (s, channel) =>
            {
                _viewModel.SelectedChannel = channel;
                _viewModel.PlaySelectedChannel();
            };
            _overlayWindow.CloseAppRequested += (s, e) => 
            {
                ExitFullscreen();
                ShowCloseConfirmation();
            };
        }
        
        // Position overlay to cover the full window
        _overlayWindow.Left = Left;
        _overlayWindow.Top = Top;
        _overlayWindow.Width = ActualWidth;
        _overlayWindow.Height = ActualHeight;
    }
    
    private void ShowOverlay()
    {
        if (_overlayWindow != null && _isFullscreen)
        {
            _overlayWindow.UpdateFromViewModel();
            _overlayWindow.Show();
            _overlayWindow.StartTracking();
            _overlayWindow.Activate();
            _overlayVisible = true;
        }
    }
    
    private void HideOverlay()
    {
        // Don't hide the window - just let the overlay handle its own visibility
        // The overlay window stays visible to capture mouse events
        if (_overlayWindow != null)
        {
            _overlayWindow.HideOverlays();
            _overlayVisible = false;
        }
    }
    
    private void ResetMouseIdleTimer()
    {
        _mouseIdleTimer?.Stop();
        _mouseIdleTimer?.Start();
    }

    private void VideoView_MouseMove(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if (_isFullscreen)
        {
            if (!_overlayVisible)
            {
                ShowOverlay();
            }
            else
            {
                ResetMouseIdleTimer();
            }
        }
    }

    private void VideoView_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        ToggleFullscreen();
    }

    private void VideoView_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        // Allow dragging the window when not in fullscreen
        if (!_isFullscreen && e.ClickCount == 1)
        {
            DragMove();
        }
    }

    private void VideoMouseOverlay_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        var now = DateTime.Now;
        var elapsed = (now - _lastClickTime).TotalMilliseconds;
        
        if (elapsed < DoubleClickTimeMs)
        {
            // Double-click detected
            ToggleFullscreen();
            _lastClickTime = DateTime.MinValue; // Reset to prevent triple-click
        }
        else
        {
            _lastClickTime = now;
            
            // Single click - allow dragging when not in fullscreen
            if (!_isFullscreen)
            {
                try
                {
                    DragMove();
                }
                catch
                {
                    // DragMove can throw if button is released quickly
                }
            }
        }
    }

    private void Window_TouchDown(object sender, TouchEventArgs e)
    {
        if (_isFullscreen)
        {
            if (!_overlayVisible)
                ShowOverlay();
            else
                ResetMouseIdleTimer();
        }
    }

    private void Window_TouchMove(object sender, TouchEventArgs e)
    {
        if (_isFullscreen)
        {
            if (!_overlayVisible)
                ShowOverlay();
            else
                ResetMouseIdleTimer();
        }
    }

    private void VideoMouseOverlay_MouseMove(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if (_isFullscreen)
        {
            if (!_overlayVisible)
            {
                ShowOverlay();
            }
            else
            {
                ResetMouseIdleTimer();
            }
        }
    }
}
