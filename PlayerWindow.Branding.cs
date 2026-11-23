using System;
using Microsoft.Win32;

namespace MeuPlayer;

public partial class PlayerWindow
{
    private System.Drawing.Icon? trayIcon;
    private bool lightTray;

    private void TrayThemeChanged(object sender, UserPreferenceChangedEventArgs e)
    {
        if (closed || Dispatcher.HasShutdownStarted) return;
        Dispatcher.BeginInvoke(new Action(() =>
        {
            if (closed || tray is null) return;
            bool light = BrandAssets.LightTaskbar;
            if (lightTray == light) return;
            var replacement = BrandAssets.LoadTrayIcon(light);
            var previous = trayIcon;
            tray.Icon = replacement;
            trayIcon = replacement;
            lightTray = light;
            previous?.Dispose();
        }));
    }
}
