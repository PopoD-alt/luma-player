using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Win32;
using Luma.Core.Library;
using Luma.Core.Sharing;
using Luma.Core.Updates;
using Luma.Core.Analysis;

namespace Luma.WindowsApp;

internal sealed class PlayerWindow : Window
{
    const double ControlsHeight = 238;
    readonly LibraryStore library;
    readonly Grid layout = new(), viewport = new();
    readonly StackPanel empty = new() { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
    readonly TextBlock nowTitle = new() { Text = "Make room for a good story", FontSize = 20, FontWeight = FontWeights.SemiBold, TextAlignment = TextAlignment.Center };
    readonly TextBlock status = new() { FontSize = 11, Text = "Windows review · ready", Margin = new Thickness(20, 0, 20, 0), VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
    readonly TextBlock timing = new() { Text = "0:00 / 0:00", FontSize = 12, VerticalAlignment = VerticalAlignment.Center };
    readonly TextBlock deviceProgress = new() { FontSize = 11, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center };
    readonly Button resumePhone = new() { Content = "Resume from phone", ToolTip = "Use the phone's saved position on this computer. Playback is never moved automatically.", VerticalAlignment = VerticalAlignment.Center };
    readonly CheckBox autoSkip = new() { Content = "Auto-skip intros / credits", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(3, 0, 12, 0), ToolTip = "Opt in to skipping labelled chapters and saved ranges. Unmarked scenes are left playing." };
    readonly Button skipSegment = new() { Visibility = Visibility.Collapsed, Content = "Skip intro" };
    readonly TextBlock skipInfo = new() { FontSize = 10, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
    readonly PlaybackSkipSession skipSession = new();
    IReadOnlyList<PlaybackSkipRange> chapterSkips = [], activeSkipRanges = [];
    PlaybackSkipRange? activeSkip;
    (Guid Item, string Kind, double Start)? skipDraft;
    DateTime lastChapterScan = DateTime.MinValue;
    CancellationTokenSource? skipAnalysis;
    string? skipAnalysisStatus;
    readonly MenuItem analyzeSkips = new() { Header = "Analyze openings / credits with next queue item…" };
    readonly MenuItem cancelSkipAnalysis = new() { Header = "Cancel intro / credit analysis", IsEnabled = false };
    readonly MenuItem reviewSkipMatches = new() { Header = "Review detected intro / credit matches…", IsEnabled = false };
    IReadOnlyList<PlaybackSkipRange> detectedSkipRanges = [];
    Guid? detectedSkipItem; string? detectedSkipPath, detectedReferenceTitle;
    Func<bool>? detectedFilesUnchanged;
    int detectedSkipGeneration;
    Window? skipReviewWindow;
    double? skipPreviewStopAt;
    readonly TextBlock queueCount = new() { FontSize = 11, VerticalAlignment = VerticalAlignment.Center };
    readonly TextBlock backend = new() { FontSize = 11, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = 360 };
    readonly ListBox queue = new() { BorderThickness = new Thickness(0), Margin = new Thickness(6), Background = Brushes.Transparent, DisplayMemberPath = "Title" };
    readonly TextBox search = new() { Margin = new Thickness(12, 0, 12, 12), ToolTip = "Search your queue" };
    readonly TextBlock searchHint = new() { Text = "Search queue…", IsHitTestVisible = false, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(23, 0, 0, 12) };
    readonly Slider seek = new() { Minimum = 0, Maximum = 1, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 8, 0, 14) };
    readonly Slider volume = new() { Minimum = 0, Maximum = 1, Value = .7, Width = 100, VerticalAlignment = VerticalAlignment.Center };
    readonly ComboBox speed = new() { Width = 76, VerticalAlignment = VerticalAlignment.Center };
    readonly Button play = new() { Content = "▶", Width = 52, Height = 42, ToolTip = "Play / pause (Space)" };
    readonly Button repeat = new() { Content = "Repeat: off", Width = 95 };
    readonly Button shareButton = new() { Content = "Share playlist", ToolTip = "Local sharing is off until you start it" };
    readonly Border stage = new() { Margin = new Thickness(14, 14, 14, 0), CornerRadius = new CornerRadius(14), Background = Brushes.Black, ClipToBounds = true };
    readonly MenuItem automatic = new() { Header = "Automatically check for updates", IsCheckable = true };
    readonly MenuItem audioTracksMenu = new() { Header = "Audio tracks", IsEnabled = false };
    readonly MenuItem embeddedSubtitlesMenu = new() { Header = "Caption tracks", IsEnabled = false };
    readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromMilliseconds(100) };
    readonly DispatcherTimer updateTimer = new() { Interval = TimeSpan.FromMinutes(1) };
    readonly CancellationTokenSource lifetimeCancellation = new();
    readonly SharingPowerRequest sharingPower = new();
    readonly Stopwatch openingClock = new();
    static readonly HashSet<string> CaptionExtensions = new(StringComparer.OrdinalIgnoreCase) { ".srt", ".vtt", ".ass", ".ssa", ".sub", ".idx", ".sup" };
    FrameworkElement header = null!, sidebar = null!, controls = null!;
    VlcPlayer? vlc;
    MediaEntry? current;
    PlaylistSharingServer? sharing;
    WindowsDnsSdAdvertisement? discovery;
    Window? sharingWindow;
    TextBlock? sharingState, pairingText;
    TextBox? sharingAddresses;
    Button? startSharing, stopSharing;
    WindowsUpdateConfig? updateConfig;
    WindowsRelease? pendingUpdate;
    string? lastError;
    int currentBuild = 1, generation, repeatMode;
    double duration, rate = 1, pendingResume, subtitleDelay;
    bool playing, opening, dragging, refreshingQueue, updatingSlider, fullscreen, dark = true, checkingUpdate, closing, dummyAudioForSmoke, smokeDiagnostics, computerEnded;
    Rect normalBounds; WindowState previousState; bool previousTopmost;
    DateTime lastSave = DateTime.UtcNow;
    readonly Dictionary<string, long> receivedProgressRevisions = new(StringComparer.Ordinal);

    public PlayerWindow(string? libraryFile = null)
    {
        library = new LibraryStore(libraryFile ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "sdas", "Luma Player", "library.json"));
        Title = "Luma Player"; Width = Math.Min(1220, SystemParameters.WorkArea.Width - 30); Height = Math.Min(800, SystemParameters.WorkArea.Height - 30); MinWidth = 940; MinHeight = 610; WindowStartupLocation = WindowStartupLocation.CenterScreen;
        FontFamily = new FontFamily("Segoe UI Variable, Segoe UI"); FontSize = 13; AllowDrop = true;
        SetResourceReference(BackgroundProperty, "WindowBrush"); SetResourceReference(ForegroundProperty, "TextBrush");
        SetupStyles(); SetTheme(true); Content = BuildLayout(); RefreshQueue();
        timer.Tick += (_, _) => Tick(); timer.Start();
        updateTimer.Tick += async (_, _) => await PollUpdatesAsync(false); updateTimer.Start(); Activated += async (_, _) => await PollUpdatesAsync(false);
        SourceInitialized += (_, _) => NativeWindow.Style(this, dark); Closing += (_, _) => Shutdown(); PreviewKeyDown += OnKey;
        Drop += async (_, e) => { if (e.Data.GetData(DataFormats.FileDrop) is string[] paths) await ImportAsync(paths); };
        DragOver += (_, e) => { e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None; e.Handled = true; };
        viewport.MouseLeftButtonDown += (_, e) => { if (e.ClickCount == 2) { ToggleFullscreen(); e.Handled = true; } };
        seek.PreviewMouseLeftButtonDown += (_, _) => dragging = true; seek.AddHandler(Thumb.DragCompletedEvent, new DragCompletedEventHandler((_, _) => { dragging = false; Seek(seek.Value); }));
        seek.PreviewMouseLeftButtonUp += (_, _) => { dragging = false; Seek(seek.Value); }; seek.ValueChanged += (_, _) => { if (!updatingSlider && !dragging && seek.IsKeyboardFocusWithin) Seek(seek.Value); };
        queue.SelectionChanged += async (_, _) => { if (!refreshingQueue && queue.SelectedItem is MediaEntry entry && entry.Id != current?.Id) await LoadEntryAsync(entry); };
        queue.MouseDoubleClick += async (_, _) => { if (queue.SelectedItem is MediaEntry entry) await LoadEntryAsync(entry); };
        search.TextChanged += (_, _) => { searchHint.Visibility = search.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed; RefreshQueue(); };
        play.Click += (_, _) => TogglePlayback(); shareButton.Click += (_, _) => ShowSharing();
        resumePhone.Click += (_, _) => ResumeFromPhone();
        autoSkip.IsChecked = library.Data.AutoSkipEnabled;
        autoSkip.Click += (_, _) => { bool enabled = autoSkip.IsChecked == true; if (enabled && !library.Data.AutoSkipEnabled) skipSession.Reset(); library.Data.AutoSkipEnabled = enabled; SaveLibrary(); UpdateSkipping(); };
        skipSegment.Click += (_, _) => SkipActiveSegment();
        analyzeSkips.Click += async (_, _) => await AnalyzeSkipsAsync();
        cancelSkipAnalysis.Click += (_, _) => skipAnalysis?.Cancel();
        reviewSkipMatches.Click += (_, _) => ReviewDetectedMatches();
        volume.ValueChanged += (_, _) => { if (vlc is not null) vlc.Volume = volume.Value; };
        speed.SelectionChanged += (_, _) => { if (speed.SelectedItem is ComboBoxItem item) { rate = (double)item.Tag; if (vlc is not null) vlc.Rate = rate; } };
        repeat.Click += (_, _) => { repeatMode = (repeatMode + 1) % 3; repeat.Content = new[] { "Repeat: off", "Repeat: all", "Repeat: one" }[repeatMode]; };
        try { var config = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "appsettings.windows.json")); updateConfig = WindowsUpdateConfig.FromJson(config); using var json = JsonDocument.Parse(config); currentBuild = json.RootElement.GetProperty("build").GetInt32(); }
        catch (Exception error) when (error is IOException or JsonException or InvalidOperationException) { updateConfig = null; }
        automatic.IsChecked = library.Data.AutomaticallyCheckUpdates; automatic.IsEnabled = updateConfig is not null;
        automatic.Click += (_, _) => { library.Data.AutomaticallyCheckUpdates = automatic.IsChecked; if (!automatic.IsChecked) pendingUpdate = null; SaveLibrary(); };
        if (library.RecoveryNotice is not null) Notice(library.RecoveryNotice);
    }
    void SetupStyles()
    {
        var buttons = new Style(typeof(Button));
        buttons.Setters.Add(new Setter(BackgroundProperty, new DynamicResourceExtension("ButtonBrush")));
        buttons.Setters.Add(new Setter(ForegroundProperty, new DynamicResourceExtension("TextBrush")));
        buttons.Setters.Add(new Setter(BorderBrushProperty, new DynamicResourceExtension("LineBrush")));
        buttons.Setters.Add(new Setter(BorderThicknessProperty, new Thickness(1)));
        buttons.Setters.Add(new Setter(PaddingProperty, new Thickness(12, 7, 12, 7)));
        buttons.Setters.Add(new Setter(MarginProperty, new Thickness(3, 0, 3, 0)));
        buttons.Setters.Add(new Setter(CursorProperty, Cursors.Hand));
        var frame = new FrameworkElementFactory(typeof(Border));
        frame.SetValue(Border.CornerRadiusProperty, new CornerRadius(8));
        frame.SetValue(Border.BackgroundProperty, new TemplateBindingExtension(Control.BackgroundProperty));
        frame.SetValue(Border.BorderBrushProperty, new TemplateBindingExtension(Control.BorderBrushProperty));
        frame.SetValue(Border.BorderThicknessProperty, new TemplateBindingExtension(Control.BorderThicknessProperty));
        var presenter = new FrameworkElementFactory(typeof(ContentPresenter));
        presenter.SetValue(ContentPresenter.ContentProperty, new TemplateBindingExtension(ContentControl.ContentProperty));
        presenter.SetValue(ContentPresenter.HorizontalAlignmentProperty, HorizontalAlignment.Center);
        presenter.SetValue(ContentPresenter.VerticalAlignmentProperty, VerticalAlignment.Center);
        presenter.SetValue(ContentPresenter.MarginProperty, new TemplateBindingExtension(Control.PaddingProperty));
        frame.AppendChild(presenter); buttons.Setters.Add(new Setter(TemplateProperty, new ControlTemplate(typeof(Button)) { VisualTree = frame }));
        var hover = new Trigger { Property = IsMouseOverProperty, Value = true }; hover.Setters.Add(new Setter(BackgroundProperty, new DynamicResourceExtension("SelectionBrush"))); buttons.Triggers.Add(hover);
        var disabled = new Trigger { Property = IsEnabledProperty, Value = false }; disabled.Setters.Add(new Setter(OpacityProperty, .4)); buttons.Triggers.Add(disabled);
        Resources[typeof(Button)] = buttons;
        var input = new Style(typeof(TextBox)); input.Setters.Add(new Setter(BackgroundProperty, new DynamicResourceExtension("ButtonBrush"))); input.Setters.Add(new Setter(ForegroundProperty, new DynamicResourceExtension("TextBrush"))); input.Setters.Add(new Setter(BorderBrushProperty, new DynamicResourceExtension("LineBrush"))); input.Setters.Add(new Setter(PaddingProperty, new Thickness(10, 8, 10, 8))); Resources[typeof(TextBox)] = input;
        var item = new Style(typeof(ListBoxItem)); item.Setters.Add(new Setter(ForegroundProperty, new DynamicResourceExtension("TextBrush"))); item.Setters.Add(new Setter(PaddingProperty, new Thickness(12, 13, 12, 13))); item.Setters.Add(new Setter(MarginProperty, new Thickness(4, 2, 4, 2))); item.Setters.Add(new Setter(HorizontalContentAlignmentProperty, HorizontalAlignment.Stretch));
        item.Setters.Add(new Setter(BackgroundProperty, Brushes.Transparent));
        var rowFrame = new FrameworkElementFactory(typeof(Border)); rowFrame.SetValue(Border.CornerRadiusProperty, new CornerRadius(8)); rowFrame.SetValue(Border.BackgroundProperty, new TemplateBindingExtension(Control.BackgroundProperty)); rowFrame.SetValue(Border.PaddingProperty, new TemplateBindingExtension(Control.PaddingProperty));
        var rowContent = new FrameworkElementFactory(typeof(StackPanel));
        var rowText = new FrameworkElementFactory(typeof(TextBlock)); rowText.SetBinding(TextBlock.TextProperty, new System.Windows.Data.Binding("Title")); rowText.SetValue(TextBlock.TextTrimmingProperty, TextTrimming.CharacterEllipsis); rowContent.AppendChild(rowText);
        foreach (string property in new[] { nameof(MediaEntry.ComputerProgressDetail), nameof(MediaEntry.PhoneProgressDetail) })
        {
            var progress = new FrameworkElementFactory(typeof(TextBlock)); progress.SetBinding(TextBlock.TextProperty, new System.Windows.Data.Binding(property));
            progress.SetValue(TextBlock.FontSizeProperty, 10d); progress.SetValue(TextBlock.MarginProperty, new Thickness(0, 5, 0, 0));
            progress.SetValue(TextBlock.TextTrimmingProperty, TextTrimming.CharacterEllipsis); progress.SetResourceReference(TextBlock.ForegroundProperty, property == nameof(MediaEntry.PhoneProgressDetail) ? "AccentBrush" : "MutedBrush"); rowContent.AppendChild(progress);
            var bar = new FrameworkElementFactory(typeof(ProgressBar)); bar.SetValue(RangeBase.MaximumProperty, 1d); bar.SetValue(FrameworkElement.HeightProperty, 2d);
            bar.SetValue(FrameworkElement.MarginProperty, new Thickness(0, 3, 0, 0)); bar.SetValue(Control.BorderThicknessProperty, new Thickness(0));
            bar.SetResourceReference(Control.BackgroundProperty, "LineBrush"); bar.SetResourceReference(Control.ForegroundProperty, property == nameof(MediaEntry.PhoneProgressDetail) ? "AccentBrush" : "MutedBrush");
            bar.SetBinding(RangeBase.ValueProperty, new System.Windows.Data.Binding(property == nameof(MediaEntry.PhoneProgressDetail) ? nameof(MediaEntry.PhoneProgressFraction) : nameof(MediaEntry.ComputerProgressFraction)) { Mode = System.Windows.Data.BindingMode.OneWay }); rowContent.AppendChild(bar);
        }
        rowFrame.AppendChild(rowContent);
        item.Setters.Add(new Setter(TemplateProperty, new ControlTemplate(typeof(ListBoxItem)) { VisualTree = rowFrame }));
        var selected = new Trigger { Property = ListBoxItem.IsSelectedProperty, Value = true }; selected.Setters.Add(new Setter(BackgroundProperty, new DynamicResourceExtension("SelectionBrush"))); selected.Setters.Add(new Setter(ForegroundProperty, new DynamicResourceExtension("TextBrush"))); item.Triggers.Add(selected); Resources[typeof(ListBoxItem)] = item;
    }
    void SetTheme(bool value)
    {
        dark = value;
        var colors = dark ? new[] { "#10131B", "#F3F4F8", "#989EAF", "#191E2A", "#222937", "#343C4D", "#3A2C25" } : new[] { "#EEF1F6", "#182133", "#667084", "#F8FAFD", "#E3E8F0", "#CBD3DF", "#F4DDCA" };
        var names = new[] { "WindowBrush", "TextBrush", "MutedBrush", "PanelBrush", "ButtonBrush", "LineBrush", "SelectionBrush" };
        for (var i = 0; i < names.Length; i++) Resources[names[i]] = new SolidColorBrush((Color)ColorConverter.ConvertFromString(colors[i]));
        Resources["AccentBrush"] = new SolidColorBrush(Color.FromRgb(244, 153, 100)); NativeWindow.Style(this, dark);
    }
    static TextBlock Text(string value, double size = 13, bool bold = false) => new() { Text = value, FontSize = size, FontWeight = bold ? FontWeights.SemiBold : FontWeights.Normal, VerticalAlignment = VerticalAlignment.Center };
    static StackPanel Row() => new() { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
    Button Button(string label, Action action, string? tip = null) { var button = new Button { Content = label, ToolTip = tip }; button.Click += (_, _) => action(); return button; }
    Border Panel(UIElement child) { var border = new Border { Child = child, CornerRadius = new CornerRadius(14), BorderThickness = new Thickness(1) }; border.SetResourceReference(Border.BackgroundProperty, "PanelBrush"); border.SetResourceReference(Border.BorderBrushProperty, "LineBrush"); return border; }
    FrameworkElement BuildLayout()
    {
        layout.SetResourceReference(Grid.BackgroundProperty, "WindowBrush");
        layout.ColumnDefinitions.Add(new() { Width = new GridLength(260) }); layout.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
        foreach (var height in new[] { new GridLength(68), new GridLength(1, GridUnitType.Star), new GridLength(ControlsHeight), new GridLength(28) }) layout.RowDefinitions.Add(new() { Height = height });
        var top = new DockPanel { Margin = new Thickness(22, 0, 18, 0) };
        var brand = Row(); var logo = Text("▶", 24, true); logo.SetResourceReference(TextBlock.ForegroundProperty, "AccentBrush"); brand.Children.Add(logo); brand.Children.Add(new TextBlock { Text = "LUMA", FontSize = 18, FontWeight = FontWeights.Bold, Margin = new Thickness(12, 0, 12, 0), VerticalAlignment = VerticalAlignment.Center }); var preview = Text("WINDOWS PREVIEW", 10); preview.SetResourceReference(TextBlock.ForegroundProperty, "MutedBrush"); brand.Children.Add(preview);
        DockPanel.SetDock(brand, Dock.Left); top.Children.Add(brand);
        var actions = Row(); actions.HorizontalAlignment = HorizontalAlignment.Right;
        actions.Children.Add(Button("Open media", () => _ = OpenMediaAsync(), "Open local media (Ctrl+O)"));
        actions.Children.Add(shareButton); actions.Children.Add(Button("Appearance", () => SetTheme(!dark)));
        var more = Button("More  ···", () => { }); var menu = BuildMenu(); more.Click += (_, _) => { menu.PlacementTarget = more; menu.IsOpen = true; }; actions.Children.Add(more); DockPanel.SetDock(actions, Dock.Right); top.Children.Add(actions);
        var headerBorder = new Border { Child = top }; headerBorder.SetResourceReference(Border.BackgroundProperty, "PanelBrush"); header = headerBorder; Grid.SetColumnSpan(header, 2); layout.Children.Add(header);

        var side = new DockPanel(); var sideTop = new StackPanel { Margin = new Thickness(0, 22, 0, 0) };
        var queueTitle = new DockPanel { Margin = new Thickness(18, 0, 18, 14) }; queueCount.HorizontalAlignment = HorizontalAlignment.Right; DockPanel.SetDock(queueCount, Dock.Right); queueTitle.Children.Add(queueCount); queueTitle.Children.Add(Text("Up next", 16, true)); sideTop.Children.Add(queueTitle);
        var searchBox = new Grid(); searchBox.Children.Add(search); searchHint.SetResourceReference(TextBlock.ForegroundProperty, "MutedBrush"); searchBox.Children.Add(searchHint); sideTop.Children.Add(searchBox); DockPanel.SetDock(sideTop, Dock.Top); side.Children.Add(sideTop);
        var sideBottom = new WrapPanel { Margin = new Thickness(10, 10, 10, 10) }; sideBottom.Children.Add(Button("↑", () => MoveSelected(-1))); sideBottom.Children.Add(Button("↓", () => MoveSelected(1))); sideBottom.Children.Add(Button("Remove", RemoveSelected)); sideBottom.Children.Add(Button("Clear", ClearQueue)); DockPanel.SetDock(sideBottom, Dock.Bottom); side.Children.Add(sideBottom); side.Children.Add(queue);
        sidebar = Panel(side); sidebar.Margin = new Thickness(12, 14, 0, 0); Grid.SetRow(sidebar, 1); Grid.SetRowSpan(sidebar, 2); layout.Children.Add(sidebar);

        var symbol = Text("▶", 58); symbol.HorizontalAlignment = HorizontalAlignment.Center; symbol.SetResourceReference(TextBlock.ForegroundProperty, "AccentBrush"); empty.Children.Add(symbol); empty.Children.Add(nowTitle); var welcome = Text("Drop a local video or audio file here", 13); welcome.Foreground = new SolidColorBrush(Color.FromRgb(160, 169, 186)); welcome.HorizontalAlignment = HorizontalAlignment.Center; welcome.Margin = new Thickness(0, 12, 0, 0); empty.Children.Add(welcome);
        viewport.Children.Add(empty); stage.Child = viewport; Grid.SetRow(stage, 1); Grid.SetColumn(stage, 1); layout.Children.Add(stage);

        var bottom = new Grid { Margin = new Thickness(18, 8, 18, 10) }; bottom.RowDefinitions.Add(new() { Height = GridLength.Auto }); bottom.RowDefinitions.Add(new() { Height = GridLength.Auto }); bottom.RowDefinitions.Add(new() { Height = GridLength.Auto }); bottom.RowDefinitions.Add(new());
        var progressRow = new DockPanel { Margin = new Thickness(0, 0, 0, 3) }; DockPanel.SetDock(resumePhone, Dock.Right); progressRow.Children.Add(resumePhone); deviceProgress.SetResourceReference(TextBlock.ForegroundProperty, "MutedBrush"); progressRow.Children.Add(deviceProgress); bottom.Children.Add(progressRow);
        var skipRow = new DockPanel { Margin = new Thickness(0, 3, 0, 0) };
        autoSkip.SetResourceReference(Control.ForegroundProperty, "TextBrush"); DockPanel.SetDock(autoSkip, Dock.Left); skipRow.Children.Add(autoSkip);
        var setupSkip = Button("Skip setup ▾", ShowSkipSetup, "Mark exact intro/credit ranges when a file has no labelled chapters"); DockPanel.SetDock(setupSkip, Dock.Right); skipRow.Children.Add(setupSkip);
        DockPanel.SetDock(skipSegment, Dock.Right); skipRow.Children.Add(skipSegment); skipInfo.SetResourceReference(TextBlock.ForegroundProperty, "MutedBrush"); skipRow.Children.Add(skipInfo);
        Grid.SetRow(skipRow, 1); bottom.Children.Add(skipRow);
        Grid.SetRow(seek, 2); bottom.Children.Add(seek);
        var transport = new WrapPanel(); Grid.SetRow(transport, 3); var left = Row(); left.Margin = new Thickness(0, 0, 0, 4); left.Children.Add(Button("│◀", () => _ = NextAsync(-1), "Previous file")); left.Children.Add(Button("−10", () => Seek(CurrentTime - 10))); left.Children.Add(play); play.SetResourceReference(BackgroundProperty, "AccentBrush"); play.Foreground = Brushes.Black; left.Children.Add(Button("+10", () => Seek(CurrentTime + 10))); left.Children.Add(Button("▶│", () => _ = NextAsync(1), "Next file")); timing.Margin = new Thickness(12, 0, 10, 0); left.Children.Add(timing); transport.Children.Add(left);
        var right = Row(); right.HorizontalAlignment = HorizontalAlignment.Right; right.Children.Add(Button("Bookmark", Bookmark)); right.Children.Add(Button("Captions", () => OpenSubtitles()));
        speed.Foreground = Brushes.Black;
        foreach (var value in new[] { .5, 1, 1.25, 1.5, 2 }) speed.Items.Add(new ComboBoxItem { Content = value.ToString("0.##", CultureInfo.InvariantCulture) + "×", Tag = value, Foreground = Brushes.Black }); speed.SelectedIndex = 1;
        right.Children.Add(speed); var volumeLabel = Text("Volume", 11); volumeLabel.Margin = new Thickness(10, 0, 4, 0); right.Children.Add(volumeLabel); right.Children.Add(volume); right.Children.Add(Button("⛶", ToggleFullscreen, "Video fullscreen (F11)")); DockPanel.SetDock(right, Dock.Right); transport.Children.Add(right); bottom.Children.Add(transport);
        var meta = new DockPanel { VerticalAlignment = VerticalAlignment.Bottom }; backend.SetResourceReference(TextBlock.ForegroundProperty, "MutedBrush"); meta.Children.Add(backend); repeat.HorizontalAlignment = HorizontalAlignment.Right; DockPanel.SetDock(repeat, Dock.Right); meta.Children.Add(repeat); Grid.SetRow(meta, 3); meta.Margin = new Thickness(8, 0, 0, -4); bottom.Children.Add(meta); transport.VerticalAlignment = VerticalAlignment.Top;
        controls = bottom; Grid.SetRow(bottom, 2); Grid.SetColumn(bottom, 1); layout.Children.Add(bottom);
        status.SetResourceReference(TextBlock.ForegroundProperty, "MutedBrush"); Grid.SetRow(status, 3); Grid.SetColumnSpan(status, 2); layout.Children.Add(status);
        return layout;
    }
    ContextMenu BuildMenu()
    {
        var menu = new ContextMenu();
        void Item(string label, Action action) { var item = new MenuItem { Header = label }; item.Click += (_, _) => action(); menu.Items.Add(item); }
        Item("Format support", () => ShowDocument("FORMAT_SUPPORT.md", "Format support")); Item("Playback diagnostics…", ShowDiagnostics);
        Item("Share playlist locally…", ShowSharing); Item("Check for updates…", () => _ = PollUpdatesAsync(true)); menu.Items.Add(automatic); menu.Items.Add(new Separator());
        menu.Items.Add(audioTracksMenu); menu.Items.Add(embeddedSubtitlesMenu);
        Item("Play original video demo", () => _ = ImportAsync([Path.Combine(AppContext.BaseDirectory, "Demos", "Luma-MSVideo1-16.avi")]));
        Item("Saved bookmarks", ShowBookmarks); Item("Caption timing −0.25s", () => ChangeCaptionDelay(-.25)); Item("Caption timing +0.25s", () => ChangeCaptionDelay(.25));
        Item("Turn captions off", () => TryControl(() => vlc?.SelectSubtitle(-1)));
        menu.Items.Add(new Separator()); menu.Items.Add(analyzeSkips); menu.Items.Add(cancelSkipAnalysis); menu.Items.Add(reviewSkipMatches);
        menu.Items.Add(new Separator()); Item("About Luma Player", () => MessageBox.Show(this, "Luma Player\n" + (typeof(PlayerWindow).Assembly.GetName().Version?.ToString(3) ?? "Unknown") + " · Windows review build " + currentBuild + "\n\nMade by sdas\nPlayback powered by libVLC3.\nLocal sharing stays off until you start it.\nThis review build is not digitally signed by a trusted Windows publisher.", "About Luma Player"));
        return menu;
    }
    async Task OpenMediaAsync()
    {
        var picker = new OpenFileDialog { Title = "Open media", Multiselect = true, Filter = "Media files|*.mp4;*.mkv;*.mka;*.mov;*.avi;*.wmv;*.webm;*.m4v;*.mp3;*.wav;*.flac;*.m4a;*.aac;*.aif;*.aiff;*.au;*.snd;*.wma;*.ogg;*.opus;*.ts;*.mts;*.m2ts;*.vob;*.mpeg;*.mpg|All files|*.*" };
        if (picker.ShowDialog(this) == true) await ImportAsync(picker.FileNames);
    }
    public async Task ImportAsync(IEnumerable<string> paths)
    {
        int expectedGeneration = generation; Guid? expectedItem = current?.Id;
        try
        {
            var list = paths.Take(2000).SelectMany(p => Directory.Exists(p) ? Directory.EnumerateFiles(p).Take(2000) : new[] { p }).Take(2000).ToList();
            var added = library.Add(list.Where(p => !CaptionExtensions.Contains(Path.GetExtension(p)))); RefreshQueue();
            if (current is null || lastError is not null)
            {
                var requested = added.FirstOrDefault() ?? library.Data.Items.FirstOrDefault(item => list.Any(p => string.Equals(Path.GetFullPath(p), item.FilePath, StringComparison.OrdinalIgnoreCase)));
                if (requested is not null) { var loading = LoadEntryAsync(requested); expectedGeneration = generation; expectedItem = requested.Id; await loading; }
            }
            if (closing || generation != expectedGeneration || current?.Id != expectedItem) return;
            string? subtitle = list.FirstOrDefault(p => CaptionExtensions.Contains(Path.GetExtension(p))); if (subtitle is not null) ReadSubtitles(subtitle);
        }
        catch (Exception error) { Notice(error.Message); }
    }
    Task LoadEntryAsync(MediaEntry entry, double? resume = null)
    {
        if (closing) return Task.CompletedTask;
        skipAnalysis?.Cancel(); ClearDetectedMatches(); SaveProgress(); ++generation; DisposePlayer(); lastError = null; duration = 0; subtitleDelay = 0; playing = false; opening = true;
        current = entry; computerEnded = false; skipSession.Reset(); chapterSkips = activeSkipRanges = []; activeSkip = null; skipDraft = null; lastChapterScan = DateTime.MinValue; library.Data.SelectedId = entry.Id; pendingResume = resume ?? (entry.ResumeTime > 3 ? entry.ResumeTime : 0); RefreshQueue();
        nowTitle.Text = entry.Title; empty.Visibility = Visibility.Visible; backend.Text = "Opening with libVLC…"; Notice("Opening " + entry.Title);
        int version = generation;
        try
        {
            if (!File.Exists(entry.FilePath)) throw new FileNotFoundException("This file has moved or is unavailable.");
            var player = new VlcPlayer(dummyAudioForSmoke) { IsEnabled = false }; vlc = player;
            bool Active() => !closing && version == generation && ReferenceEquals(vlc, player);
            player.Opened += (_, _) =>
            {
                if (!Active()) return; opening = false; openingClock.Reset(); duration = player.Duration;
                if (pendingResume > 0) { player.Seek(Math.Min(pendingResume, Math.Max(0, duration - .2))); pendingResume = 0; }
                player.Rate = rate; player.Volume = volume.Value; playing = !player.IsPaused; backend.Text = "libVLC " + VlcPlayer.RuntimeVersion.Split(' ')[0];
                RefreshTracks(); UpdateSkipping(); Notice("Playing " + entry.Title); UpdateTransport();
            };
            player.TracksChanged += (_, _) => { if (Active()) RefreshTracks(); };
            player.Ended += (_, _) => { if (Active()) Ended(); };
            player.Warning += (_, e) => { if (Active()) Notice(e.Message); };
            player.Failed += (_, e) => { if (Active()) Fail(e.Message); };
            player.DoubleClicked += (_, _) => { if (Active()) ToggleFullscreen(); };
            viewport.Children.Insert(0, player.View); player.Volume = volume.Value; player.Rate = rate; player.Load(entry.FilePath); player.Play();
            playing = true; openingClock.Restart();
        }
        catch (Exception error) { Fail(error.Message); }
        UpdateTransport(); return Task.CompletedTask;
    }
    void DisposePlayer()
    {
        var previous = vlc; vlc = null; if (previous is not null) { previous.Dispose(); viewport.Children.Remove(previous.View); }
        audioTracksMenu.Items.Clear(); embeddedSubtitlesMenu.Items.Clear(); audioTracksMenu.IsEnabled = embeddedSubtitlesMenu.IsEnabled = false;
    }
    void RefreshTracks()
    {
        var player = vlc; if (player is null) return; int version = generation;
        void Fill(MenuItem menu, IReadOnlyList<VlcTrack> tracks, bool subtitles)
        {
            menu.Items.Clear(); menu.IsEnabled = tracks.Any(t => t.Id >= 0);
            var available = tracks.Any(t => t.Id == -1) ? tracks : new[] { new VlcTrack(-1, "Off") }.Concat(tracks).ToArray();
            foreach (var track in available)
            {
                var item = new MenuItem { Header = track.Id == -1 ? "Off" : track.Name[..Math.Min(track.Name.Length, 160)], IsCheckable = true, IsChecked = track.Id == (subtitles ? player.SubtitleTrack : player.AudioTrack) };
                item.Click += (_, _) => { if (version != generation || !ReferenceEquals(vlc, player)) return; TryControl(() => { if (subtitles) player.SelectSubtitle(track.Id); else player.SelectAudio(track.Id); RefreshTracks(); }); };
                menu.Items.Add(item);
            }
        }
        Fill(audioTracksMenu, player.AudioTracks, false); Fill(embeddedSubtitlesMenu, player.SubtitleTracks, true);
    }
    double CurrentTime => vlc?.CurrentTime ?? 0;
    void TogglePlayback()
    {
        skipPreviewStopAt = null;
        if (lastError is not null && current is not null) { _ = LoadEntryAsync(current); return; }
        if (current is null) { var entry = queue.SelectedItem as MediaEntry ?? library.Data.Items.Find(i => i.Id == library.Data.SelectedId) ?? library.Data.Items.FirstOrDefault(); if (entry is not null) _ = LoadEntryAsync(entry); else _ = OpenMediaAsync(); return; }
        if (opening) return; if (playing) Pause(); else Play();
    }
    void Pause() { skipPreviewStopAt = null; vlc?.Pause(); playing = false; UpdateTransport(); SaveProgress(); }
    void Play() { if (duration > 0 && (computerEnded || CurrentTime >= duration - .05)) { skipSession.Reset(); vlc?.Seek(0); } computerEnded = false; vlc?.Play(); playing = vlc is not null; UpdateTransport(); }
    void Seek(double value)
    {
        if (current is null || !double.IsFinite(value) || duration <= 0) return;
        skipPreviewStopAt = null; double origin = CurrentTime, target = Math.Clamp(value, 0, duration);
        // Native seeking is asynchronous: suppress the old segment as well as
        // the destination so a stale clock cannot overwrite this user request.
        skipSession.UserSeek(origin); skipSession.UserSeek(target);
        if (PlaybackSkipPolicy.Active(activeSkipRanges, origin) is { } originSegment) skipSession.Consume(originSegment);
        if (PlaybackSkipPolicy.Active(activeSkipRanges, target) is { } segment) skipSession.Consume(segment);
        computerEnded = false; vlc?.Seek(target); UpdateTransport();
    }
    void Tick()
    {
        sharingPower.Update(sharing?.IsRunning == true && sharing.ActiveMediaStreams > 0);
        if (closing) return;
        if (opening && openingClock.Elapsed > TimeSpan.FromSeconds(30)) Fail("libVLC took too long to open this file. Retry or choose another file.");
        if (vlc is not null && !opening)
        {
            double latest = vlc.Duration; if (latest > 0) duration = latest;
            bool video = vlc.NaturalVideoWidth > 0; vlc.Visibility = video ? Visibility.Visible : Visibility.Hidden; empty.Visibility = video ? Visibility.Collapsed : Visibility.Visible;
        }
        UpdateSkipping(); UpdateTransport(); RefreshSharingUI();
        if ((DateTime.UtcNow - lastSave).TotalSeconds >= 3) { SaveProgress(); lastSave = DateTime.UtcNow; }
    }
    void UpdateSkipping()
    {
        if (current is null || vlc is null || opening || duration <= 0)
        { activeSkip = null; skipSegment.Visibility = Visibility.Collapsed; skipInfo.Text = current is null ? "Open a video to configure skipping" : "Reading chapter metadata…"; return; }
        if (skipPreviewStopAt is { } previewEnd && playing && CurrentTime >= previewEnd)
        { Pause(); Notice("Preview finished; save the suggested range only if it matches the opening / credits"); }
        if (detectedSkipRanges.Count > 0 && !DetectedIdentityIsCurrent()) ClearDetectedMatches();
        if ((DateTime.UtcNow - lastChapterScan).TotalSeconds >= 1)
        { lastChapterScan = DateTime.UtcNow; chapterSkips = PlaybackSkipPolicy.FromChapters(vlc.Chapters, duration); }
        activeSkipRanges = PlaybackSkipPolicy.Merge(current.SkipRanges, chapterSkips, duration);
        activeSkip = PlaybackSkipPolicy.Active(activeSkipRanges, CurrentTime);
        skipSegment.Visibility = activeSkip is null ? Visibility.Collapsed : Visibility.Visible;
        skipSegment.IsEnabled = vlc.IsSeekable;
        skipSegment.Content = activeSkip?.Kind == "credits" ? "Skip credits" : "Skip intro";
        skipSegment.ToolTip = activeSkip is null ? null : (activeSkip.Source == "chapter" ? "Labelled chapter: " + activeSkip.Label : "Saved range") + " · " + MediaEntry.Clock(activeSkip.StartSeconds) + "–" + MediaEntry.Clock(activeSkip.EndSeconds);
        skipInfo.Text = skipAnalysisStatus ?? (skipDraft is { } draft ? "Mark the " + (draft.Kind == "intro" ? "intro" : "credits") + " end in Skip setup"
            : activeSkipRanges.Count == 0 ? "No labelled chapters or saved ranges" : activeSkipRanges.Count + " known segment" + (activeSkipRanges.Count == 1 ? "" : "s"));
        if (!dragging && skipDraft is null && skipPreviewStopAt is null && skipSession.Automatic(activeSkipRanges, CurrentTime, library.Data.AutoSkipEnabled, playing && !vlc.IsPaused, vlc.IsSeekable) is { } automaticRange)
            SeekSkipRange(automaticRange, automatic: true);
    }
    void SeekSkipRange(PlaybackSkipRange range, bool automatic)
    {
        if (vlc is null || !vlc.IsSeekable || duration <= 0) return;
        skipSession.Consume(range); computerEnded = false;
        vlc.Seek(Math.Min(range.EndSeconds, Math.Max(0, duration - .05)));
        Notice((automatic ? "Auto-skipped " : "Skipped ") + (range.Kind == "intro" ? "intro" : "credits") + " · " + MediaEntry.Clock(range.StartSeconds) + "–" + MediaEntry.Clock(range.EndSeconds));
    }
    void SkipActiveSegment()
    {
        if (PlaybackSkipPolicy.Active(activeSkipRanges, CurrentTime) is { } range) SeekSkipRange(range, automatic: false);
    }
    void ShowSkipSetup()
    {
        var menu = new ContextMenu(); bool ready = current is not null && vlc?.IsSeekable == true && !opening && duration > 0;
        void Item(string title, Action action, bool enabled)
        { var item = new MenuItem { Header = title, IsEnabled = enabled }; item.Click += (_, _) => action(); menu.Items.Add(item); }
        foreach (string kind in new[] { "intro", "credits" })
        {
            string name = kind == "intro" ? "intro" : "credits";
            Item("Mark " + name + " start here", () => MarkSkipStart(kind), ready);
            Item("Mark " + name + " end here", () => MarkSkipEnd(kind), ready && skipDraft is { } draft && draft.Item == current?.Id && draft.Kind == kind);
        }
        menu.Items.Add(new Separator());
        Item("Clear saved intro / credit ranges", () => { if (current is not null) { library.SetSkipRanges(current.Id, []); skipDraft = null; skipSession.UserSeek(CurrentTime); PushSharedPlaylist(); UpdateSkipping(); Notice("Saved ranges cleared; labelled chapters remain available"); } }, current?.SkipRanges.Count > 0);
        Item("Cancel unfinished range", () => { skipDraft = null; UpdateSkipping(); }, skipDraft is not null);
        var note = new MenuItem { Header = "Uses labelled chapters or your saved ranges; no AI guesses", IsEnabled = false }; menu.Items.Add(note);
        menu.PlacementTarget = autoSkip; menu.IsOpen = true;
    }
    void MarkSkipStart(string kind)
    {
        if (current is null || opening || duration <= 0 || vlc?.IsSeekable != true) return;
        skipDraft = (current.Id, kind, CurrentTime); UpdateSkipping(); Notice("Now seek to the " + kind + " end, then choose Mark " + kind + " end here");
    }
    void MarkSkipEnd(string kind)
    {
        if (current is null || skipDraft is not { } draft || draft.Item != current.Id || draft.Kind != kind) return;
        var range = new PlaybackSkipRange(kind, draft.Start, CurrentTime, "manual", kind == "intro" ? "Saved intro" : "Saved credits");
        var candidate = current.SkipRanges.Where(r => r.Kind != kind).Append(range).ToArray();
        if (!PlaybackSkipPolicy.ValidatedRanges(candidate, duration).Any(r => r == range)) { Notice("Choose an end after the start, within the video, without overlapping another saved range"); return; }
        library.SetSkipRanges(current.Id, candidate); skipDraft = null; PushSharedPlaylist(); UpdateSkipping(); Notice("Saved " + kind + " range for this video and paired phones");
    }
    async Task AnalyzeSkipsAsync()
    {
        if (skipAnalysis is not null || current is not { } item || vlc is null) { Notice("Open a video before starting analysis"); return; }
        int index = library.Data.Items.FindIndex(i => i.Id == item.Id);
        var next = index >= 0 && index + 1 < library.Data.Items.Count ? library.Data.Items[index + 1] : null;
        if (next is null || !File.Exists(item.FilePath) || !File.Exists(next.FilePath)) { Notice("Put another episode from the same series immediately after this one in the queue"); return; }
        if (MessageBox.Show(this, "Compare repeated opening / ending audio in these two queue items?\n\n" + item.Title + "\n" + next.Title
            + "\n\nAnalysis stays on this computer and can take about 12 minutes for long episodes. You can cancel from More. Any suggested ranges require your confirmation; the files are unchanged.", "Analyze intros / credits", MessageBoxButton.YesNo) != MessageBoxResult.Yes) return;
        int expectedGeneration = generation; string expectedPath = item.FilePath; Guid expectedId = item.Id;
        long firstLength, nextLength; DateTime firstWrite, nextWrite;
        try
        {
            var firstInfo = new FileInfo(expectedPath); var nextInfo = new FileInfo(next.FilePath);
            firstLength = firstInfo.Length; nextLength = nextInfo.Length; firstWrite = firstInfo.LastWriteTimeUtc; nextWrite = nextInfo.LastWriteTimeUtc;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { Notice("A selected file is unavailable; choose the current files and try again"); return; }
        bool FilesUnchanged()
        {
            try { var first = new FileInfo(expectedPath); var second = new FileInfo(next.FilePath); return first.Exists && second.Exists && first.Length == firstLength && first.LastWriteTimeUtc == firstWrite && second.Length == nextLength && second.LastWriteTimeUtc == nextWrite; }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return false; }
        }
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(lifetimeCancellation.Token); skipAnalysis = cancellation;
        analyzeSkips.IsEnabled = false; cancelSkipAnalysis.IsEnabled = true;
        try
        {
            skipAnalysisStatus = "Analyzing repeated audio · 0%"; UpdateSkipping();
            var progress = new Progress<double>(fraction => { if (!closing && ReferenceEquals(skipAnalysis, cancellation)) { skipAnalysisStatus = "Analyzing repeated audio · " + Math.Clamp((int)(fraction * 100), 0, 100) + "% · More to cancel"; UpdateSkipping(); } });
            var candidates = await SkipAudioAnalyzer.AnalyzeAsync(expectedPath, next.FilePath, VlcPlayer.RuntimePath, cancellation.Token, progress);
            if (closing || cancellation.IsCancellationRequested || generation != expectedGeneration || current?.Id != expectedId || !string.Equals(current.FilePath, expectedPath, StringComparison.OrdinalIgnoreCase)
                || !library.Data.Items.Any(entry => entry.Id == expectedId && string.Equals(entry.FilePath, expectedPath, StringComparison.OrdinalIgnoreCase)) || !FilesUnchanged()) return;
            if (candidates.Count == 0) { Notice("No confident repeated opening / ending audio found; use labelled chapters or mark exact ranges in Skip setup"); return; }
            var oldReview = skipReviewWindow; skipReviewWindow = null; if (oldReview?.IsVisible == true) oldReview.Close();
            detectedSkipRanges = candidates.Take(8).Select(candidate => new PlaybackSkipRange(candidate.Kind,
                candidate.FirstStartSeconds, candidate.FirstEndSeconds, "manual", "Confirmed repeated audio"))
                .Where(range => PlaybackSkipPolicy.ValidatedRanges([range], duration).Count == 1).ToArray();
            detectedSkipItem = expectedId; detectedSkipPath = expectedPath; detectedSkipGeneration = expectedGeneration;
            detectedReferenceTitle = next.Title; detectedFilesUnchanged = FilesUnchanged; reviewSkipMatches.IsEnabled = detectedSkipRanges.Count > 0;
            if (detectedSkipRanges.Count > 0) { Notice("Detected audio matches are ready to preview in More → Review detected matches"); ReviewDetectedMatches(); }
            else Notice("Suggested ranges were outside the current video or ambiguous; no ranges were saved");
        }
        catch (OperationCanceledException) { if (!closing) Notice("Intro / credit analysis cancelled"); }
        catch (Exception error) { if (!closing) Notice("Intro / credit analysis could not finish: " + error.Message); }
        finally
        {
            if (ReferenceEquals(skipAnalysis, cancellation)) { skipAnalysis = null; skipAnalysisStatus = null; analyzeSkips.IsEnabled = true; cancelSkipAnalysis.IsEnabled = false; if (!closing) UpdateSkipping(); }
        }
    }
    bool DetectedIdentityIsCurrent()
        => !closing && current?.Id == detectedSkipItem && generation == detectedSkipGeneration && current is not null
            && string.Equals(current.FilePath, detectedSkipPath, StringComparison.OrdinalIgnoreCase);
    bool DetectedContextIsCurrent() => DetectedIdentityIsCurrent() && detectedFilesUnchanged?.Invoke() == true;
    void ClearDetectedMatches()
    {
        bool wasPreview = skipPreviewStopAt is not null; skipPreviewStopAt = null;
        if (wasPreview) { vlc?.Pause(); playing = false; UpdateTransport(); }
        detectedSkipRanges = []; detectedSkipItem = null; detectedSkipPath = detectedReferenceTitle = null; detectedFilesUnchanged = null;
        reviewSkipMatches.IsEnabled = false; var review = skipReviewWindow; skipReviewWindow = null; if (review?.IsVisible == true) review.Close();
    }
    void PreviewDetectedRange(PlaybackSkipRange range)
    {
        if (!DetectedContextIsCurrent() || !detectedSkipRanges.Contains(range)) { ClearDetectedMatches(); Notice("The selected video or detected result changed; analyze the current files again"); return; }
        Pause(); Seek(Math.Max(0, range.StartSeconds - 1));
        // Preview is an explicit user seek, including entry into a range that
        // might only become known from chapter metadata a moment later.
        skipSession.UserSeek(range.StartSeconds); skipSession.UserSeek(Math.Max(range.StartSeconds, range.EndSeconds - .1));
        skipPreviewStopAt = Math.Min(range.EndSeconds + 1, Math.Max(.1, duration - .2)); Play();
        Notice("Previewing suggested " + range.Kind + " · playback pauses after the range; nothing is saved");
    }
    bool SaveDetectedRange(PlaybackSkipRange range)
    {
        if (!DetectedContextIsCurrent() || current is null || !detectedSkipRanges.Contains(range)) { ClearDetectedMatches(); Notice("The selected video or detected result changed; nothing was saved"); return false; }
        var combined = current.SkipRanges.Where(saved => saved.Kind != range.Kind).Append(range).ToArray();
        if (!PlaybackSkipPolicy.ValidatedRanges(combined, duration).Any(saved => saved == range)) { Notice("Suggested range overlaps another saved range; nothing was changed"); return false; }
        library.SetSkipRanges(current.Id, combined); PushSharedPlaylist(); UpdateSkipping(); Notice("Saved confirmed " + range.Kind + " range for this video"); return true;
    }
    void ReviewDetectedMatches()
    {
        if (!DetectedContextIsCurrent() || detectedSkipRanges.Count == 0) { ClearDetectedMatches(); Notice("No current detected matches; run Analyze with the next queue item first"); return; }
        if (skipReviewWindow is { IsVisible: true }) { skipReviewWindow.Activate(); return; }
        var content = new StackPanel { Margin = new Thickness(18) };
        content.Children.Add(Text("Review repeated audio matches", 18, true));
        var explanation = new TextBlock { Text = "Compared " + current!.Title + " with " + detectedReferenceTitle
            + ". Repetition can also match story scenes. Preview a range before saving. This window stays open beside playback, so the main seek bar remains available."
            + "\n\nSaving replaces the saved range of the same kind for the current video. Auto-skip is a separate opt-in setting. Close this window to defer; More → Review detected matches reopens the same results.", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 12, 0, 12) }; content.Children.Add(explanation);
        foreach (var range in detectedSkipRanges)
        {
            var row = new StackPanel { Margin = new Thickness(0, 0, 0, 16) };
            row.Children.Add(Text((range.Kind == "intro" ? "Possible intro" : "Possible credits") + " · " + MediaEntry.Clock(range.StartSeconds) + "–" + MediaEntry.Clock(range.EndSeconds), 14, true));
            var actions = Row(); actions.Margin = new Thickness(0, 8, 0, 0); actions.Children.Add(Button("Preview", () => PreviewDetectedRange(range)));
            var save = Button("Save range", () => { }); save.IsEnabled = current!.SkipRanges.All(saved => saved != range); if (!save.IsEnabled) save.Content = "Saved";
            save.Click += (_, _) => { if (SaveDetectedRange(range)) { save.Content = "Saved"; save.IsEnabled = false; } }; actions.Children.Add(save); row.Children.Add(actions); content.Children.Add(row);
        }
        var note = new TextBlock { Text = "Preview plays from just before the suggested range, then pauses. You can stop or seek at any time. Closing the review window stops its preview and retains the suggestions.", TextWrapping = TextWrapping.Wrap, FontSize = 11 }; content.Children.Add(note);
        var review = new Window { Owner = this, Title = "Review intro / credit matches", Width = 540, MaxHeight = 620, SizeToContent = SizeToContent.Height, Content = new ScrollViewer { Content = content, VerticalScrollBarVisibility = ScrollBarVisibility.Auto }, WindowStartupLocation = WindowStartupLocation.CenterOwner };
        review.Closed += (_, _) => { if (ReferenceEquals(skipReviewWindow, review)) skipReviewWindow = null; if (skipPreviewStopAt is not null) Pause(); };
        skipReviewWindow = review; review.Show();
    }
    void UpdateTransport()
    {
        play.Content = playing ? "Ⅱ" : "▶"; updatingSlider = true; seek.Maximum = Math.Max(1, duration); if (!dragging) seek.Value = Math.Min(seek.Maximum, CurrentTime); updatingSlider = false;
        timing.Text = MediaEntry.Clock(CurrentTime) + " / " + MediaEntry.Clock(duration); seek.IsEnabled = current is not null && !opening && duration > 0;
        UpdateDeviceProgress();
    }
    void UpdateDeviceProgress()
    {
        var item = current ?? queue.SelectedItem as MediaEntry;
        deviceProgress.Text = item is null ? "Computer and phone positions appear here after playback." : item.ComputerProgressDetail + SavedAt(item.ComputerUpdatedAtUnixSeconds) + "\n" + item.PhoneProgressDetail + SavedAt(item.MobileUpdatedAtUnixSeconds);
        resumePhone.IsEnabled = item?.MobileProgressSeconds is >= 0 && !item.MobileCompleted && !opening;
    }
    static string SavedAt(double stamp) => double.IsFinite(stamp) && stamp is > 0 and <= 4_102_444_800 ? " · saved " + DateTimeOffset.FromUnixTimeMilliseconds((long)(stamp * 1000)).ToLocalTime().ToString("t", CultureInfo.CurrentCulture) : "";
    void ResumeFromPhone()
    {
        var item = current ?? queue.SelectedItem as MediaEntry;
        if (item?.MobileProgressSeconds is not { } position || position < 0 || item.MobileCompleted || opening) return;
        if (current?.Id == item.Id && vlc is not null) { Seek(position); Play(); SaveProgress(); }
        else _ = LoadEntryAsync(item, position);
        Notice("Resumed this computer from the phone position at " + MediaEntry.Clock(position));
    }
    void Ended() { if (!playing) return; bool preview = skipPreviewStopAt is not null; skipPreviewStopAt = null; playing = false; computerEnded = true; SaveProgress(); if (preview) { UpdateTransport(); Notice("Preview finished; detected matches remain available in More"); return; } if (repeatMode == 2) { skipSession.Reset(); computerEnded = false; vlc?.Seek(0); vlc?.Play(); playing = vlc is not null; UpdateTransport(); } else _ = NextAsync(1, true); }
    async Task NextAsync(int delta, bool atEnd = false)
    {
        if (library.Data.Items.Count == 0) return; if (delta < 0 && CurrentTime > 3) { Seek(0); return; }
        int index = library.Data.Items.FindIndex(i => i.Id == current?.Id), next = index + delta;
        if (next >= library.Data.Items.Count) { if (repeatMode == 1) next = 0; else { if (atEnd) Notice("Queue finished"); return; } }
        await LoadEntryAsync(library.Data.Items[Math.Max(0, next)]);
    }
    void RefreshQueue()
    {
        refreshingQueue = true; queue.ItemsSource = library.Data.Items.Where(i => i.Title.Contains(search.Text, StringComparison.OrdinalIgnoreCase)).ToList(); queue.SelectedItem = library.Data.Items.Find(i => i.Id == (current?.Id ?? library.Data.SelectedId)); queueCount.Text = library.Data.Items.Count.ToString(CultureInfo.InvariantCulture); refreshingQueue = false; PushSharedPlaylist();
    }
    void MoveSelected(int direction) { if (queue.SelectedItem is MediaEntry entry) { library.Move(entry.Id, direction); RefreshQueue(); } }
    void RemoveSelected() { if (queue.SelectedItem is not MediaEntry entry) return; if (entry.Id == current?.Id) Unload(); library.Remove(entry.Id); RefreshQueue(); }
    void ClearQueue() { if (MessageBox.Show(this, "Clear the saved queue? Your media files will stay in place.", "Clear queue", MessageBoxButton.OKCancel) != MessageBoxResult.OK) return; Unload(); library.Clear(); RefreshQueue(); }
    void Bookmark() { if (current is null || opening) return; library.Bookmark(current.Id, CurrentTime); Notice("Saved moment at " + MediaEntry.Clock(CurrentTime)); }
    void ShowBookmarks()
    {
        if (current is null || current.Bookmarks.Count == 0) { Notice("No saved moments for this file yet. Use Ctrl+B to save one."); return; }
        var menu = new ContextMenu(); foreach (var bookmark in current.Bookmarks) { var item = new MenuItem { Header = bookmark.Label }; item.Click += (_, _) => Seek(bookmark.Time); menu.Items.Add(item); } menu.PlacementTarget = play; menu.IsOpen = true;
    }
    void OpenSubtitles()
    {
        var picker = new OpenFileDialog { Title = "Add captions", Filter = "Subtitle files|*.srt;*.vtt;*.ass;*.ssa;*.sub;*.idx;*.sup|All files|*.*" };
        if (picker.ShowDialog(this) == true) TryControl(() => ReadSubtitles(picker.FileName));
    }
    void ReadSubtitles(string path) { if (vlc is null) { Notice("Open media before adding captions."); return; } vlc.AddSubtitles(path); subtitleDelay = 0; vlc.SubtitleDelay = 0; Notice("Captions: " + Path.GetFileName(path)); }
    void ChangeCaptionDelay(double adjustment) { subtitleDelay = Math.Clamp(subtitleDelay + adjustment, -86400, 86400); if (vlc is not null) vlc.SubtitleDelay = subtitleDelay; Notice($"Caption delay: {subtitleDelay:+0.00;-0.00;0.00}s"); }
    void TryControl(Action action) { try { action(); } catch (Exception error) { Notice(error.Message); } }
    void OnKey(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape && fullscreen) { ToggleFullscreen(); e.Handled = true; return; }
        if (e.Key == Key.Escape && opening) { Unload(); Notice("Opening canceled"); e.Handled = true; return; }
        if (e.Key == Key.F11) { ToggleFullscreen(); e.Handled = true; return; }
        if (Keyboard.Modifiers == ModifierKeys.Control && e.Key == Key.O) { _ = OpenMediaAsync(); e.Handled = true; return; }
        if (Keyboard.FocusedElement is TextBox || e.Key == Key.Space && Keyboard.FocusedElement is ButtonBase) return;
        if (Keyboard.Modifiers == ModifierKeys.Control && e.Key == Key.B) Bookmark(); else if (e.Key == Key.Space) TogglePlayback(); else if (e.Key == Key.Left) Seek(CurrentTime - 10); else if (e.Key == Key.Right) Seek(CurrentTime + 10); else return; e.Handled = true;
    }
    void SaveProgress() { if (current is not null && !opening && lastError is null) library.UpdateProgress(current.Id, computerEnded ? duration : CurrentTime, duration, computerEnded); SaveLibrary(); PushSharedPlaylist(); }
    void SaveLibrary() { try { library.Flush(); } catch (IOException error) { Notice("The saved library could not be written: " + error.Message); } }
    void Unload() { skipAnalysis?.Cancel(); ClearDetectedMatches(); SaveProgress(); ++generation; DisposePlayer(); current = null; playing = opening = false; duration = 0; openingClock.Reset(); empty.Visibility = Visibility.Visible; nowTitle.Text = "Make room for a good story"; backend.Text = ""; UpdateTransport(); }
    void Fail(string error) { lastError = error; opening = playing = false; ++generation; DisposePlayer(); openingClock.Reset(); if (fullscreen) ToggleFullscreen(); Notice(error); nowTitle.Text = "This file could not be played"; empty.Visibility = Visibility.Visible; }
    void Notice(string message) => status.Text = message;
    void ShowDiagnostics()
    {
        var details = JsonSerializer.Serialize(new { engine = "libVLC3", version = VlcPlayer.RuntimeVersion, changeset = VlcPlayer.RuntimeChangeset, runtime = VlcPlayer.RuntimePath, runtimeBundled = VlcPlayer.RuntimeBundled, displayedPictures = vlc?.DisplayedPictures, decodedVideo = vlc?.DecodedVideo, decodedAudio = vlc?.DecodedAudio, playedAudioBuffers = vlc?.PlayedAudioBuffers,
            audioTracks = vlc?.AudioTracks, captionTracks = vlc?.SubtitleTracks, sharing = sharing?.IsRunning == true, activeSharedStreams = sharing?.ActiveMediaStreams, sharingPower = SharingPowerStatus, discovery = discovery?.Status, lastError }, new JsonSerializerOptions { WriteIndented = true });
        var text = new TextBox { Text = details, IsReadOnly = true, TextWrapping = TextWrapping.Wrap, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Margin = new Thickness(16) };
        new Window { Owner = this, Title = "Local playback diagnostics", Width = 760, Height = 540, Content = text, WindowStartupLocation = WindowStartupLocation.CenterOwner }.Show();
    }
    void PushSharedPlaylist() { sharing?.SetPlaylist(library.Data.Items.Select(i => new SharedMediaItem(i.Id.ToString("N"), i.FilePath, i.Title, i.Duration, i.ResumeTime,
        i.MobileProgressSeconds, i.MobileUpdatedAtUnixSeconds, i.ComputerUpdatedAtUnixSeconds, i.MobileDurationSeconds, i.MobileCompleted, i.ComputerProgressSeconds, i.ComputerCompleted, SkipRanges: PlaybackSkipPolicy.ValidatedRanges(i.SkipRanges, i.Duration)))); }
    void ReceiveMobileProgress(PlaylistSharingServer source, SharedPlaybackProgress report)
    {
        // Only the UI thread touches the saved library. A queued update from a
        // stopped/replaced sharing session or removed item cannot resurrect it.
        if (closing || !ReferenceEquals(sharing, source) || !source.IsRunning || !Guid.TryParseExact(report.ItemId, "N", out var id)) return;
        var item = library.Data.Items.Find(i => i.Id == id);
        if (item is null || !string.Equals(item.FilePath, report.FilePath, StringComparison.OrdinalIgnoreCase) || !File.Exists(item.FilePath)
            || receivedProgressRevisions.TryGetValue(report.ItemId, out long revision) && report.Revision <= revision) return;
        if (!library.UpdateMobileProgress(id, report.PositionSeconds, report.DurationSeconds, report.Completed, report.UpdatedAtUnixSeconds)) return;
        receivedProgressRevisions[report.ItemId] = report.Revision;
        SaveLibrary(); PushSharedPlaylist(); UpdateDeviceProgress();
    }
    void StartSharing()
    {
        if (sharing?.IsRunning == true) return;
        try
        {
            var server = new PlaylistSharingServer("Luma on " + Environment.MachineName); sharing = server; receivedProgressRevisions.Clear();
            server.ProgressReported += report =>
            {
                if (closing || Dispatcher.HasShutdownStarted) return;
                try { Dispatcher.BeginInvoke(new Action(() => ReceiveMobileProgress(server, report))); } catch (InvalidOperationException) { }
            };
            PushSharedPlaylist(); server.Start(); discovery = new WindowsDnsSdAdvertisement(server); Notice("Local playlist sharing is on");
        }
        catch (Exception error) { sharing?.Stop(); discovery?.Dispose(); sharing = null; discovery = null; Notice("Sharing could not start: " + error.Message); }
        RefreshSharingUI();
    }
    void StopSharing()
    {
        sharingPower.Update(false);
        if (smokeDiagnostics) Console.WriteLine("INFO: StopSharing closes HTTP listener and revokes tokens");
        sharing?.Stop();
        if (smokeDiagnostics) Console.WriteLine("INFO: StopSharing queues native DNS-SD withdrawal");
        discovery?.Dispose(); sharing = null; discovery = null;
        if (smokeDiagnostics) Console.WriteLine("INFO: StopSharing native withdrawal returned to UI");
        RefreshSharingUI(); Notice("Sharing stopped; paired devices were revoked");
    }
    void ShowSharing()
    {
        if (sharingWindow is not null) { sharingWindow.Activate(); return; }
        var panel = new StackPanel { Margin = new Thickness(24) };
        panel.Children.Add(new TextBlock { Text = "Share this playlist", FontSize = 22, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 12) });
        panel.Children.Add(new TextBlock { Text = "On a trusted home network, open Luma on Android and discover this computer or enter a URL below. Pairing grants access to the current playlist until you stop sharing. Streaming uses local HTTP and buffers media on the phone; this computer must stay awake.", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 16) });
        sharingState = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 12) }; panel.Children.Add(sharingState);
        sharingAddresses = new TextBox { IsReadOnly = true, AcceptsReturn = true, MinHeight = 65, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 12) }; panel.Children.Add(sharingAddresses);
        pairingText = new TextBlock { FontSize = 22, FontWeight = FontWeights.Bold, Margin = new Thickness(0, 0, 0, 12) }; panel.Children.Add(pairingText);
        var buttons = Row(); startSharing = Button("Start sharing", StartSharing); stopSharing = Button("Stop and revoke", StopSharing); buttons.Children.Add(startSharing); buttons.Children.Add(stopSharing);
        buttons.Children.Add(Button("Copy connection details", () => { if (sharing?.IsRunning == true) Clipboard.SetText(string.Join(Environment.NewLine, sharing.Addresses) + Environment.NewLine + "Pairing code: " + sharing.PairingCode); })); panel.Children.Add(buttons);
        sharingWindow = new Window { Owner = this, Title = "Local playlist sharing", Width = 620, Height = 480, Content = panel, WindowStartupLocation = WindowStartupLocation.CenterOwner };
        sharingWindow.Closed += (_, _) => { sharingWindow = null; sharingState = pairingText = null; sharingAddresses = null; startSharing = stopSharing = null; };
        RefreshSharingUI(); sharingWindow.Show();
    }
    string SharingPowerStatus => sharingPower.IsActive ? "Idle sleep prevention requested while streaming" : sharingPower.IsUnavailable ? "Sleep prevention unavailable — keep this computer awake" : "Computer must remain awake to share";
    void RefreshSharingUI()
    {
        bool active = sharing?.IsRunning == true; shareButton.Content = active ? "Sharing on" : "Share playlist";
        shareButton.SetResourceReference(BackgroundProperty, active ? "AccentBrush" : "ButtonBrush");
        if (sharingState is null) return;
        sharingState.Text = active ? $"Active · {sharing!.PairedDevices} paired device(s) · {discovery?.Status ?? "Manual URL entry available"} · {SharingPowerStatus}" : "Sharing is off. No playlist or media is exposed.";
        sharingAddresses!.Text = active ? string.Join(Environment.NewLine, sharing!.Addresses) : "Start sharing to get local connection URLs.";
        pairingText!.Text = active ? DateTimeOffset.UtcNow < sharing!.PairingExpiresAt ? "Pairing code: " + sharing.PairingCode + "  (expires " + sharing.PairingExpiresAt.LocalDateTime.ToString("t") + ")" : "Code expired — stop and restart to pair another device" : "";
        startSharing!.IsEnabled = !active; stopSharing!.IsEnabled = active;
    }
    void Shutdown()
    {
        skipAnalysis?.Cancel();
        if (closing) return; closing = true; ClearDetectedMatches(); SaveProgress(); ++generation; timer.Stop(); updateTimer.Stop(); lifetimeCancellation.Cancel(); StopSharing(); sharingPower.Dispose(); sharingWindow?.Close(); DisposePlayer(); lifetimeCancellation.Dispose();
    }
    void ToggleFullscreen()
    {
        fullscreen = !fullscreen;
        if (fullscreen)
        {
            normalBounds = RestoreBounds; previousState = WindowState; previousTopmost = Topmost; WindowState = WindowState.Normal; WindowStyle = WindowStyle.None; ResizeMode = ResizeMode.NoResize; Topmost = true;
            header.Visibility = sidebar.Visibility = controls.Visibility = status.Visibility = Visibility.Collapsed; layout.ColumnDefinitions[0].Width = new GridLength(0); layout.RowDefinitions[0].Height = layout.RowDefinitions[2].Height = layout.RowDefinitions[3].Height = new GridLength(0); stage.Margin = new Thickness(0); stage.CornerRadius = new CornerRadius(0); NativeWindow.FillMonitor(this);
        }
        else
        {
            Topmost = previousTopmost; WindowStyle = WindowStyle.SingleBorderWindow; ResizeMode = ResizeMode.CanResize; WindowState = WindowState.Normal; Left = normalBounds.Left; Top = normalBounds.Top; Width = normalBounds.Width; Height = normalBounds.Height; WindowState = previousState;
            header.Visibility = sidebar.Visibility = controls.Visibility = status.Visibility = Visibility.Visible; layout.ColumnDefinitions[0].Width = new GridLength(260); layout.RowDefinitions[0].Height = new GridLength(68); layout.RowDefinitions[2].Height = new GridLength(ControlsHeight); layout.RowDefinitions[3].Height = new GridLength(28); stage.Margin = new Thickness(14, 14, 14, 0); stage.CornerRadius = new CornerRadius(14); NativeWindow.Style(this, dark);
        }
    }
    void ShowDocument(string file, string title)
    {
        var text = File.Exists(Path.Combine(AppContext.BaseDirectory, file)) ? File.ReadAllText(Path.Combine(AppContext.BaseDirectory, file)) : "Support information is unavailable in this build.";
        var content = new TextBox { Text = text, IsReadOnly = true, TextWrapping = TextWrapping.Wrap, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Margin = new Thickness(16), BorderThickness = new Thickness(0) };
        new Window { Owner = this, Title = title, Width = 740, Height = 600, Content = content, WindowStartupLocation = WindowStartupLocation.CenterOwner }.ShowDialog();
    }
    async Task PollUpdatesAsync(bool manual)
    {
        if (closing || checkingUpdate) return;
        if (updateConfig is null) { if (manual) MessageBox.Show(this, "Updates are not connected for this Windows preview. Download review builds from the project's GitHub page.", "Luma Player"); return; }
        var idle = IsActive && IsEnabled && !playing && !opening && !fullscreen;
        if (!manual && !IsActive) return;
        var data = library.Data; var now = DateTimeOffset.UtcNow;
        var due = UpdatePolicy.Due(now, data.AutomaticallyCheckUpdates, data.LastUpdateAttempt, data.DeferUpdatesUntil);
        if (!manual && !due && pendingUpdate is null) return;
        checkingUpdate = true; var explicitAction = manual;
        try
        {
            using var client = new WindowsUpdateClient();
            if (manual || due) { data.LastUpdateAttempt = now; SaveLibrary(); pendingUpdate = await client.CheckAsync(updateConfig, lifetimeCancellation.Token); }
            if (closing || pendingUpdate is not { } release) return;
            idle = IsActive && IsEnabled && !playing && !opening && !fullscreen;
            if (!UpdatePolicy.Offer(release.Build, currentBuild, manual, data.AutomaticallyCheckUpdates, idle, DateTimeOffset.UtcNow, data.DeferUpdatesUntil)) { if (manual) MessageBox.Show(this, "You have the latest published Windows build.", "Luma Player"); return; }
            var accepted = MessageBox.Show(this, "Luma Player " + release.Version + " is available.\n\n" + release.Notes[..Math.Min(2000, release.Notes.Length)] + "\n\nDownload the verified update? Installation is manual.", "Luma Player update", MessageBoxButton.YesNo) == MessageBoxResult.Yes;
            data.DeferUpdatesUntil = DateTimeOffset.UtcNow.AddDays(1); SaveLibrary(); pendingUpdate = null; if (!accepted) return;
            explicitAction = true;
            var picker = new SaveFileDialog { Title = "Save verified update", FileName = "Luma-Windows-" + release.Build + "-" + Guid.NewGuid().ToString("N")[..8] + Path.GetExtension(new Uri(release.Url).AbsolutePath), OverwritePrompt = true };
            if (picker.ShowDialog(this) != true) return;
            if (File.Exists(picker.FileName)) { MessageBox.Show(this, "Choose a new filename for the verified update. Existing files are preserved.", "Luma Player"); return; }
            await client.DownloadAsync(release, picker.FileName, lifetimeCancellation.Token);
            if (!closing) MessageBox.Show(this, "The verified update is saved. Quit Luma and manually replace the app from the downloaded package. Your saved library stays in place.", "Update downloaded");
        }
        catch (OperationCanceledException) { }
        catch (Exception) { if (explicitAction && !closing) MessageBox.Show(this, "The update could not be verified. Nothing was installed.", "Luma Player"); }
        finally { checkingUpdate = false; }
    }
    internal async Task RunSmokeAsync(string fixtures, string output)
    {
        Directory.CreateDirectory(output);
        void Check(bool value, string label) { if (!value) throw new InvalidOperationException(label); }
        async Task Until(Func<bool> condition, string label, int milliseconds = 20000)
        { var end = DateTime.UtcNow.AddMilliseconds(milliseconds); while (!condition()) { if (DateTime.UtcNow >= end) throw new TimeoutException(label + ": " + lastError); await Task.Delay(40); } }
        smokeDiagnostics = true;
        var endpoints = NativeAudio.ActiveRenderEndpoints(); dummyAudioForSmoke = endpoints == 0;
        Console.WriteLine("INFO: active audio endpoints: " + (endpoints?.ToString() ?? "unknown") + "; audio mode: " + (dummyAudioForSmoke ? "explicit test-only dummy output" : "system output"));
        async Task<VlcPlayer> Open(string path)
        {
            var entry = library.Add([path]).FirstOrDefault() ?? library.Data.Items.Single(i => i.FilePath == Path.GetFullPath(path)); await LoadEntryAsync(entry);
            await Until(() => !opening || lastError is not null, "libVLC opens " + Path.GetFileName(path)); Check(lastError is null && vlc is not null, "libVLC failed: " + lastError); return vlc!;
        }
        var demo = await Open(Path.Combine(fixtures, "Luma-Demo.mp4"));
        string? expectedRoute = Environment.GetEnvironmentVariable("LUMA_EXPECT_BUNDLED_RUNTIME");
        if (expectedRoute is not null) Check(bool.TryParse(expectedRoute, out bool expectedBundled) && VlcPlayer.RuntimeBundled == expectedBundled, "The native player uses the requested bundled/external runtime route");
        await Until(() => demo.DisplayedPictures is > 1 && demo.NaturalVideoWidth == 960, "Native libVLC displayed video frames");
        Pause(); Seek(3); speed.SelectedIndex = 2; volume.Value = .4; Play(); await Task.Delay(400);
        Check(CurrentTime > 3 && Math.Abs(demo.Rate - 1.25) < .01 && demo.Volume == .4, "libVLC seek/rate/volume"); Pause(); speed.SelectedIndex = 1;
        ToggleFullscreen(); await Task.Delay(300); Check(fullscreen && ReferenceEquals(vlc, demo) && Math.Abs(stage.ActualWidth - layout.ActualWidth) < 2, "Fullscreen retained player and fills client"); ToggleFullscreen();
        ReadSubtitles(Path.Combine(fixtures, "Luma-Demo.srt")); await Until(() => demo.SubtitleTracks.Any(t => t.Id >= 0), "External native subtitle track");
        var subtitle = demo.SubtitleTracks.First(t => t.Id >= 0); demo.SelectSubtitle(subtitle.Id); ChangeCaptionDelay(.25); Check(demo.SubtitleTrack == subtitle.Id && demo.SubtitleDelay == .25, "Native external subtitle selection and delay"); demo.SelectSubtitle(-1);
        var snapshot = Path.Combine(output, "libvlc-avc-frame.png"); demo.Snapshot(snapshot); await Until(() => File.Exists(snapshot) && new FileInfo(snapshot).Length > 100, "Native libVLC AVC snapshot");
        Console.WriteLine("PASS: libVLC native displayed frames, snapshot, seek/pause/rate/volume/fullscreen and external caption selection/delay");
        var keyboardSource = PresentationSource.FromVisual(this) ?? throw new InvalidOperationException("Native WPF keyboard source unavailable");
        Check(autoSkip.Focus(), "Auto-skip checkbox accepts keyboard focus");
        bool playbackBeforeSpace = playing, originalAutoSkip = library.Data.AutoSkipEnabled;
        void SpaceOnAutoSkip()
        {
            var previewSpace = new KeyEventArgs(Keyboard.PrimaryDevice, keyboardSource, Environment.TickCount, Key.Space) { RoutedEvent = Keyboard.PreviewKeyDownEvent };
            autoSkip.RaiseEvent(previewSpace);
            Check(!previewSpace.Handled && playing == playbackBeforeSpace, "Focused checkbox Space bypasses global play/pause shortcut");
            autoSkip.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, keyboardSource, Environment.TickCount, Key.Space) { RoutedEvent = Keyboard.KeyDownEvent });
            autoSkip.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, keyboardSource, Environment.TickCount, Key.Space) { RoutedEvent = Keyboard.KeyUpEvent });
        }
        SpaceOnAutoSkip(); Check(library.Data.AutoSkipEnabled != originalAutoSkip && autoSkip.IsChecked == library.Data.AutoSkipEnabled, "Standard routed Space activates and persists the focused checkbox");
        SpaceOnAutoSkip(); Check(library.Data.AutoSkipEnabled == originalAutoSkip && playing == playbackBeforeSpace, "Second checkbox Space restores opt-out without toggling playback");
        Console.WriteLine("PASS: focused Auto-skip checkbox accepts standard routed Space activation while global video playback remains unchanged");
        var chaptered = await Open(Path.Combine(fixtures, "Luma-Skip-Chapters.mkv"));
        bool loggedActiveChapters = false;
        try { await Until(() =>
        {
            var chapters = chaptered.Chapters;
            if (chaptered.DisplayedPictures is > 0 && !loggedActiveChapters)
            { loggedActiveChapters = true; Console.WriteLine("INFO: active skip chapter read: " + JsonSerializer.Serialize(new { chaptered.ChapterDiagnostics, duration = chaptered.Duration, time = chaptered.CurrentTime, names = chapters.Select(c => c.Name).ToArray() })); }
            return chaptered.DisplayedPictures is > 0 && chapters.Any(c => c.Name == "Opening") && chapters.Any(c => c.Name == "Credits");
        }, "Native labelled chapter metadata and decoded frames"); }
        catch (TimeoutException)
        {
            Console.WriteLine("INFO: skip fixture native diagnostics: " + JsonSerializer.Serialize(new { chaptered.ChapterDiagnostics, duration = chaptered.Duration, time = chaptered.CurrentTime, decoded = chaptered.DecodedVideo, displayed = chaptered.DisplayedPictures, names = chaptered.Chapters.Select(c => c.Name).ToArray() }));
            throw;
        }
        Pause(); var nativeRanges = PlaybackSkipPolicy.FromChapters(chaptered.Chapters, duration);
        Check(nativeRanges.Count == 2 && Math.Abs(nativeRanges[0].StartSeconds - 1) < .02 && Math.Abs(nativeRanges[0].EndSeconds - 3) < .02 && Math.Abs(nativeRanges[1].EndSeconds - 10) < .02, "Native chapter ABI uses milliseconds and preserves post-credit scene");
        int introFrames = chaptered.DisplayedPictures ?? 0; autoSkip.IsChecked = true; library.Data.AutoSkipEnabled = true; skipSession.Reset(); chaptered.Seek(.7); Play();
        await Until(() => skipSession.AutomaticSkips == 1 && CurrentTime >= 3.15 && chaptered.DisplayedPictures > introFrames, "Automatic opening skip advances real native time and frames"); Pause();
        Check(ReferenceEquals(vlc, chaptered), "Automatic skip retains the native player");
        Seek(1.4); Play(); await Task.Delay(350); Check(CurrentTime >= 1.4 && CurrentTime < 2.8 && skipSession.AutomaticSkips == 1, "Explicit rewind is respected without repeated automatic skip"); Pause();
        int creditsFrames = chaptered.DisplayedPictures ?? 0; chaptered.Seek(7.7); Play(); await Until(() => skipSession.AutomaticSkips == 2 && CurrentTime >= 10.15 && chaptered.DisplayedPictures > creditsFrames, "Automatic credits skip advances into post-credit scene"); Pause();
        Check(CurrentTime < duration - .1 && ReferenceEquals(vlc, chaptered), "Credits skip does not end media or skip post-credit content");
        library.Data.AutoSkipEnabled = false; autoSkip.IsChecked = false; skipSession.Reset(); chaptered.Seek(1.3); Play(); await Task.Delay(350); Pause(); UpdateSkipping();
        Check(CurrentTime >= 1.3 && CurrentTime < 2.8 && skipSession.AutomaticSkips == 0 && skipSegment.Visibility == Visibility.Visible, "Opt-out preserves opening and exposes manual skip button");
        SkipActiveSegment(); await Until(() => CurrentTime >= 2.95, "Manual Skip intro button seeks the actual paused native player");
        Check(chaptered.IsPaused && ReferenceEquals(vlc, chaptered), "Manual skip preserves paused intent and player");
        Seek(1.4); autoSkip.IsChecked = true; library.Data.AutoSkipEnabled = true; UpdateSkipping(); UpdateLayout();
        var skipImage = new RenderTargetBitmap((int)Math.Ceiling(layout.ActualWidth), (int)Math.Ceiling(layout.ActualHeight), 96, 96, PixelFormats.Pbgra32); skipImage.Render(layout);
        var skipPng = new PngBitmapEncoder(); skipPng.Frames.Add(BitmapFrame.Create(skipImage)); using (var skipFile = File.Create(Path.Combine(output, "Luma-Windows-auto-skip.png"))) skipPng.Save(skipFile);
        library.Data.AutoSkipEnabled = false; autoSkip.IsChecked = false;
        Console.WriteLine("PASS: actual libVLC3 labelled chapters, opt-in opening/credits automatic native seeks, preserved post-credit scene/paused intent/same player, explicit rewind and manual Skip intro UI");
        var generatedAudio = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(output))!, "generated", "Luma-review-skip-audio.wav");
        string secondAudio = Path.Combine(output, "original-audio-match-second.wav"); File.Copy(generatedAudio, secondAudio, true);
        IReadOnlyList<SkipAudioCandidate> observedAudioCandidates = [];
        using (var audioAnalysisTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(90)))
        {
            var audioCandidates = await SkipAudioAnalyzer.AnalyzeAsync(generatedAudio, secondAudio, VlcPlayer.RuntimePath, audioAnalysisTimeout.Token, minimumMatchSeconds: 3, maxWindowSeconds: 4); observedAudioCandidates = audioCandidates;
            Check(audioCandidates.Any(candidate => candidate.Kind == "intro" && candidate.FirstEndSeconds - candidate.FirstStartSeconds >= 3)
                && audioCandidates.Any(candidate => candidate.Kind == "credits" && candidate.FirstEndSeconds - candidate.FirstStartSeconds >= 3), "Real approved libVLC audio analysis captures matching authored PCM opening and ending windows");
            Check(current is not null && current.SkipRanges.Count == 0 && ReferenceEquals(vlc, chaptered), "Analysis candidates do not apply ranges or replace playback without confirmation");
        }
        Console.WriteLine("PASS: approved native libVLC audio-memory decoding finds authored repeated opening/ending audio candidates without automatically applying ranges");
        Pause(); chaptered.Seek(.5); await Until(() => CurrentTime is >= .45 and < .7, "Native manual intro start position"); MarkSkipStart("intro");
        chaptered.Seek(1.2); await Until(() => CurrentTime is >= 1.15 and < 1.4, "Native manual intro end position"); MarkSkipEnd("intro");
        var markedIntro = current!.SkipRanges.Single(); Check(markedIntro.Source == "manual" && markedIntro.Kind == "intro" && markedIntro.StartSeconds is >= .45 and < .7 && markedIntro.EndSeconds is >= 1.15 and < 1.4, "Skip setup saves actual native start/end positions");
        chaptered.Seek(.8); await Until(() => CurrentTime is >= .75 and < 1, "Native saved intro revisit"); UpdateSkipping();
        Check(activeSkip?.Source == "manual" && skipSegment.Visibility == Visibility.Visible, "Saved intro overrides native chapter and presents manual button");
        SkipActiveSegment(); await Until(() => CurrentTime >= markedIntro.EndSeconds - .05, "Saved-range skip seeks actual native time");
        Check(chaptered.IsPaused && ReferenceEquals(vlc, chaptered), "Saved-range skip preserves the paused native player");
        library.SetSkipRanges(current.Id, []); skipDraft = null;
        Console.WriteLine("PASS: Skip setup marks native timestamps, persists a manual range overriding chapters, and Skip intro seeks it without changing paused playback intent");
        var previewAudio = await Open(generatedAudio); Pause();
        detectedSkipRanges = observedAudioCandidates.Select(candidate => new PlaybackSkipRange(candidate.Kind, candidate.FirstStartSeconds, candidate.FirstEndSeconds, "manual", "Confirmed repeated audio"))
            .Where(range => PlaybackSkipPolicy.ValidatedRanges([range], duration).Count == 1).ToArray();
        detectedSkipItem = current!.Id; detectedSkipPath = current.FilePath; detectedSkipGeneration = generation;
        detectedReferenceTitle = Path.GetFileName(secondAudio); detectedFilesUnchanged = () => File.Exists(generatedAudio) && File.Exists(secondAudio); reviewSkipMatches.IsEnabled = true;
        var reviewRange = detectedSkipRanges.First(range => range.Kind == "intro"); int cachedRanges = detectedSkipRanges.Count;
        ReviewDetectedMatches(); await Task.Delay(100);
        Check(skipReviewWindow?.IsVisible == true && IsEnabled && current.SkipRanges.Count == 0, "Detected-match review is modeless and does not save before confirmation");
        skipReviewWindow!.UpdateLayout(); var reviewImage = new RenderTargetBitmap((int)Math.Ceiling(skipReviewWindow.ActualWidth), (int)Math.Ceiling(skipReviewWindow.ActualHeight), 96, 96, PixelFormats.Pbgra32); reviewImage.Render(skipReviewWindow);
        var reviewPng = new PngBitmapEncoder(); reviewPng.Frames.Add(BitmapFrame.Create(reviewImage)); using (var reviewFile = File.Create(Path.Combine(output, "Luma-Windows-detected-matches.png"))) reviewPng.Save(reviewFile);
        PreviewDetectedRange(reviewRange); await Until(() => !previewAudio.IsPaused && CurrentTime <= .5, "Preview starts at the actual native opening position");
        double previewAnchor = CurrentTime; await Until(() => CurrentTime >= previewAnchor + .3 && !previewAudio.IsPaused && previewAudio.DecodedAudio is > 0, "Preview advances the actual native audio clock before saving it");
        Check(current.SkipRanges.Count == 0 && !library.Data.AutoSkipEnabled, "Preview neither saves a range nor enables automatic skipping");
        skipReviewWindow!.Close(); Check(skipReviewWindow is null && previewAudio.IsPaused && detectedSkipRanges.Count == cachedRanges && reviewSkipMatches.IsEnabled, "Declining review stops preview but retains cached results");
        ReviewDetectedMatches(); Check(skipReviewWindow?.IsVisible == true && detectedSkipRanges.Count == cachedRanges, "Review reopens cached matches without another analysis");
        double beforeSavingRange = CurrentTime;
        Check(SaveDetectedRange(reviewRange) && current.SkipRanges.Single() == reviewRange && previewAudio.IsPaused && Math.Abs(CurrentTime - beforeSavingRange) < .1 && ReferenceEquals(vlc, previewAudio), "Explicit Save confirms one cached range without moving or rebuilding paused playback");
        skipReviewWindow!.Close();
        Console.WriteLine("PASS: modeless detected-match review previews native audio before saving, retains declined results, reopens without reanalysis, and saves only by explicit action");
        var hevc = await Open(Path.Combine(fixtures, "Luma-HEVC10-AAC51.mp4"));
        await Until(() => hevc.DecodedVideo is > 0 && hevc.DecodedAudio is > 0 && hevc.DisplayedPictures is > 0, "HEVC Main10/AAC5.1 decoded audio and displayed video");
        Pause(); Seek(.75); Play(); await Task.Delay(300); Check(CurrentTime >= .75, "HEVC/AAC seek retained synchronized native clock"); Pause();
        var hevcShot = Path.Combine(output, "libvlc-hevc10-frame.png"); hevc.Snapshot(hevcShot); await Until(() => File.Exists(hevcShot) && new FileInfo(hevcShot).Length > 100, "HEVC native snapshot");
        Console.WriteLine("PASS: raw hev1 Main10/AAC5.1 local MP4 reaches libVLC decoded video/audio and native presentation without container normalization");
        var generated = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(output))!, "generated");
        var hevcMatroska = await Open(Path.Combine(generated, "Luma-review-hevc10.mkv"));
        await Until(() => hevcMatroska.DecodedVideo is > 0 && hevcMatroska.DisplayedPictures is > 0 && hevcMatroska.NaturalVideoWidth == 960,
            "Direct Matroska HEVC Main10 decoded and displayed frames");
        Check(ReferenceEquals(vlc, hevcMatroska), "Direct Matroska HEVC retained the loaded native player"); Pause();
        Console.WriteLine("PASS: V_MPEGH/ISO/HEVC Main10 Matroska opens directly in the same libVLC player with decoded/displayed video frames; video-only fixture, no remux");
        var mkv = await Open(Path.Combine(generated, "Luma-review-text.mkv")); await Until(() => mkv.SubtitleTracks.Count(t => t.Id >= 0) >= 2, "Matroska native ASS/UTF8 tracks");
        Pause(); Seek(.75); foreach (var track in mkv.SubtitleTracks.Where(t => t.Id >= 0)) { mkv.SelectSubtitle(track.Id); Check(mkv.SubtitleTrack == track.Id, "Embedded caption track selection"); } mkv.SelectSubtitle(-1);
        foreach (string file in new[] { "Luma-MSVideo1-16.avi", "Luma-Silence.flac", "Luma-MSADPCM.wav" })
        { var media = await Open(Path.Combine(fixtures, file)); await Until(() => media.DecodedVideo is > 0 || media.DecodedAudio is > 0, "General local codec " + file); Pause(); }
        Console.WriteLine("PASS: direct local Matroska ASS/UTF8 selection, AVI, FLAC and ADPCM playback without remux/size gate");
        Unload(); var pending = library.Add([Path.Combine(fixtures, "Luma-Demo.mp4")]).FirstOrDefault() ?? library.Data.Items.First(i => i.FilePath.EndsWith("Luma-Demo.mp4"));
        await LoadEntryAsync(pending); var obsolete = vlc; await LoadEntryAsync(pending); await Until(() => !opening, "Replacement player load"); Check(!ReferenceEquals(obsolete, vlc) && lastError is null, "Native generations isolated");
        Pause(); Seek(5); await Task.Delay(250); SaveProgress(); Check(library.Data.Items.Single(i => i.Id == pending.Id).ResumeTime >= 4.5, "Saved resume position");
        string idleSleepPrevention = "not-tested";
        WindowsDnsSdAdvertisement? retiredByPowerProbe = null;
        string? registrationAtPowerProbe = null;
        StartSharing(); Check(sharing?.IsRunning == true && shareButton.Content.ToString() == "Sharing on", "Sharing starts only from explicit UI action");
        using (var http = new HttpClient())
        {
            string address = "http://127.0.0.1:" + sharing!.Port;
            var paired = await http.PostAsync(address + "/v1/pair", new StringContent(JsonSerializer.Serialize(new { code = sharing.PairingCode }), System.Text.Encoding.UTF8, "application/json")); paired.EnsureSuccessStatusCode();
            using var json = JsonDocument.Parse(await paired.Content.ReadAsStringAsync()); http.DefaultRequestHeaders.Authorization = new("Bearer", json.RootElement.GetProperty("token").GetString());
            var playlist = await http.GetStringAsync(address + "/v1/playlist"); Check(!playlist.Contains(fixtures, StringComparison.Ordinal), "Sharing GUI exposes no source paths");
            double computerBeforePhone = CurrentTime, savedBeforePhone = pending.ResumeTime; var playerBeforePhone = vlc;
            using (var progressResponse = await http.PostAsync(address + "/v1/progress", new StringContent(JsonSerializer.Serialize(new { itemId = pending.Id.ToString("N"), positionSeconds = 7d, durationSeconds = 999d, completed = false }), System.Text.Encoding.UTF8, "application/json"))) progressResponse.EnsureSuccessStatusCode();
            await Until(() => pending.MobileProgressSeconds == 7 && deviceProgress.Text.Contains("Phone", StringComparison.Ordinal) && resumePhone.IsEnabled, "Phone progress persisted and visible in the Windows UI");
            Check(pending.Duration == duration && pending.MobileDurationSeconds == duration && Math.Abs(CurrentTime - computerBeforePhone) < .4 && pending.ResumeTime == savedBeforePhone && ReferenceEquals(vlc, playerBeforePhone), "Phone progress does not move or rebuild the computer player or overwrite its bookmark/duration");
            var progressSession = sharing!;
            ReceiveMobileProgress(progressSession, new(pending.Id.ToString("N"), pending.FilePath, 2, duration, false, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000d, 0));
            Check(pending.MobileProgressSeconds == 7, "An out-of-order queued callback cannot roll back phone progress");
            ResumeFromPhone(); await Until(() => CurrentTime >= 6.7 && playing && ReferenceEquals(vlc, playerBeforePhone), "Explicit Resume from phone seeks the existing native player"); Pause();
            Check(pending.MobileProgressSeconds == 7 && pending.ComputerProgressSeconds >= 6.7 && pending.PhoneProgressFraction > 0 && pending.ComputerProgressFraction > 0, "Distinct phone/computer progress remains visible after handoff");
            Console.WriteLine("PASS: paired phone progress persists through the UI without automatic seek, distinct device labels/bars, and explicit Resume from phone uses the retained native player");
            string powerFile = Path.Combine(output, "original-power-backpressure.bin");
            using (var file = File.Create(powerFile)) file.SetLength(64L * 1024 * 1024);
            var powerEntry = library.Add([powerFile]).Single(); PushSharedPlaylist();
            try
            {
                using var stalled = new TcpClient { ReceiveBufferSize = 1024 };
                await stalled.ConnectAsync(IPAddress.Loopback, sharing!.Port);
                string token = json.RootElement.GetProperty("token").GetString()!;
                await stalled.GetStream().WriteAsync(System.Text.Encoding.ASCII.GetBytes("GET /v1/media/" + powerEntry.Id.ToString("N") + "?token=" + token + " HTTP/1.1\r\nHost: localhost\r\n\r\n"));
                await Until(() => sharing!.ActiveMediaStreams == 1 && (sharingPower.IsActive || sharingPower.IsUnavailable), "Streaming-only idle sleep request");
                bool requested = sharingPower.IsActive;
                retiredByPowerProbe = discovery; registrationAtPowerProbe = discovery?.Status;
                StopSharing();
                ReceiveMobileProgress(progressSession, new(pending.Id.ToString("N"), pending.FilePath, 9, duration, false, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000d, long.MaxValue));
                Check(pending.MobileProgressSeconds == 7, "A stopped sharing session cannot persist its queued callback");
                Check(!sharingPower.IsActive, "Stopping sharing releases the idle-system-sleep request");
                idleSleepPrevention = requested && sharingPower.LastReleaseSucceeded == true ? "system-required-handle-created-and-released" : "unavailable-keep-computer-awake";
                Console.WriteLine("INFO: idle sleep prevention: " + idleSleepPrevention + "; forced sleep/display policy is unchanged and physical sleep behavior is not asserted");
            }
            finally { library.Remove(powerEntry.Id); File.Delete(powerFile); RefreshQueue(); }

        }
        await Task.Delay(300); string dnsSdRegistration = registrationAtPowerProbe ?? discovery?.Status ?? "not attempted";
        Console.WriteLine("INFO: Windows DNS-SD registration: " + dnsSdRegistration + "; network discovery itself is not asserted by this hosted smoke.");
        var retiringDiscovery = retiredByPowerProbe ?? discovery;
        StopSharing(); Check(sharing is null && shareButton.Content.ToString() == "Share playlist", "UI stop revokes sharing");
        if (retiringDiscovery is not null)
        {
            try { await retiringDiscovery.CompletionTask.WaitAsync(TimeSpan.FromSeconds(5)); }
            catch (TimeoutException) { Console.WriteLine("INFO: DNS-SD withdrawal callback remains pending; listener is closed and tokens are revoked"); }
            Console.WriteLine("INFO: DNS-SD withdrawal status: " + retiringDiscovery.Status);
        }
        Console.WriteLine("PASS: StopSharing returned, HTTP access revoked; DNS-SD withdrawal status is recorded separately");
        foreach (bool theme in new[] { true, false })
        {
            SetTheme(theme); await Task.Delay(100); UpdateLayout(); var image = new RenderTargetBitmap((int)Math.Ceiling(layout.ActualWidth), (int)Math.Ceiling(layout.ActualHeight), 96, 96, PixelFormats.Pbgra32); image.Render(layout);
            var png = new PngBitmapEncoder(); png.Frames.Add(BitmapFrame.Create(image)); using var file = File.Create(Path.Combine(output, theme ? "Luma-Windows-dark.png" : "Luma-Windows-light.png")); png.Save(file);
        }
        File.WriteAllText(Path.Combine(output, "smoke-results.json"), JsonSerializer.Serialize(new { libvlcPlayback = "passed", hevc10Aac51 = "passed", hevc10Matroska = "passed", subtitleSelection = "passed", lifecycleRecovery = "passed", sharingUi = "passed", autoSkipChapters = "passed", repeatedAudioAnalysis = "passed", detectedMatchReview = "passed", keyboardSkipToggle = "passed", runtimeVersion = VlcPlayer.RuntimeVersion, runtimeChangeset = VlcPlayer.RuntimeChangeset, runtimeBundled = VlcPlayer.RuntimeBundled, dnsSdRegistration, idleSleepPrevention,
            physicalAudio = endpoints == 0 ? "unverified-no-active-render-endpoint" : "system-output-requested-audibility-not-measured", activeAudioRenderEndpoints = endpoints }, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine("PASS: libVLC native lifecycle/resume, opt-in sharing UI pairing/revoke, and native UI theme snapshots; physical audibility remains explicitly separate");
    }
}
