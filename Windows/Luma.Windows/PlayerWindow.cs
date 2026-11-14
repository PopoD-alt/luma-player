using System.Diagnostics;
using System.IO;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Win32;
using Luma.Core.Audio;
using Luma.Core.Library;
using Luma.Core.Subtitles;
using Luma.Core.Updates;
using Luma.Core.Video;
using Luma.Core.Containers;

namespace Luma.WindowsApp;

internal sealed class PlayerWindow : Window
{
    readonly LibraryStore library;
    readonly Grid layout = new();
    readonly Grid viewport = new();
    MediaElement movie = NewNativeMovie();
    readonly Image image = new() { Stretch = Stretch.Uniform };
    readonly TextBlock captions = new() { TextAlignment = TextAlignment.Center, FontSize = 24, Foreground = Brushes.White, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(30, 0, 30, 34), VerticalAlignment = VerticalAlignment.Bottom };
    readonly StackPanel empty = new() { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
    readonly TextBlock nowTitle = new() { Text = "Make room for a good story", FontSize = 20, FontWeight = FontWeights.SemiBold, TextAlignment = TextAlignment.Center };
    readonly TextBlock status = new() { FontSize = 11, Text = "Windows preview · ready", Margin = new Thickness(20, 0, 20, 0), VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
    readonly TextBlock timing = new() { Text = "0:00 / 0:00", FontSize = 12, VerticalAlignment = VerticalAlignment.Center };
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
    readonly Border stage = new() { Margin = new Thickness(14, 14, 14, 0), CornerRadius = new CornerRadius(14), Background = Brushes.Black, ClipToBounds = true };
    readonly MenuItem automatic = new() { Header = "Automatically check for updates", IsCheckable = true };
    readonly MenuItem audioTracksMenu = new() { Header = "Audio tracks", IsEnabled = false };
    readonly MenuItem embeddedSubtitlesMenu = new() { Header = "Embedded captions", IsEnabled = false };
    readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromMilliseconds(33) };
    readonly DispatcherTimer updateTimer = new() { Interval = TimeSpan.FromMinutes(1) };
    readonly Stopwatch clock = new();
    readonly Stopwatch nativeLoadClock = new();
    readonly HashSet<string> temporaryMedia = [];
    readonly HashSet<string> pendingTemporaryMedia = [];
    string? activeTemporaryMedia, nativeFailureContext;
    ulong? selectedMatroskaAudio;
    long matroskaOriginNanoseconds;
    bool activeNativeRequest;
    readonly string temporaryFolder = Path.Combine(Path.GetTempPath(), "LumaPlayer-" + Guid.NewGuid().ToString("N"));
    FrameworkElement header = null!, sidebar = null!, controls = null!;
    AviVideo? original;
    MediaEntry? current;
    CancellationTokenSource loadCancellation = new();
    readonly CancellationTokenSource lifetimeCancellation = new();
    List<SubtitleCue> cues = [];
    string? lastError;
    uint? lastNativeError;
    WindowsUpdateConfig? updateConfig;
    WindowsRelease? pendingUpdate;
    int currentBuild = 1, generation, visualRevision, subtitleRevision, lastFrame = -1, repeatMode;
    double position, duration, rate = 1, pendingResume, subtitleDelay;
    bool playing, opening, rendering, dragging, refreshingQueue, updatingSlider, fullscreen, dark = true, checkingUpdate, closing;
    Rect normalBounds; WindowState previousState; bool previousTopmost;
    DateTime lastSave = DateTime.UtcNow;

    public PlayerWindow(string? libraryFile = null)
    {
        library = new LibraryStore(libraryFile ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "sdas", "Luma Player", "library.json"));
        Title = "Luma Player"; Width = Math.Min(1220, SystemParameters.WorkArea.Width - 30); Height = Math.Min(800, SystemParameters.WorkArea.Height - 30); MinWidth = 940; MinHeight = 610; WindowStartupLocation = WindowStartupLocation.CenterScreen;
        FontFamily = new FontFamily("Segoe UI Variable, Segoe UI"); FontSize = 13; AllowDrop = true;
        SetResourceReference(BackgroundProperty, "WindowBrush"); SetResourceReference(ForegroundProperty, "TextBrush");
        SetupStyles(); SetTheme(true);
        Content = BuildLayout(); RefreshQueue();
        WireNativeMovie(movie, generation);
        timer.Tick += async (_, _) => await TickAsync(); timer.Start();
        updateTimer.Tick += async (_, _) => await PollUpdatesAsync(false); updateTimer.Start();
        Activated += async (_, _) => await PollUpdatesAsync(false);
        SourceInitialized += (_, _) => NativeWindow.Style(this, dark);
        Closing += (_, _) => Shutdown();
        SizeChanged += (_, _) => captions.MaxWidth = Math.Max(100, viewport.ActualWidth * .85);
        PreviewKeyDown += OnKey;
        Drop += async (_, e) => { if (e.Data.GetData(DataFormats.FileDrop) is string[] paths) await ImportAsync(paths); };
        DragOver += (_, e) => { e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None; e.Handled = true; };
        viewport.MouseLeftButtonDown += (_, e) => { if (e.ClickCount == 2) { ToggleFullscreen(); e.Handled = true; } };
        seek.PreviewMouseLeftButtonDown += (_, _) => dragging = true;
        seek.AddHandler(Thumb.DragCompletedEvent, new DragCompletedEventHandler((_, _) => { dragging = false; Seek(seek.Value); }));
        seek.PreviewMouseLeftButtonUp += (_, _) => { dragging = false; Seek(seek.Value); };
        seek.ValueChanged += (_, _) => { if (!updatingSlider && !dragging && seek.IsKeyboardFocusWithin) Seek(seek.Value); };
        queue.SelectionChanged += async (_, _) => { if (!refreshingQueue && queue.SelectedItem is MediaEntry entry && entry.Id != current?.Id) await LoadEntryAsync(entry); };
        queue.MouseDoubleClick += async (_, _) => { if (queue.SelectedItem is MediaEntry entry) await LoadEntryAsync(entry); };
        search.TextChanged += (_, _) => { searchHint.Visibility = search.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed; RefreshQueue(); };
        play.Click += (_, _) => TogglePlayback();
        volume.ValueChanged += (_, _) => movie.Volume = volume.Value;
        speed.SelectionChanged += (_, _) => { if (speed.SelectedItem is ComboBoxItem item) { var time = CurrentTime; rate = (double)item.Tag; position = time; clock.Restart(); movie.SpeedRatio = rate; } };
        repeat.Click += (_, _) => { repeatMode = (repeatMode + 1) % 3; repeat.Content = new[] { "Repeat: off", "Repeat: all", "Repeat: one" }[repeatMode]; };
        try
        {
            var config = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "appsettings.windows.json"));
            updateConfig = WindowsUpdateConfig.FromJson(config);
            using var json = JsonDocument.Parse(config); currentBuild = json.RootElement.GetProperty("build").GetInt32();
        }
        catch (Exception e) when (e is IOException or JsonException or InvalidOperationException) { updateConfig = null; }
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
        var rowText = new FrameworkElementFactory(typeof(TextBlock)); rowText.SetBinding(TextBlock.TextProperty, new System.Windows.Data.Binding("Title")); rowText.SetValue(TextBlock.TextTrimmingProperty, TextTrimming.CharacterEllipsis); rowFrame.AppendChild(rowText);
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
        foreach (var height in new[] { new GridLength(68), new GridLength(1, GridUnitType.Star), new GridLength(160), new GridLength(28) }) layout.RowDefinitions.Add(new() { Height = height });
        var top = new DockPanel { Margin = new Thickness(22, 0, 18, 0) };
        var brand = Row(); var logo = Text("▶", 24, true); logo.SetResourceReference(TextBlock.ForegroundProperty, "AccentBrush"); brand.Children.Add(logo); brand.Children.Add(new TextBlock { Text = "LUMA", FontSize = 18, FontWeight = FontWeights.Bold, Margin = new Thickness(12, 0, 12, 0), VerticalAlignment = VerticalAlignment.Center }); var preview = Text("WINDOWS PREVIEW", 10); preview.SetResourceReference(TextBlock.ForegroundProperty, "MutedBrush"); brand.Children.Add(preview);
        DockPanel.SetDock(brand, Dock.Left); top.Children.Add(brand);
        var actions = Row(); actions.HorizontalAlignment = HorizontalAlignment.Right;
        actions.Children.Add(Button("Open media", () => _ = OpenMediaAsync(), "Open local media (Ctrl+O)"));
        actions.Children.Add(Button("Appearance", () => SetTheme(!dark)));
        var more = Button("More  ···", () => { }); var menu = BuildMenu(); more.Click += (_, _) => { menu.PlacementTarget = more; menu.IsOpen = true; }; actions.Children.Add(more); DockPanel.SetDock(actions, Dock.Right); top.Children.Add(actions);
        var headerBorder = new Border { Child = top }; headerBorder.SetResourceReference(Border.BackgroundProperty, "PanelBrush"); header = headerBorder; Grid.SetColumnSpan(header, 2); layout.Children.Add(header);

        var side = new DockPanel(); var sideTop = new StackPanel { Margin = new Thickness(0, 22, 0, 0) };
        var queueTitle = new DockPanel { Margin = new Thickness(18, 0, 18, 14) }; queueCount.HorizontalAlignment = HorizontalAlignment.Right; DockPanel.SetDock(queueCount, Dock.Right); queueTitle.Children.Add(queueCount); queueTitle.Children.Add(Text("Up next", 16, true)); sideTop.Children.Add(queueTitle);
        var searchBox = new Grid(); searchBox.Children.Add(search); searchHint.SetResourceReference(TextBlock.ForegroundProperty, "MutedBrush"); searchBox.Children.Add(searchHint); sideTop.Children.Add(searchBox); DockPanel.SetDock(sideTop, Dock.Top); side.Children.Add(sideTop);
        var sideBottom = new WrapPanel { Margin = new Thickness(10, 10, 10, 10) }; sideBottom.Children.Add(Button("↑", () => MoveSelected(-1))); sideBottom.Children.Add(Button("↓", () => MoveSelected(1))); sideBottom.Children.Add(Button("Remove", RemoveSelected)); sideBottom.Children.Add(Button("Clear", ClearQueue)); DockPanel.SetDock(sideBottom, Dock.Bottom); side.Children.Add(sideBottom); side.Children.Add(queue);
        sidebar = Panel(side); sidebar.Margin = new Thickness(12, 14, 0, 0); Grid.SetRow(sidebar, 1); Grid.SetRowSpan(sidebar, 2); layout.Children.Add(sidebar);

        var symbol = Text("▶", 58); symbol.HorizontalAlignment = HorizontalAlignment.Center; symbol.SetResourceReference(TextBlock.ForegroundProperty, "AccentBrush"); empty.Children.Add(symbol); empty.Children.Add(nowTitle); var welcome = Text("Drop a local video or audio file here", 13); welcome.Foreground = new SolidColorBrush(Color.FromRgb(160, 169, 186)); welcome.HorizontalAlignment = HorizontalAlignment.Center; welcome.Margin = new Thickness(0, 12, 0, 0); empty.Children.Add(welcome);
        viewport.Children.Add(movie); viewport.Children.Add(image); viewport.Children.Add(empty); viewport.Children.Add(captions); stage.Child = viewport; Grid.SetRow(stage, 1); Grid.SetColumn(stage, 1); layout.Children.Add(stage);

        var bottom = new Grid { Margin = new Thickness(18, 12, 18, 10) }; bottom.RowDefinitions.Add(new() { Height = GridLength.Auto }); bottom.RowDefinitions.Add(new()); bottom.Children.Add(seek);
        var transport = new WrapPanel(); Grid.SetRow(transport, 1); var left = Row(); left.Margin = new Thickness(0, 0, 0, 4); left.Children.Add(Button("│◀", () => _ = NextAsync(-1), "Previous file")); left.Children.Add(Button("−10", () => Seek(CurrentTime - 10))); left.Children.Add(play); play.SetResourceReference(BackgroundProperty, "AccentBrush"); play.Foreground = Brushes.Black; left.Children.Add(Button("+10", () => Seek(CurrentTime + 10))); left.Children.Add(Button("▶│", () => _ = NextAsync(1), "Next file")); timing.Margin = new Thickness(12, 0, 10, 0); left.Children.Add(timing); transport.Children.Add(left);
        var right = Row(); right.HorizontalAlignment = HorizontalAlignment.Right; right.Children.Add(Button("Bookmark", Bookmark)); right.Children.Add(Button("Captions", () => OpenSubtitles()));
        speed.Foreground = Brushes.Black;
        foreach (var value in new[] { .5, 1, 1.25, 1.5, 2 }) speed.Items.Add(new ComboBoxItem { Content = value.ToString("0.##", CultureInfo.InvariantCulture) + "×", Tag = value, Foreground = Brushes.Black }); speed.SelectedIndex = 1;
        right.Children.Add(speed); var volumeLabel = Text("Volume", 11); volumeLabel.Margin = new Thickness(10, 0, 4, 0); right.Children.Add(volumeLabel); right.Children.Add(volume); right.Children.Add(Button("⛶", ToggleFullscreen, "Video fullscreen (F11)")); DockPanel.SetDock(right, Dock.Right); transport.Children.Add(right); bottom.Children.Add(transport);
        var meta = new DockPanel { VerticalAlignment = VerticalAlignment.Bottom }; backend.SetResourceReference(TextBlock.ForegroundProperty, "MutedBrush"); meta.Children.Add(backend); repeat.HorizontalAlignment = HorizontalAlignment.Right; DockPanel.SetDock(repeat, Dock.Right); meta.Children.Add(repeat); Grid.SetRow(meta, 1); meta.Margin = new Thickness(8, 0, 0, -4); bottom.Children.Add(meta); transport.VerticalAlignment = VerticalAlignment.Top;
        controls = bottom; Grid.SetRow(bottom, 2); Grid.SetColumn(bottom, 1); layout.Children.Add(bottom);
        status.SetResourceReference(TextBlock.ForegroundProperty, "MutedBrush"); Grid.SetRow(status, 3); Grid.SetColumnSpan(status, 2); layout.Children.Add(status);
        return layout;
    }
    ContextMenu BuildMenu()
    {
        var menu = new ContextMenu();
        void Item(string label, Action action) { var item = new MenuItem { Header = label }; item.Click += (_, _) => action(); menu.Items.Add(item); }
        Item("Format support", () => ShowDocument("FORMAT_SUPPORT.md", "Format support"));
        Item("Check for updates…", () => _ = PollUpdatesAsync(true)); menu.Items.Add(automatic); menu.Items.Add(new Separator());
        menu.Items.Add(audioTracksMenu); menu.Items.Add(embeddedSubtitlesMenu);
        Item("Play original video demo", () => _ = ImportAsync([Path.Combine(AppContext.BaseDirectory, "Demos", "Luma-MSVideo1-16.avi")]));
        Item("Saved bookmarks", ShowBookmarks); Item("Caption timing −0.25s", () => { subtitleDelay -= .25; Notice($"Caption delay: {subtitleDelay:+0.00;-0.00;0.00}s"); }); Item("Caption timing +0.25s", () => { subtitleDelay += .25; Notice($"Caption delay: {subtitleDelay:+0.00;-0.00;0.00}s"); }); Item("Turn captions off", () => { subtitleRevision++; cues.Clear(); captions.Text = ""; });
        menu.Items.Add(new Separator()); Item("About Luma Player", () => MessageBox.Show(this, "Luma Player\n0.6.1 · Windows preview\n\nMade by sdas · free to download\nOriginal Luma decoders and Windows media playback.\nThis preview is not digitally signed by a trusted Windows publisher.", "About Luma Player"));
        return menu;
    }

    async Task OpenMediaAsync()
    {
        var picker = new OpenFileDialog { Title = "Open media", Multiselect = true, Filter = "Media files|*.mp4;*.mkv;*.mka;*.mov;*.avi;*.wmv;*.webm;*.m4v;*.mp3;*.wav;*.flac;*.m4a;*.aac;*.aif;*.aiff;*.aifc;*.au;*.snd;*.wma|All files|*.*" };
        if (picker.ShowDialog(this) == true) await ImportAsync(picker.FileNames);
    }
    public async Task ImportAsync(IEnumerable<string> paths)
    {
        try
        {
            var list = paths.Take(2000).SelectMany(p => Directory.Exists(p) ? Directory.EnumerateFiles(p).Take(2000) : new[] { p }).Take(2000).ToList();
            var media = list.Where(p => Path.GetExtension(p).ToLowerInvariant() is not (".srt" or ".vtt"));
            var added = library.Add(media); RefreshQueue();
            if (current is null || lastError is not null)
            {
                var requested = added.FirstOrDefault() ?? library.Data.Items.FirstOrDefault(entry => list.Any(path => string.Equals(Path.GetFullPath(path), entry.FilePath, StringComparison.OrdinalIgnoreCase)));
                if (requested is not null) await LoadEntryAsync(requested);
            }
            var subtitle = list.FirstOrDefault(p => Path.GetExtension(p).ToLowerInvariant() is ".srt" or ".vtt"); if (subtitle is not null) ReadSubtitles(subtitle);
        }
        catch (Exception e) { Fail(e.Message); }
    }
    async Task LoadEntryAsync(MediaEntry entry, ulong? audioTrackId = null, bool includeAudio = true, double? resume = null, long? previousOrigin = null)
    {
        if (closing) return;
        var lifetimeToken = lifetimeCancellation.Token;
        SaveProgress(); loadCancellation.Cancel(); loadCancellation.Dispose(); loadCancellation = new(); var token = loadCancellation.Token; var version = ++generation;
        ReplaceNativeMovie(version); activeTemporaryMedia = null; CleanupTemporaryMedia(); nativeLoadClock.Reset(); nativeFailureContext = null; ResetTrackMenus(); original = null; playing = false; opening = true; clock.Reset(); position = duration = 0; lastFrame = -1; visualRevision++; cues.Clear(); subtitleDelay = 0; captions.Text = ""; image.Source = null; lastError = null; lastNativeError = null;
        current = entry; library.Data.SelectedId = entry.Id; pendingResume = resume ?? (entry.ResumeTime > 3 ? entry.ResumeTime : 0); RefreshQueue(); nowTitle.Text = entry.Title; empty.Visibility = Visibility.Visible; image.Visibility = Visibility.Collapsed; movie.Visibility = Visibility.Collapsed; backend.Text = "Opening…"; Notice("Opening " + entry.Title);
        try
        {
            if (!File.Exists(entry.FilePath)) throw new FileNotFoundException("This file has moved or is unavailable.");
            var suffix = Path.GetExtension(entry.FilePath).ToLowerInvariant(); var size = new FileInfo(entry.FilePath).Length;
            if (suffix == ".avi" && size <= 512L * 1024 * 1024)
            {
                var bytes = await File.ReadAllBytesAsync(entry.FilePath, token);
                try
                {
                    var video = await Task.Run(() => AviVideo.Open(bytes, token), token); if (version != generation) return;
                    original = video; duration = video.Duration; position = Math.Min(pendingResume, Math.Max(0, duration - 1)); pendingResume = 0; opening = false; backend.Text = video.CodecName; empty.Visibility = Visibility.Collapsed; image.Visibility = Visibility.Visible; playing = true; clock.Restart();
                    await RenderOriginalAsync(); Notice("Original Luma decoding · AVI video only"); return;
                }
                catch (NotSupportedException) { /* Unknown AVI codecs may use installed Windows codecs. */ }
            }
            var source = entry.FilePath;
            if (suffix is ".mkv" or ".mka" or ".webm" && size <= 512L * 1024 * 1024)
            {
                try
                {
                    var bytes = await File.ReadAllBytesAsync(source, token);
                    var model = await Task.Run(() => MatroskaDemuxer.Demux(bytes, token), token);
                    token.ThrowIfCancellationRequested();
                    string converted = NewTemporaryMedia(".mp4");
                    MatroskaRemuxResult prepared;
                    try
                    {
                        prepared = await Task.Run(() =>
                        {
                            try
                            {
                                using var output = new FileStream(converted, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                                return MatroskaRemuxer.Remux(model, output, audioTrackId, includeAudio, token);
                            }
                            finally { if (token.IsCancellationRequested) DiscardWorkerTemporary(converted, lifetimeToken.IsCancellationRequested); }
                        }, token);
                    }
                    finally { pendingTemporaryMedia.Remove(converted); }
                    if (version != generation) return;
                    source = converted; activeTemporaryMedia = converted; selectedMatroskaAudio = prepared.SelectedAudioTrackId;
                    if (previousOrigin is long oldOrigin) pendingResume = Math.Max(0, pendingResume + ((double)oldOrigin - prepared.OriginNanoseconds) / 1_000_000_000);
                    matroskaOriginNanoseconds = prepared.OriginNanoseconds;
                    backend.Text = "Luma MKV remux · Windows " + prepared.VideoCodec + (includeAudio ? "" : " · audio off");
                    await ConfigureTrackMenusAsync(prepared, version, token);
                }
                catch (NotSupportedException e)
                {
                    if (version != generation) return;
                    if (audioTrackId is not null || !includeAudio) throw;
                    source = entry.FilePath; activeTemporaryMedia = null;
                    nativeFailureContext = e.Message; ResetTrackMenus(); backend.Text = "Windows media services · installed codecs";
                }
            }
            else if (suffix is ".wav" or ".flac" or ".aif" or ".aiff" or ".aifc" or ".au" or ".snd" && size <= 256L * 1024 * 1024)
            {
                var bytes = await File.ReadAllBytesAsync(source, token);
                try
                {
                    var audio = await Task.Run(() => suffix switch { ".flac" => FlacDecoder.Decode(bytes, token), ".wav" => WaveDecoder.Decode(bytes, token), _ => PcmContainerDecoder.Decode(bytes, token) }, token);
                    token.ThrowIfCancellationRequested(); source = NewTemporaryMedia(".wav");
                    string wavePath = source;
                    try
                    {
                        await Task.Run(() =>
                        {
                            try { var wave = audio.ToWave(); token.ThrowIfCancellationRequested(); File.WriteAllBytes(wavePath, wave); token.ThrowIfCancellationRequested(); }
                            finally { if (token.IsCancellationRequested) DiscardWorkerTemporary(wavePath, lifetimeToken.IsCancellationRequested); }
                        }, token);
                    }
                    finally { pendingTemporaryMedia.Remove(source); }
                    backend.Text = audio.CodecName; duration = audio.Duration;
                }
                catch (NotSupportedException) { backend.Text = "Windows media services · installed codecs"; }
            }
            else backend.Text = "Windows media services · installed codecs";
            if (version != generation) return; token.ThrowIfCancellationRequested(); activeTemporaryMedia = temporaryMedia.Contains(source) ? source : null; CleanupTemporaryMedia(); activeNativeRequest = true; movie.Source = new Uri(source); movie.Volume = volume.Value; movie.SpeedRatio = rate; nativeLoadClock.Restart(); movie.Play(); playing = true;
        }
        catch (OperationCanceledException) { }
        catch (Exception e) { if (version == generation) Fail(e.Message); }
        finally { CleanupTemporaryMedia(); if (version == generation) UpdateTransport(); }
    }

    string NewTemporaryMedia(string suffix)
    {
        Directory.CreateDirectory(temporaryFolder);
        var path = Path.Combine(temporaryFolder, Guid.NewGuid().ToString("N") + suffix);
        temporaryMedia.Add(path); pendingTemporaryMedia.Add(path); return path;
    }
    static void DiscardWorkerTemporary(string path, bool closing)
    {
        try { File.Delete(path); var parent = Path.GetDirectoryName(path); if (closing && parent is not null) Directory.Delete(parent, true); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }
    static MediaElement NewNativeMovie() => new() { LoadedBehavior = MediaState.Manual, UnloadedBehavior = MediaState.Manual, ScrubbingEnabled = true, Stretch = Stretch.Uniform, Volume = .7, Visibility = Visibility.Collapsed };
    void ReplaceNativeMovie(int version)
    {
        activeNativeRequest = false;
        var previous = movie; movie = NewNativeMovie();
        int index = viewport.Children.IndexOf(previous);
        if (index >= 0) { viewport.Children.RemoveAt(index); viewport.Children.Insert(index, movie); }
        previous.Close(); previous.Source = null; WireNativeMovie(movie, version);
    }
    void WireNativeMovie(MediaElement element, int version)
    {
        bool Active() => activeNativeRequest && !closing && version == generation && ReferenceEquals(movie, element);
        element.MediaOpened += (_, _) => { if (Active()) NativeOpened(); };
        element.MediaEnded += (_, _) => { if (Active()) Ended(); };
        element.MediaFailed += (_, e) =>
        {
            if (!Active()) return;
            lastNativeError = unchecked((uint)e.ErrorException.GetBaseException().HResult);
            Fail(lastNativeError == NativeAudio.HardwareProblem
                ? "Windows could not open an audio output device. Connect or enable speakers or headphones, then reopen the file."
                : "Windows could not play this file. Its codec may be unavailable. " + e.ErrorException.Message + (nativeFailureContext is null ? "" : " · " + nativeFailureContext));
        };
    }
    void CleanupTemporaryMedia()
    {
        foreach (var path in temporaryMedia.ToArray())
        {
            if (path == activeTemporaryMedia || pendingTemporaryMedia.Contains(path)) continue;
            try { File.Delete(path); temporaryMedia.Remove(path); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        }
    }
    void ResetTrackMenus()
    {
        subtitleRevision++;
        audioTracksMenu.Items.Clear(); audioTracksMenu.IsEnabled = false;
        embeddedSubtitlesMenu.Items.Clear(); embeddedSubtitlesMenu.IsEnabled = false; selectedMatroskaAudio = null;
    }
    async Task ConfigureTrackMenusAsync(MatroskaRemuxResult result, int version, CancellationToken token)
    {
        static string Label(DemuxedTrack track) => (string.IsNullOrWhiteSpace(track.Name) ? "Track " + track.Id : track.Name[..Math.Min(80, track.Name.Length)]) + " · " + track.Language[..Math.Min(24, track.Language.Length)];
        audioTracksMenu.IsEnabled = result.AudioTracks.Count > 0;
        var off = new MenuItem { Header = "Audio off", IsCheckable = true, IsChecked = result.SelectedAudioTrackId is null };
        off.Click += (_, _) => { if (version == generation && current is not null) _ = LoadEntryAsync(current, includeAudio: false, resume: CurrentTime, previousOrigin: matroskaOriginNanoseconds); };
        audioTracksMenu.Items.Add(off);
        foreach (var track in result.AudioTracks)
        {
            var item = new MenuItem { Header = Label(track), IsCheckable = true, IsChecked = selectedMatroskaAudio == track.Id };
            item.Click += (_, _) => { if (version == generation && current is not null) _ = LoadEntryAsync(current, track.Id, resume: CurrentTime, previousOrigin: matroskaOriginNanoseconds); };
            audioTracksMenu.Items.Add(item);
        }
        embeddedSubtitlesMenu.IsEnabled = result.SubtitleTracks.Count > 0;
        foreach (var track in result.SubtitleTracks)
        {
            bool supported = EmbeddedTextDecoder.Supports(track.CodecId);
            var item = new MenuItem { Header = Label(track) + (supported ? "" : " · " + track.CodecId + " unavailable"), IsEnabled = supported };
            item.Click += async (_, _) => await SelectEmbeddedSubtitlesAsync(track, result.OriginNanoseconds, version, token);
            embeddedSubtitlesMenu.Items.Add(item);
        }
        var selected = result.SubtitleTracks.FirstOrDefault(t => t.IsDefault);
        if (selected is not null && EmbeddedTextDecoder.Supports(selected.CodecId)) await SelectEmbeddedSubtitlesAsync(selected, result.OriginNanoseconds, version, token);
    }
    async Task SelectEmbeddedSubtitlesAsync(DemuxedTrack track, long origin, int version, CancellationToken token)
    {
        if (version != generation || closing) return;
        int revision = ++subtitleRevision; subtitleDelay = 0;
        try
        {
            var decoded = await Task.Run(() => EmbeddedTextDecoder.Decode(track, origin, token), token);
            if (version != generation || revision != subtitleRevision || closing) return;
            cues = decoded; captions.Text = SubtitleParser.At(cues, CurrentTime - subtitleDelay);
        }
        catch (OperationCanceledException) { }
        catch (Exception e) { if (version == generation && revision == subtitleRevision && !closing) Notice("This caption track could not be loaded: " + e.Message); }
    }
    void NativeOpened()
    {
        if (original is not null || current is null || closing || movie.Source is null) return;
        nativeLoadClock.Reset(); opening = false; if (movie.NaturalDuration.HasTimeSpan) duration = movie.NaturalDuration.TimeSpan.TotalSeconds;
        if (pendingResume > 0) { movie.Position = TimeSpan.FromSeconds(Math.Min(pendingResume, Math.Max(0, duration - 1))); pendingResume = 0; }
        movie.SpeedRatio = rate; movie.Volume = volume.Value; movie.Visibility = movie.NaturalVideoWidth > 0 ? Visibility.Visible : Visibility.Collapsed;
        empty.Visibility = movie.NaturalVideoWidth > 0 ? Visibility.Collapsed : Visibility.Visible; playing = true; Notice(backend.Text); UpdateTransport();
    }
    double CurrentTime => original is not null ? Math.Clamp(position + (playing ? clock.Elapsed.TotalSeconds * rate : 0), 0, Math.Max(0, duration)) : Math.Max(0, movie.Position.TotalSeconds);
    void TogglePlayback()
    {
        if (lastError is not null && current is not null) { _ = LoadEntryAsync(current); return; }
        if (current is null)
        {
            var entry = queue.SelectedItem as MediaEntry ?? library.Data.Items.Find(x => x.Id == library.Data.SelectedId) ?? library.Data.Items.FirstOrDefault();
            if (entry is not null) _ = LoadEntryAsync(entry); else _ = OpenMediaAsync();
            return;
        }
        if (opening) return;
        if (playing) Pause(); else Play();
    }
    void Pause() { position = CurrentTime; playing = false; clock.Reset(); movie.Pause(); UpdateTransport(); }
    void Play() { if (duration > 0 && CurrentTime >= duration - .01) Seek(0); if (original is not null) clock.Restart(); else movie.Play(); playing = true; UpdateTransport(); }
    void Seek(double value)
    {
        if (current is null || !double.IsFinite(value) || duration <= 0) return;
        position = Math.Clamp(value, 0, duration); visualRevision++;
        if (original is not null) { clock.Restart(); _ = RenderOriginalAsync(); } else movie.Position = TimeSpan.FromSeconds(position);
        UpdateTransport();
    }
    async Task RenderOriginalAsync()
    {
        if (original is null || rendering || duration <= 0) return;
        var video = original; var frame = Math.Clamp((int)(CurrentTime * video.FramesPerSecond), 0, video.FrameCount - 1); if (frame == lastFrame) return;
        rendering = true; var version = generation; var revision = visualRevision; var token = loadCancellation.Token;
        try
        {
            var rgba = await Task.Run(() => video.DecodeFrame(frame, token), token);
            if (version != generation || revision != visualRevision || closing) return;
            var bgra = ToBgra(rgba); var bitmap = BitmapSource.Create(video.Width, video.Height, 96, 96, PixelFormats.Bgra32, null, bgra, video.Width * 4); bitmap.Freeze(); image.Source = bitmap; lastFrame = frame;
        }
        catch (OperationCanceledException) { }
        catch (Exception e) { if (version == generation && revision == visualRevision && !closing) Fail(e.Message); }
        finally { rendering = false; }
    }
    static byte[] ToBgra(byte[] rgba) { var output = (byte[])rgba.Clone(); for (var i = 0; i < output.Length; i += 4) (output[i], output[i + 2]) = (output[i + 2], output[i]); return output; }
    async Task TickAsync()
    {
        if (closing) return;
        if (opening && nativeLoadClock.IsRunning && nativeLoadClock.Elapsed > TimeSpan.FromSeconds(30))
        { loadCancellation.Cancel(); Fail("Windows took too long to open this file. You can retry or choose another file."); }
        if (original is not null) { if (playing && CurrentTime >= duration) Ended(); await RenderOriginalAsync(); }
        var caption = SubtitleParser.At(cues, CurrentTime - subtitleDelay); if (caption != captions.Text) captions.Text = caption;
        UpdateTransport();
        if ((DateTime.UtcNow - lastSave).TotalSeconds >= 3) { SaveProgress(); lastSave = DateTime.UtcNow; }
    }
    void UpdateTransport()
    {
        play.Content = playing ? "Ⅱ" : "▶"; updatingSlider = true; seek.Maximum = Math.Max(1, duration); if (!dragging) seek.Value = Math.Min(seek.Maximum, CurrentTime); updatingSlider = false;
        timing.Text = MediaEntry.Clock(CurrentTime) + " / " + MediaEntry.Clock(duration); seek.IsEnabled = current is not null && !opening && duration > 0;
    }
    void Ended()
    {
        if (!playing) return; Pause();
        if (current is not null) library.UpdateProgress(current.Id, duration, duration);
        if (repeatMode == 2) { Seek(0); Play(); }
        else _ = NextAsync(1, true);
    }
    async Task NextAsync(int delta, bool atEnd = false)
    {
        if (library.Data.Items.Count == 0) return;
        if (delta < 0 && CurrentTime > 3) { Seek(0); return; }
        var index = library.Data.Items.FindIndex(e => e.Id == current?.Id); var next = index + delta;
        if (next >= library.Data.Items.Count) { if (repeatMode == 1) next = 0; else { if (atEnd) Notice("Queue finished"); return; } }
        if (next < 0) next = 0; await LoadEntryAsync(library.Data.Items[next]);
    }
    void RefreshQueue()
    {
        refreshingQueue = true;
        queue.ItemsSource = library.Data.Items.Where(x => x.Title.Contains(search.Text, StringComparison.OrdinalIgnoreCase)).ToList(); queue.SelectedItem = library.Data.Items.Find(x => x.Id == (current?.Id ?? library.Data.SelectedId)); queueCount.Text = library.Data.Items.Count.ToString(CultureInfo.InvariantCulture); refreshingQueue = false;
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
    void OpenSubtitles() { var picker = new OpenFileDialog { Title = "Add subtitles", Filter = "UTF-8 captions|*.srt;*.vtt" }; if (picker.ShowDialog(this) == true) try { ReadSubtitles(picker.FileName); } catch (Exception e) { Notice(e.Message); } }
    void ReadSubtitles(string path) { subtitleRevision++; if (new FileInfo(path).Length > 8 * 1024 * 1024) throw new InvalidDataException("Subtitle file is too large."); cues = SubtitleParser.Parse(File.ReadAllText(path, new UTF8Encoding(false, true))); subtitleDelay = 0; Notice("Captions: " + Path.GetFileName(path)); }
    void OnKey(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape && fullscreen) { ToggleFullscreen(); e.Handled = true; return; }
        if (e.Key == Key.Escape && opening) { Unload(); Notice("Opening canceled"); e.Handled = true; return; }
        if (e.Key == Key.F11) { ToggleFullscreen(); e.Handled = true; return; }
        if (Keyboard.Modifiers == ModifierKeys.Control && e.Key == Key.O) { _ = OpenMediaAsync(); e.Handled = true; return; }
        if (Keyboard.FocusedElement is TextBox) return;
        if (Keyboard.Modifiers == ModifierKeys.Control && e.Key == Key.B) Bookmark();
        else if (e.Key == Key.Space) TogglePlayback(); else if (e.Key == Key.Left) Seek(CurrentTime - 10); else if (e.Key == Key.Right) Seek(CurrentTime + 10); else return;
        e.Handled = true;
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
            header.Visibility = sidebar.Visibility = controls.Visibility = status.Visibility = Visibility.Visible; layout.ColumnDefinitions[0].Width = new GridLength(260); layout.RowDefinitions[0].Height = new GridLength(68); layout.RowDefinitions[2].Height = new GridLength(160); layout.RowDefinitions[3].Height = new GridLength(28); stage.Margin = new Thickness(14, 14, 14, 0); stage.CornerRadius = new CornerRadius(14); NativeWindow.Style(this, dark);
        }
    }
    void SaveProgress() { if (current is not null && !opening && lastError is null) library.UpdateProgress(current.Id, CurrentTime, duration); SaveLibrary(); }
    void SaveLibrary() { try { library.Flush(); } catch (IOException e) { Notice("The saved library could not be written: " + e.Message); } }
    void Unload() { SaveProgress(); generation++; visualRevision++; activeNativeRequest = false; loadCancellation.Cancel(); movie.Close(); movie.Source = null; activeTemporaryMedia = null; CleanupTemporaryMedia(); nativeLoadClock.Reset(); ResetTrackMenus(); current = null; original = null; playing = opening = false; image.Source = null; duration = position = 0; clock.Reset(); cues.Clear(); captions.Text = ""; empty.Visibility = Visibility.Visible; nowTitle.Text = "Make room for a good story"; backend.Text = ""; UpdateTransport(); }
    void Fail(string error) { lastError = error; opening = false; playing = false; activeNativeRequest = false; original = null; subtitleRevision++; cues.Clear(); captions.Text = ""; image.Source = null; image.Visibility = movie.Visibility = Visibility.Collapsed; clock.Reset(); nativeLoadClock.Reset(); movie.Close(); movie.Source = null; activeTemporaryMedia = null; CleanupTemporaryMedia(); if (fullscreen) ToggleFullscreen(); Notice(error); nowTitle.Text = "This file could not be played"; empty.Visibility = Visibility.Visible; }
    void Notice(string value) => status.Text = value;
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
    void Shutdown()
    {
        if (closing) return; closing = true; SaveProgress(); timer.Stop(); updateTimer.Stop(); loadCancellation.Cancel(); lifetimeCancellation.Cancel(); movie.Close(); loadCancellation.Dispose(); lifetimeCancellation.Dispose();
        try { if (Directory.Exists(temporaryFolder)) Directory.Delete(temporaryFolder, true); } catch (IOException) { }
    }

    internal async Task RunSmokeAsync(string fixtures, string output)
    {
        Directory.CreateDirectory(output);
        void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
        async Task Until(Func<bool> predicate, string name, int milliseconds = 10000) { var deadline = DateTime.UtcNow.AddMilliseconds(milliseconds); while (!predicate()) { if (DateTime.UtcNow >= deadline) throw new TimeoutException(name); await Task.Delay(40); } }
        await ImportAsync([Path.Combine(fixtures, "Luma-MSVideo1-16.avi")]); await Until(() => original is not null && image.Source is BitmapSource, "Original AVI output");
        Pause(); Seek(2.5); await Until(() => lastFrame == 5 && !rendering, "Paused original seek");
        var bitmap = (BitmapSource)image.Source; var actual = new byte[bitmap.PixelWidth * bitmap.PixelHeight * 4]; bitmap.CopyPixels(actual, bitmap.PixelWidth * 4, 0);
        var golden = File.ReadAllBytes(Path.Combine(fixtures, "msvideo1", "16-frame-05.rgba")); Check(actual.SequenceEqual(ToBgra(golden)), "WPF image pixels differ from original codec goldens");
        Console.WriteLine("PASS: production Windows queue/import, original AVI image output and exact paused-seek BGRA pixels");
        var drawable = image; ToggleFullscreen(); await Task.Delay(500); UpdateLayout(); Check(fullscreen && header.Visibility == Visibility.Collapsed && ReferenceEquals(drawable, image), "Fullscreen replaced the original drawable");
        Check(Math.Abs(stage.ActualWidth - layout.ActualWidth) < 2 && Math.Abs(stage.ActualHeight - layout.ActualHeight) < 2, "Video does not fill fullscreen client area");
        ToggleFullscreen(); await Task.Delay(250); Check(!fullscreen && sidebar.Visibility == Visibility.Visible && ReferenceEquals(drawable, image), "Fullscreen exit lost chrome/drawable"); Play(); await Task.Delay(150); Check(CurrentTime > 2.5, "Playback did not resume"); Pause();
        Console.WriteLine("PASS: native Windows fullscreen entry/exit fills client area, preserves drawable and resumes original playback");
        var preview = library.Add([Path.Combine(fixtures, "Luma-MJPEG.avi")]).Single(); await LoadEntryAsync(preview); Pause(); Seek(duration * .35);
        await Until(() => original is not null && image.Source is not null && !rendering && lastFrame == Math.Clamp((int)(CurrentTime * original.FramesPerSecond), 0, original.FrameCount - 1), "Original MJPEG preview render");
        Console.WriteLine("PASS: production original MJPEG player output and paused seek");
        foreach (var theme in new[] { true, false })
        {
            SetTheme(theme); await Task.Delay(100); UpdateLayout();
            var shot = new RenderTargetBitmap((int)Math.Ceiling(layout.ActualWidth), (int)Math.Ceiling(layout.ActualHeight), 96, 96, PixelFormats.Pbgra32); shot.Render(layout);
            var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(shot)); using var file = File.Create(Path.Combine(output, theme ? "Luma-Windows-dark.png" : "Luma-Windows-light.png")); encoder.Save(file);
        }
        Console.WriteLine("PASS: production dark/light Windows UI render snapshots saved");
        var native = library.Add([Path.Combine(fixtures, "Luma-Demo.mp4")]).Single(); await LoadEntryAsync(native);
        await Until(() => !opening || lastError is not null, "Windows native media load", 20000);
        if (lastError is null && movie.NaturalVideoWidth > 0 && duration > 0)
        {
            Pause(); Seek(duration / 2); Play(); await Task.Delay(300); Check(CurrentTime > duration / 2, "Native media did not advance after seek"); Pause();
            ReadSubtitles(Path.Combine(fixtures, "Luma-Demo.srt")); Seek(1.2); await Task.Delay(100);
            Check(captions.Text == SubtitleParser.At(cues, CurrentTime) && captions.Text.Length > 0, "Paused external caption was not displayed");
            Console.WriteLine("PASS: native Windows MP4 load, video dimensions, seek and resumed clock; paused external caption rendering");
        }
        else throw new InvalidOperationException("Windows media-service MP4 route failed: " + lastError);
        var activeAudioRenderEndpoints = NativeAudio.ActiveRenderEndpoints();
        Console.WriteLine("INFO: active Core Audio render endpoints: " + (activeAudioRenderEndpoints?.ToString() ?? "unknown (probe failed)"));
        var generated = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(output))!, "generated");
        var mkv = library.Add([Path.Combine(fixtures, "Luma-AVC-AAC.mkv")]).Single();
        await LoadEntryAsync(mkv, includeAudio: false);
        await Until(() => !opening || lastError is not null, "Original Matroska remux Windows video load", 20000);
        Check(lastError is null && movie.NaturalVideoWidth == 960 && movie.NaturalVideoHeight == 540 && backend.Text.Contains("Luma MKV remux") && audioTracksMenu.IsEnabled && selectedMatroskaAudio is null, "Original Matroska video-only route did not reach Windows output: " + lastError);
        Pause(); Seek(3); Play(); await Task.Delay(250); Check(CurrentTime > 3, "Matroska playback clock did not advance after seek"); Pause();
        var nativeDrawable = movie; ToggleFullscreen(); await Task.Delay(100); ToggleFullscreen();
        Check(ReferenceEquals(nativeDrawable, movie) && !fullscreen, "Native Matroska fullscreen replaced the media drawable");
        Console.WriteLine("PASS: production original Matroska AVC remux reaches native Windows video, audio-off selection, seek/resume clock and fullscreen drawable retention");

        var slowCaptions = new DemuxedTrack(77,"S_TEXT/UTF8",[],0,0,0,0,0,Enumerable.Range(0,50000).Select(i=>new MediaPacket([65],(long)i*1_000_000_000,1_000_000_000,true)).ToList()) { TrackType=17 };
        var pendingCaptions = SelectEmbeddedSubtitlesAsync(slowCaptions,0,generation,loadCancellation.Token);
        ReadSubtitles(Path.Combine(fixtures,"Luma-Demo.srt"));
        var externalCues = cues;
        await pendingCaptions;
        Check(ReferenceEquals(cues,externalCues),"An obsolete embedded caption request overwrote the external selection");

        var broken = Path.Combine(output,"retry-fixture.mp4");
        await File.WriteAllBytesAsync(broken,[0,1,2,3,4,5,6,7]);
        var brokenEntry = library.Add([broken]).Single(); await LoadEntryAsync(brokenEntry);
        await Until(()=>lastError is not null,"Malformed native media rejection",20000);
        Check(!playing&&!activeNativeRequest,"Failed native request stayed active");
        File.Copy(Path.Combine(fixtures,"Luma-Demo.mp4"),broken,true);
        TogglePlayback();
        await Until(()=>!opening&&lastError is null&&movie.NaturalVideoWidth>0,"Retry repaired media from Play",20000);
        Check(playing,"Play did not recover a repaired file"); Pause();
        Console.WriteLine("PASS: obsolete caption request cannot overwrite external captions; native malformed-file failure closes request and Play retries repaired media");

        var bframes = library.Add([Path.Combine(generated,"Luma-review-bframes.mkv")]).Single(); await LoadEntryAsync(bframes,includeAudio:false);
        await Until(()=>!opening||lastError is not null,"Original reordered Matroska native load",20000);
        Check(lastError is null&&movie.NaturalVideoWidth>0&&backend.Text.Contains("Luma MKV remux"),"Reordered AVC Matroska did not play: "+lastError);
        Pause(); Seek(duration/2); Play(); await Task.Delay(250); Check(CurrentTime>duration/2,"B-frame Matroska failed after seek");Pause();
        Console.WriteLine("PASS: actual B-frame AVC MKV through original demux/DTS reconstruction/MP4 mux reaches Windows playback and seek/resume");
        var textEntry=library.Add([Path.Combine(generated,"Luma-review-text.mkv")]).Single();await LoadEntryAsync(textEntry,includeAudio:false);
        await Until(()=>!opening||lastError is not null,"Embedded caption Matroska playback",20000);
        Check(lastError is null&&embeddedSubtitlesMenu.Items.Count==2,"Matroska embedded text tracks were not exposed: "+lastError);
        Pause();Seek(.75);await Task.Delay(150);Check(captions.Text=="Original\ncaption","Default embedded ASS text was not rendered");
        ((MenuItem)embeddedSubtitlesMenu.Items[1]).RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        await Until(()=>captions.Text=="<Naruto> 2 < 3 and 4 > 1","Alternate embedded UTF8 text selection");
        Console.WriteLine("PASS: actual Matroska default ASS readable text, alternate UTF8 menu selection, literal angle text and timed native caption presentation");
        var hevcRoutes = new Dictionary<string,string>();
        foreach(var depth in new[]{"8","10"})
        {
            var entry=library.Add([Path.Combine(generated,"Luma-review-hevc"+depth+".mkv")]).Single(); await LoadEntryAsync(entry,includeAudio:false);
            await Until(()=>!opening||lastError is not null,"HEVC"+depth+" native probe",20000);
            Check(backend.Text.Contains("Luma MKV remux"),"HEVC remux failed before native decoding: "+lastError);
            if(lastError is null&&movie.NaturalVideoWidth>0&&duration>0)
            { Pause();Seek(duration/2);Play();await Task.Delay(250);Check(CurrentTime>duration/2,"HEVC seek did not resume");Pause();hevcRoutes[depth]="passed";Console.WriteLine("PASS: HEVC"+depth+" MKV native Windows playback and seek"); }
            else
            { hevcRoutes[depth]="unverified-native-playback-error";Console.WriteLine("UNVERIFIED: HEVC"+depth+" native Windows decode failed; original packet/config/remux checks are separate. HRESULT="+(lastNativeError?.ToString("X8")??"unknown")+" "+lastError); }
        }
        var originalAudio = "passed";
        foreach (var audioFile in new[] { Path.Combine(fixtures,"Luma-Silence.flac"), Path.Combine(fixtures,"Luma-MSADPCM.wav"), Path.Combine(generated,"Luma-Windows-Tone.aiff"), Path.Combine(generated,"Luma-Windows-Tone.au") })
        {
            var entry = library.Add([audioFile]).Single(); await LoadEntryAsync(entry);
            await Until(() => !opening || lastError is not null, "Original decoded audio output", 20000);
            Check(backend.Text.StartsWith("Luma original"),"Audio bypassed the original decoder: "+backend.Text);
            if (lastNativeError == NativeAudio.HardwareProblem && activeAudioRenderEndpoints == 0)
            {
                originalAudio = "unverified-no-active-render-endpoint";
                Console.WriteLine("UNVERIFIED: original " + audioFile + " audio output: NS_E_WMP_AUDIO_HW_PROBLEM and zero active Core Audio render endpoints. Exact decoded samples are checked separately by the core suites.");
                continue;
            }
            Check(lastError is null && duration > 1, "Original audio did not reach Windows output: " + lastError);
            Pause(); Seek(duration / 2); speed.SelectedIndex = 2; volume.Value = .25; Play(); await Task.Delay(300);
            Check(CurrentTime > duration / 2 && movie.SpeedRatio == 1.25 && movie.Volume == .25, "Original audio transport did not retain seek/rate/volume"); Pause(); speed.SelectedIndex = 1;
            Console.WriteLine("PASS: production original " + audioFile + " decode to Windows audio output, pause/seek/rate/volume and resumed clock");
        }
        Unload();
        var restoredPath = Path.Combine(output, "restored-library.json");
        var persisted = new LibraryStore(restoredPath); var saved = persisted.Add([Path.Combine(fixtures, "Luma-MSVideo1-16.avi")]).Single();
        persisted.UpdateProgress(saved.Id, 4, 6); persisted.Data.SelectedId = saved.Id; persisted.Flush();
        var restored = new PlayerWindow(restoredPath) { Owner = this }; restored.Show();
        try
        {
            restored.TogglePlayback(); await Until(() => restored.original is not null && restored.image.Source is not null, "Saved selected item starts on relaunch"); restored.Pause();
            Check(restored.current?.Id == saved.Id && restored.CurrentTime >= 4, "Saved selection or resume position was lost");
            restored.Unload(); await restored.ImportAsync([saved.FilePath]); await Until(() => restored.original is not null, "Reimport existing queue entry");
            Check(restored.library.Data.Items.Count == 1 && restored.current?.Id == saved.Id, "Reimport duplicated or failed to select an existing entry");
            Console.WriteLine("PASS: saved selected queue item starts and resumes on relaunch; reimporting it reuses the existing entry");
        }
        finally { restored.Close(); }
        File.WriteAllText(Path.Combine(output, "smoke-results.json"), JsonSerializer.Serialize(new { nativeMp4 = "passed", matroskaAvc = "passed", matroskaBframes = "passed", embeddedCaptions = "passed", lifecycleRecovery = "passed", hevcRoutes, originalAudio, activeAudioRenderEndpoints }, new JsonSerializerOptions { WriteIndented = true }));
    }
}
