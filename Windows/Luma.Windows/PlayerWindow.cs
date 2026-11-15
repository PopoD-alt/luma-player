using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net.Http;
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

namespace Luma.WindowsApp;

internal sealed class PlayerWindow : Window
{
    readonly LibraryStore library;
    readonly Grid layout = new(), viewport = new();
    readonly StackPanel empty = new() { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
    readonly TextBlock nowTitle = new() { Text = "Make room for a good story", FontSize = 20, FontWeight = FontWeights.SemiBold, TextAlignment = TextAlignment.Center };
    readonly TextBlock status = new() { FontSize = 11, Text = "Windows review · ready", Margin = new Thickness(20, 0, 20, 0), VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
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
    readonly Button shareButton = new() { Content = "Share playlist", ToolTip = "Local sharing is off until you start it" };
    readonly Border stage = new() { Margin = new Thickness(14, 14, 14, 0), CornerRadius = new CornerRadius(14), Background = Brushes.Black, ClipToBounds = true };
    readonly MenuItem automatic = new() { Header = "Automatically check for updates", IsCheckable = true };
    readonly MenuItem audioTracksMenu = new() { Header = "Audio tracks", IsEnabled = false };
    readonly MenuItem embeddedSubtitlesMenu = new() { Header = "Caption tracks", IsEnabled = false };
    readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromMilliseconds(100) };
    readonly DispatcherTimer updateTimer = new() { Interval = TimeSpan.FromMinutes(1) };
    readonly CancellationTokenSource lifetimeCancellation = new();
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
    bool playing, opening, dragging, refreshingQueue, updatingSlider, fullscreen, dark = true, checkingUpdate, closing, dummyAudioForSmoke;
    Rect normalBounds; WindowState previousState; bool previousTopmost;
    DateTime lastSave = DateTime.UtcNow;

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
        Item("Format support", () => ShowDocument("FORMAT_SUPPORT.md", "Format support")); Item("Playback diagnostics…", ShowDiagnostics);
        Item("Share playlist locally…", ShowSharing); Item("Check for updates…", () => _ = PollUpdatesAsync(true)); menu.Items.Add(automatic); menu.Items.Add(new Separator());
        menu.Items.Add(audioTracksMenu); menu.Items.Add(embeddedSubtitlesMenu);
        Item("Play original video demo", () => _ = ImportAsync([Path.Combine(AppContext.BaseDirectory, "Demos", "Luma-MSVideo1-16.avi")]));
        Item("Saved bookmarks", ShowBookmarks); Item("Caption timing −0.25s", () => ChangeCaptionDelay(-.25)); Item("Caption timing +0.25s", () => ChangeCaptionDelay(.25));
        Item("Turn captions off", () => TryControl(() => vlc?.SelectSubtitle(-1)));
        menu.Items.Add(new Separator()); Item("About Luma Player", () => MessageBox.Show(this, "Luma Player\n0.6.1 · Windows review build " + currentBuild + "\n\nMade by sdas\nPlayback powered by separately installed libVLC3.\nLocal sharing stays off until you start it.\nThis review build is not digitally signed by a trusted Windows publisher.", "About Luma Player"));
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
        SaveProgress(); ++generation; DisposePlayer(); lastError = null; duration = 0; subtitleDelay = 0; playing = false; opening = true;
        current = entry; library.Data.SelectedId = entry.Id; pendingResume = resume ?? (entry.ResumeTime > 3 ? entry.ResumeTime : 0); RefreshQueue();
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
                RefreshTracks(); Notice("Playing " + entry.Title); UpdateTransport();
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
        if (lastError is not null && current is not null) { _ = LoadEntryAsync(current); return; }
        if (current is null) { var entry = queue.SelectedItem as MediaEntry ?? library.Data.Items.Find(i => i.Id == library.Data.SelectedId) ?? library.Data.Items.FirstOrDefault(); if (entry is not null) _ = LoadEntryAsync(entry); else _ = OpenMediaAsync(); return; }
        if (opening) return; if (playing) Pause(); else Play();
    }
    void Pause() { vlc?.Pause(); playing = false; UpdateTransport(); }
    void Play() { if (duration > 0 && CurrentTime >= duration - .05) vlc?.Seek(0); vlc?.Play(); playing = vlc is not null; UpdateTransport(); }
    void Seek(double value) { if (current is null || !double.IsFinite(value) || duration <= 0) return; vlc?.Seek(Math.Clamp(value, 0, duration)); UpdateTransport(); }
    void Tick()
    {
        if (closing) return;
        if (opening && openingClock.Elapsed > TimeSpan.FromSeconds(30)) Fail("libVLC took too long to open this file. Retry or choose another file.");
        if (vlc is not null && !opening)
        {
            double latest = vlc.Duration; if (latest > 0) duration = latest;
            bool video = vlc.NaturalVideoWidth > 0; vlc.Visibility = video ? Visibility.Visible : Visibility.Hidden; empty.Visibility = video ? Visibility.Collapsed : Visibility.Visible;
        }
        UpdateTransport(); RefreshSharingUI();
        if ((DateTime.UtcNow - lastSave).TotalSeconds >= 3) { SaveProgress(); lastSave = DateTime.UtcNow; }
    }
    void UpdateTransport()
    {
        play.Content = playing ? "Ⅱ" : "▶"; updatingSlider = true; seek.Maximum = Math.Max(1, duration); if (!dragging) seek.Value = Math.Min(seek.Maximum, CurrentTime); updatingSlider = false;
        timing.Text = MediaEntry.Clock(CurrentTime) + " / " + MediaEntry.Clock(duration); seek.IsEnabled = current is not null && !opening && duration > 0;
    }
    void Ended() { if (!playing) return; playing = false; if (current is not null) library.UpdateProgress(current.Id, duration, duration); if (repeatMode == 2) { Seek(0); Play(); } else _ = NextAsync(1, true); }
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
        if (Keyboard.FocusedElement is TextBox) return;
        if (Keyboard.Modifiers == ModifierKeys.Control && e.Key == Key.B) Bookmark(); else if (e.Key == Key.Space) TogglePlayback(); else if (e.Key == Key.Left) Seek(CurrentTime - 10); else if (e.Key == Key.Right) Seek(CurrentTime + 10); else return; e.Handled = true;
    }
    void SaveProgress() { if (current is not null && !opening && lastError is null) library.UpdateProgress(current.Id, CurrentTime, duration); SaveLibrary(); PushSharedPlaylist(); }
    void SaveLibrary() { try { library.Flush(); } catch (IOException error) { Notice("The saved library could not be written: " + error.Message); } }
    void Unload() { SaveProgress(); ++generation; DisposePlayer(); current = null; playing = opening = false; duration = 0; openingClock.Reset(); empty.Visibility = Visibility.Visible; nowTitle.Text = "Make room for a good story"; backend.Text = ""; UpdateTransport(); }
    void Fail(string error) { lastError = error; opening = playing = false; ++generation; DisposePlayer(); openingClock.Reset(); if (fullscreen) ToggleFullscreen(); Notice(error); nowTitle.Text = "This file could not be played"; empty.Visibility = Visibility.Visible; }
    void Notice(string message) => status.Text = message;
    void ShowDiagnostics()
    {
        var details = JsonSerializer.Serialize(new { engine = "libVLC3", version = VlcPlayer.RuntimeVersion, runtime = VlcPlayer.RuntimePath, displayedPictures = vlc?.DisplayedPictures, decodedVideo = vlc?.DecodedVideo, decodedAudio = vlc?.DecodedAudio, playedAudioBuffers = vlc?.PlayedAudioBuffers,
            audioTracks = vlc?.AudioTracks, captionTracks = vlc?.SubtitleTracks, sharing = sharing?.IsRunning == true, discovery = discovery?.Status, lastError }, new JsonSerializerOptions { WriteIndented = true });
        var text = new TextBox { Text = details, IsReadOnly = true, TextWrapping = TextWrapping.Wrap, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Margin = new Thickness(16) };
        new Window { Owner = this, Title = "Local playback diagnostics", Width = 760, Height = 540, Content = text, WindowStartupLocation = WindowStartupLocation.CenterOwner }.Show();
    }
    void PushSharedPlaylist() { sharing?.SetPlaylist(library.Data.Items.Select(i => new SharedMediaItem(i.Id.ToString("N"), i.FilePath, i.Title, i.Duration, i.ResumeTime))); }
    void StartSharing()
    {
        if (sharing?.IsRunning == true) return;
        try { sharing = new PlaylistSharingServer("Luma on " + Environment.MachineName); PushSharedPlaylist(); sharing.Start(); discovery = new WindowsDnsSdAdvertisement(sharing); Notice("Local playlist sharing is on"); }
        catch (Exception error) { sharing?.Stop(); discovery?.Dispose(); sharing = null; discovery = null; Notice("Sharing could not start: " + error.Message); }
        RefreshSharingUI();
    }
    void StopSharing() { sharing?.Stop(); discovery?.Dispose(); sharing = null; discovery = null; RefreshSharingUI(); Notice("Sharing stopped; paired devices were revoked"); }
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
    void RefreshSharingUI()
    {
        bool active = sharing?.IsRunning == true; shareButton.Content = active ? "Sharing on" : "Share playlist";
        shareButton.SetResourceReference(BackgroundProperty, active ? "AccentBrush" : "ButtonBrush");
        if (sharingState is null) return;
        sharingState.Text = active ? $"Active · {sharing!.PairedDevices} paired device(s) · {discovery?.Status ?? "Manual URL entry available"}" : "Sharing is off. No playlist or media is exposed.";
        sharingAddresses!.Text = active ? string.Join(Environment.NewLine, sharing!.Addresses) : "Start sharing to get local connection URLs.";
        pairingText!.Text = active ? DateTimeOffset.UtcNow < sharing!.PairingExpiresAt ? "Pairing code: " + sharing.PairingCode + "  (expires " + sharing.PairingExpiresAt.LocalDateTime.ToString("t") + ")" : "Code expired — stop and restart to pair another device" : "";
        startSharing!.IsEnabled = !active; stopSharing!.IsEnabled = active;
    }
    void Shutdown()
    {
        if (closing) return; closing = true; SaveProgress(); ++generation; timer.Stop(); updateTimer.Stop(); lifetimeCancellation.Cancel(); StopSharing(); sharingWindow?.Close(); DisposePlayer(); lifetimeCancellation.Dispose();
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
        var endpoints = NativeAudio.ActiveRenderEndpoints(); dummyAudioForSmoke = endpoints == 0;
        Console.WriteLine("INFO: active audio endpoints: " + (endpoints?.ToString() ?? "unknown") + "; audio mode: " + (dummyAudioForSmoke ? "explicit test-only dummy output" : "system output"));
        async Task<VlcPlayer> Open(string path)
        {
            var entry = library.Add([path]).FirstOrDefault() ?? library.Data.Items.Single(i => i.FilePath == Path.GetFullPath(path)); await LoadEntryAsync(entry);
            await Until(() => !opening || lastError is not null, "libVLC opens " + Path.GetFileName(path)); Check(lastError is null && vlc is not null, "libVLC failed: " + lastError); return vlc!;
        }
        var demo = await Open(Path.Combine(fixtures, "Luma-Demo.mp4"));
        await Until(() => demo.DisplayedPictures is > 1 && demo.NaturalVideoWidth == 960, "Native libVLC displayed video frames");
        Pause(); Seek(3); speed.SelectedIndex = 2; volume.Value = .4; Play(); await Task.Delay(400);
        Check(CurrentTime > 3 && Math.Abs(demo.Rate - 1.25) < .01 && demo.Volume == .4, "libVLC seek/rate/volume"); Pause(); speed.SelectedIndex = 1;
        ToggleFullscreen(); await Task.Delay(300); Check(fullscreen && ReferenceEquals(vlc, demo) && Math.Abs(stage.ActualWidth - layout.ActualWidth) < 2, "Fullscreen retained player and fills client"); ToggleFullscreen();
        ReadSubtitles(Path.Combine(fixtures, "Luma-Demo.srt")); await Until(() => demo.SubtitleTracks.Any(t => t.Id >= 0), "External native subtitle track");
        var subtitle = demo.SubtitleTracks.First(t => t.Id >= 0); demo.SelectSubtitle(subtitle.Id); ChangeCaptionDelay(.25); Check(demo.SubtitleTrack == subtitle.Id && demo.SubtitleDelay == .25, "Native external subtitle selection and delay"); demo.SelectSubtitle(-1);
        var snapshot = Path.Combine(output, "libvlc-avc-frame.png"); demo.Snapshot(snapshot); await Until(() => File.Exists(snapshot) && new FileInfo(snapshot).Length > 100, "Native libVLC AVC snapshot");
        Console.WriteLine("PASS: libVLC native displayed frames, snapshot, seek/pause/rate/volume/fullscreen and external caption selection/delay");
        var hevc = await Open(Path.Combine(fixtures, "Luma-HEVC10-AAC51.mp4"));
        await Until(() => hevc.DecodedVideo is > 0 && hevc.DecodedAudio is > 0 && hevc.DisplayedPictures is > 0, "HEVC Main10/AAC5.1 decoded audio and displayed video");
        Pause(); Seek(.75); Play(); await Task.Delay(300); Check(CurrentTime >= .75, "HEVC/AAC seek retained synchronized native clock"); Pause();
        var hevcShot = Path.Combine(output, "libvlc-hevc10-frame.png"); hevc.Snapshot(hevcShot); await Until(() => File.Exists(hevcShot) && new FileInfo(hevcShot).Length > 100, "HEVC native snapshot");
        Console.WriteLine("PASS: raw hev1 Main10/AAC5.1 local MP4 reaches libVLC decoded video/audio and native presentation without container normalization");
        var generated = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(output))!, "generated");
        var mkv = await Open(Path.Combine(generated, "Luma-review-text.mkv")); await Until(() => mkv.SubtitleTracks.Count(t => t.Id >= 0) >= 2, "Matroska native ASS/UTF8 tracks");
        Pause(); Seek(.75); foreach (var track in mkv.SubtitleTracks.Where(t => t.Id >= 0)) { mkv.SelectSubtitle(track.Id); Check(mkv.SubtitleTrack == track.Id, "Embedded caption track selection"); } mkv.SelectSubtitle(-1);
        foreach (string file in new[] { "Luma-MSVideo1-16.avi", "Luma-Silence.flac", "Luma-MSADPCM.wav" })
        { var media = await Open(Path.Combine(fixtures, file)); await Until(() => media.DecodedVideo is > 0 || media.DecodedAudio is > 0, "General local codec " + file); Pause(); }
        Console.WriteLine("PASS: direct local Matroska ASS/UTF8 selection, AVI, FLAC and ADPCM playback without remux/size gate");
        Unload(); var pending = library.Add([Path.Combine(fixtures, "Luma-Demo.mp4")]).FirstOrDefault() ?? library.Data.Items.First(i => i.FilePath.EndsWith("Luma-Demo.mp4"));
        await LoadEntryAsync(pending); var obsolete = vlc; await LoadEntryAsync(pending); await Until(() => !opening, "Replacement player load"); Check(!ReferenceEquals(obsolete, vlc) && lastError is null, "Native generations isolated");
        Pause(); Seek(5); await Task.Delay(250); SaveProgress(); Check(library.Data.Items.Single(i => i.Id == pending.Id).ResumeTime >= 4.5, "Saved resume position");
        StartSharing(); Check(sharing?.IsRunning == true && shareButton.Content.ToString() == "Sharing on", "Sharing starts only from explicit UI action");
        using (var http = new HttpClient())
        {
            string address = "http://127.0.0.1:" + sharing!.Port;
            var paired = await http.PostAsync(address + "/v1/pair", new StringContent(JsonSerializer.Serialize(new { code = sharing.PairingCode }), System.Text.Encoding.UTF8, "application/json")); paired.EnsureSuccessStatusCode();
            using var json = JsonDocument.Parse(await paired.Content.ReadAsStringAsync()); http.DefaultRequestHeaders.Authorization = new("Bearer", json.RootElement.GetProperty("token").GetString());
            var playlist = await http.GetStringAsync(address + "/v1/playlist"); Check(!playlist.Contains(fixtures, StringComparison.Ordinal), "Sharing GUI exposes no source paths");
        }
        await Task.Delay(300); string dnsSdRegistration = discovery?.Status ?? "not attempted";
        Console.WriteLine("INFO: Windows DNS-SD registration: " + dnsSdRegistration + "; network discovery itself is not asserted by this hosted smoke.");
        StopSharing(); Check(sharing is null && shareButton.Content.ToString() == "Share playlist", "UI stop revokes sharing");
        foreach (bool theme in new[] { true, false })
        {
            SetTheme(theme); await Task.Delay(100); UpdateLayout(); var image = new RenderTargetBitmap((int)Math.Ceiling(layout.ActualWidth), (int)Math.Ceiling(layout.ActualHeight), 96, 96, PixelFormats.Pbgra32); image.Render(layout);
            var png = new PngBitmapEncoder(); png.Frames.Add(BitmapFrame.Create(image)); using var file = File.Create(Path.Combine(output, theme ? "Luma-Windows-dark.png" : "Luma-Windows-light.png")); png.Save(file);
        }
        File.WriteAllText(Path.Combine(output, "smoke-results.json"), JsonSerializer.Serialize(new { libvlcPlayback = "passed", hevc10Aac51 = "passed", subtitleSelection = "passed", lifecycleRecovery = "passed", sharingUi = "passed", runtimeVersion = VlcPlayer.RuntimeVersion, runtimeBundled = false, dnsSdRegistration,
            physicalAudio = endpoints == 0 ? "unverified-no-active-render-endpoint" : "system-output-requested-audibility-not-measured", activeAudioRenderEndpoints = endpoints }, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine("PASS: libVLC native lifecycle/resume, opt-in sharing UI pairing/revoke, and native UI theme snapshots; physical audibility remains explicitly separate");
    }
}
