using System.Collections.Concurrent;
using System.Collections.Specialized;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;

namespace OwkCopy;

public partial class MainWindow : Window
{
    const int MaxLogLines = 3000;

    readonly Settings _settings = Settings.Load();
    readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(250) };
    readonly ConcurrentQueue<(string Text, bool IsError)> _pendingLog = new();
    readonly Queue<(double Seconds, long Bytes)> _samples = new();
    readonly Stopwatch _clock = new();

    CopyEngine? _engine;
    Task? _job;
    string _jobTitle = "";
    bool _autoClose;
    bool _clearClipboardOnSuccess;
    Action? _pendingStart;

    public MainWindow()
    {
        InitializeComponent();
        ApplySettingsToUi();
        DefaultBox.IsChecked = PasteAgent.IsDefault();
        MenuBox.IsChecked = ShellIntegration.IsRegistered();

        ((INotifyCollectionChanged)SourceList.Items).CollectionChanged +=
            (_, _) => DropHint.Visibility = SourceList.Items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

        Drop += OnDrop;
        DragOver += (_, e) =>
        {
            e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) && _job == null ? DragDropEffects.Copy : DragDropEffects.None;
            e.Handled = true;
        };
        _timer.Tick += (_, _) => Tick();
        Loaded += (_, _) => { _pendingStart?.Invoke(); _pendingStart = null; };
        Closing += (_, e) =>
        {
            if (_job is { IsCompleted: false } &&
                MessageBox.Show(this, "A transfer is running. Cancel it and close?", "Owk Copy",
                    MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
            {
                e.Cancel = true;
                return;
            }
            _engine?.Cancel();
            ReadSettingsFromUi();
            _settings.Save();
        };
    }

    /// <summary>Start immediately (used by "paste" and command-line launches).</summary>
    public void StartJob(IEnumerable<string> sources, string destination, bool move, bool autoClose, bool clearClipboardOnSuccess = false)
    {
        SourceList.Items.Clear();
        foreach (var s in sources) SourceList.Items.Add(s);
        DestBox.Text = destination;
        MoveRadio.IsChecked = move;
        CopyRadio.IsChecked = !move;
        _autoClose = autoClose;
        _clearClipboardOnSuccess = clearClipboardOnSuccess;
        SetupPanel.Visibility = Visibility.Collapsed; // go straight to the progress view
        _pendingStart = () => Start(saveDestination: false);
    }

    // ---------------------------------------------------------------- settings

    void ApplySettingsToUi()
    {
        ThreadSlider.Value = _settings.Threads;
        SkipLinksBox.IsChecked = _settings.SkipLinks;
        VerifyBox.IsChecked = _settings.Verify;
        ExcludeBox.Text = _settings.Excludes;
        DestBox.Text = _settings.LastDestination;
        ConflictBox.SelectedItem = ConflictBox.Items.Cast<ComboBoxItem>()
            .FirstOrDefault(i => (string)i.Tag == _settings.Conflict.ToString()) ?? ConflictBox.Items[0];
    }

    void ReadSettingsFromUi(bool saveDestination = true)
    {
        _settings.Threads = (int)ThreadSlider.Value;
        _settings.SkipLinks = SkipLinksBox.IsChecked == true;
        _settings.Verify = VerifyBox.IsChecked == true;
        _settings.Excludes = ExcludeBox.Text.Trim();
        if (saveDestination && !_autoClose) _settings.LastDestination = DestBox.Text.Trim();
        if (ConflictBox.SelectedItem is ComboBoxItem { Tag: string tag } && Enum.TryParse<ConflictPolicy>(tag, out var c))
            _settings.Conflict = c;
    }

    void DefaultBox_Click(object sender, RoutedEventArgs e)
    {
        try { PasteAgent.SetDefault(DefaultBox.IsChecked == true); }
        catch (Exception ex) { Log("Default handler: " + ex.Message, true); }
        DefaultBox.IsChecked = PasteAgent.IsDefault();
    }

    void MenuBox_Click(object sender, RoutedEventArgs e)
    {
        try { if (MenuBox.IsChecked == true) ShellIntegration.Register(); else ShellIntegration.Unregister(); }
        catch (Exception ex) { Log("Explorer menu: " + ex.Message, true); }
        MenuBox.IsChecked = ShellIntegration.IsRegistered();
    }

    // ---------------------------------------------------------------- sources

    void OnDrop(object sender, DragEventArgs e)
    {
        if (_job != null || e.Data.GetData(DataFormats.FileDrop) is not string[] paths) return;
        if (SetupPanel.Visibility != Visibility.Visible) ShowSetup();
        foreach (var p in paths)
            if (!SourceList.Items.Contains(p)) SourceList.Items.Add(p);
    }

    void AddFiles_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog { Multiselect = true, Title = "Choose files" };
        if (dlg.ShowDialog(this) == true)
            foreach (var f in dlg.FileNames)
                if (!SourceList.Items.Contains(f)) SourceList.Items.Add(f);
    }

    void AddFolder_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFolderDialog { Multiselect = true, Title = "Choose folders" };
        if (dlg.ShowDialog(this) == true)
            foreach (var f in dlg.FolderNames)
                if (!SourceList.Items.Contains(f)) SourceList.Items.Add(f);
    }

    void Remove_Click(object sender, RoutedEventArgs e)
    {
        foreach (var item in SourceList.SelectedItems.Cast<object>().ToList()) SourceList.Items.Remove(item);
    }

    void Clear_Click(object sender, RoutedEventArgs e) => SourceList.Items.Clear();

    void Browse_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFolderDialog { Title = "Choose destination" };
        if (Directory.Exists(DestBox.Text)) dlg.InitialDirectory = DestBox.Text;
        if (dlg.ShowDialog(this) == true) DestBox.Text = dlg.FolderName;
    }

    // ---------------------------------------------------------------- job control

    void Start_Click(object sender, RoutedEventArgs e) => Start(saveDestination: true);

    void Start(bool saveDestination)
    {
        var sources = SourceList.Items.Cast<string>().ToList();
        var dest = DestBox.Text.Trim();
        if (sources.Count == 0 || dest.Length == 0)
        {
            DetailsToggle.IsChecked = true;
            Log(sources.Count == 0 ? "Add something to copy first." : "Choose a destination folder.", true);
            return;
        }

        ReadSettingsFromUi(saveDestination);
        _settings.Save();
        bool move = MoveRadio.IsChecked == true;

        var what = sources.Count == 1 ? Path.GetFileName(sources[0].TrimEnd('\\')) : $"{sources.Count} items";
        if (string.IsNullOrEmpty(what)) what = sources[0];
        var where = Path.GetFileName(dest.TrimEnd('\\', '.'));
        if (string.IsNullOrEmpty(where)) where = dest;
        _jobTitle = $"{(move ? "Moving" : "Copying")} {what} to {where}";

        LogList.Items.Clear();
        Log($"{(move ? "Move" : "Copy")} {sources.Count} item(s) -> {dest}", false);
        Begin(new CopyEngine(_settings, move), eng => eng.RunAsync(sources, dest));
    }

    void Retry_Click(object sender, RoutedEventArgs e)
    {
        if (_engine == null || _engine.FailedItems.IsEmpty) return;
        var items = _engine.FailedItems.ToList();
        Log($"Retrying {items.Count} failed file(s)…", false);
        Begin(_engine, eng => eng.RetryAsync(items), reuse: true);
    }

    void New_Click(object sender, RoutedEventArgs e) => ShowSetup();

    void ShowSetup()
    {
        _autoClose = false;
        ProgressPanel.Visibility = Visibility.Collapsed;
        SetupPanel.Visibility = Visibility.Visible;
        OptionsToggle.IsEnabled = true;
        if (string.IsNullOrEmpty(DestBox.Text)) DestBox.Text = _settings.LastDestination;
        Title = "Owk Copy";
    }

    void Begin(CopyEngine engine, Func<CopyEngine, Task> run, bool reuse = false)
    {
        if (!reuse)
        {
            engine.Info += m => _pendingLog.Enqueue((m, false));
            engine.Error += m => _pendingLog.Enqueue((m, true));
        }
        _engine = engine;
        SetRunning(true);
        _samples.Clear();
        _clock.Restart();
        _timer.Start();
        Tick();
        _job = run(engine);
        _job.ContinueWith(t => Dispatcher.Invoke(() => Finished(t)));
    }

    void Finished(Task t)
    {
        Tick();
        _timer.Stop();
        _clock.Stop();
        _job = null;
        SetRunning(false);
        var eng = _engine!;
        if (t.Exception != null) Log("Stopped: " + t.Exception.GetBaseException().Message, true);

        var elapsed = _clock.Elapsed;
        var avg = Size(eng.DoneBytes / Math.Max(elapsed.TotalSeconds, 0.001));
        Log($"{eng.Phase}: {eng.DoneFiles:N0} done, {eng.SkippedFiles:N0} skipped, {eng.FailedFiles:N0} failed in {Fmt(elapsed)} ({avg}/s avg)",
            eng.FailedFiles > 0);
        PhaseText.Text = eng.Cancelled ? "Cancelled" : eng.FailedFiles > 0 ? $"Finished with {eng.FailedFiles:N0} error(s)" : "Done";
        SpeedText.Text = $"{Fmt(elapsed)} · {avg}/s average";
        CurrentText.Text = " ";
        RetryButton.Visibility = eng.FailedItems.IsEmpty ? Visibility.Collapsed : Visibility.Visible;
        if (eng.FailedFiles > 0) DetailsToggle.IsChecked = true;

        bool clean = !eng.Cancelled && eng.FailedFiles == 0 && t.Exception == null;
        if (clean && _clearClipboardOnSuccess) { try { Clipboard.Clear(); } catch { } }
        if (clean && _autoClose)
        {
            var close = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1.2) };
            close.Tick += (_, _) => { close.Stop(); Close(); };
            close.Start();
        }
    }

    void Pause_Click(object sender, RoutedEventArgs e)
    {
        if (_engine == null) return;
        _engine.Paused = !_engine.Paused;
        PauseButton.Content = _engine.Paused ? "Resume" : "Pause";
        if (_engine.Paused) _clock.Stop(); else _clock.Start();
    }

    void Cancel_Click(object sender, RoutedEventArgs e)
    {
        _autoClose = false;
        _engine?.Cancel();
        CancelButton.IsEnabled = false;
    }

    void SetRunning(bool running)
    {
        if (running)
        {
            SetupPanel.Visibility = Visibility.Collapsed;
            OptionsToggle.IsChecked = false;
            OptionsToggle.IsEnabled = false;
            ProgressPanel.Visibility = Visibility.Visible;
        }
        PauseButton.Visibility = CancelButton.Visibility = running ? Visibility.Visible : Visibility.Collapsed;
        PauseButton.IsEnabled = CancelButton.IsEnabled = running;
        PauseButton.Content = "Pause";
        RetryButton.Visibility = Visibility.Collapsed;
        NewButton.Visibility = running ? Visibility.Collapsed : Visibility.Visible;
    }

    // ---------------------------------------------------------------- display

    void Tick()
    {
        while (_pendingLog.TryDequeue(out var entry)) Log(entry.Text, entry.IsError);
        var eng = _engine;
        if (eng == null) return;

        long total = Interlocked.Read(ref eng.TotalBytes);
        long done = Interlocked.Read(ref eng.DoneBytes);
        double frac = total > 0 ? Math.Clamp((double)done / total, 0, 1) : (eng.Phase == "Done" ? 1 : 0);

        double now = _clock.Elapsed.TotalSeconds;
        _samples.Enqueue((now, done));
        while (_samples.Count > 2 && now - _samples.Peek().Seconds > 3) _samples.Dequeue();
        var first = _samples.Peek();
        double speed = now - first.Seconds > 0.2 ? (done - first.Bytes) / (now - first.Seconds) : 0;

        Bar.Value = frac * 1000;
        PercentText.Text = $"{frac * 100:0}%";
        Title = $"{frac * 100:0}% · Owk Copy";

        bool transferring = eng.Phase is "Copying" or "Moving";
        PhaseText.Text = eng.Paused ? "Paused" : transferring ? _jobTitle : eng.Phase + "…";
        var eta = speed > 1 && total > done ? " · " + Fmt(TimeSpan.FromSeconds((total - done) / speed)) + " left" : "";
        SpeedText.Text = transferring ? $"{Size(speed)}/s{eta}" : _jobTitle;
        StatsText.Text = $"{eng.DoneFiles:N0} / {eng.TotalFiles:N0} files · {Size(done)} of {Size(total)}" +
                         (eng.SkippedFiles > 0 ? $" · {eng.SkippedFiles:N0} skipped" : "") +
                         (eng.FailedFiles > 0 ? $" · {eng.FailedFiles:N0} failed" : "");
        CurrentText.Text = string.IsNullOrEmpty(eng.Current) ? " " : eng.Current;
    }

    void Log(string text, bool isError)
    {
        var line = new TextBlock { Text = text, TextWrapping = TextWrapping.NoWrap };
        if (isError) line.Foreground = new SolidColorBrush(Color.FromRgb(0xE8, 0x5C, 0x5C));
        LogList.Items.Add(line);
        if (LogList.Items.Count > MaxLogLines) LogList.Items.RemoveAt(0);
        LogList.ScrollIntoView(line);
    }

    static string Size(double bytes)
    {
        string[] u = ["B", "KB", "MB", "GB", "TB"];
        int i = 0;
        while (bytes >= 1024 && i < u.Length - 1) { bytes /= 1024; i++; }
        return $"{bytes:0.##} {u[i]}";
    }

    static string Fmt(TimeSpan t) =>
        t.TotalHours >= 1 ? $"{(int)t.TotalHours}h {t.Minutes}m" :
        t.TotalMinutes >= 1 ? $"{t.Minutes}m {t.Seconds}s" : $"{t.TotalSeconds:0.0}s";
}
