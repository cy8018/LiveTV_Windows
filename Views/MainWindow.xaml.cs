using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
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

    [DllImport("dwmapi.dll")]
    private static extern int DwmFlush();

    [DllImport("dwmapi.dll")]
    private static extern int DwmExtendFrameIntoClientArea(IntPtr hWnd, ref MARGINS pMarInset);

    [StructLayout(LayoutKind.Sequential)]
    private struct MARGINS { public int Left, Right, Top, Bottom; }

    // Windows API for subclassing the video HWND to paint black background
    private delegate IntPtr SubclassProc(IntPtr hWnd, uint uMsg, IntPtr wParam, IntPtr lParam, UIntPtr uIdSubclass, IntPtr dwRefData);

    [DllImport("comctl32.dll")]
    private static extern bool SetWindowSubclass(IntPtr hWnd, SubclassProc pfnSubclass, UIntPtr uIdSubclass, IntPtr dwRefData);

    [DllImport("comctl32.dll")]
    private static extern IntPtr DefSubclassProc(IntPtr hWnd, uint uMsg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool GetClientRect(IntPtr hWnd, out RECT lpRect);

    [DllImport("user32.dll")]
    private static extern int FillRect(IntPtr hDC, ref RECT lprc, IntPtr hBrush);

    [DllImport("gdi32.dll")]
    private static extern IntPtr GetStockObject(int fnObject);

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }

    [DllImport("user32.dll")]
    private static extern IntPtr GetParent(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern int GetWindowLong(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll")]
    private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);

    private const int GWL_STYLE = -16;
    private const int GWL_EXSTYLE = -20;
    private const int WS_CLIPCHILDREN = 0x02000000;
    private const int WS_EX_TRANSPARENT = 0x00000020;

    [DllImport("user32.dll")]
    private static extern bool InvalidateRect(IntPtr hWnd, IntPtr lpRect, bool bErase);

    [DllImport("user32.dll")]
    private static extern bool RedrawWindow(IntPtr hWnd, IntPtr lprcUpdate, IntPtr hrgnUpdate, uint flags);

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);

    private const uint RDW_INVALIDATE = 0x0001;
    private const uint RDW_UPDATENOW = 0x0100;
    private const uint RDW_ALLCHILDREN = 0x0080;
    private const uint RDW_ERASE = 0x0004;
    private const uint SWP_NOMOVE = 0x0002;
    private const uint SWP_NOSIZE = 0x0001;
    private const uint SWP_NOZORDER = 0x0004;
    private const uint SWP_FRAMECHANGED = 0x0020;

    private delegate bool EnumChildProc(IntPtr hwnd, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool EnumChildWindows(IntPtr hWndParent, EnumChildProc lpEnumFunc, IntPtr lParam);

    // Windows API for setting window class background brush
    [DllImport("user32.dll", EntryPoint = "SetClassLongPtrW")]
    private static extern IntPtr SetClassLongPtr64(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

    [DllImport("user32.dll", EntryPoint = "SetClassLongW")]
    private static extern int SetClassLong32(IntPtr hWnd, int nIndex, int dwNewLong);

    private const int GCLP_HBRBACKGROUND = -10;

    private const int BLACK_BRUSH = 4;
    private const uint WM_ERASEBKGND = 0x0014;
    private const uint WM_WINDOWPOSCHANGING = 0x0046;
    private const uint WM_SIZE = 0x0005;
    private const uint WM_CTLCOLORSTATIC = 0x0138;
    private const uint SWP_NOCOPYBITS = 0x0100;

    [DllImport("gdi32.dll")]
    private static extern int SetBkColor(IntPtr hdc, int crColor);

    [DllImport("gdi32.dll")]
    private static extern int SetTextColor(IntPtr hdc, int crColor);
    private EnumChildProc? _enumChildProc;
    private readonly List<SubclassProc> _childSubclassProcs = new(); // prevent GC of child subclass delegates
    private readonly HashSet<IntPtr> _subclassedHwnds = new(); // track which HWNDs are already subclassed
    private IntPtr _videoHwndHostHandle;

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
        var hwnd = new WindowInteropHelper(this).Handle;

        // Apply dark title bar — makes DWM frame dark
        int value = 1;
        DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE, ref value, sizeof(int));

        // Set WPF composition target background to black.
        // With GlassFrameThickness="0", WindowChrome does NOT call _ExtendGlassFrame
        // so it won't override this with Colors.Transparent.
        var source = HwndSource.FromHwnd(hwnd);
        if (source?.CompositionTarget != null)
        {
            source.CompositionTarget.BackgroundColor = Colors.Black;
        }

        // Change the Win32 class background brush to black
        IntPtr blackBrush = GetStockObject(BLACK_BRUSH);
        if (IntPtr.Size == 8)
            SetClassLongPtr64(hwnd, GCLP_HBRBACKGROUND, blackBrush);
        else
            SetClassLong32(hwnd, GCLP_HBRBACKGROUND, blackBrush.ToInt32());

        // Hook WndProc for resize handling
        source?.AddHook(MainWindowWndProc);
    }

    private const uint WM_ENTERSIZEMOVE = 0x0231;
    private const uint WM_EXITSIZEMOVE = 0x0232;

    [StructLayout(LayoutKind.Sequential)]
    private struct WINDOWPOS
    {
        public IntPtr hwnd, hwndInsertAfter;
        public int x, y, cx, cy;
        public uint flags;
    }

    private IntPtr MainWindowWndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == (int)WM_WINDOWPOSCHANGING)
        {
            // Add SWP_NOCOPYBITS to prevent the white flash from BitBlt during resize
            var pos = Marshal.PtrToStructure<WINDOWPOS>(lParam);
            pos.flags |= SWP_NOCOPYBITS;
            Marshal.StructureToPtr(pos, lParam, false);
        }
        else if (msg == (int)WM_ERASEBKGND)
        {
            // Fill main window background with black
            GetClientRect(hwnd, out RECT rect);
            FillRect(wParam, ref rect, GetStockObject(BLACK_BRUSH));
            handled = true;
            return (IntPtr)1;
        }
        else if (msg == (int)WM_CTLCOLORSTATIC)
        {
            // The Win32 "static" control (used by LibVLCSharp VideoHwndHost) sends
            // WM_CTLCOLORSTATIC to its PARENT to get a background brush for WM_PAINT.
            // The static control does ALL background painting in WM_PAINT via FillRect
            // with this brush — it ignores WM_ERASEBKGND entirely.
            // Default brush is COLOR_3DFACE (gray/white). Return BLACK_BRUSH instead.
            IntPtr hdc = wParam;
            SetBkColor(hdc, 0x00000000);
            handled = true;
            return GetStockObject(BLACK_BRUSH);
        }
        else if (msg == (int)WM_SIZE)
        {
            // Re-subclass any new child windows (VLC may create them dynamically)
            SubclassAllDescendants(hwnd);
        }
        return IntPtr.Zero;
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
        
        // Set the internal HWND background to black when VideoView becomes visible
        VideoView.Loaded += (s2, e2) => SetVideoViewBackgroundColor();

        // After playback starts, VLC creates its own child windows — subclass them too
        if (_viewModel.MediaPlayer != null)
        {
            _viewModel.MediaPlayer.Playing += (s3, e3) =>
            {
                Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
                {
                    var mainHwnd = new WindowInteropHelper(this).Handle;
                    SubclassAllDescendants(mainHwnd);
                }));
            };
        }
        
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
                var hwndHost = FindVisualChild<HwndHost>(VideoView);
                if (hwndHost != null)
                {
                    // HwndHost doesn't expose a Child control like WindowsFormsHost,
                    // mouse events are handled via the WPF overlay instead
                    Debug.WriteLine("[IPTV] Found HwndHost for video view");
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
        try
        {
            var hwndHost = FindVisualChild<HwndHost>(VideoView);
            if (hwndHost != null && hwndHost.Handle != IntPtr.Zero)
            {
                _videoHwndHostHandle = hwndHost.Handle;

                // FIX: Remove WS_EX_TRANSPARENT from the "static" HWND.
                // LibVLCSharp creates it with WS_EX_TRANSPARENT which means it relies on
                // the parent to paint its background. But the parent (HwndHost intermediate)
                // has WS_CLIPCHILDREN, so it clips around the child. Result: nobody paints
                // the background → white DWM content shows through during resize.
                int exStyle = GetWindowLong(hwndHost.Handle, GWL_EXSTYLE);
                SetWindowLong(hwndHost.Handle, GWL_EXSTYLE, exStyle & ~WS_EX_TRANSPARENT);

                // FIX: Remove WS_CLIPCHILDREN from the intermediate HwndHost HWND
                // so it paints its black background over the entire area (including child).
                // VLC then paints video on top.
                IntPtr parentHwnd = GetParent(hwndHost.Handle);
                if (parentHwnd != IntPtr.Zero)
                {
                    int parentStyle = GetWindowLong(parentHwnd, GWL_STYLE);
                    SetWindowLong(parentHwnd, GWL_STYLE, parentStyle & ~WS_CLIPCHILDREN);
                }

                // Subclass the "static" child HWND
                SubclassHwnd(hwndHost.Handle);

                // Walk up the parent chain to catch all intermediate HWNDs
                parentHwnd = GetParent(hwndHost.Handle);
                IntPtr mainHwnd = new WindowInteropHelper(this).Handle;
                while (parentHwnd != IntPtr.Zero && parentHwnd != mainHwnd)
                {
                    SubclassHwnd(parentHwnd);
                    parentHwnd = GetParent(parentHwnd);
                }

                // Subclass ALL child windows of the main window HWND
                SubclassAllDescendants(mainHwnd);

                Debug.WriteLine("[IPTV] Fixed window styles and subclassed HWNDs for black background");
            }
            else
            {
                Debug.WriteLine("[IPTV] HwndHost not found or handle is zero");
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[IPTV] Failed to set video background: {ex.Message}");
        }
    }

    private void SubclassHwnd(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero || _subclassedHwnds.Contains(hwnd))
            return;

        // Set the class background brush to black for this window
        IntPtr blackBrush = GetStockObject(BLACK_BRUSH);
        if (IntPtr.Size == 8)
            SetClassLongPtr64(hwnd, GCLP_HBRBACKGROUND, blackBrush);
        else
            SetClassLong32(hwnd, GCLP_HBRBACKGROUND, blackBrush.ToInt32());

        // Also subclass to intercept WM_ERASEBKGND and WM_WINDOWPOSCHANGING
        SubclassProc proc = VideoViewSubclassProc;
        _childSubclassProcs.Add(proc); // prevent GC
        SetWindowSubclass(hwnd, proc, (UIntPtr)(100 + _subclassedHwnds.Count), IntPtr.Zero);
        _subclassedHwnds.Add(hwnd);
    }

    private void SubclassAllDescendants(IntPtr parentHwnd)
    {
        _enumChildProc = (childHwnd, _) =>
        {
            SubclassHwnd(childHwnd);
            return true;
        };
        EnumChildWindows(parentHwnd, _enumChildProc, IntPtr.Zero);
    }

    private IntPtr VideoViewSubclassProc(IntPtr hWnd, uint uMsg, IntPtr wParam, IntPtr lParam, UIntPtr uIdSubclass, IntPtr dwRefData)
    {
        if (uMsg == WM_ERASEBKGND)
        {
            // Fill with black (backup, though static controls ignore this)
            GetClientRect(hWnd, out RECT rect);
            FillRect(wParam, ref rect, GetStockObject(BLACK_BRUSH));
            return (IntPtr)1;
        }
        if (uMsg == WM_CTLCOLORSTATIC)
        {
            // THIS IS THE KEY FIX: The Win32 "static" control does ALL its
            // background painting in WM_PAINT using the brush returned from
            // WM_CTLCOLORSTATIC (sent to the parent). The default brush from
            // DefWindowProc is COLOR_3DFACE (gray/white). We return BLACK_BRUSH
            // so the static control fills its background with black.
            IntPtr hdc = wParam;
            SetBkColor(hdc, 0x00000000);   // RGB(0,0,0)
            SetTextColor(hdc, 0x00000000); // RGB(0,0,0)
            return GetStockObject(BLACK_BRUSH);
        }
        if (uMsg == WM_WINDOWPOSCHANGING)
        {
            var pos = Marshal.PtrToStructure<WINDOWPOS>(lParam);
            pos.flags |= SWP_NOCOPYBITS;
            Marshal.StructureToPtr(pos, lParam, false);
        }
        return DefSubclassProc(hWnd, uMsg, wParam, lParam);
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
