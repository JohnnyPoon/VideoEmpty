using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using VideoEmpty.Core.Api;
using VideoEmpty.Core.Diagnostics;
using VideoEmpty.Core.Model;
using VideoEmpty.Core.Serialization;
using VideoEmpty.Rendering;

namespace VideoEmpty.UI;

public partial class MainWindow : Window
{
    private readonly IVideoEmptyApi _api = VideoEmptyServices.CreateApi();
    private readonly UiSettings _settings = UiSettingsStore.Load();
    private Project _project;
    private string? _projectPath;
    private string? _armedTemplateId;
    private int _currentTimeMs;
    private DispatcherTimer? _playTimer;
    private bool _isApplyingProject;

    public ObservableCollection<Template> Templates { get; } = new();
    public ObservableCollection<InstanceListItem> Instances { get; } = new();
    public ObservableCollection<RecentProjectItem> RecentProjects { get; } = new();
    public ObservableCollection<ElementListItem> ElementsListItems { get; } = new();

    private bool _isApplyingTemplateEditor;

    public MainWindow()
    {
        InitializeComponent();

        _project = _api.CreateProject("Untitled");
        DataContext = this;

        TemplatesList.ItemsSource = Templates;
        InstancesList.ItemsSource = Instances;
        RecentProjectsList.ItemsSource = RecentProjects;
        ElementsList.ItemsSource = ElementsListItems;
        TemplateEnterBox.ItemsSource = Enum.GetValues<AnimationStyle>();
        TemplateExitBox.ItemsSource = Enum.GetValues<AnimationStyle>();
        ShapeKindBox.ItemsSource = Enum.GetValues<ShapeKind>();
        TextHAlignBox.ItemsSource = Enum.GetValues<HorizontalAlign>();
        TextVAlignBox.ItemsSource = Enum.GetValues<VerticalAlign>();

        SaveProjectButton.Click += OnSaveProject;
        UndoButton.Click += OnUndo;
        SettingsButton.Click += OnSettings;
        DashboardButton.Click += (_, _) => ShowDashboard();
        OpenVideoButton.Click += OnOpenVideo;
        ExportButton.Click += OnExport;
        InstallDepsButton.Click += OnInstallDeps;
        OpenLogButton.Click += (_, _) => OpenInShell(Log.LogPath);

        DashboardNewProjectButton.Click += OnNewProject;
        DashboardOpenProjectButton.Click += OnOpenProject;
        DashboardOpenLogButton.Click += (_, _) => OpenInShell(Log.LogPath);
        RecentProjectsList.DoubleTapped += OnOpenRecentProject;
        AddTemplateButton.Click += OnAddTemplate;
        DuplicateTemplateButton.Click += OnDuplicateTemplate;
        DeleteTemplateButton.Click += OnDeleteTemplate;
        ApplyTemplateJsonButton.Click += OnApplyTemplateJson;
        WireTemplateEditorAutoApply();

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
            if (TemplatesList.SelectedItem is Template t)
            {
                _armedTemplateId = t.Id;
                ArmedLabel.Text = $"Armed: {t.Name}";
            }
            else
            {
                _armedTemplateId = null;
                ArmedLabel.Text = "(none armed)";
            }
            UpdateTemplateEditor();
        };

        PreviewImage.PointerPressed += OnPreviewClicked;
        InstancesList.SelectionChanged += (_, _) => UpdateInstanceEditor();
        DeleteInstanceButton.Click += OnDeleteInstance;
        ApplyInstanceButton.Click += OnApplyInstance;
        PreviewInstanceButton.Click += OnPreviewInstance;

        PlayPauseButton.Click += (_, _) => TogglePlay();
        StepBackButton.Click += (_, _) => SeekRelative(-FrameDurationMs());
        StepForwardButton.Click += (_, _) => SeekRelative(+FrameDurationMs());
        JumpBack1sButton.Click += (_, _) => SeekRelative(-1000);
        JumpForward1sButton.Click += (_, _) => SeekRelative(+1000);
        JumpBack10sButton.Click += (_, _) => SeekRelative(-10000);
        JumpForward10sButton.Click += (_, _) => SeekRelative(+10000);

        InstanceTextBox.LostFocus += (_, _) => CommitInstanceEdit();
        InstanceTextBox.KeyDown += (_, args) =>
        {
            if (args.Key == Key.Enter && args.KeyModifiers.HasFlag(KeyModifiers.Control))
            {
                CommitInstanceEdit();
                args.Handled = true;
            }
        };

        LoadRecentProjects();
        ApplyProject(_project, null, showDashboard: true);
        Dispatcher.UIThread.Post(async () => await CheckDependenciesAsync(promptIfMissing: true), DispatcherPriority.Background);
    }

    private void ApplyProject(Project project, string? path, bool showDashboard = false)
    {
        _isApplyingProject = true;
        try
        {
            _project = project;
            _projectPath = path;
            _currentTimeMs = 0;
            TimeSlider.Value = 0;
            TimeSlider.Maximum = Math.Max(1, _project.VideoDurationMs);
            VideoInfoLabel.Text = string.IsNullOrWhiteSpace(_project.VideoPath)
                ? "No video loaded."
                : $"{Path.GetFileName(_project.VideoPath)} • {_project.VideoResolution.Width}x{_project.VideoResolution.Height} @ {_project.VideoFps:0.##} fps • {_project.VideoDurationMs / 1000.0:0.0}s";
            RefreshTemplates();
            RefreshInstances();
            UpdateTemplateEditor();
            UpdateInstanceEditor();
            DashboardRoot.IsVisible = showDashboard;
            EditorRoot.IsVisible = !showDashboard;
            _ = RefreshPreviewAsync();
        }
        finally
        {
            _isApplyingProject = false;
        }
    }

    private void ShowDashboard()
    {
        LoadRecentProjects();
        DashboardRoot.IsVisible = true;
        EditorRoot.IsVisible = false;
    }

    private async void OnNewProject(object? sender, RoutedEventArgs e)
    {
        var defaultName = $"{DateTime.Today:yyyy-MM-dd}-Project";
        var dlg = new TextEntryDialog("Project name", "New Project", defaultName);
        var input = await dlg.ShowDialog<string?>(this);
        if (string.IsNullOrWhiteSpace(input)) return;

        var name = input.Trim();
        var p = _api.CreateProject(name);
        var path = UiSettingsStore.GetProjectPath(name);
        ApplyProject(p, path);
        RememberRecentProject(path);
        await AutoSaveAsync("new-project");
    }

    private async void OnOpenProject(object? sender, RoutedEventArgs e)
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Open project",
            AllowMultiple = false,
            FileTypeFilter = new[] { new FilePickerFileType("VideoEmpty Project") { Patterns = new[] { "*.veproj" } } }
        });
        var path = files.FirstOrDefault()?.TryGetLocalPath();
        if (string.IsNullOrWhiteSpace(path)) return;
        await OpenProjectPathAsync(path);
    }

    private async void OnOpenRecentProject(object? sender, RoutedEventArgs e)
    {
        if (RecentProjectsList.SelectedItem is not RecentProjectItem item) return;
        await OpenProjectPathAsync(item.Path);
    }

    private async Task OpenProjectPathAsync(string path)
    {
        try
        {
            var p = _api.OpenProject(path);
            ApplyProject(p, path);
            RememberRecentProject(path);
            if (!string.IsNullOrWhiteSpace(_project.VideoPath)) await RefreshPreviewAsync();
        }
        catch (Exception ex)
        {
            Log.Error("UI", $"Open project failed: {path}", ex);
            VideoInfoLabel.Text = $"Open project failed: {ex.Message}";
        }
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
        if (string.IsNullOrWhiteSpace(path)) return;
        _projectPath = path;
        await AutoSaveAsync("manual-save");
    }

    private async void OnUndo(object? sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(_projectPath))
        {
            ExportStatus.Text = "Undo unavailable: project not saved yet.";
            return;
        }

        var backupDir = UiSettingsStore.GetBackupDir(_projectPath);
        var latest = Directory.Exists(backupDir)
            ? Directory.GetFiles(backupDir, "*.veproj.bak").OrderByDescending(x => x).FirstOrDefault()
            : null;
        if (latest is null)
        {
            ExportStatus.Text = "Undo unavailable: no backup file.";
            return;
        }

        try
        {
            File.Copy(latest, _projectPath, overwrite: true);
            var p = _api.OpenProject(_projectPath);
            ApplyProject(p, _projectPath);
            ExportStatus.Text = $"Undo restored backup: {Path.GetFileName(latest)}";
            await RefreshPreviewAsync();
        }
        catch (Exception ex)
        {
            Log.Error("UI", "Undo restore failed", ex);
            ExportStatus.Text = $"Undo failed: {ex.Message}";
        }
    }

    private async void OnSettings(object? sender, RoutedEventArgs e)
    {
        var dlg = new SettingsDialog(_settings);
        var ok = await dlg.ShowDialog<bool>(this);
        if (!ok) return;
        _settings.AutoDeleteBackupsEnabled = dlg.EnableAutoDelete == true;
        _settings.AutoDeleteBackupsDays = Math.Max(1, dlg.AutoDeleteDays ?? 90);
        UiSettingsStore.Save(_settings);
        CleanupBackups();
        ExportStatus.Text = "Settings saved.";
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
        _playTimer ??= new DispatcherTimer();
        _playTimer.Interval = TimeSpan.FromMilliseconds(FrameDurationMs());
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
            if (LoopCheck.IsChecked == true) next = 0;
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
            if (missing.Count == 0) return;
            if (!promptIfMissing) return;
            var confirm = await ConfirmDialog.ShowAsync(this,
                "FFmpeg required",
                $"VideoEmpty needs FFmpeg. Missing: {string.Join(", ", missing)}.\nInstall now?");
            if (confirm) await InstallDepsAsync();
        }
        catch (Exception ex)
        {
            Log.Error("UI", "Dependency check failed", ex);
            VideoInfoLabel.Text = $"Dependency check failed: {ex.Message}";
        }
    }

    private async void OnInstallDeps(object? sender, RoutedEventArgs e) => await InstallDepsAsync();

    private async Task InstallDepsAsync()
    {
        InstallDepsButton.IsEnabled = false;
        var progress = new Progress<DependencyInstallProgress>(p => ExportStatus.Text = $"Install {p.Name}: {p.Stage} {p.Detail}".Trim());
        try
        {
            await _api.Dependencies.InstallMissingAsync(progress);
            await CheckDependenciesAsync(promptIfMissing: false);
            ExportStatus.Text = "Install complete.";
        }
        catch (Exception ex)
        {
            Log.Error("UI", "Install failed", ex);
            ExportStatus.Text = $"Install failed: {ex.Message}";
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
            var dir = Path.GetDirectoryName(path) ?? path;
            if (OperatingSystem.IsWindows())
                Process.Start(new ProcessStartInfo("explorer.exe", $"\"{dir}\"") { UseShellExecute = true });
            else if (OperatingSystem.IsMacOS())
                Process.Start("open", dir);
            else
                Process.Start("xdg-open", dir);
        }
        catch (Exception ex) { Log.Error("UI", "Open folder failed", ex); }
    }

    private void RefreshTemplates()
    {
        Templates.Clear();
        foreach (var t in _api.ListTemplates(_project)) Templates.Add(t);
    }

    private void RefreshInstances()
    {
        Instances.Clear();
        foreach (var i in _api.ListInstances(_project).OrderBy(i => i.StartMs))
        {
            var templateName = _project.Templates.FirstOrDefault(t => t.Id == i.TemplateId)?.Name ?? i.TemplateId;
            var startTs = TimeSpan.FromMilliseconds(i.StartMs);
            Instances.Add(new InstanceListItem
            {
                Instance = i,
                TemplateName = templateName,
                TimeLabel = $"{(int)startTs.TotalMinutes}:{startTs.Seconds:00}.{startTs.Milliseconds:000}"
            });
        }
    }

    private TemplateInstance? SelectedInstance =>
        InstancesList.SelectedItem is InstanceListItem item ? item.Instance : null;

    private async void OnOpenVideo(object? sender, RoutedEventArgs e)
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Open video",
            AllowMultiple = false,
            FileTypeFilter = new[] { new FilePickerFileType("Video") { Patterns = new[] { "*.mp4", "*.mov", "*.mkv", "*.avi", "*.webm" } } }
        });
        var path = files.FirstOrDefault()?.TryGetLocalPath();
        if (string.IsNullOrWhiteSpace(path)) return;

        try
        {
            _project = await _api.SetVideoAsync(_project, path);
            TimeSlider.Maximum = Math.Max(1, _project.VideoDurationMs);
            TimeSlider.Value = 0;
            VideoInfoLabel.Text = $"{Path.GetFileName(path)} • {_project.VideoResolution.Width}x{_project.VideoResolution.Height} @ {_project.VideoFps:0.##} fps • {_project.VideoDurationMs / 1000.0:0.0}s";
            await RefreshPreviewAsync();
            await AutoSaveAsync("set-video");
        }
        catch (Exception ex)
        {
            Log.Error("UI", "OpenVideo failed", ex);
            VideoInfoLabel.Text = $"Error: {ex.Message}";
        }
    }

    private void CommitInstanceEdit()
    {
        if (InstancesList.SelectedItem is not InstanceListItem) return;
        OnApplyInstance(this, new RoutedEventArgs());
        _ = RefreshPreviewAsync();
    }

    private async Task RefreshPreviewAsync()
    {
        if (string.IsNullOrEmpty(_project.VideoPath)) return;
        try
        {
            var bytes = await _api.RenderFrameAsync(_project, _currentTimeMs);
            using var ms = new MemoryStream(bytes);
            PreviewImage.Source = new Bitmap(ms);
            TimeLabel.Text = $"{_currentTimeMs} ms";
            PlaybackTimeLabel.Text = $"{FormatTime(_currentTimeMs)} / {FormatTime(_project.VideoDurationMs)}";
        }
        catch (Exception ex)
        {
            Log.Error("UI", "RefreshPreview failed", ex);
            VideoInfoLabel.Text = $"Preview error: {ex.Message}";
        }
    }

    private async void OnPreviewClicked(object? sender, PointerPressedEventArgs e)
    {
        if (_armedTemplateId is null || PreviewImage.Source is null) return;
        var pos = e.GetPosition(PreviewImage);
        double cx = Math.Clamp(pos.X / Math.Max(1, PreviewImage.Bounds.Width), 0, 1);
        double cy = Math.Clamp(pos.Y / Math.Max(1, PreviewImage.Bounds.Height), 0, 1);

        if (_playTimer is { IsEnabled: true }) TogglePlay();

        var template = _api.GetTemplate(_project, _armedTemplateId);
        var placement = ResolveClickPlacement(template, cx, cy);
        var values = template.Elements.OfType<TextElement>().ToDictionary(t => t.Id, t => t.DefaultText ?? "");
        var inst = _api.AddInstance(_project, new AddInstanceRequest(template.Id, placement.centerX, placement.centerY, _currentTimeMs, null, values, placement.animationOverride));
        RefreshInstances();
        InstancesList.SelectedItem = Instances.FirstOrDefault(item => item.Instance.Id == inst.Id);
        Dispatcher.UIThread.Post(() => { InstanceTextBox.Focus(); InstanceTextBox.SelectAll(); }, DispatcherPriority.Background);
        await RefreshPreviewAsync();
        await AutoSaveAsync("add-instance");
    }

    private async void OnDeleteInstance(object? sender, RoutedEventArgs e)
    {
        if (SelectedInstance is not { } i) return;
        _api.DeleteInstance(_project, i.Id);
        RefreshInstances();
        await AutoSaveAsync("delete-instance");
    }

    private static Dictionary<string, string> MapTextToElements(Template t, string text)
    {
        var lines = text.Replace("\r\n", "\n").Split('\n');
        var textElements = t.Elements.OfType<TextElement>().ToList();
        var d = new Dictionary<string, string>();
        for (int i = 0; i < textElements.Count; i++)
            d[textElements[i].Id] = i < lines.Length ? lines[i] : "";
        return d;
    }

    private void UpdateInstanceEditor()
    {
        if (SelectedInstance is not { } i)
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
        InstanceTemplateNameLabel.Text = template is not null ? $"Template: {template.Name}" : $"Template ID: {i.TemplateId}";
        var lines = new List<string>();
        if (template is not null)
        {
            foreach (var te in template.Elements.OfType<TextElement>())
                lines.Add(i.TextValues.TryGetValue(te.Id, out var v) ? v : te.DefaultText);
        }
        InstanceTextBox.Text = string.Join(Environment.NewLine, lines);
    }

    private async void OnApplyInstance(object? sender, RoutedEventArgs e)
    {
        if (SelectedInstance is not { } i) return;
        var template = _project.Templates.First(t => t.Id == i.TemplateId);
        var req = new UpdateInstanceRequest(
            i.Id,
            double.TryParse(InstanceXBox.Text, out var x) ? x : null,
            double.TryParse(InstanceYBox.Text, out var y) ? y : null,
            int.TryParse(InstanceStartBox.Text, out var s) ? s : null,
            int.TryParse(InstanceDurationBox.Text, out var d) ? d : null,
            MapTextToElements(template, InstanceTextBox.Text ?? ""),
            null);
        _api.UpdateInstance(_project, req);
        RefreshInstances();
        await AutoSaveAsync("update-instance");
    }

    private void UpdateTemplateEditor()
    {
        if (TemplatesList.SelectedItem is not Template t)
        {
            TemplateEditorScroll.IsVisible = false;
            TemplateJsonBox.Text = "";
            ElementsListItems.Clear();
            ElementEditorPanel.IsVisible = false;
            return;
        }

        TemplateEditorScroll.IsVisible = true;
        _isApplyingTemplateEditor = true;
        try
        {
            TemplateNameBox.Text = t.Name;
            TemplateWidthBox.Text = t.Width.ToString();
            TemplateHeightBox.Text = t.Height.ToString();
            TemplateDurationBox.Text = t.DefaultDurationMs.ToString();
            TemplateEnterBox.SelectedItem = t.Animation.Enter;
            TemplateExitBox.SelectedItem = t.Animation.Exit;
            TemplateEnterMsBox.Text = t.Animation.EnterMs.ToString();
            TemplateExitMsBox.Text = t.Animation.ExitMs.ToString();
            TemplateSoundEnterBox.Text = t.Sound.EnterFile ?? "";
            TemplateSoundExitBox.Text = t.Sound.ExitFile ?? "";
            TemplateSoundVolumeSlider.Value = t.Sound.Volume;
            TemplateSoundVolumeLabel.Text = t.Sound.Volume.ToString("0.00");
            TemplateJsonBox.Text = JsonSerializer.Serialize(t, ProjectJson.Options);

            RebuildElementsList(t, preserveSelectionId: (ElementsList.SelectedItem as ElementListItem)?.Element.Id);
        }
        finally
        {
            _isApplyingTemplateEditor = false;
        }

        UpdateElementEditor();
    }

    private void RebuildElementsList(Template t, string? preserveSelectionId)
    {
        ElementsListItems.Clear();
        foreach (var el in t.Elements)
            ElementsListItems.Add(BuildElementListItem(el));

        if (preserveSelectionId is not null)
        {
            var match = ElementsListItems.FirstOrDefault(x => x.Element.Id == preserveSelectionId);
            if (match is not null) ElementsList.SelectedItem = match;
        }
    }

    private static ElementListItem BuildElementListItem(Element el) => el switch
    {
        ShapeElement s => new ElementListItem
        {
            Element = s,
            Icon = s.Shape switch { ShapeKind.Ellipse => "⬭", ShapeKind.RoundedRectangle => "▢", _ => "▭" },
            Summary = $"{s.Shape} {s.Width}×{s.Height}"
        },
        TextElement tx => new ElementListItem
        {
            Element = tx,
            Icon = "T",
            Summary = $"Text: \"{Truncate(tx.DefaultText, 28)}\" ({tx.FontSize}pt)"
        },
        _ => new ElementListItem { Element = el, Icon = "?", Summary = el.Id }
    };

    private static string Truncate(string s, int max) =>
        string.IsNullOrEmpty(s) ? "" : (s.Length <= max ? s : s[..max] + "…");

    private void UpdateElementEditor()
    {
        var item = ElementsList.SelectedItem as ElementListItem;
        if (item is null)
        {
            ElementEditorPanel.IsVisible = false;
            return;
        }

        ElementEditorPanel.IsVisible = true;
        _isApplyingTemplateEditor = true;
        try
        {
            var el = item.Element;
            ElementOffsetXBox.Text = el.OffsetX.ToString();
            ElementOffsetYBox.Text = el.OffsetY.ToString();
            ElementWidthBox.Text = el.Width.ToString();
            ElementHeightBox.Text = el.Height.ToString();

            if (el is ShapeElement s)
            {
                ElementEditorHeader.Text = $"Shape — {s.Shape}";
                ShapeElementEditor.IsVisible = true;
                TextElementEditor.IsVisible = false;
                ShapeKindBox.SelectedItem = s.Shape;
                ShapeFillBox.Text = s.Fill.ToHex();
                ShapeBorderColorBox.Text = s.BorderColor.ToHex();
                ShapeBorderThicknessBox.Text = s.BorderThickness.ToString();
                ShapeCornerRadiusBox.Text = s.CornerRadius.ToString();
                UpdateColorSwatch(ShapeFillSwatch, s.Fill);
                UpdateColorSwatch(ShapeBorderSwatch, s.BorderColor);
            }
            else if (el is TextElement tx)
            {
                ElementEditorHeader.Text = "Text element";
                ShapeElementEditor.IsVisible = false;
                TextElementEditor.IsVisible = true;
                TextFontFamilyBox.Text = tx.FontFamily;
                TextFontSizeBox.Text = tx.FontSize.ToString();
                TextBoldBox.IsChecked = tx.Bold;
                TextItalicBox.IsChecked = tx.Italic;
                TextColorBox.Text = tx.TextColor.ToHex();
                TextHAlignBox.SelectedItem = tx.HAlign;
                TextVAlignBox.SelectedItem = tx.VAlign;
                TextLineSpacingBox.Text = tx.LineSpacing.ToString();
                TextDefaultTextBox.Text = tx.DefaultText;
                UpdateColorSwatch(TextColorSwatch, tx.TextColor);
            }
        }
        finally
        {
            _isApplyingTemplateEditor = false;
        }
    }

    private static void UpdateColorSwatch(Border swatch, Color c)
    {
        swatch.Background = new Avalonia.Media.SolidColorBrush(
            Avalonia.Media.Color.FromArgb(c.A, c.R, c.G, c.B));
    }

    // ── Auto-apply wiring ──────────────────────────────────────────────────

    private void WireTemplateEditorAutoApply()
    {
        // Template-level
        TemplateNameBox.LostFocus += (_, _) => CommitTemplateEditor();
        TemplateWidthBox.LostFocus += (_, _) => CommitTemplateEditor();
        TemplateHeightBox.LostFocus += (_, _) => CommitTemplateEditor();
        TemplateDurationBox.LostFocus += (_, _) => CommitTemplateEditor();
        TemplateEnterMsBox.LostFocus += (_, _) => CommitTemplateEditor();
        TemplateExitMsBox.LostFocus += (_, _) => CommitTemplateEditor();
        TemplateEnterBox.SelectionChanged += (_, _) => CommitTemplateEditor();
        TemplateExitBox.SelectionChanged += (_, _) => CommitTemplateEditor();
        TemplateSoundEnterBox.LostFocus += (_, _) => CommitTemplateEditor();
        TemplateSoundExitBox.LostFocus += (_, _) => CommitTemplateEditor();
        TemplateSoundVolumeSlider.PropertyChanged += (_, e) =>
        {
            if (e.Property == Slider.ValueProperty)
            {
                TemplateSoundVolumeLabel.Text = TemplateSoundVolumeSlider.Value.ToString("0.00");
                CommitTemplateEditor();
            }
        };
        TemplateSoundEnterBrowse.Click += async (_, _) => await BrowseForSoundAsync(TemplateSoundEnterBox);
        TemplateSoundExitBrowse.Click += async (_, _) => await BrowseForSoundAsync(TemplateSoundExitBox);
        TemplateSoundEnterClear.Click += (_, _) => { TemplateSoundEnterBox.Text = ""; CommitTemplateEditor(); };
        TemplateSoundExitClear.Click += (_, _) => { TemplateSoundExitBox.Text = ""; CommitTemplateEditor(); };

        // Elements list + toolbar
        ElementsList.SelectionChanged += (_, _) => UpdateElementEditor();
        AddShapeElementButton.Click += async (_, _) => await OnAddShapeElementAsync();
        AddTextElementButton.Click += async (_, _) => await OnAddTextElementAsync();
        DuplicateElementButton.Click += async (_, _) => await OnDuplicateElementAsync();
        DeleteElementButton.Click += async (_, _) => await OnDeleteElementAsync();
        MoveElementUpButton.Click += async (_, _) => await OnMoveElementAsync(-1);
        MoveElementDownButton.Click += async (_, _) => await OnMoveElementAsync(+1);

        // Common element fields
        ElementOffsetXBox.LostFocus += (_, _) => CommitElementEditor();
        ElementOffsetYBox.LostFocus += (_, _) => CommitElementEditor();
        ElementWidthBox.LostFocus += (_, _) => CommitElementEditor();
        ElementHeightBox.LostFocus += (_, _) => CommitElementEditor();

        // Shape fields
        ShapeKindBox.SelectionChanged += (_, _) => CommitElementEditor();
        ShapeFillBox.LostFocus += (_, _) => CommitElementEditor();
        ShapeBorderColorBox.LostFocus += (_, _) => CommitElementEditor();
        ShapeBorderThicknessBox.LostFocus += (_, _) => CommitElementEditor();
        ShapeCornerRadiusBox.LostFocus += (_, _) => CommitElementEditor();

        // Text fields
        TextFontFamilyBox.LostFocus += (_, _) => CommitElementEditor();
        TextFontSizeBox.LostFocus += (_, _) => CommitElementEditor();
        TextBoldBox.IsCheckedChanged += (_, _) => CommitElementEditor();
        TextItalicBox.IsCheckedChanged += (_, _) => CommitElementEditor();
        TextColorBox.LostFocus += (_, _) => CommitElementEditor();
        TextHAlignBox.SelectionChanged += (_, _) => CommitElementEditor();
        TextVAlignBox.SelectionChanged += (_, _) => CommitElementEditor();
        TextLineSpacingBox.LostFocus += (_, _) => CommitElementEditor();
        TextDefaultTextBox.LostFocus += (_, _) => CommitElementEditor();
    }

    private async Task BrowseForSoundAsync(TextBox target)
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Choose sound file",
            AllowMultiple = false,
            FileTypeFilter = new[] { new FilePickerFileType("Audio") { Patterns = new[] { "*.mp3", "*.wav", "*.ogg", "*.m4a", "*.aac" } } }
        });
        var path = files.FirstOrDefault()?.TryGetLocalPath();
        if (string.IsNullOrWhiteSpace(path)) return;
        target.Text = path;
        CommitTemplateEditor();
    }

    private async void CommitTemplateEditor()
    {
        if (_isApplyingTemplateEditor) return;
        if (TemplatesList.SelectedItem is not Template t) return;

        try
        {
            t.Name = string.IsNullOrWhiteSpace(TemplateNameBox.Text) ? t.Name : TemplateNameBox.Text.Trim();
            if (int.TryParse(TemplateWidthBox.Text, out var w)) t.Width = Math.Max(10, w);
            if (int.TryParse(TemplateHeightBox.Text, out var h)) t.Height = Math.Max(10, h);
            if (int.TryParse(TemplateDurationBox.Text, out var dur)) t.DefaultDurationMs = Math.Max(1, dur);
            if (TemplateEnterBox.SelectedItem is AnimationStyle enter) t.Animation.Enter = enter;
            if (TemplateExitBox.SelectedItem is AnimationStyle exit) t.Animation.Exit = exit;
            if (int.TryParse(TemplateEnterMsBox.Text, out var enterMs)) t.Animation.EnterMs = Math.Max(0, enterMs);
            if (int.TryParse(TemplateExitMsBox.Text, out var exitMs)) t.Animation.ExitMs = Math.Max(0, exitMs);
            t.Sound.EnterFile = string.IsNullOrWhiteSpace(TemplateSoundEnterBox.Text) ? null : TemplateSoundEnterBox.Text.Trim();
            t.Sound.ExitFile = string.IsNullOrWhiteSpace(TemplateSoundExitBox.Text) ? null : TemplateSoundExitBox.Text.Trim();
            t.Sound.Volume = Math.Clamp(TemplateSoundVolumeSlider.Value, 0, 4);

            _api.UpdateTemplate(_project, t);
            _isApplyingTemplateEditor = true;
            try
            {
                TemplateJsonBox.Text = JsonSerializer.Serialize(t, ProjectJson.Options);
                RefreshTemplates();
                TemplatesList.SelectedItem = Templates.FirstOrDefault(x => x.Id == t.Id);
            }
            finally { _isApplyingTemplateEditor = false; }
            await AutoSaveAsync("update-template");
            await RefreshPreviewAsync();
        }
        catch (Exception ex)
        {
            Log.Error("UI", "CommitTemplateEditor failed", ex);
            ExportStatus.Text = $"Template error: {ex.Message}";
        }
    }

    private async void CommitElementEditor()
    {
        if (_isApplyingTemplateEditor) return;
        if (TemplatesList.SelectedItem is not Template t) return;
        if (ElementsList.SelectedItem is not ElementListItem item) return;

        var el = item.Element;
        try
        {
            if (int.TryParse(ElementOffsetXBox.Text, out var ox)) el.OffsetX = ox;
            if (int.TryParse(ElementOffsetYBox.Text, out var oy)) el.OffsetY = oy;
            if (int.TryParse(ElementWidthBox.Text, out var ew)) el.Width = Math.Max(1, ew);
            if (int.TryParse(ElementHeightBox.Text, out var eh)) el.Height = Math.Max(1, eh);

            if (el is ShapeElement s)
            {
                if (ShapeKindBox.SelectedItem is ShapeKind sk) s.Shape = sk;
                if (TryParseColor(ShapeFillBox.Text, out var fill)) { s.Fill = fill; UpdateColorSwatch(ShapeFillSwatch, fill); }
                if (TryParseColor(ShapeBorderColorBox.Text, out var bc)) { s.BorderColor = bc; UpdateColorSwatch(ShapeBorderSwatch, bc); }
                if (int.TryParse(ShapeBorderThicknessBox.Text, out var bt)) s.BorderThickness = Math.Max(0, bt);
                if (int.TryParse(ShapeCornerRadiusBox.Text, out var cr)) s.CornerRadius = Math.Max(0, cr);
            }
            else if (el is TextElement tx)
            {
                if (!string.IsNullOrWhiteSpace(TextFontFamilyBox.Text)) tx.FontFamily = TextFontFamilyBox.Text.Trim();
                if (int.TryParse(TextFontSizeBox.Text, out var fs)) tx.FontSize = Math.Max(4, fs);
                tx.Bold = TextBoldBox.IsChecked == true;
                tx.Italic = TextItalicBox.IsChecked == true;
                if (TryParseColor(TextColorBox.Text, out var tc)) { tx.TextColor = tc; UpdateColorSwatch(TextColorSwatch, tc); }
                if (TextHAlignBox.SelectedItem is HorizontalAlign ha) tx.HAlign = ha;
                if (TextVAlignBox.SelectedItem is VerticalAlign va) tx.VAlign = va;
                if (int.TryParse(TextLineSpacingBox.Text, out var ls)) tx.LineSpacing = Math.Max(0, ls);
                tx.DefaultText = TextDefaultTextBox.Text ?? "";
            }

            _api.UpdateTemplate(_project, t);
            _isApplyingTemplateEditor = true;
            try
            {
                TemplateJsonBox.Text = JsonSerializer.Serialize(t, ProjectJson.Options);
                // Update list-item summary in-place
                var rebuilt = BuildElementListItem(el);
                var idx = ElementsListItems.IndexOf(item);
                if (idx >= 0)
                {
                    ElementsListItems[idx] = rebuilt;
                    ElementsList.SelectedItem = rebuilt;
                }
            }
            finally { _isApplyingTemplateEditor = false; }
            await AutoSaveAsync("update-element");
            await RefreshPreviewAsync();
        }
        catch (Exception ex)
        {
            Log.Error("UI", "CommitElementEditor failed", ex);
            ExportStatus.Text = $"Element error: {ex.Message}";
        }
    }

    private static bool TryParseColor(string? hex, out Color c)
    {
        c = default;
        if (string.IsNullOrWhiteSpace(hex)) return false;
        try { c = Color.FromHex(hex.Trim()); return true; }
        catch { return false; }
    }

    private async Task OnAddShapeElementAsync()
    {
        if (TemplatesList.SelectedItem is not Template t) return;
        var el = new ShapeElement
        {
            Id = Guid.NewGuid().ToString("n"),
            OffsetX = 0, OffsetY = 0,
            Width = Math.Max(40, t.Width / 2),
            Height = Math.Max(40, t.Height / 2),
            Shape = ShapeKind.Rectangle,
            Fill = Color.White,
            BorderColor = Color.Black,
            BorderThickness = 2,
            CornerRadius = 0
        };
        t.Elements.Add(el);
        _api.UpdateTemplate(_project, t);
        UpdateTemplateEditor();
        ElementsList.SelectedItem = ElementsListItems.FirstOrDefault(x => x.Element.Id == el.Id);
        await AutoSaveAsync("add-shape-element");
        await RefreshPreviewAsync();
    }

    private async Task OnAddTextElementAsync()
    {
        if (TemplatesList.SelectedItem is not Template t) return;
        var el = new TextElement
        {
            Id = Guid.NewGuid().ToString("n"),
            OffsetX = 8, OffsetY = 8,
            Width = Math.Max(40, t.Width - 16),
            Height = Math.Max(20, t.Height - 16),
            FontFamily = "Segoe UI",
            FontSize = 24,
            TextColor = Color.Black,
            HAlign = HorizontalAlign.Center,
            VAlign = VerticalAlign.Center,
            DefaultText = "Text"
        };
        t.Elements.Add(el);
        _api.UpdateTemplate(_project, t);
        UpdateTemplateEditor();
        ElementsList.SelectedItem = ElementsListItems.FirstOrDefault(x => x.Element.Id == el.Id);
        await AutoSaveAsync("add-text-element");
        await RefreshPreviewAsync();
    }

    private async Task OnDuplicateElementAsync()
    {
        if (TemplatesList.SelectedItem is not Template t) return;
        if (ElementsList.SelectedItem is not ElementListItem item) return;
        var json = JsonSerializer.Serialize<Element>(item.Element, ProjectJson.Options);
        var dup = JsonSerializer.Deserialize<Element>(json, ProjectJson.Options);
        if (dup is null) return;
        dup.Id = Guid.NewGuid().ToString("n");
        dup.OffsetX += 10;
        dup.OffsetY += 10;
        var idx = t.Elements.IndexOf(item.Element);
        t.Elements.Insert(Math.Max(0, idx + 1), dup);
        _api.UpdateTemplate(_project, t);
        UpdateTemplateEditor();
        ElementsList.SelectedItem = ElementsListItems.FirstOrDefault(x => x.Element.Id == dup.Id);
        await AutoSaveAsync("duplicate-element");
        await RefreshPreviewAsync();
    }

    private async Task OnDeleteElementAsync()
    {
        if (TemplatesList.SelectedItem is not Template t) return;
        if (ElementsList.SelectedItem is not ElementListItem item) return;
        t.Elements.Remove(item.Element);
        _api.UpdateTemplate(_project, t);
        UpdateTemplateEditor();
        await AutoSaveAsync("delete-element");
        await RefreshPreviewAsync();
    }

    private async Task OnMoveElementAsync(int delta)
    {
        if (TemplatesList.SelectedItem is not Template t) return;
        if (ElementsList.SelectedItem is not ElementListItem item) return;
        var idx = t.Elements.IndexOf(item.Element);
        var newIdx = idx + delta;
        if (idx < 0 || newIdx < 0 || newIdx >= t.Elements.Count) return;
        (t.Elements[idx], t.Elements[newIdx]) = (t.Elements[newIdx], t.Elements[idx]);
        _api.UpdateTemplate(_project, t);
        UpdateTemplateEditor();
        ElementsList.SelectedItem = ElementsListItems.FirstOrDefault(x => x.Element.Id == item.Element.Id);
        await AutoSaveAsync("move-element");
        await RefreshPreviewAsync();
    }

    private async void OnApplyTemplateJson(object? sender, RoutedEventArgs e)
    {
        if (TemplatesList.SelectedItem is not Template current) return;
        try
        {
            var parsed = JsonSerializer.Deserialize<Template>(TemplateJsonBox.Text ?? "", ProjectJson.Options);
            if (parsed is null) throw new InvalidOperationException("Template JSON is empty.");
            parsed.Id = current.Id; // preserve selection and instance references
            _api.UpdateTemplate(_project, parsed);
            RefreshTemplates();
            TemplatesList.SelectedItem = Templates.FirstOrDefault(x => x.Id == parsed.Id);
            UpdateTemplateEditor();
            await AutoSaveAsync("update-template-json");
            await RefreshPreviewAsync();
        }
        catch (Exception ex)
        {
            Log.Error("UI", "Apply template JSON failed", ex);
            ExportStatus.Text = $"Template JSON error: {ex.Message}";
        }
    }

    private async void OnAddTemplate(object? sender, RoutedEventArgs e)
    {
        var t = new Template
        {
            Id = Guid.NewGuid().ToString("n"),
            Name = "New Template",
            Width = 420,
            Height = 140,
            DefaultDurationMs = 3000,
            Animation = new Animation { Enter = AnimationStyle.SlideLeft, Exit = AnimationStyle.SlideLeft, EnterMs = 350, ExitMs = 350 },
            Elements = new List<Element>
            {
                new ShapeElement
                {
                    Id = "shape.bg",
                    OffsetX = 0, OffsetY = 0, Width = 420, Height = 140,
                    Shape = ShapeKind.Rectangle, Fill = Color.Black, BorderColor = Color.White, BorderThickness = 4, CornerRadius = 0
                },
                new TextElement
                {
                    Id = "text.main",
                    OffsetX = 16, OffsetY = 16, Width = 388, Height = 108,
                    FontFamily = "Segoe UI", FontSize = 34, Bold = false, Italic = false,
                    TextColor = Color.White, HAlign = HorizontalAlign.Left, VAlign = VerticalAlign.Top,
                    DefaultText = "New caption"
                }
            }
        };
        _api.CreateTemplate(_project, t);
        RefreshTemplates();
        TemplatesList.SelectedItem = Templates.FirstOrDefault(x => x.Id == t.Id);
        await AutoSaveAsync("create-template");
    }

    private async void OnDuplicateTemplate(object? sender, RoutedEventArgs e)
    {
        if (TemplatesList.SelectedItem is not Template selected) return;
        var dup = _api.DuplicateTemplate(_project, selected.Id, $"{selected.Name} copy");
        RefreshTemplates();
        TemplatesList.SelectedItem = Templates.FirstOrDefault(x => x.Id == dup.Id);
        await AutoSaveAsync("duplicate-template");
    }

    private async void OnDeleteTemplate(object? sender, RoutedEventArgs e)
    {
        if (TemplatesList.SelectedItem is not Template selected) return;
        try
        {
            _api.DeleteTemplate(_project, selected.Id);
            RefreshTemplates();
            UpdateTemplateEditor();
            await AutoSaveAsync("delete-template");
        }
        catch (Exception ex)
        {
            Log.Error("UI", "Delete template failed", ex);
            ExportStatus.Text = $"Delete template failed: {ex.Message}";
        }
    }

    // ── Hover action handlers ──────────────────────────────────────────────

    private async void OnTemplateItemDuplicate(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string id }) return;
        var template = _project.Templates.FirstOrDefault(t => t.Id == id);
        if (template is null) return;
        var dup = _api.DuplicateTemplate(_project, id, $"{template.Name} copy");
        RefreshTemplates();
        TemplatesList.SelectedItem = Templates.FirstOrDefault(x => x.Id == dup.Id);
        await AutoSaveAsync("duplicate-template");
    }

    private async void OnTemplateItemDelete(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string id }) return;
        try
        {
            _api.DeleteTemplate(_project, id);
            RefreshTemplates();
            UpdateTemplateEditor();
            await AutoSaveAsync("delete-template");
        }
        catch (Exception ex)
        {
            Log.Error("UI", "Delete template failed", ex);
            ExportStatus.Text = $"Delete template failed: {ex.Message}";
        }
    }

    private void OnInstanceItemPreview(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string id }) return;
        var inst = _project.Instances.FirstOrDefault(i => i.Id == id);
        if (inst is not null)
            TimeSlider.Value = inst.StartMs;
    }

    private async void OnInstanceItemDelete(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string id }) return;
        _api.DeleteInstance(_project, id);
        RefreshInstances();
        await AutoSaveAsync("delete-instance");
    }

    private void OnPreviewInstance(object? sender, RoutedEventArgs e)
    {
        if (SelectedInstance is { } inst)
            TimeSlider.Value = inst.StartMs;
    }

    private Task AutoSaveAsync(string reason)
    {
        if (_isApplyingProject) return Task.CompletedTask;
        try
        {
            _projectPath ??= UiSettingsStore.GetProjectPath(_project.Name);
            if (File.Exists(_projectPath))
            {
                var backupDir = UiSettingsStore.GetBackupDir(_projectPath);
                var backupFile = Path.Combine(backupDir, $"{DateTime.Now:yyyyMMdd-HHmmss-fff}.veproj.bak");
                File.Copy(_projectPath, backupFile, overwrite: false);
            }

            _api.SaveProject(_project, _projectPath);
            RememberRecentProject(_projectPath);
            CleanupBackups();
            ExportStatus.Text = $"Auto-saved ({reason})";
        }
        catch (Exception ex)
        {
            Log.Error("UI", "Auto-save failed", ex);
            ExportStatus.Text = $"Auto-save failed: {ex.Message}";
        }
        return Task.CompletedTask;
    }

    private void CleanupBackups()
    {
        if (!_settings.AutoDeleteBackupsEnabled) return;
        try
        {
            var root = Path.Combine(UiSettingsStore.AppDataDir, "backups");
            if (!Directory.Exists(root)) return;
            var cutoff = DateTime.Now.AddDays(-Math.Max(1, _settings.AutoDeleteBackupsDays));
            foreach (var file in Directory.GetFiles(root, "*.bak", SearchOption.AllDirectories))
            {
                if (File.GetLastWriteTime(file) < cutoff) File.Delete(file);
            }
        }
        catch (Exception ex)
        {
            Log.Error("UI", "Backup cleanup failed", ex);
        }
    }

    private void LoadRecentProjects()
    {
        RecentProjects.Clear();
        foreach (var path in _settings.RecentProjects.Where(File.Exists))
        {
            RecentProjects.Add(new RecentProjectItem
            {
                Path = path,
                Name = Path.GetFileNameWithoutExtension(path),
                CreatedLabel = RelativeTime(File.GetCreationTime(path)),
                UpdatedLabel = RelativeTime(File.GetLastWriteTime(path))
            });
        }
    }

    private static string RelativeTime(DateTime dt)
    {
        var elapsed = DateTime.Now - dt;
        if (elapsed.TotalSeconds < 60) return "just now";
        if (elapsed.TotalMinutes < 60) return $"{(int)elapsed.TotalMinutes} min. ago";
        if (elapsed.TotalHours < 24) return $"{(int)elapsed.TotalHours} hr. ago";
        if (elapsed.TotalDays < 7) return $"{(int)elapsed.TotalDays} days ago";
        if (elapsed.TotalDays < 30) return $"{(int)(elapsed.TotalDays / 7)} wk. ago";
        if (elapsed.TotalDays < 365) return $"{(int)(elapsed.TotalDays / 30)} mo. ago";
        return dt.ToString("yyyy-MM-dd");
    }

    private void RememberRecentProject(string path)
    {
        _settings.RecentProjects.RemoveAll(x => string.Equals(x, path, StringComparison.OrdinalIgnoreCase));
        _settings.RecentProjects.Insert(0, path);
        if (_settings.RecentProjects.Count > 20) _settings.RecentProjects = _settings.RecentProjects.Take(20).ToList();
        UiSettingsStore.Save(_settings);
        LoadRecentProjects();
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

/// <summary>Wraps a <see cref="TemplateInstance"/> with resolved display data for the instances list.</summary>
public sealed class InstanceListItem
{
    public required TemplateInstance Instance { get; init; }
    public required string TemplateName { get; init; }
    public required string TimeLabel { get; init; }
}

/// <summary>Display model for a recent project entry on the dashboard.</summary>
public sealed class RecentProjectItem
{
    public required string Path { get; init; }
    public required string Name { get; init; }
    public required string CreatedLabel { get; init; }
    public required string UpdatedLabel { get; init; }
}

/// <summary>Display row for the elements ListBox in the visual template editor.</summary>
public sealed class ElementListItem
{
    public required Element Element { get; init; }
    public required string Icon { get; init; }
    public required string Summary { get; init; }
}
