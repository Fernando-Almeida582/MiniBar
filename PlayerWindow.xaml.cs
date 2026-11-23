using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Windows.Media.Control;
using Forms = System.Windows.Forms;
using Session = Windows.Media.Control.GlobalSystemMediaTransportControlsSession;

namespace MeuPlayer;

public partial class PlayerWindow : Window
{
    private readonly DispatcherTimer layoutTimer = new() { Interval = TimeSpan.FromMilliseconds(350) };
    private readonly DispatcherTimer mediaTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly DispatcherTimer batteryTimer = new() { Interval = TimeSpan.FromMilliseconds(180) };
    private readonly PeripheralBattery batteries = new();
    private readonly Forms.NotifyIcon? tray;
    private readonly Forms.ContextMenuStrip? trayMenu;
    private GlobalSystemMediaTransportControlsSessionManager? manager;
    private nint window;
    private bool floating, vertical, behind, mediaVisible = true, batteryVisible = true, closed;
    private bool refreshingMedia, sendingCommand;
    private string? artworkKey;
    private string? mediaSourceId;
    private string mediaSourceTitle = "";
    private bool mediaIsAdvertisement;
    private bool openingSource;
    private string? manuallyPausedSource;
    private long pauseConfirmationDeadline;
    private System.Windows.Point? mediaDragStart;
    private bool suppressMediaClick;
    private (int X, int Y)? floatingPosition;
    private readonly PlayerPreferences preferences;
    private (Native.Rect Bounds, Native.Rect Work)? screen;
    private bool batteryUnavailable;

    public PlayerWindow() : this(null, null) { }

    private PlayerWindow(PlayerWindow? controller, Native.Display? display)
    {
        InitializeComponent();
        ContextMenuOpening += (_, _) =>
        {
            (controller ?? this).seekFlyout?.Close();
            if (window != 0) FocusContextMenu(window);
        };
        this.controller = controller;
        this.display = display;
        preferences = controller?.preferences ?? PlayerPreferences.Load();
        if (controller is not null)
        {
            CopyDisplaySettings();
            CopyDisplayState();
            return;
        }
        preferences.Theme = Math.Clamp(preferences.Theme, 0, PlayerTheme.All.Length - 1);
        floating = preferences.Floating;
        vertical = preferences.Vertical;
        behind = preferences.Behind;
        batteryVisible = preferences.BatteryVisible;
        mediaVisible = preferences.MediaVisible || !batteryVisible;
        floatingPosition = preferences.X is int x && preferences.Y is int y ? (x, y) : null;
        trayMenu = new Forms.ContextMenuStrip();
        lightTray = BrandAssets.LightTaskbar;
        trayIcon = BrandAssets.LoadTrayIcon(lightTray);
        tray = new Forms.NotifyIcon { Icon = trayIcon, Text = "MiniBar", ContextMenuStrip = trayMenu };
        Microsoft.Win32.SystemEvents.UserPreferenceChanged += TrayThemeChanged;
        tray.DoubleClick += (_, _) => SetFloating(!floating);

        Closed += (_, _) =>
        {
            closed = true;
            CloseToolTips();
            Microsoft.Win32.SystemEvents.UserPreferenceChanged -= TrayThemeChanged;
            layoutTimer.Stop();
            mediaTimer.Stop();
            batteryTimer.Stop();
            seekFlyout?.Close();
            inlineLyrics?.Dispose();
            CloseAdditionalDisplays();
            batteries.Dispose();
            tray.Dispose();
            trayIcon?.Dispose();
            trayMenu.Dispose();
        };
        layoutTimer.Tick += (_, _) => { if (!HasOpenMenu()) { Place(); RefreshDisplays(); } };
        mediaTimer.Tick += async (_, _) => await RefreshMedia();
        batteryTimer.Tick += (_, _) => RefreshBattery();
        ApplyMenu();
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        window = new WindowInteropHelper(this).Handle;
        Native.SetToolWindow(window);
        screen = display is { } target ? (target.Bounds, target.Work) : Native.MainScreen();

    }

    private bool started;
    protected override async void OnContentRendered(EventArgs e)
    {
        base.OnContentRendered(e);
        if (started) return;
        started = true;
        if (controller is not null)
        {
            Place();
            return;
        }
        tray!.Visible = true;
        Place();
        if (floating && behind) Native.KeepBehind(window);
        RefreshDisplays();
        layoutTimer.Start();
        mediaTimer.Start();
        try { batteries.Start(); }
        catch (Exception ex) when (ex is COMException or UnauthorizedAccessException or InvalidOperationException or ArgumentException)
        {
            batteryUnavailable = true;
        }
        batteryTimer.Start();
        RefreshBattery();
        await RefreshMedia();
    }

    private async Task RefreshMedia()
    {
        if (refreshingMedia || closed || !mediaVisible) return;
        refreshingMedia = true;
        try
        {
            manager ??= await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
            var session = manager.GetCurrentSession();
            if (closed) return;
            if (session is null)
            {
                trackTitle = "Sem mídia ativa";
                ShowLyricLine(null);
                Artist.Text = "MiniBar";
                OpenSource.ToolTip = null;
                OpenSource.IsEnabled = false;
                mediaSourceId = null;
                SetControls(null);
                UpdateMediaTools(null);
                artworkKey = null;
                SetArtwork(null);
                return;
            }

            var playback = session.GetPlaybackInfo();
            SetControls(playback, session.SourceAppUserModelId);
            var metadata = await session.TryGetMediaPropertiesAsync();
            if (closed) return;
            if (!mediaVisible) return;
            trackTitle = LimitText(metadata.Title, "Mídia ativa");
            UpdateMediaTools(session, metadata, playback);
            Track.Text = lyricLine ?? trackTitle;
            Artist.Text = LimitText(metadata.Artist, "Sessão do Windows");
            mediaSourceId = session.SourceAppUserModelId;
            mediaSourceTitle = metadata.Title ?? "";
            mediaIsAdvertisement = MediaSource.IsAdvertisement(mediaSourceTitle, metadata.Artist ?? "");
            OpenSource.IsEnabled = !string.IsNullOrWhiteSpace(mediaSourceId);
            OpenSource.ToolTip = "Abrir origem da mídia\n" + trackTitle + "\n" + Artist.Text;
            string key = string.Join("\u001f", session.SourceAppUserModelId, trackTitle, Artist.Text, metadata.AlbumTitle);
            if (key == artworkKey) return;
            artworkKey = key;
            SetArtwork(null);

            if (metadata.Thumbnail is null) return;
            using var stream = await metadata.Thumbnail.OpenReadAsync();
            if (closed || stream.Size is 0 or > 2_000_000) return;
            using var input = stream.AsStreamForRead();
            var image = new BitmapImage();
            image.BeginInit();
            image.StreamSource = input;
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.DecodePixelWidth = 96;
            image.DecodePixelHeight = 96;
            image.EndInit();
            SetArtwork(image);
        }
        catch (Exception ex) when (ex is COMException or IOException or UnauthorizedAccessException or NotSupportedException or FormatException or ArgumentException or InvalidOperationException)
        {
            // An app may close its media session or provide unreadable artwork.
            if (!closed && manager is null)
            {
                SetControls(null);
                trackTitle = "Mídia indisponível";
                ShowLyricLine(null);
            }
        }
        finally { refreshingMedia = false; }
    }

    private static string LimitText(string? text, string fallback)
    {
        if (string.IsNullOrWhiteSpace(text)) return fallback;
        string singleLine = text.Replace('\r', ' ').Replace('\n', ' ').Trim();
        return singleLine.Length > 180 ? singleLine[..180] + "…" : singleLine;
    }

    private void SetControls(GlobalSystemMediaTransportControlsSessionPlaybackInfo? info, string? source = null)
    {
        Previous.IsEnabled = info?.Controls.IsPreviousEnabled == true;
        Next.IsEnabled = info?.Controls.IsNextEnabled == true;
        bool playing = info?.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing;
        Play.IsEnabled = info?.Controls.IsPlayPauseToggleEnabled == true ||
            (playing ? info?.Controls.IsPauseEnabled : info?.Controls.IsPlayEnabled) == true;
        Play.Content = playing ? "\uE769" : "\uE768";
        Play.ToolTip = playing ? "Pausar" : "Reproduzir";
        AutomationProperties.SetName(Play, (string)Play.ToolTip);
        UpdatePauseHighlight(source, info?.PlaybackStatus);
    }

    private void UpdatePauseHighlight(string? source, GlobalSystemMediaTransportControlsSessionPlaybackStatus? status)
    {
        bool paused = status == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Paused;
        if (source != manuallyPausedSource || status is null or GlobalSystemMediaTransportControlsSessionPlaybackStatus.Stopped ||
            (!paused && (pauseConfirmationDeadline == 0 || Environment.TickCount64 > pauseConfirmationDeadline)))
            manuallyPausedSource = null;

        if (manuallyPausedSource is not null && paused)
        {
            pauseConfirmationDeadline = 0;
            Play.SetResourceReference(System.Windows.Controls.Control.BackgroundProperty, "PlayerHover");
        }
        else
        {
            Play.ClearValue(System.Windows.Controls.Control.BackgroundProperty);
        }
    }

    private async Task Send(Func<Session, Task<bool>> command, bool clearPauseOnSuccess = false)
    {
        if (sendingCommand || closed) return;
        sendingCommand = true;
        try
        {
            if (manager?.GetCurrentSession() is { } session && await command(session) && clearPauseOnSuccess)
            {
                manuallyPausedSource = null;
                UpdatePauseHighlight(null, null);
            }
        }
        catch (Exception ex) when (ex is COMException or UnauthorizedAccessException or InvalidOperationException) { }
        finally { sendingCommand = false; }
        await RefreshMedia();
    }

    private async void OpenMediaSource(object sender, RoutedEventArgs e)
    {
        if (suppressMediaClick) return;
        var owner = controller ?? this;
        owner.seekFlyout?.Close();
        await owner.OpenSourceWindow();
    }

    private async Task OpenSourceWindow()
    {
        if (openingSource || mediaSourceId is not { } source) return;
        openingSource = true;
        try
        {
            string title = mediaSourceTitle;
            bool isAdvertisement = mediaIsAdvertisement;
            var (windows, isBrowser) = MediaSource.FindWindows(source);
            var target = windows.OrderByDescending(item => MediaSource.TitleScore(item.Title, title)).FirstOrDefault();
            if (target is null)
            {
                OpenSource.ToolTip = "O Windows não identificou uma janela aberta para esta mídia.";
                return;
            }

            if (!isBrowser || MediaSource.TitleScore(target.Title, title) > 0)
            {
                MediaSource.BringForward(target);
                return;
            }

            nint foreground = Native.GetForegroundWindow();
            if (windows.Length == 1 && MediaSource.IsMinimized(target))
            {
                MediaSource.BringForward(target);
                await Task.Delay(180);
                if (closed) return;
            }
            var tab = await Task.Run(() => MediaSource.FindBrowserTab(windows, title, isAdvertisement));
            nint currentForeground = Native.GetForegroundWindow();
            if (closed || (currentForeground != foreground && currentForeground != window &&
                !windows.Any(candidate => candidate.Handle == currentForeground))) return;
            MediaSource.BringForward(tab?.Window ?? target);
            if (tab is not null) await Task.Run(() => MediaSource.SelectTab(tab));
        }
        finally { openingSource = false; }
    }

    private async void GoPrevious(object sender, RoutedEventArgs e) => await (controller ?? this).Send(async s => await s.TrySkipPreviousAsync(), clearPauseOnSuccess: true);
    private async void GoNext(object sender, RoutedEventArgs e) => await (controller ?? this).Send(async s => await s.TrySkipNextAsync(), clearPauseOnSuccess: true);
    private async void TogglePlayback(object sender, RoutedEventArgs e) => await (controller ?? this).TogglePlayback();

    private Task TogglePlayback() => Send(async s =>
    {
        var info = s.GetPlaybackInfo();
        bool pausing = info.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing;
        bool accepted = info.Controls.IsPlayPauseToggleEnabled ? await s.TryTogglePlayPauseAsync() :
            pausing ? await s.TryPauseAsync() : await s.TryPlayAsync();
        if (accepted && !closed)
        {
            manuallyPausedSource = pausing ? s.SourceAppUserModelId : null;
            pauseConfirmationDeadline = pausing ? Environment.TickCount64 + 4000 : 0;
            UpdatePauseHighlight(s.SourceAppUserModelId, s.GetPlaybackInfo().PlaybackStatus);
        }
        return accepted;
    });

    private void RefreshBattery()
    {
        if (batteryVisible && batteries.Changes() is { } devices) ShowBatteries(devices);
    }

    private void ShowBatteries(IReadOnlyList<PeripheralBattery.Reading> devices)
    {
        batteryReadings = devices;
        Battery.Children.Clear();
        if (devices.Count == 0)
        {
            string description = batteryUnavailable
                ? "Não foi possível consultar a bateria dos dispositivos."
                : "Nenhum dispositivo Bluetooth ou USB compatível com leitura de bateria foi detectado.";
            var status = new StackPanel
            {
                Margin = new Thickness(3, 0, 3, 0),
                VerticalAlignment = VerticalAlignment.Center,
                Background = System.Windows.Media.Brushes.Transparent,
                ToolTip = description
            };
            AutomationProperties.SetName(status, description);
            var symbol = new Grid
            {
                Width = 26,
                Height = 18,
                HorizontalAlignment = System.Windows.HorizontalAlignment.Center
            };
            var bluetooth = new TextBlock
            {
                Text = "\uE702",
                FontFamily = new System.Windows.Media.FontFamily("Segoe MDL2 Assets"),
                FontSize = 16,
                HorizontalAlignment = System.Windows.HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Center
            };
            bluetooth.SetResourceReference(TextBlock.ForegroundProperty, "PlayerText");
            var disconnected = new TextBlock
            {
                Text = "×",
                FontFamily = new System.Windows.Media.FontFamily("Segoe UI"),
                FontWeight = FontWeights.Bold,
                FontSize = 13,
                Foreground = System.Windows.Media.Brushes.IndianRed,
                HorizontalAlignment = System.Windows.HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Bottom
            };
            symbol.Children.Add(bluetooth);
            symbol.Children.Add(disconnected);
            var detail = new TextBlock
            {
                Text = batteryUnavailable ? "Bateria indisponível" : "Nenhum dispositivo conectado",
                FontSize = 9,
                TextWrapping = TextWrapping.Wrap,
                TextAlignment = TextAlignment.Center,
                HorizontalAlignment = System.Windows.HorizontalAlignment.Center
            };
            detail.SetBinding(FrameworkElement.MaxWidthProperty,
                new System.Windows.Data.Binding(nameof(FrameworkElement.MaxWidth)) { Source = Battery });
            detail.SetResourceReference(TextBlock.ForegroundProperty, "PlayerDetail");
            status.Children.Add(symbol);
            status.Children.Add(detail);
            Battery.Children.Add(status);
            return;
        }

        foreach (var device in devices.Take(4))
            AddBattery(device.Glyph, device.Label, $"{device.Name} · {device.Label}");
        if (devices.Count > 4)
        {
            var remaining = devices.Skip(4).ToArray();
            AddBattery("\uE712", $"+{remaining.Length}", string.Join("\n", remaining.Select(device => $"{device.Name} · {device.Label}")));
        }
    }

    private void AddBattery(string glyph, string label, string description)
    {
        var badge = new StackPanel
        {
            MinWidth = 30,
            Background = System.Windows.Media.Brushes.Transparent,
            ToolTip = description,
            VerticalAlignment = VerticalAlignment.Center
        };
        AutomationProperties.SetName(badge, description);
        var icon = new TextBlock
        {
            Text = glyph,
            FontFamily = new System.Windows.Media.FontFamily("Segoe MDL2 Assets"),
            FontSize = 16,
            HorizontalAlignment = System.Windows.HorizontalAlignment.Center
        };
        icon.SetResourceReference(TextBlock.ForegroundProperty, "PlayerText");
        var value = new TextBlock
        {
            Text = label,
            FontSize = 10,
            HorizontalAlignment = System.Windows.HorizontalAlignment.Center
        };
        value.SetResourceReference(TextBlock.ForegroundProperty, "PlayerDetail");
        badge.Children.Add(icon);
        badge.Children.Add(value);
        Battery.Children.Add(badge);
    }

    private void SetArtwork(BitmapSource? artwork)
    {
        if (ReferenceEquals(Cover.Source, artwork)) return;
        Cover.Source = artwork;
        if (ReferenceEquals(PlayerTheme.All[preferences.Theme], PlayerTheme.Adaptive)) ApplyTheme();
    }

    private void ApplyTheme()
    {
        var theme = PlayerTheme.All[preferences.Theme];
        if (ReferenceEquals(theme, PlayerTheme.Adaptive)) theme = PlayerTheme.FromArtwork(Cover.Source as BitmapSource);
        Frame.Background = PlayerTheme.Brush(theme.Surface);
        Frame.BorderBrush = PlayerTheme.Brush(theme.Border);
        Resources["PlayerText"] = PlayerTheme.Brush(theme.Text);
        Resources["PlayerDetail"] = PlayerTheme.Brush(theme.Detail);
        Resources["PlayerHover"] = PlayerTheme.Brush((theme.Accent & 0x00FFFFFF) | 0x33000000);
        Resources["PlayerPress"] = PlayerTheme.Brush((theme.Accent & 0x00FFFFFF) | 0x88000000);
    }

    private void Place()
    {
        if (window == 0 || !IsLoaded || HasOpenMenu()) return;
        screen = display is { } target ? (target.Bounds, target.Work) : Native.MainScreen();
        if (screen is not { } area) return;

        if (!floating && preferences.HideInFullScreen && Native.FullScreenOn(window, area.Bounds))
        {
            if (IsVisible)
            {
                (controller ?? this).seekFlyout?.CloseIfTarget(OpenSource);
                CloseToolTips();
                Hide();
            }
            return;
        }
        if (!IsVisible) Show();
        var dpi = System.Windows.Media.VisualTreeHelper.GetDpi(this);
        int width = (int)Math.Ceiling(ActualWidth * dpi.DpiScaleX);
        int height = (int)Math.Ceiling(ActualHeight * dpi.DpiScaleY);
        if (width < 1 || height < 1) return;

        if (!floating)
        {
            double preferredText = lyricLine is null ? 143 : preferredLyricWidth;
            double fixedWidth = Frame.Padding.Left + Frame.Padding.Right + Frame.BorderThickness.Left + Frame.BorderThickness.Right;
            if (mediaVisible)
                fixedWidth += Transport.DesiredSize.Width + ArtworkFrame.DesiredSize.Width +
                    MediaText.Margin.Left + MediaText.Margin.Right;
            if (batteryVisible) fixedWidth += Battery.DesiredSize.Width;
            preferredText = Math.Min(preferredText, Math.Max(55, (area.Work.Right - area.Work.Left) / dpi.DpiScaleX - fixedWidth - 16));
            int requested = (int)Math.Ceiling((fixedWidth + (mediaVisible ? preferredText : 0)) * dpi.DpiScaleX);
            int minimum = (int)Math.Ceiling((fixedWidth + (mediaVisible ? 55 : 0)) * dpi.DpiScaleX);
            nint taskbar = Native.TaskbarOn(display?.Handle ?? 0);
            var slot = area.Work.Bottom < area.Bounds.Bottom
                ? TaskbarSpace.Available(taskbar, area.Bounds, requested, minimum, (int)(8 * dpi.DpiScaleX), preferences.DockRight) : null;
            double textWidth = mediaVisible && slot is { } space
                ? Math.Min(preferredText, Math.Max(55, space.Width / dpi.DpiScaleX - fixedWidth)) : preferredText;
            if (MediaText.Width != textWidth)
            {
                MediaText.Width = textWidth;
                UpdateLayout();
                width = (int)Math.Ceiling(ActualWidth * dpi.DpiScaleX);
            }
            Native.KeepDockVisible(window, area.Bounds, area.Work, width, height, dpi.DpiScaleX, display?.Handle ?? 0, slot, preferences.DockRight, preferences.DockBehind);
        }
        else if (floatingPosition is { } position)
        {
            var work = Forms.Screen.AllScreens
                .FirstOrDefault(s => s.WorkingArea.Contains(position.X, position.Y))?.WorkingArea;
            int x = work is { } display ? Math.Min(position.X, Math.Max(display.Left, display.Right - width)) : area.Work.Left + 12;
            int y = work is { } monitor ? Math.Min(position.Y, Math.Max(monitor.Top, monitor.Bottom - height)) : area.Work.Bottom - height - 12;
            Native.Move(window, x, y);
            floatingPosition = null;
        }
        // Repositioning the owner can put it above its separate popup HWNDs.
        RaiseOpenToolTips();
        (controller ?? this).seekFlyout?.BringForward();
    }

    private void SetDockSide(bool right)
    {
        preferences.DockRight = right;
        if (floating) SetFloating(false);
        else ApplyMenu();
        SavePreferences();
    }

    private void SetFloating(bool enabled)
    {
        if (floating == enabled) return;
        RememberPosition();
        seekFlyout?.Close();
        floating = enabled;
        if (floating && screen is { } area)
            floatingPosition = preferences.X is int x && preferences.Y is int y ? (x, y) :
                (area.Work.Left + 16, Math.Max(area.Work.Top, area.Work.Bottom - 105));
        ApplyMenu();
        SavePreferences();
    }

    private void StartMediaGesture(object sender, MouseButtonEventArgs e)
    {
        suppressMediaClick = false;
        mediaDragStart = floating ? e.GetPosition(this) : null;
    }

    private void MoveMediaGesture(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if (!floating || mediaDragStart is not { } start) return;
        if (e.LeftButton != MouseButtonState.Pressed)
        {
            mediaDragStart = null;
            return;
        }

        var distance = e.GetPosition(this) - start;
        if (Math.Abs(distance.X) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(distance.Y) < SystemParameters.MinimumVerticalDragDistance) return;

        mediaDragStart = null;
        suppressMediaClick = true;
        (controller ?? this).seekFlyout?.Close();
        e.Handled = true;
        OpenSource.ReleaseMouseCapture();
        try
        {
            if (e.LeftButton == MouseButtonState.Pressed) DragFloatingWindow();
        }
        finally
        {
            Dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(() => suppressMediaClick = false));
        }
    }

    private void EndMediaGesture(object sender, MouseButtonEventArgs e)
    {
        mediaDragStart = null;
        if (suppressMediaClick) e.Handled = true;
    }

    private void Drag(object sender, MouseButtonEventArgs e)
    {
        if (!floating || e.Handled || e.ChangedButton != MouseButton.Left || e.ButtonState != MouseButtonState.Pressed) return;
        DragFloatingWindow();
    }

    private void DragFloatingWindow()
    {
        DragMove();
        if (behind) Native.KeepBehind(window);
        RememberPosition();
        SavePreferences();
    }

    private void RememberPosition()
    {
        if (floating && Native.WindowPosition(window) is { } position)
        {
            preferences.X = position.X;
            preferences.Y = position.Y;
        }
    }

    private void SavePreferences()
    {
        preferences.Floating = floating;
        preferences.Vertical = vertical;
        preferences.Behind = behind;
        preferences.MediaVisible = mediaVisible;
        preferences.BatteryVisible = batteryVisible;
        preferences.Save();
    }

    private void ApplyMenu()
    {
        bool portrait = floating && vertical;
        Layout.Orientation = portrait ? Orientation.Vertical : Orientation.Horizontal;
        Frame.Padding = portrait ? new Thickness(9, 8, 9, 9) : new Thickness(5, 2, 5, 2);
        Transport.HorizontalAlignment = portrait ? System.Windows.HorizontalAlignment.Center : System.Windows.HorizontalAlignment.Stretch;
        ArtworkFrame.Width = ArtworkFrame.Height = portrait ? 52 : 27;
        ArtworkFrame.Margin = portrait ? new Thickness(0, 5, 0, 6) : new Thickness(4, 0, 4, 0);
        ArtworkFrame.HorizontalAlignment = portrait ? System.Windows.HorizontalAlignment.Center : System.Windows.HorizontalAlignment.Stretch;
        UpdateTrackWidth();
        MediaText.Margin = portrait ? new Thickness(1, 0, 1, 3) : new Thickness(3, 0, 5, 0);
        Track.TextAlignment = Artist.TextAlignment = portrait ? TextAlignment.Center : TextAlignment.Left;
        Track.TextWrapping = portrait ? TextWrapping.Wrap : TextWrapping.NoWrap;
        Track.MaxHeight = portrait ? 30 : double.PositiveInfinity;
        Battery.MaxWidth = portrait ? 108 : double.PositiveInfinity;
        Battery.HorizontalAlignment = portrait ? System.Windows.HorizontalAlignment.Center : System.Windows.HorizontalAlignment.Stretch;
        Battery.Margin = portrait ? new Thickness(0, mediaVisible ? 6 : 0, 0, 0) : new Thickness(4, 0, 4, 0);
        Media.Orientation = Layout.Orientation;
        MediaInfo.Orientation = Layout.Orientation;
        Media.Visibility = mediaVisible ? Visibility.Visible : Visibility.Collapsed;
        if (!mediaVisible) StopInlineLyrics();
        Topmost = floating ? !behind : !preferences.DockBehind;
        Battery.Visibility = batteryVisible ? Visibility.Visible : Visibility.Collapsed;
        ApplyTheme();
        BuildMenus();
        Place();
        if (floating && behind && window != 0) Native.KeepBehind(window);
        if (controller is null && started)
        {
            RefreshDisplays();
            foreach (var copy in additionalDisplays.Values) copy.CopyDisplaySettings();
            _ = RefreshMedia();
        }
    }

    private record Option(string Label, bool Checked = false, Action? Run = null, bool Enabled = true, Option[]? Children = null);

    private Option[] Options() =>
    [
        new("Posição", Children:
        [
            new("Fixar à esquerda", !floating && !preferences.DockRight, () => SetDockSide(false)),
            new("Fixar à direita", !floating && preferences.DockRight, () => SetDockSide(true)),
            new("Solto na tela (arrastar)", floating, () => SetFloating(true)),
            new("Jogar para trás (fixo)", preferences.DockBehind, () => ChangeOption(() => preferences.DockBehind = !preferences.DockBehind), !floating),
            new("Layout vertical (solto)", vertical, () => ChangeOption(() => vertical = !vertical), floating),
            new("Atrás das janelas (solto)", behind, () => ChangeOption(() => behind = !behind), floating)
        ]),
        new("Exibir", Children:
        [
            new("Controles de mídia", mediaVisible, () => ChangeOption(() => mediaVisible = !mediaVisible), batteryVisible),
            new("Bateria dos dispositivos", batteryVisible, () => ChangeOption(() => batteryVisible = !batteryVisible), mediaVisible),
            new("Ocultar em tela cheia (fixo)", preferences.HideInFullScreen,
                () => ChangeOption(() => preferences.HideInFullScreen = !preferences.HideInFullScreen), !floating),
            new("Exibir letra da faixa", preferences.ShowTrackLyrics, ToggleTrackLyrics)
        ]),
        new("Tema", Children: PlayerTheme.All.Select((theme, index) =>
            new Option(theme.Name, preferences.Theme == index, () => ChangeOption(() => preferences.Theme = index))).ToArray()),
        new("Iniciar com o Windows", WindowsStartup.IsRegistered, ToggleStartup),
        new("Encerrar MiniBar", Run: Close)
    ];

    private void ChangeOption(Action change)
    {
        change();
        ApplyMenu();
        SavePreferences();
    }

    private void ToggleStartup()
    {
        if (!WindowsStartup.Set(!WindowsStartup.IsRegistered))
            System.Windows.MessageBox.Show("Não foi possível alterar a inicialização desta cópia.", "MiniBar",
                MessageBoxButton.OK, MessageBoxImage.Information);
        BuildMenus();
    }

    private void BuildMenus()
    {
        BuildWindowMenu((controller ?? this).Options());
        if (trayMenu is null) return;
        foreach (var old in trayMenu.Items.Cast<Forms.ToolStripItem>().ToArray()) old.Dispose();
        trayMenu.Items.Clear();
        var options = Options();
        foreach (var option in options) trayMenu.Items.Add(TrayOption(option));
        foreach (var copy in additionalDisplays.Values) copy.BuildWindowMenu(options);
    }

    private bool HasOpenMenu()
    {
        var root = controller ?? this;
        return root.ContextMenu?.IsOpen == true || root.trayMenu?.Visible == true ||
            root.additionalDisplays.Values.Any(copy => copy.ContextMenu?.IsOpen == true);
    }

    private void BuildWindowMenu(Option[] options)
    {
        if (ContextMenu is null)
        {
            ContextMenu = new System.Windows.Controls.ContextMenu();
            ContextMenu.Opened += (_, _) =>
            {
                (controller ?? this).seekFlyout?.Close();
            };
        }
        ContextMenu.Items.Clear();
        foreach (var option in options) ContextMenu.Items.Add(WindowOption(option));
    }


    [DllImport("user32.dll", EntryPoint = "SetForegroundWindow")]
    private static extern bool FocusContextMenu(nint handle);

    private static System.Windows.Controls.MenuItem WindowOption(Option option)
    {
        var item = new System.Windows.Controls.MenuItem { Header = option.Label, IsChecked = option.Checked, IsEnabled = option.Enabled };
        if (option.Children is { } children)
        {
            foreach (var child in children) item.Items.Add(WindowOption(child));
            MenuPlacement.Configure(item);
        }
        if (option.Run is { } run) item.Click += (_, _) => run();
        return item;
    }

    private Forms.ToolStripMenuItem TrayOption(Option option)
    {
        var item = new Forms.ToolStripMenuItem(option.Label) { Checked = option.Checked, Enabled = option.Enabled };
        if (option.Children is { } children)
        {
            foreach (var child in children) item.DropDownItems.Add(TrayOption(child));
        }
        if (option.Run is { } run) item.Click += (_, _) => run();
        return item;
    }
}





































