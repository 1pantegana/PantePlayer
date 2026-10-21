using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Threading;
using LettoreVideo.Mpv;
using LettoreVideo.Playlist;
using Microsoft.Win32;

namespace LettoreVideo.ViewModels;

public sealed class MainViewModel : INotifyPropertyChanged, IDisposable
{
    private readonly MpvClient _mpv = new();
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

    /// <summary>
    /// Wires mpv to the native surface. Called once the host window handle exists,
    /// because "wid" must be set before mpv_initialize.
    /// </summary>
    public void Attach(IntPtr videoHandle)
    {
        if (_initialized) return;

        _mpv.Create();

        // --- Rendering: D3D11 end to end, so NVDEC frames never leave the GPU. ---
        _mpv.SetOption("wid", videoHandle.ToInt64().ToString());
        _mpv.SetOption("vo", "gpu-next");
        _mpv.SetOption("gpu-api", "d3d11");
        _mpv.SetOption("gpu-context", "d3d11");
        _mpv.SetOption("profile", "high-quality");

        // --- Frame interpolation ("Fluido"): video-sync and interpolation are
        //     switched at runtime by ApplySmoothMotion; tscale only matters while
        //     interpolation is on. oversample keeps frames sharp and only blends
        //     at the transitions, instead of smearing every frame. ---
        _mpv.SetOption("tscale", "oversample");

        // --- HDR passthrough: off until the user enables it. With gpu-next on D3D11
        //     the hint also switches the swapchain to HDR10 by itself, so the output
        //     format stays on auto — a fixed 10-bit swapchain would only cost an SDR
        //     monitor its 8-bit dithering. ---
        _mpv.SetOption("target-colorspace-hint", "no");

        // --- Decoding: NVDEC on the RTX 5080, keeping frames in D3D11 textures
        //     (plain "d3d11va", never "-copy", which is what d3d11vpp requires). ---
        _mpv.SetOption("hwdec", "d3d11va");
        _mpv.SetOption("hwdec-codecs", "all");

        // --- CPU side: let ffmpeg spread demux/filter work over the Ryzen's cores. ---
        _mpv.SetOption("vd-lavc-threads", "0");
        _mpv.SetOption("ad-lavc-threads", "0");
        _mpv.SetOption("cache", "yes");
        _mpv.SetOption("demuxer-max-bytes", "256MiB");

        // --- Playback behaviour: we drive the playlist ourselves. ---
        // keep-open must stay off: with it enabled mpv parks on the last frame and
        // never emits end-file, which is exactly the event the auto-advance needs.
        _mpv.SetOption("keep-open", "no");
        _mpv.SetOption("idle", "yes");          // stay alive between files
        _mpv.SetOption("force-window", "yes");  // keep the surface painted while idle
        _mpv.SetOption("osc", "no");            // the WPF controls are the UI
        _mpv.SetOption("input-default-bindings", "no");
        _mpv.SetOption("input-vo-keyboard", "no");

        // Diagnostics: set LETTOREVIDEO_MPVLOG to a path to capture mpv's own log
        // (shows which hwdec was picked and how the filter chain was built).
        if (Environment.GetEnvironmentVariable("LETTOREVIDEO_MPVLOG") is { Length: > 0 } logPath)
        {
            _mpv.SetOption("log-file", logPath);
            _mpv.SetOption("msg-level", "all=v");
        }

        _mpv.Initialize();

        _mpv.PropertyChanged += OnMpvPropertyChanged;
        _mpv.EndFile += OnMpvEndFile;

        // A file that actually opened clears the failure streak.
        _mpv.FileLoaded += (_, _) => _dispatcher.BeginInvoke(() => _consecutiveFailures = 0);

        _mpv.LoopFailed += (_, ex) => _dispatcher.BeginInvoke(() =>
            StatusText = $"Il motore di riproduzione si è arrestato: {ex.Message}");

        _mpv.ObserveProperty("time-pos", MpvNative.MPV_FORMAT_DOUBLE);
        _mpv.ObserveProperty("duration", MpvNative.MPV_FORMAT_DOUBLE);
        _mpv.ObserveProperty("pause", MpvNative.MPV_FORMAT_FLAG);
        _mpv.ObserveProperty("video-params/h", MpvNative.MPV_FORMAT_INT64);
        _mpv.ObserveProperty("container-fps", MpvNative.MPV_FORMAT_DOUBLE);

        _mpv.SetProperty("volume", Volume);
        _initialized = true;

        StatusText = "Pronto — apri una cartella per iniziare.";
    }

    public void Dispose()
    {
        _fpsProbe?.Stop();
        _mpv.Dispose();
    }

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

        _mpv.Command("loadfile", Playlist[index].Path, "replace");
        _mpv.SetProperty("pause", false);
    }

    private void OnMpvEndFile(object? sender, MpvEndFileEventArgs e)
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
            if (_initialized) _mpv.SetProperty("volume", value);
        }
    }

    private void TogglePause() => _mpv.Command("cycle", "pause");

    /// <summary>Called by the view while the user drags the seek bar.</summary>
    public void BeginSeek() => _seeking = true;

    public void EndSeek(double seconds)
    {
        _seeking = false;

        // Move the bar now instead of waiting for mpv to report the new time-pos:
        // the "time-pos" events still in flight carry the pre-seek position and
        // would otherwise yank the handle back for a frame or two.
        Position = seconds;

        _mpv.Command("seek", seconds.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture), "absolute");
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

    private int _sourceHeight;

    /// <summary>Filter chain currently installed in mpv, to avoid redundant rebuilds.</summary>
    private string _appliedFilter = string.Empty;

    private RtxVsrMode _vsrMode = RtxVsrMode.Auto;
    public RtxVsrMode VsrMode
    {
        get => _vsrMode;
        set { if (Set(ref _vsrMode, value)) ApplyVsr(); }
    }

    private bool _trueHdr;
    /// <summary>RTX Video HDR — only meaningful on an HDR-capable display.</summary>
    public bool TrueHdr
    {
        get => _trueHdr;
        set { if (Set(ref _trueHdr, value)) ApplyVsr(); }
    }

    private string _vsrStatus = "RTX VSR: —";
    public string VsrStatus
    {
        get => _vsrStatus;
        private set => Set(ref _vsrStatus, value);
    }

    private bool _vsrActive;
    public bool VsrActive
    {
        get => _vsrActive;
        private set => Set(ref _vsrActive, value);
    }

    /// <summary>
    /// Applies (or clears) the d3d11vpp chain for the current source. Safe to call
    /// mid-playback: "vf set" swaps the filter chain without reloading the file.
    /// </summary>
    public void ApplyVsr()
    {
        if (!_initialized) return;

        if (_sourceHeight <= 0)
        {
            SetVsrOff("nessun video");
            return;
        }

        bool wanted = VsrMode switch
        {
            RtxVsrMode.Always => true,
            RtxVsrMode.Auto => _sourceHeight < RtxVideoFilter.AutoMaxSourceHeight,
            _ => false,
        };

        if (!wanted)
        {
            string why = VsrMode == RtxVsrMode.Off ? "disattivato" : $"sorgente {_sourceHeight}p già ≥ 1080p";
            SetVsrOff(why);
            return;
        }

        int targetHeight = GetTargetHeight?.Invoke() ?? 0;
        double? scale = RtxVideoFilter.ComputeScale(_sourceHeight, targetHeight);
        if (scale is null)
        {
            SetVsrOff("finestra troppo piccola per un upscale utile");
            return;
        }

        SetFilter(RtxVideoFilter.Build(scale.Value, TrueHdr));
        VsrActive = true;

        // Read the width here rather than caching it from an observed property: it
        // only ever feeds this label, and a cached copy goes stale the moment mpv
        // reports the property as unavailable — the notification carries no value,
        // so the field would silently keep the previous file's width.
        string source = _mpv.GetLong("video-params/w") is long w
            ? $"{w}×{_sourceHeight}"
            : $"{_sourceHeight}p";

        VsrStatus = $"RTX VSR attivo — {source} ×{scale.Value:0.##}"
                    + (TrueHdr ? " + RTX HDR" : string.Empty);
    }

    private void SetVsrOff(string reason)
    {
        SetFilter(string.Empty);
        VsrActive = false;
        VsrStatus = $"RTX VSR off — {reason}";
    }

    /// <summary>
    /// Installs a filter chain only when it differs from the one already running:
    /// every "vf set" tears down and re-creates the d3d11vpp processor, which is
    /// far too expensive to repeat on each resize tick.
    /// </summary>
    private void SetFilter(string filter)
    {
        if (filter == _appliedFilter) return;

        _mpv.Command("vf", "set", filter);
        _appliedFilter = filter;
    }

    #endregion

    #region Movimento fluido e HDR

    /// <summary>Frame rate declared by the container; 0 when it declares none.</summary>
    private double _sourceFps;

    /// <summary>
    /// Frame rate measured from the decoded frames, used only when the container
    /// declares none — common for VP9/AV1 web downloads in MKV/WebM.
    /// </summary>
    private double _estimatedFps;

    /// <summary>
    /// Polls estimated-vf-fps instead of observing it: that property changes on
    /// nearly every frame, and one settled reading is all the decision needs.
    /// </summary>
    private DispatcherTimer? _fpsProbe;
    private int _fpsProbeTicks;
    private const int FpsProbeMaxTicks = 10;   // 10 × 500 ms, then give up

    /// <summary>Interpolation state currently set in mpv; null until first applied.</summary>
    private bool? _smoothApplied;

    private SmoothMotionMode _smoothMode = SmoothMotionMode.Auto;
    public SmoothMotionMode SmoothMode
    {
        get => _smoothMode;
        set { if (Set(ref _smoothMode, value)) ApplySmoothMotion(); }
    }

    private string _smoothStatus = "Fluido: —";
    public string SmoothStatus
    {
        get => _smoothStatus;
        private set => Set(ref _smoothStatus, value);
    }

    /// <summary>
    /// Turns interpolation on or off for the current source. Both properties are
    /// switched live, without reloading the file.
    /// </summary>
    private void ApplySmoothMotion()
    {
        if (!_initialized) return;

        bool estimated = _sourceFps <= 0;
        double rate = estimated ? _estimatedFps : _sourceFps;
        string fps = (estimated ? "~" : "")
                     + rate.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture);

        // A rate that is still unknown stays off in Auto: retiming a stream whose
        // cadence we cannot see is more likely to hurt than help.
        (bool wanted, string status) = SmoothMode switch
        {
            SmoothMotionMode.Always => (true, "Fluido attivo — sempre ON"),
            SmoothMotionMode.Off => (false, "Fluido off — disattivato"),
            _ when rate <= 0 => (false, "Fluido off — fps sconosciuti"),
            _ when rate < SmoothMotion.AutoMaxSourceFps => (true, $"Fluido attivo — {fps} fps"),
            _ => (false, $"Fluido off — sorgente {fps} fps ≥ {SmoothMotion.AutoMaxSourceFps:0}"),
        };

        SmoothStatus = status;
        if (wanted == _smoothApplied) return;

        _mpv.SetProperty("video-sync", wanted ? SmoothMotion.SyncOn : SmoothMotion.SyncOff);
        _mpv.SetProperty("interpolation", wanted);
        _smoothApplied = wanted;
    }

    private void StartFpsProbe()
    {
        if (_fpsProbe is null)
        {
            _fpsProbe = new DispatcherTimer(DispatcherPriority.Background, _dispatcher)
            {
                Interval = TimeSpan.FromMilliseconds(500),
            };
            _fpsProbe.Tick += OnFpsProbeTick;
        }

        _fpsProbeTicks = 0;
        _fpsProbe.Stop();
        _fpsProbe.Start();
    }

    private void OnFpsProbeTick(object? sender, EventArgs e)
    {
        // The first readings after a load are still settling, so skip one tick.
        if (++_fpsProbeTicks > 1 && _mpv.GetDouble("estimated-vf-fps") is > 0 and var est)
        {
            _fpsProbe!.Stop();
            _estimatedFps = est;
            ApplySmoothMotion();
        }
        else if (_fpsProbeTicks >= FpsProbeMaxTicks)
        {
            _fpsProbe!.Stop();
        }
    }

    private bool _hdrPassthrough;
    /// <summary>
    /// Hands HDR10 metadata to the display instead of tone-mapping to SDR — only
    /// meaningful on an HDR monitor with HDR enabled in Windows.
    /// </summary>
    public bool HdrPassthrough
    {
        get => _hdrPassthrough;
        set
        {
            if (!Set(ref _hdrPassthrough, value)) return;
            // As a string: newer mpv builds made this a choice (auto/yes/no), which
            // would reject a flag-typed write.
            if (_initialized) _mpv.SetProperty("target-colorspace-hint", value ? "yes" : "no");
        }
    }

    #endregion

    #region mpv property plumbing

    private void OnMpvPropertyChanged(object? sender, MpvPropertyChangedEventArgs e)
    {
        _dispatcher.BeginInvoke(() =>
        {
            switch (e.Name)
            {
                case "time-pos" when !_seeking && e.Value is double pos:
                    _position = pos;
                    OnPropertyChanged(nameof(Position));
                    OnPropertyChanged(nameof(PositionText));
                    break;

                case "duration" when e.Value is double dur:
                    Duration = dur;
                    break;

                case "pause" when e.Value is bool paused:
                    IsPaused = paused;
                    break;

                case "video-params/h":
                    _sourceHeight = e.Value is long h ? (int)h : 0;
                    ApplyVsr();
                    break;

                case "container-fps":
                    _sourceFps = e.Value is double fps ? fps : 0;
                    _estimatedFps = 0;
                    if (_sourceFps <= 0 && CurrentItem is not null) StartFpsProbe();
                    else _fpsProbe?.Stop();
                    ApplySmoothMotion();
                    break;
            }
        });
    }

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
