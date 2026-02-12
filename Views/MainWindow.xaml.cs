using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using IPTVPlayer.Models;
using IPTVPlayer.Services;
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

    // Settings for sidebar position
    private readonly SettingsService _settingsService = new();

    // Circular scrolling for windowed channel list
    private const int CircularCopies = 5;
    private const int MiddleCopy = CircularCopies / 2; // copy index 2 of 0-4
    private List<Channel> _circularItems = new();
    private bool _isRecentering = false;
    private bool _suppressSelectionChanged = false;
    private bool _selectionFromListClick = false;
    private bool _isCircularMode = false;
    private int _manualSelectedIndex = -1;

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
        
        // Setup EPG popup positioning
        EpgPopup.CustomPopupPlacementCallback = EpgPopup_Placement;
        
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
        
        // Track MediaPlayer and channel list property changes
        _viewModel.PropertyChanged += (s, args) =>
        {
            if (args.PropertyName == nameof(MainViewModel.MediaPlayer) && _viewModel.MediaPlayer != null)
            {
                Dispatcher.Invoke(() =>
                {
                    VideoView.MediaPlayer = _viewModel.MediaPlayer;
                });
            }
            else if (args.PropertyName == nameof(MainViewModel.FilteredChannels))
            {
                Dispatcher.BeginInvoke(DispatcherPriority.Background, BuildCircularChannelList);
            }
            else if (args.PropertyName == nameof(MainViewModel.SelectedChannel))
            {
                Dispatcher.BeginInvoke(DispatcherPriority.Background, SyncWindowSelectedChannel);
            }
        };
        
        // Apply saved sidebar position
        ApplySidebarPosition(_settingsService.Settings.ChannelListOnRight);

        // Reposition EPG popup when window or video area changes
        LocationChanged += (s, e) => UpdateEpgPopupPosition();
        SizeChanged += (s, e) => UpdateEpgPopupPosition();
        VideoArea.SizeChanged += (s, e) => UpdateEpgPopupPosition();

        // Hide EPG popup when window loses focus so it doesn't float over other apps.
        // Use binding clear/restore instead of setting IsOpen directly, which would
        // break the data binding and prevent ToggleEpg from working afterwards.
        Deactivated += (s, e) =>
        {
            if (!_isFullscreen)
            {
                System.Windows.Data.BindingOperations.ClearBinding(EpgPopup, System.Windows.Controls.Primitives.Popup.IsOpenProperty);
                EpgPopup.IsOpen = false;
            }
        };
        Activated += (s, e) =>
        {
            if (!_isFullscreen)
            {
                var epgBinding = new System.Windows.Data.Binding("IsEpgVisible") { Mode = System.Windows.Data.BindingMode.TwoWay };
                EpgPopup.SetBinding(System.Windows.Controls.Primitives.Popup.IsOpenProperty, epgBinding);
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
        if (_suppressSelectionChanged) return;
        if (e.AddedItems.Count > 0 && e.AddedItems[0] is Channel channel)
        {
            _manualSelectedIndex = ChannelList.SelectedIndex;
            _selectionFromListClick = true;
            _viewModel.SelectedChannel = channel;
            _viewModel.PlaySelectedChannel();
        }
    }

    private void ChannelList_ScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        if (!_isCircularMode || _isRecentering || _viewModel == null) return;
        var channels = _viewModel.FilteredChannels;
        if (channels.Count == 0) return;

        var scrollViewer = FindListBoxScrollViewer(ChannelList);
        if (scrollViewer == null || scrollViewer.ScrollableHeight <= 0) return;

        double oneCopyHeight = scrollViewer.ScrollableHeight / (CircularCopies - 1);

        // Re-center if scrolled beyond 1 copy from the middle (far enough to be invisible)
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
                SetWindowManualSelection(middleCopyIndex);
            }

            _isRecentering = false;
        }
    }

    private void BuildCircularChannelList()
    {
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
        ChannelList.ItemsSource = null;
        ChannelList.ItemsSource = channels;
        _suppressSelectionChanged = false;

        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () =>
        {
            var scrollViewer = FindListBoxScrollViewer(ChannelList);
            bool needsCircular = scrollViewer != null && scrollViewer.ScrollableHeight > 0;

            if (!needsCircular)
            {
                // Channels don't fill the viewport — plain list is fine
                _isCircularMode = false;
                SyncWindowSelectedChannel();
                return;
            }

            // Build circular list with N copies
            _circularItems.Clear();
            for (int copy = 0; copy < CircularCopies; copy++)
            {
                foreach (var ch in channels)
                {
                    _circularItems.Add(ch);
                }
            }

            _suppressSelectionChanged = true;
            ChannelList.ItemsSource = null;
            ChannelList.ItemsSource = _circularItems;
            _suppressSelectionChanged = false;
            _isCircularMode = true;

            Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () =>
            {
                var sv = FindListBoxScrollViewer(ChannelList);
                if (sv != null && sv.ScrollableHeight > 0)
                {
                    double oneCopyHeight = sv.ScrollableHeight / (CircularCopies - 1);
                    sv.ScrollToVerticalOffset(MiddleCopy * oneCopyHeight);
                }
                SyncWindowSelectedChannel();
            });
        });
    }

    private void SyncWindowSelectedChannel()
    {
        var channels = _viewModel.FilteredChannels;
        var selected = _viewModel.SelectedChannel;
        if (selected == null || channels.Count == 0) return;

        if (_selectionFromListClick)
        {
            _selectionFromListClick = false;
            if (_isCircularMode)
            {
                int clickIdx = channels.IndexOf(selected);
                if (clickIdx >= 0)
                {
                    int middleIndex = MiddleCopy * channels.Count + clickIdx;
                    SetWindowManualSelection(middleIndex);
                }
            }
            return;
        }

        int idx = channels.IndexOf(selected);
        if (idx < 0) return;

        if (_isCircularMode)
        {
            int middleIndex = MiddleCopy * channels.Count + idx;
            SetWindowManualSelection(middleIndex);
        }
        else
        {
            _suppressSelectionChanged = true;
            ChannelList.SelectedIndex = idx;
            _suppressSelectionChanged = false;
        }
    }

    private void SetWindowManualSelection(int index)
    {
        _suppressSelectionChanged = true;
        var channels = _viewModel.FilteredChannels;
        int channelCount = channels.Count;

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

        ChannelList.SelectedIndex = -1;
        _manualSelectedIndex = index;

        // Select all copies of the new channel
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

    private static ScrollViewer? FindListBoxScrollViewer(DependencyObject parent)
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is ScrollViewer sv) return sv;
            var result = FindListBoxScrollViewer(child);
            if (result != null) return result;
        }
        return null;
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
            case Key.G:
                ViewModel.ToggleEpgCommand.Execute(null);
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

    private void ToggleSidebar_Click(object sender, RoutedEventArgs e)
    {
        if (Sidebar.Visibility == Visibility.Visible)
        {
            Sidebar.Visibility = Visibility.Collapsed;
            SidebarColumn.Width = new GridLength(0);
        }
        else
        {
            Sidebar.Visibility = Visibility.Visible;
            SidebarColumn.Width = new GridLength(200);
        }
    }

    private void ToggleSidebarPosition_Click(object sender, RoutedEventArgs e)
    {
        bool newValue = !_settingsService.Settings.ChannelListOnRight;
        _settingsService.Settings.ChannelListOnRight = newValue;
        _settingsService.Save();
        ApplySidebarPosition(newValue);
    }

    private void ApplySidebarPosition(bool onRight)
    {
        if (onRight)
        {
            // Sidebar on right: video col 0, sidebar col 1
            Grid.SetColumn(Sidebar, 1);
            Grid.SetColumn(VideoArea, 0);
            MainGrid.ColumnDefinitions[0].Width = new GridLength(1, GridUnitType.Star);
            MainGrid.ColumnDefinitions[1].Width = Sidebar.Visibility == Visibility.Visible
                ? new GridLength(200) : new GridLength(0);
            SidebarColumn = MainGrid.ColumnDefinitions[1];
            ToggleSidebarPositionIcon.Text = "\uE72B"; // Arrow left
            ToggleSidebarPositionButton.ToolTip = "Move channel list to left side";
        }
        else
        {
            // Sidebar on left (default): sidebar col 0, video col 1
            Grid.SetColumn(Sidebar, 0);
            Grid.SetColumn(VideoArea, 1);
            MainGrid.ColumnDefinitions[0].Width = Sidebar.Visibility == Visibility.Visible
                ? new GridLength(200) : new GridLength(0);
            MainGrid.ColumnDefinitions[1].Width = new GridLength(1, GridUnitType.Star);
            SidebarColumn = MainGrid.ColumnDefinitions[0];
            ToggleSidebarPositionIcon.Text = "\uE72A"; // Arrow right
            ToggleSidebarPositionButton.ToolTip = "Move channel list to right side";
        }

        // Reposition EPG popup to the opposite side
        UpdateEpgPopupPosition();
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
        // Clear the binding and force-close the popup so it stays hidden during fullscreen
        System.Windows.Data.BindingOperations.ClearBinding(EpgPopup, System.Windows.Controls.Primitives.Popup.IsOpenProperty);
        EpgPopup.IsOpen = false;
        
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
        
        // Get the screen the window is currently on
        var windowInteropHelper = new System.Windows.Interop.WindowInteropHelper(this);
        var screen = System.Windows.Forms.Screen.FromHandle(windowInteropHelper.Handle);
        var bounds = screen.Bounds;
        
        // Convert physical pixels to WPF DIPs (device-independent pixels)
        var source = PresentationSource.FromVisual(this);
        double dpiScaleX = source?.CompositionTarget?.TransformFromDevice.M11 ?? 1.0;
        double dpiScaleY = source?.CompositionTarget?.TransformFromDevice.M22 ?? 1.0;
        
        // Position window to cover entire current screen including taskbar
        Left = bounds.Left * dpiScaleX;
        Top = bounds.Top * dpiScaleY;
        Width = bounds.Width * dpiScaleX;
        Height = bounds.Height * dpiScaleY;

        _isFullscreen = true;
        
        // Update fullscreen icon
        FullscreenIcon.Text = "\uE73F"; // Exit fullscreen icon
        
        // Delay overlay creation until window is positioned
        Dispatcher.BeginInvoke(new Action(() =>
        {
            CreateOverlayWindow();
            
            // Update overlay position to match fullscreen window on current screen
            if (_overlayWindow != null)
            {
                _overlayWindow.Left = Left;
                _overlayWindow.Top = Top;
                _overlayWindow.Width = Width;
                _overlayWindow.Height = Height;
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
        SidebarColumn.Width = new GridLength(200);
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
        
        // Restore EPG popup binding to IsEpgVisible
        var epgBinding = new System.Windows.Data.Binding("IsEpgVisible") { Mode = System.Windows.Data.BindingMode.TwoWay };
        EpgPopup.SetBinding(System.Windows.Controls.Primitives.Popup.IsOpenProperty, epgBinding);
        
        // Update fullscreen icon
        FullscreenIcon.Text = "\uE740"; // Enter fullscreen icon

        // Re-sync windowed channel list (selection may have changed in fullscreen)
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, BuildCircularChannelList);
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

    private CustomPopupPlacement[] EpgPopup_Placement(System.Windows.Size popupSize, System.Windows.Size targetSize, System.Windows.Point offset)
    {
        // Position EPG on the opposite side of the sidebar
        bool sidebarOnRight = _settingsService.Settings.ChannelListOnRight;
        double x;
        if (sidebarOnRight)
        {
            // Sidebar on right → EPG on bottom-left of video area
            x = 20;
        }
        else
        {
            // Sidebar on left → EPG on bottom-right of video area
            x = targetSize.Width - popupSize.Width - 20;
        }

        var placement = new CustomPopupPlacement(
            new System.Windows.Point(x, targetSize.Height - popupSize.Height - 20),
            PopupPrimaryAxis.None);
        return new[] { placement };
    }

    private void UpdateEpgPopupPosition()
    {
        if (!EpgPopup.IsOpen) return;
        // Force the Popup to recalculate its position by toggling the offset
        var offset = EpgPopup.HorizontalOffset;
        EpgPopup.HorizontalOffset = offset + 1;
        EpgPopup.HorizontalOffset = offset;
    }
}
