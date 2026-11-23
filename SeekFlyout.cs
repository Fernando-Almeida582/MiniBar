using System;
using System.Globalization;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;

namespace MeuPlayer;

internal sealed class SeekFlyout
{
    private readonly Popup popup;
    private readonly Slider slider;
    private readonly TextBlock positionLabel;
    private readonly TextBlock durationLabel;
    private readonly Border panel;
    private readonly DispatcherTimer dismiss = new() { Interval = TimeSpan.FromMilliseconds(280) };
    private readonly Func<double, string, string, Task> seek;
    private string source = "";
    private string title = "";
    private bool selecting, committing;

    internal SeekFlyout(Func<double, string, string, Task> seek)
    {
        this.seek = seek;
        slider = new Slider
        {
            Width = 225,
            IsMoveToPointEnabled = true,
            SmallChange = 5000,
            LargeChange = 15000,
            Margin = new Thickness(0, 2, 0, 5)
        };
        AutomationProperties.SetName(slider, "Posição da reprodução");
        positionLabel = new TextBlock { FontSize = 11 };
        durationLabel = new TextBlock { FontSize = 11, HorizontalAlignment = HorizontalAlignment.Right };
        var times = new Grid();
        times.Children.Add(positionLabel);
        times.Children.Add(durationLabel);
        var body = new StackPanel();
        body.Children.Add(slider);
        body.Children.Add(times);
        panel = new Border
        {
            Background = PlayerTheme.Brush(0xFF19212B),
            BorderBrush = PlayerTheme.Brush(0xFF546477),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(7),
            Padding = new Thickness(10, 6, 10, 7),
            Child = body
        };
        positionLabel.Foreground = durationLabel.Foreground = PlayerTheme.Brush(0xFFE4ECF6);
        popup = new Popup
        {
            AllowsTransparency = true,
            Placement = PlacementMode.Top,
            StaysOpen = true,
            Child = panel
        };
        popup.Opened += (_, _) => BringForward();

        slider.ValueChanged += (_, _) => positionLabel.Text = FormatTime(slider.Value);
        slider.AddHandler(UIElement.PreviewMouseLeftButtonDownEvent,
            new MouseButtonEventHandler((_, _) => { selecting = true; dismiss.Stop(); }), handledEventsToo: true);
        slider.AddHandler(UIElement.PreviewMouseLeftButtonUpEvent, new MouseButtonEventHandler(async (_, _) =>
        {
            if (!selecting) return;
            selecting = false;
            await Commit();
            if (!panel.IsMouseOver) ScheduleDismiss();
        }), handledEventsToo: true);
        slider.LostMouseCapture += (_, _) =>
        {
            selecting = false;
            if (!panel.IsMouseOver) ScheduleDismiss();
        };
        slider.PreviewKeyUp += async (_, e) =>
        {
            if (e.Key is Key.Left or Key.Right or Key.Up or Key.Down or Key.Home or Key.End or Key.PageUp or Key.PageDown)
                await Commit();
        };
        panel.MouseEnter += (_, _) => dismiss.Stop();
        panel.MouseLeave += (_, _) => ScheduleDismiss();
        dismiss.Tick += (_, _) =>
        {
            dismiss.Stop();
            if (!selecting && !panel.IsMouseOver && popup.PlacementTarget?.IsMouseOver != true) Close();
        };
    }

    internal bool IsOpen => popup.IsOpen;

    internal void BringForward()
    {
        if (popup.IsOpen && PresentationSource.FromVisual(panel) is HwndSource source)
            Native.BringPopupForward(source.Handle);
    }

    internal void Open(UIElement target, string sessionSource, string track, PlayerWindow.MediaTimeline time)
    {
        dismiss.Stop();
        if (popup.PlacementTarget != target) Close();
        source = sessionSource;
        title = track;
        popup.PlacementTarget = target;
        ToolTipService.SetIsEnabled(target, false);
        Update(time);
        popup.IsOpen = true;
    }

    internal void Refresh(string? sessionSource, string? track, PlayerWindow.MediaTimeline? time)
    {
        if (!IsOpen) return;
        if (source != sessionSource || title != track || time is not { CanSeek: true } current)
        {
            Close();
            return;
        }
        Update(current);
    }

    internal void ScheduleDismiss()
    {
        if (!IsOpen) return;
        dismiss.Stop();
        dismiss.Start();
    }

    internal void CloseIfTarget(UIElement target)
    {
        if (popup.PlacementTarget == target) Close();
    }

    internal void Close()
    {
        dismiss.Stop();
        selecting = false;
        popup.IsOpen = false;
        if (popup.PlacementTarget is { } target) target.ClearValue(ToolTipService.IsEnabledProperty);
        popup.PlacementTarget = null;
    }

    private void Update(PlayerWindow.MediaTimeline time)
    {
        if (selecting || committing) return;
        slider.Minimum = time.MinSeek;
        slider.Maximum = time.MaxSeek;
        slider.Value = Math.Clamp(time.Clock.Position, slider.Minimum, slider.Maximum);
        positionLabel.Text = FormatTime(slider.Value);
        durationLabel.Text = FormatTime(double.IsFinite(time.Clock.Duration) && time.Clock.Duration != double.MaxValue
            ? time.Clock.Duration : time.MaxSeek);
    }

    private async Task Commit()
    {
        if (!IsOpen || committing) return;
        committing = true;
        try { await seek(slider.Value, source, title); }
        finally { committing = false; }
    }

    internal static string FormatTime(double milliseconds)
    {
        var seconds = (long)Math.Max(0, Math.Floor(milliseconds / 1000));
        return seconds >= 3600
            ? string.Create(CultureInfo.InvariantCulture, $"{seconds / 3600}:{seconds / 60 % 60:00}:{seconds % 60:00}")
            : string.Create(CultureInfo.InvariantCulture, $"{seconds / 60}:{seconds % 60:00}");
    }
}


