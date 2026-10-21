using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using LettoreVideo.Mpv;
using LettoreVideo.Playlist;
using LettoreVideo.ViewModels;

namespace LettoreVideo;

public partial class MainWindow : Window
{
    private readonly MainViewModel _vm = new();

    private WindowState _preFullscreenState;
    private WindowStyle _preFullscreenStyle;
    private GridLength _prePlaylistWidth;
    private bool _isFullscreen;

    /// <summary>
    /// Coalesces the SizeChanged storm of a resize drag into one VSR update:
    /// re-scaling means rebuilding the d3d11vpp chain, which stutters if done
    /// on every intermediate size.
    /// </summary>
    private readonly DispatcherTimer _vsrResizeDebounce = new() { Interval = TimeSpan.FromMilliseconds(250) };

    public MainWindow()
    {
        InitializeComponent();
        DataContext = _vm;

        // mpv scales up to the physical pixels of the video surface, not WPF's
        // device-independent units, so RTX VSR targets the real output resolution.
        _vm.GetTargetHeight = () =>
        {
            double dpiScale = VisualTreeHelper.GetDpi(this).DpiScaleY;
            return (int)Math.Round(VideoHost.ActualHeight * dpiScale);
        };

        _vm.FullscreenRequested += (_, _) => ToggleFullscreen();
        _vm.PropertyChanged += OnViewModelPropertyChanged;

        // "wid" has to be set on an uninitialized mpv handle, so we attach as soon
        // as the native surface exists.
        VideoHost.SurfaceCreated += (_, _) => _vm.Attach(VideoHost.VideoHandle);

        // Re-evaluate the upscale factor once the resize has settled.
        _vsrResizeDebounce.Tick += (_, _) =>
        {
            _vsrResizeDebounce.Stop();
            _vm.ApplyVsr();
        };
        VideoHost.SizeChanged += (_, _) =>
        {
            _vsrResizeDebounce.Stop();
            _vsrResizeDebounce.Start();
        };

        // Slider's own class handler implements IsMoveToPointEnabled and marks the
        // event handled, so a plain XAML handler would never see a track click.
        // handledEventsToo also puts us after it, once Value is already updated.
        SeekBar.AddHandler(PreviewMouseLeftButtonDownEvent,
            new MouseButtonEventHandler(SeekBar_PreviewMouseLeftButtonDown), handledEventsToo: true);

        // Loading the playlist touches the ListBox, so it has to wait until the
        // visual tree is built — SurfaceCreated fires mid-layout, too early.
        Loaded += (_, _) =>
        {
            if (App.StartupFolder is { } folder)
                _vm.LoadFolder(folder);
        };
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(MainViewModel.CurrentIndex)) return;

        if (_vm.CurrentItem is { } item && PlaylistBox.Items.Count > 0)
            PlaylistBox.ScrollIntoView(item);
    }

    #region Playlist

    private void PlaylistBox_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (((FrameworkElement)e.OriginalSource).DataContext is PlaylistItem item)
            _vm.PlayItemCommand.Execute(item);
    }

    #endregion

    #region Seeking

    private void SeekBar_DragStarted(object sender, DragStartedEventArgs e) => _vm.BeginSeek();

    private void SeekBar_DragCompleted(object sender, DragCompletedEventArgs e) => _vm.EndSeek(SeekBar.Value);

    /// <summary>
    /// Seeks on a click somewhere along the track. It has to happen on the button
    /// *down*: IsMoveToPointEnabled makes the Slider snap Value to the clicked
    /// point right there, and waiting for the button up would let a "time-pos"
    /// update arrive in between and pull Value back to the current playback
    /// position through the two-way binding — turning the click into a no-op.
    /// </summary>
    private void SeekBar_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        // A press on the thumb starts a drag instead; DragCompleted owns that one.
        if (IsWithinThumb(e.OriginalSource as DependencyObject)) return;

        _vm.EndSeek(SeekBar.Value);
    }

    private bool IsWithinThumb(DependencyObject? source)
    {
        for (DependencyObject? node = source; node is Visual && node != SeekBar;
             node = VisualTreeHelper.GetParent(node))
        {
            if (node is Thumb) return true;
        }

        return false;
    }

    #endregion

    #region RTX VSR

    private void VsrModeBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (DataContext is null) return;

        _vm.VsrMode = VsrModeBox.SelectedIndex switch
        {
            1 => RtxVsrMode.Always,
            2 => RtxVsrMode.Off,
            _ => RtxVsrMode.Auto,
        };
    }

    #endregion

    #region Fullscreen and keyboard

    private void Fullscreen_Click(object sender, RoutedEventArgs e) => ToggleFullscreen();

    private void ToggleFullscreen()
    {
        if (!_isFullscreen)
        {
            _preFullscreenState = WindowState;
            _preFullscreenStyle = WindowStyle;
            _prePlaylistWidth = PlaylistColumn.Width;   // may have been dragged by the user

            WindowStyle = WindowStyle.None;
            WindowState = WindowState.Normal;   // forces a redraw when already maximized
            WindowState = WindowState.Maximized;
            ResizeMode = ResizeMode.NoResize;

            PlaylistPanel.Visibility = Visibility.Collapsed;
            Splitter.Visibility = Visibility.Collapsed;
            PlaylistColumn.Width = new GridLength(0);
            ControlBar.Visibility = Visibility.Collapsed;
        }
        else
        {
            WindowStyle = _preFullscreenStyle;
            WindowState = _preFullscreenState;
            ResizeMode = ResizeMode.CanResize;

            PlaylistPanel.Visibility = Visibility.Visible;
            Splitter.Visibility = Visibility.Visible;
            PlaylistColumn.Width = _prePlaylistWidth;
            ControlBar.Visibility = Visibility.Visible;
        }

        _isFullscreen = !_isFullscreen;
    }

    /// <summary>
    /// True when the focused control legitimately owns this key, so the global
    /// transport shortcut must step aside. Without this the tunneling handler
    /// below would make the RTX VSR combo box and the playlist impossible to
    /// drive from the keyboard.
    /// </summary>
    private bool FocusedControlOwns(Key key)
    {
        if (Keyboard.FocusedElement is not DependencyObject focused) return false;

        // The playlist is deliberately absent from every arm below. Clicking a track
        // parks the focus on a ListBoxItem for the rest of the session, so yielding
        // to it would leave the transport keys permanently dead — which is exactly
        // what happens in the normal "open folder, click a track" flow. The list
        // stays navigable anyway: Up/Down are never intercepted here.
        return key switch
        {
            // Plain buttons are absent too: Space keeps meaning play/pause even when
            // a transport button holds focus, which is what a player should do.
            Key.Space => focused is ToggleButton or TextBoxBase,

            // A focused seek bar is the one Slider that must not swallow the arrows:
            // its own SmallChange moves the handle without ever issuing a seek.
            Key.Left or Key.Right =>
                focused is ComboBox or TextBoxBase || (focused is Slider && focused != SeekBar),

            Key.PageUp or Key.PageDown => focused is ComboBox,

            // Fullscreen is always ours, whatever has focus.
            _ => false,
        };
    }

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        if (FocusedControlOwns(e.Key))
        {
            base.OnPreviewKeyDown(e);
            return;
        }

        switch (e.Key)
        {
            case Key.Space:
                _vm.PlayPauseCommand.Execute(null);
                break;
            case Key.F11:
                ToggleFullscreen();
                break;
            case Key.Escape when _isFullscreen:
                ToggleFullscreen();
                break;
            case Key.Right:
                _vm.EndSeek(_vm.Position + 5);
                break;
            case Key.Left:
                _vm.EndSeek(Math.Max(0, _vm.Position - 5));
                break;
            case Key.PageDown:
                _vm.NextCommand.Execute(null);
                break;
            case Key.PageUp:
                _vm.PreviousCommand.Execute(null);
                break;
            default:
                base.OnPreviewKeyDown(e);
                return;
        }

        e.Handled = true;
    }

    #endregion

    protected override void OnClosed(EventArgs e)
    {
        _vsrResizeDebounce.Stop();
        _vm.Dispose();
        base.OnClosed(e);
    }
}
