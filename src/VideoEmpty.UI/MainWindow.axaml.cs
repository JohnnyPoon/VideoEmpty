using System;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using VideoEmpty.Core.Api;
using VideoEmpty.Core.Diagnostics;
using VideoEmpty.Core.Model;
using VideoEmpty.Rendering;

namespace VideoEmpty.UI;

public partial class MainWindow : Window
{
    private readonly IVideoEmptyApi _api = VideoEmptyServices.CreateApi();
    private Project _project;
    private string? _armedTemplateId;
    private int _currentTimeMs;
    private DispatcherTimer? _playTimer;
    private bool _suppressSliderRefresh;

    public ObservableCollection<Template> Templates { get; } = new();
    public ObservableCollection<TemplateInstance> Instances { get; } = new();

    public MainWindow()
    {
        InitializeComponent();
        _project = _api.CreateProject("Untitled");
        RefreshTemplates();
        RefreshInstances();
        DataContext = this;

        TemplatesList.ItemsSource = Templates;
        InstancesList.ItemsSource = Instances;

        OpenVideoButton.Click += OnOpenVideo;
        SaveProjectButton.Click += OnSaveProject;
        ExportButton.Click += OnExport;
        TimeSlider.PropertyChanged += (_, e) =>
        {
            if (e.Property == Slider.ValueProperty)
            {
                _currentTimeMs = (int)TimeSlider.Value;
                _ = RefreshPreviewAsync();
            }
        };
        TemplatesList.SelectionChanged += (_, _) =>
        {
            _armedTemplateId = (TemplatesList.SelectedItem as Template)?.Id;
            ArmedLabel.Text = _armedTemplateId is null ? "(none armed)" : $"Armed: {((Template)TemplatesList.SelectedItem!).Name}";
        };
        PreviewImage.PointerPressed += OnPreviewClicked;
        InstancesList.SelectionChanged += (_, _) => UpdateInstanceEditor();
        DeleteInstanceButton.Click += (_, _) =>
        {
            if (InstancesList.SelectedItem is TemplateInstance i)
            {
                _api.DeleteInstance(_project, i.Id);
                RefreshInstances();
            }
        };
        ApplyInstanceButton.Click += OnApplyInstance;
        InstallDepsButton.Click += OnInstallDeps;
        OpenLogButton.Click += (_, _) => OpenInShell(Log.LogPath);

        // Playback wiring
        PlayPauseButton.Click       += (_, _) => TogglePlay();
        StepBackButton.Click        += (_, _) => SeekRelative(-FrameDurationMs());
        StepForwardButton.Click     += (_, _) => SeekRelative(+FrameDurationMs());
        JumpBack1sButton.Click      += (_, _) => SeekRelative(-1000);
        JumpForward1sButton.Click   += (_, _) => SeekRelative(+1000);
        JumpBack10sButton.Click     += (_, _) => SeekRelative(-10000);
        JumpForward10sButton.Click  += (_, _) => SeekRelative(+10000);

        InstanceTextBox.LostFocus += (_, _) => CommitInstanceEdit();
        InstanceTextBox.KeyDown += (_, args) =>
        {
            // Ctrl+Enter applies; plain Enter inserts newline (multi-line text box).
            if (args.Key == Avalonia.Input.Key.Enter && args.KeyModifiers.HasFlag(Avalonia.Input.KeyModifiers.Control))
            {
                CommitInstanceEdit();
                args.Handled = true;
            }
        };

        Dispatcher.UIThread.Post(async () => await CheckDependenciesAsync(promptIfMissing: true), DispatcherPriority.Background);
    }

    private int FrameDurationMs()
    {
        var fps = _project.VideoFps > 0 ? _project.VideoFps : 30.0;
        return Math.Max(1, (int)Math.Round(1000.0 / fps));
    }

    private void TogglePlay()
    {
        if (_project.VideoDurationMs <= 0) return;
        if (_playTimer is { IsEnabled: true })
        {
            _playTimer.Stop();
            PlayPauseButton.Content = "▶ Play";
            return;
        }
        var interval = TimeSpan.FromMilliseconds(FrameDurationMs());
        _playTimer ??= new DispatcherTimer();
        _playTimer.Interval = interval;
        _playTimer.Tick -= OnPlayTick;
        _playTimer.Tick += OnPlayTick;
        _playTimer.Start();
        PlayPauseButton.Content = "⏸ Pause";
    }

    private void OnPlayTick(object? sender, EventArgs e)
    {
        var next = _currentTimeMs + FrameDurationMs();
        if (next >= _project.VideoDurationMs)
        {
            if (LoopCheck.IsChecked == true) { next = 0; }
            else { _playTimer?.Stop(); PlayPauseButton.Content = "▶ Play"; next = _project.VideoDurationMs; }
        }
        TimeSlider.Value = next;
    }

    private void SeekRelative(int deltaMs)
    {
        var v = Math.Clamp(_currentTimeMs + deltaMs, 0, (int)TimeSlider.Maximum);
        TimeSlider.Value = v;
    }

    private static string FormatTime(int ms)
    {
        var ts = TimeSpan.FromMilliseconds(Math.Max(0, ms));
        return $"{(int)ts.TotalMinutes}:{ts.Seconds:00}.{ts.Milliseconds:000}";
    }

    private static bool IsHorizontalSlide(AnimationStyle style) =>
        style is AnimationStyle.SlideLeft or AnimationStyle.SlideRight;

    private static Animation CloneAnimation(Animation source) => new()
    {
        Enter = source.Enter,
        Exit = source.Exit,
        EnterMs = source.EnterMs,
        ExitMs = source.ExitMs
    };

    private (double centerX, double centerY, Animation? animationOverride) ResolveClickPlacement(
        Template template, double clickX, double clickY)
    {
        var anim = template.Animation;
        bool horizontalTemplate = IsHorizontalSlide(anim.Enter) || IsHorizontalSlide(anim.Exit);
        if (!horizontalTemplate || _project.VideoResolution.Width <= 0 || _project.VideoResolution.Height <= 0)
            return (clickX, clickY, null);

        bool fromLeft = clickX < 0.5;
        double halfWNorm = Math.Min(0.5, (template.Width / 2.0) / _project.VideoResolution.Width);
        double halfHNorm = Math.Min(0.5, (template.Height / 2.0) / _project.VideoResolution.Height);

        double centerX = fromLeft ? halfWNorm : 1.0 - halfWNorm;
        double centerY = Math.Clamp(clickY, halfHNorm, 1.0 - halfHNorm);
        var sideStyle = fromLeft ? AnimationStyle.SlideLeft : AnimationStyle.SlideRight;

        var overrideAnim = CloneAnimation(anim);
        if (IsHorizontalSlide(overrideAnim.Enter)) overrideAnim.Enter = sideStyle;
        if (IsHorizontalSlide(overrideAnim.Exit)) overrideAnim.Exit = sideStyle;
        return (centerX, centerY, overrideAnim);
    }

    private async Task CheckDependenciesAsync(bool promptIfMissing)
    {
        try
        {
            var statuses = await _api.Dependencies.CheckAsync();
            var missing = statuses.Where(s => s.State != DependencyState.Installed).Select(s => s.Name).ToList();
            InstallDepsButton.IsVisible = missing.Count > 0;
            if (missing.Count == 0)
            {
                VideoInfoLabel.Text = $"FFmpeg ready. (Log: {Log.LogPath})";
                return;
            }
            VideoInfoLabel.Text = $"Missing: {string.Join(", ", missing)}. Click 'Install FFmpeg…' to set up.";
            if (promptIfMissing)
            {
                var confirm = await ConfirmDialog.ShowAsync(this,
                    "FFmpeg required",
                    $"VideoEmpty needs FFmpeg to read & render videos. Missing: {string.Join(", ", missing)}.\n\n" +
                    (OperatingSystem.IsWindows() ? "Install now via winget (Gyan.FFmpeg)?" :
                     OperatingSystem.IsMacOS()   ? "Install now via Homebrew (brew install ffmpeg)?" :
                                                   "Install now via your system package manager?"));
                if (confirm) await InstallDepsAsync();
            }
        }
        catch (Exception ex)
        {
            Log.Error("UI", "Dependency check failed", ex);
            VideoInfoLabel.Text = $"Dependency check failed: {ex.Message} (See log: {Log.LogPath})";
        }
    }

    private async void OnInstallDeps(object? sender, RoutedEventArgs e) => await InstallDepsAsync();

    private async Task InstallDepsAsync()
    {
        InstallDepsButton.IsEnabled = false;
        var progress = new Progress<DependencyInstallProgress>(p =>
            ExportStatus.Text = $"Install {p.Name}: {p.Stage} {p.Detail}".Trim());
        try
        {
            ExportStatus.Text = "Starting installer (this may prompt for elevation)…";
            await _api.Dependencies.InstallMissingAsync(progress);
            ExportStatus.Text = "Install complete.";
            await CheckDependenciesAsync(promptIfMissing: false);
        }
        catch (Exception ex)
        {
            Log.Error("UI", "Install failed", ex);
            VideoInfoLabel.Text = $"Install failed: {ex.Message} (See log: {Log.LogPath})";
            ExportStatus.Text = "Install failed.";
        }
        finally
        {
            InstallDepsButton.IsEnabled = true;
        }
    }

    private static void OpenInShell(string path)
    {
        try
        {
            var dir = System.IO.Path.GetDirectoryName(path) ?? path;
            if (OperatingSystem.IsWindows())
                Process.Start(new ProcessStartInfo("explorer.exe", $"\"{dir}\"") { UseShellExecute = true });
            else if (OperatingSystem.IsMacOS())
                Process.Start("open", dir);
            else
                Process.Start("xdg-open", dir);
        }
        catch (Exception ex) { Log.Error("UI", "Open log folder failed", ex); }
    }

    private void RefreshTemplates()
    {
        Templates.Clear();
        foreach (var t in _api.ListTemplates(_project)) Templates.Add(t);
    }

    private void RefreshInstances()
    {
        Instances.Clear();
        foreach (var i in _api.ListInstances(_project)
                 .OrderBy(i => i.StartMs)) Instances.Add(i);
    }

    private async void OnOpenVideo(object? sender, RoutedEventArgs e)
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Open video",
            AllowMultiple = false,
            FileTypeFilter = new[]
            {
                new FilePickerFileType("Video") { Patterns = new[] { "*.mp4","*.mov","*.mkv","*.avi","*.webm" } }
            }
        });
        if (files.Count == 0) return;
        var path = files[0].TryGetLocalPath();
        if (path is null) return;
        try
        {
            _project = await _api.SetVideoAsync(_project, path);
            TimeSlider.Maximum = Math.Max(1, _project.VideoDurationMs);
            TimeSlider.Value = 0;
            VideoInfoLabel.Text = $"{System.IO.Path.GetFileName(path)} • {_project.VideoResolution.Width}x{_project.VideoResolution.Height} @ {_project.VideoFps:0.##} fps • {_project.VideoDurationMs / 1000.0:0.0}s";
            await RefreshPreviewAsync();
        }
        catch (Exception ex)
        {
            Log.Error("UI", "OpenVideo failed", ex);
            VideoInfoLabel.Text = $"Error: {ex.Message} (See log: {Log.LogPath})";
        }
    }

    private void CommitInstanceEdit()
    {
        if (InstancesList.SelectedItem is not TemplateInstance) return;
        OnApplyInstance(this, new RoutedEventArgs());
        _ = RefreshPreviewAsync();
    }

    private async Task RefreshPreviewAsync()
    {
        if (string.IsNullOrEmpty(_project.VideoPath)) return;
        try
        {
            var bytes = await _api.RenderFrameAsync(_project, _currentTimeMs);
            using var ms = new System.IO.MemoryStream(bytes);
            PreviewImage.Source = new Bitmap(ms);
            TimeLabel.Text = $"{_currentTimeMs} ms";
            PlaybackTimeLabel.Text = $"{FormatTime(_currentTimeMs)} / {FormatTime(_project.VideoDurationMs)}";
        }
        catch (Exception ex)
        {
            Log.Error("UI", "RefreshPreview failed", ex);
            VideoInfoLabel.Text = $"Preview error: {ex.Message} (See log: {Log.LogPath})";
        }
    }

    private void OnPreviewClicked(object? sender, PointerPressedEventArgs e)
    {
        if (_armedTemplateId is null || PreviewImage.Source is null) return;
        var pos = e.GetPosition(PreviewImage);
        double cx = Math.Clamp(pos.X / Math.Max(1, PreviewImage.Bounds.Width), 0, 1);
        double cy = Math.Clamp(pos.Y / Math.Max(1, PreviewImage.Bounds.Height), 0, 1);

        // Pause playback while editing.
        if (_playTimer is { IsEnabled: true }) TogglePlay();

        var template = _api.GetTemplate(_project, _armedTemplateId);
        var placement = ResolveClickPlacement(template, cx, cy);
        // Pre-fill text with each text element's default so the user can edit in place.
        var values = template.Elements.OfType<TextElement>()
            .ToDictionary(t => t.Id, t => t.DefaultText ?? "");
        var inst = _api.AddInstance(_project, new AddInstanceRequest(
            template.Id, placement.centerX, placement.centerY, _currentTimeMs, null, values, placement.animationOverride));
        RefreshInstances();
        InstancesList.SelectedItem = Instances.FirstOrDefault(i => i.Id == inst.Id);

        // Focus the text box so the user can immediately type the caption.
        Dispatcher.UIThread.Post(() =>
        {
            InstanceTextBox.Focus();
            InstanceTextBox.SelectAll();
        }, DispatcherPriority.Background);
        _ = RefreshPreviewAsync();
    }

    /// <summary>Distributes lines of input across the template's text elements (in order).</summary>
    private static System.Collections.Generic.Dictionary<string, string> MapTextToElements(Template t, string text)
    {
        var lines = text.Replace("\r\n", "\n").Split('\n');
        var textElements = t.Elements.OfType<TextElement>().ToList();
        var d = new System.Collections.Generic.Dictionary<string, string>();
        for (int i = 0; i < textElements.Count; i++)
            d[textElements[i].Id] = i < lines.Length ? lines[i] : "";
        return d;
    }

    private void UpdateInstanceEditor()
    {
        if (InstancesList.SelectedItem is not TemplateInstance i)
        {
            InstanceEditor.IsVisible = false;
            return;
        }
        InstanceEditor.IsVisible = true;
        InstanceStartBox.Text = i.StartMs.ToString();
        InstanceDurationBox.Text = i.DurationMs.ToString();
        InstanceXBox.Text = i.Center.X.ToString("0.###");
        InstanceYBox.Text = i.Center.Y.ToString("0.###");
        var template = _project.Templates.FirstOrDefault(t => t.Id == i.TemplateId);
        var sb = new System.Text.StringBuilder();
        if (template is not null)
        {
            foreach (var te in template.Elements.OfType<TextElement>())
            {
                if (sb.Length > 0) sb.AppendLine();
                sb.Append(i.TextValues.TryGetValue(te.Id, out var v) ? v : te.DefaultText);
            }
        }
        InstanceTextBox.Text = sb.ToString();
    }

    private void OnApplyInstance(object? sender, RoutedEventArgs e)
    {
        if (InstancesList.SelectedItem is not TemplateInstance i) return;
        var template = _project.Templates.First(t => t.Id == i.TemplateId);
        var req = new UpdateInstanceRequest(
            i.Id,
            double.TryParse(InstanceXBox.Text, out var x) ? x : null,
            double.TryParse(InstanceYBox.Text, out var y) ? y : null,
            int.TryParse(InstanceStartBox.Text, out var s) ? s : null,
            int.TryParse(InstanceDurationBox.Text, out var d) ? d : null,
            MapTextToElements(template, InstanceTextBox.Text ?? ""));
        _api.UpdateInstance(_project, req);
        RefreshInstances();
    }

    private async void OnSaveProject(object? sender, RoutedEventArgs e)
    {
        var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Save project",
            DefaultExtension = "veproj",
            SuggestedFileName = (_project.Name ?? "project") + ".veproj"
        });
        var path = file?.TryGetLocalPath();
        if (path is null) return;
        _api.SaveProject(_project, path);
    }

    private async void OnExport(object? sender, RoutedEventArgs e)
    {
        if (string.IsNullOrEmpty(_project.VideoPath))
        {
            VideoInfoLabel.Text = "Open a video first.";
            return;
        }
        var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Export to",
            DefaultExtension = "mp4",
            SuggestedFileName = "export.mp4"
        });
        var path = file?.TryGetLocalPath();
        if (path is null) return;
        var jobId = _api.StartExport(_project, new ExportOptions(path));
        ExportStatus.Text = $"Job {jobId[..8]}… running";
        _ = PollJob(jobId);
    }

    private async Task PollJob(string jobId)
    {
        while (true)
        {
            await Task.Delay(500);
            var s = _api.GetJobStatus(jobId);
            ExportStatus.Text = $"Job {jobId[..8]}: {s.State} ({s.Progress * 100:0}%) {s.Message}";
            if (s.State is JobState.Completed or JobState.Failed or JobState.Cancelled)
            {
                if (s.State == JobState.Failed) ExportStatus.Text += " — " + s.Error;
                else if (s.State == JobState.Completed) ExportStatus.Text = $"Done → {s.OutputPath}";
                break;
            }
        }
    }
}
