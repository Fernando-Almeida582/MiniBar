using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Threading;

namespace MeuPlayer;

public partial class PlayerWindow
{
    private readonly HashSet<ToolTip> openToolTips = new();

    private void ToolTipOpened(object sender, RoutedEventArgs e)
    {
        if (sender is not ToolTip tip) return;
        openToolTips.Add(tip);
        RaiseOpenToolTips();
        // The popup HWND may only be connected after Opened finishes.
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() =>
        {
            if (!closed) RaiseOpenToolTips();
        }));
    }

    private void ToolTipClosed(object sender, RoutedEventArgs e)
    {
        if (sender is ToolTip tip) openToolTips.Remove(tip);
    }

    private void RaiseOpenToolTips()
    {
        foreach (var tip in openToolTips)
            if (tip.IsOpen && PresentationSource.FromVisual(tip) is HwndSource source)
                Native.BringPopupForward(source.Handle);
    }

    private void CloseToolTips()
    {
        foreach (var tip in openToolTips.ToArray()) tip.IsOpen = false;
        openToolTips.Clear();
    }
}
