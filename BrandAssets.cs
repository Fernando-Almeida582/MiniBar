using System;
using System.Drawing;
using System.IO;
using System.Security;
using Microsoft.Win32;

namespace MeuPlayer;

internal static class BrandAssets
{
    internal static bool LightTaskbar
    {
        get
        {
            try { return Registry.GetValue(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize", "SystemUsesLightTheme", 0) is int value && value == 1; }
            catch (Exception ex) when (ex is SecurityException or UnauthorizedAccessException or IOException) { return false; }
        }
    }

    internal static Icon LoadTrayIcon(bool light)
    {
        string resource = light ? "tray-on-light.ico" : "tray-on-dark.ico";
        using var stream = typeof(BrandAssets).Assembly.GetManifestResourceStream("MiniBar.Brand." + resource)
            ?? throw new InvalidOperationException("Ícone de bandeja não encontrado.");
        using var icon = new Icon(stream, 32, 32);
        return (Icon)icon.Clone();
    }
}
