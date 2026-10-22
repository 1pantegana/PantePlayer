using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Threading;
using LettoreVideo.Engine;
using LettoreVideo.Engine.Playback;
using LettoreVideo.Playlist;
using Microsoft.Win32;

namespace LettoreVideo.ViewModels;

public sealed class MainViewModel : INotifyPropertyChanged, IDisposable
{
    private readonly PanteEngine _engine = new();
    private readonly Dispatcher _dispatcher = Application.Current.Dispatcher;

    private bool _initialized;
    private bool _seeking;

    /// <summary>Bail out of auto-advance once this many files in a row fail to open.</summary>
    private const int MaxConsecutiveFailures = 5;
    private int _consecutiveFailures;

    public MainViewModel()
    {
        OpenFolderCommand = new RelayCommand(OpenFolder);
        PlayPauseCommand = new RelayCommand(TogglePause, () => HasPlaylist);
        NextCommand = new RelayCommand(() => PlayAt(CurrentIndex + 1), () => CurrentIndex + 1 < Playlist.Count);
        PreviousCommand = new RelayCommand(() => PlayAt(CurrentIndex - 1), () => CurrentIndex > 0);
        PlayItemCommand = new RelayCommand<PlaylistItem>(item => PlayAt(Playlist.IndexOf(item)));
    }

    public ObservableCollection<PlaylistItem> Playlist { get; } = [];

    public RelayCommand OpenFolderCommand { get; }
    public RelayCommand PlayPauseCommand { get; }
    public RelayCommand NextCommand { get; }
    public RelayCommand PreviousCommand { get; }
    public RelayCommand<PlaylistItem> PlayItemCommand { get; }

    /// <summary>Height in physical pixels of the surface the video is scaled up to.</summary>
    public Func<int>? GetTargetHeight { get; set; }

    /// <summary>Raised when the user asks for fullscreen (handled by the window).</summary>
    public event EventHandler? FullscreenRequested;

    #region Attach / teardown

    /// <summary>Wires the engine to the native surface, once the host window handle exists.</summary>
    public void Attach(IntPtr videoHandle)
    {
        if (_initialized) return;

        try
        {
            _engine.Attach(videoHandle);
        }
        catch (Exception ex)
        {
            StatusText = $"Motore di riproduzione non disponibile: {ex.Message}";
            return;
        }

        _engine.EndFile += OnEngineEndFile;
        _engine.FileLoaded += (_, info) => _dispatcher.BeginInvoke(() => OnFileLoaded(info));
        _engine.PausedChanged += (_, paused) => _dispatcher.BeginInvoke(() => IsPaused = paused);
        _engine.PositionChanged += (_, pos) => _dispatcher.BeginInvoke(() =>
        {
            if (_seeking) return;
            _position = pos;
            OnPropertyChanged(nameof(Position));
            OnPropertyChanged(nameof(PositionText));
        });

        _engine.Volume = Volume;
        _initialized = true;

        StatusText = "Pronto — apri una cartella per iniziare.";
    }

    public void Dispose() => _engine.Dispose();

    #endregion

    #region Playlist

    private int _currentIndex = -1;
    public int CurrentIndex
    {
        get => _currentIndex;
        private set { if (Set(ref _currentIndex, value)) OnPropertyChanged(nameof(HasPlaylist)); }
    }

    public bool HasPlaylist => Playlist.Count > 0;

    private PlaylistItem? _currentItem;
    public PlaylistItem? CurrentItem
    {
        get => _currentItem;
        private set => Set(ref _currentItem, value);
    }

    private string _folderName = string.Empty;
    public string FolderName
    {
        get => _folderName;
        private set => Set(ref _folderName, value);
    }

    private void OpenFolder()
    {
        var dialog = new OpenFolderDialog { Title = "Scegli la cartella con i video" };
        if (dialog.ShowDialog() != true) return;

        LoadFolder(dialog.FolderName);
    }

    public void LoadFolder(string folder)
    {
        // Scan before touching the playlist: an unreachable share must leave whatever
        // is currently loaded playable instead of wiping it.
        List<PlaylistItem> found;
        try
        {
            found = VideoFileScanner.Scan(folder);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                                      or ArgumentException or NotSupportedException)
        {
            StatusText = $"Impossibile leggere «{folder}»: {ex.Message}";
            return;
        }

        Playlist.Clear();

        // Reset before repopulating: clearing the list drops the ListBox selection,
        // so CurrentIndex must actually change for the binding to restore it — going
        // straight from 0 to 0 would leave the new first track unhighlighted.
        CurrentIndex = -1;
        CurrentItem = null;
        _consecutiveFailures = 0;

        foreach (var item in found)
            Playlist.Add(item);

        // DirectoryInfo.Name, unlike GetFileName, keeps a drive root readable ("C:\"
        // rather than "") and tolerates a trailing forward slash. It still comes back
        // empty for the root of a UNC share, so fall back to the path itself —
        // "\\NAS\Video" is exactly what the user calls that folder anyway.
        // Cannot throw here: the scan above would have rejected a malformed path.
        string name = new DirectoryInfo(folder).Name;
        FolderName = name.Length > 0 ? name : folder;
        OnPropertyChanged(nameof(HasPlaylist));

        if (Playlist.Count == 0)
        {
            StatusText = "Nessun video trovato in questa cartella.";
            return;
        }

        PlayAt(0);
    }

    private void PlayAt(int index)
    {
        if (index < 0 || index >= Playlist.Count) return;

        // CurrentItem first: the view scrolls the playlist when CurrentIndex changes.
        CurrentItem = Playlist[index];
        CurrentIndex = index;
        Position = 0;
        Duration = 0;

        if (!_initialized) return;

        _engine.Load(Playlist[index].Path);
        _engine.SetPaused(false);
    }

    private void OnFileLoaded(MediaInfo info)
    {
        // A file that actually opened clears the failure streak.
        _consecutiveFailures = 0;
        Duration = info.Duration;
        _sourceWidth = info.Width;
        _sourceHeight = info.Height;
        ApplyVsr();

        string fps = info.Fps > 0
            ? " " + info.Fps.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture) + " fps"
            : string.Empty;
        StatusText = $"{info.Width}×{info.Height}{fps} · {info.Codec} · "
                     + (info.HardwareDecoding ? "decodifica Vulkan" : "decodifica software");
    }

    private void OnEngineEndFile(object? sender, EndFileEventArgs e)
    {
        // Chain forward on a natural end, and also when a file simply cannot be
        // played — one broken entry must not strand the rest of the folder.
        // Every other reason (stop, quit, redirect) means someone already moved us.
        if (!e.ReachedEnd && !e.Failed) return;

        string? failedName = e.Failed ? CurrentItem?.DisplayName : null;

        _dispatcher.BeginInvoke(() =>
        {
            if (failedName is null)
            {
                _consecutiveFailures = 0;
            }
            else if (++_consecutiveFailures >= MaxConsecutiveFailures)
            {
                // Everything is failing — the share is probably gone. Racing through
                // the remaining hundreds of entries would only hide the real problem.
                StatusText = $"Riproduzione interrotta: {_consecutiveFailures} file consecutivi non leggibili "
                             + "(sorgente non raggiungibile?).";
                return;
            }
            else
            {
                StatusText = $"Impossibile riprodurre «{failedName}» — passo al successivo.";
            }

            if (CurrentIndex + 1 < Playlist.Count)
                PlayAt(CurrentIndex + 1);
            else if (failedName is null)
                StatusText = "Fine della playlist.";
        });
    }

    #endregion

    #region Transport

    private bool _isPaused;
    public bool IsPaused
    {
        get => _isPaused;
        private set { if (Set(ref _isPaused, value)) OnPropertyChanged(nameof(PlayPauseGlyph)); }
    }

    public string PlayPauseGlyph => IsPaused ? "▶" : "❚❚";

    private double _position;
    public double Position
    {
        get => _position;
        set
        {
            if (!Set(ref _position, value)) return;
            OnPropertyChanged(nameof(PositionText));
        }
    }

    private double _duration;
    public double Duration
    {
        get => _duration;
        private set
        {
            if (!Set(ref _duration, value)) return;
            OnPropertyChanged(nameof(DurationText));
        }
    }

    public string PositionText => FormatTime(Position);
    public string DurationText => FormatTime(Duration);

    private double _volume = 70;
    public double Volume
    {
        get => _volume;
        set
        {
            if (!Set(ref _volume, value)) return;
            if (_initialized) _engine.Volume = value;
        }
    }

    private void TogglePause() => _engine.TogglePause();

    /// <summary>Called by the view while the user drags the seek bar.</summary>
    public void BeginSeek() => _seeking = true;

    public void EndSeek(double seconds)
    {
        _seeking = false;

        // Move the bar now instead of waiting for the engine to report the new
        // position: the reports still in flight carry the pre-seek position and
        // would otherwise yank the handle back for a frame or two.
        Position = seconds;

        _engine.Seek(seconds);
    }

    public void ToggleFullscreen() => FullscreenRequested?.Invoke(this, EventArgs.Empty);

    private static string FormatTime(double seconds)
    {
        if (double.IsNaN(seconds) || seconds < 0) seconds = 0;
        var t = TimeSpan.FromSeconds(seconds);
        return t.TotalHours >= 1 ? t.ToString(@"h\:mm\:ss") : t.ToString(@"m\:ss");
    }

    #endregion

    #region RTX Video Super Resolution

    private int _sourceWidth, _sourceHeight;

    private VsrMode _vsrMode = VsrMode.Auto;
    public VsrMode VsrMode
    {
        get => _vsrMode;
        set { if (Set(ref _vsrMode, value)) ApplyVsr(); }
    }

    private string _vsrStatus = "VSR —";
    public string VsrStatus
    {
        get => _vsrStatus;
        private set => Set(ref _vsrStatus, value);
    }

    private string _vsrDetail = "RTX VSR: —";
    /// <summary>Full explanation behind the short <see cref="VsrStatus"/> label (tooltip).</summary>
    public string VsrDetail
    {
        get => _vsrDetail;
        private set => Set(ref _vsrDetail, value);
    }

    private bool _vsrActive;
    public bool VsrActive
    {
        get => _vsrActive;
        private set => Set(ref _vsrActive, value);
    }

    /// <summary>Re-evaluates VSR for the current source and surface size.</summary>
    public void ApplyVsr()
    {
        if (!_initialized) return;

        if (_sourceHeight <= 0)
        {
            SetVsrOff("nessun video");
            return;
        }

        if (!Vsr.Wanted(VsrMode, _sourceHeight))
        {
            string why = VsrMode == VsrMode.Off ? "disattivato" : $"sorgente {_sourceHeight}p già ≥ 1080p";
            SetVsrOff(why);
            return;
        }

        int targetHeight = GetTargetHeight?.Invoke() ?? 0;
        double? scale = Vsr.ComputeScale(_sourceHeight, targetHeight);
        if (scale is null)
        {
            SetVsrOff("finestra troppo piccola per un upscale utile");
            return;
        }

        // The RTX Video SDK path arrives with stage 3; until then the shader scales.
        string factor = scale.Value.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture);
        SetVsrOff($"servirebbe {_sourceWidth}×{_sourceHeight} ×{factor}, ma nel nuovo motore la VSR arriva con la tappa 3");
    }

    private void SetVsrOff(string reason)
    {
        VsrActive = false;
        VsrStatus = "VSR off";
        VsrDetail = $"RTX VSR off — {reason}";
    }

    #endregion

    #region Status

    private string _statusText = string.Empty;
    public string StatusText
    {
        get => _statusText;
        private set => Set(ref _statusText, value);
    }

    #endregion

    #region INotifyPropertyChanged

    public event PropertyChangedEventHandler? PropertyChanged;

    private bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        OnPropertyChanged(name);
        return true;
    }

    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    #endregion
}
