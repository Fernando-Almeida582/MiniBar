using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Automation;

namespace MeuPlayer;

internal static class MediaSource
{
    private static readonly (string Id, string Process, bool Browser)[] Known =
    [
        ("Brave", "brave", true), ("Chrome", "chrome", true), ("MSEdge", "msedge", true),
        ("Microsoft.MicrosoftEdge", "msedge", true), ("Firefox", "firefox", true),
        ("Mozilla.Firefox", "firefox", true), ("Opera", "opera", true), ("Vivaldi", "vivaldi", true),
        ("Chromium", "chromium", true), ("Spotify", "spotify", false), ("SpotifyAB", "spotify", false)
    ];

    internal record Window(nint Handle, string Title);
    internal record Tab(Window Window, AutomationElement Element);


    internal static (Window[] Windows, bool Browser) FindWindows(string source)
    {
        var known = Known.FirstOrDefault(entry => MatchesId(source, entry.Id));
        string? processName = known.Process;
        if (processName is null && source.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            processName = Path.GetFileNameWithoutExtension(source);

        var windows = new List<Window>();
        var matchingProcesses = new Dictionary<uint, bool>();
        EnumWindows((handle, _) =>
        {
            if (!IsWindowVisible(handle) || GetWindow(handle, 4) != 0) return true;
            GetWindowThreadProcessId(handle, out uint pid);
            if (!matchingProcesses.TryGetValue(pid, out bool matches))
            {
                matches = MatchesProcess(pid, source, processName);
                matchingProcesses[pid] = matches;
            }
            if (matches)
            {
                var title = new StringBuilder(768);
                if (GetWindowText(handle, title, title.Capacity) != 0) windows.Add(new Window(handle, title.ToString()));
            }
            return true;
        }, 0);
        return (windows.ToArray(), known.Browser);
    }

    private static readonly string[] BrowserSuffixes =
    [
        "Google Chrome", "Brave", "Brave Browser", "Microsoft Edge", "Mozilla Firefox",
        "Opera", "Vivaldi", "Chromium"
    ];
    private static readonly string[] PageSuffixes = ["YouTube", "YouTube Music"];
    private static readonly string[] PlayingSuffixes =
    [
        "Audio playing", "Playing audio", "Reprodução de áudio", "Reproduzindo áudio",
        "Áudio em reprodução", "Tocando áudio"
    ];
    private static readonly string[] MutedSuffixes = ["Audio muted", "Áudio silenciado", "Áudio desativado"];
    private static readonly string[] AdvertisementLabels =
        ["Anúncio", "Anúncios", "Publicidade", "Ad", "Ads", "Advertisement", "Sponsored", "Patrocinado"];

    internal static bool IsAdvertisement(string title, string artist) =>
        IsAdvertisementLabel(title) || IsAdvertisementLabel(artist);

    private static bool IsAdvertisementLabel(string value)
    {
        string normalized = NormalizeTitle(value);
        return AdvertisementLabels.Any(label =>
            string.Equals(normalized, label, StringComparison.OrdinalIgnoreCase) ||
            normalized.StartsWith(label + ": ", StringComparison.OrdinalIgnoreCase) ||
            normalized.StartsWith(label + " - ", StringComparison.OrdinalIgnoreCase) ||
            normalized.StartsWith(label + " · ", StringComparison.OrdinalIgnoreCase));
    }

    private static (string Title, bool YouTubeVideo, bool Audible) ParseTabLabel(string label)
    {
        string caption = RemoveSuffix(NormalizeTitle(label), BrowserSuffixes);
        string beforeMemory = RemoveSuffix(caption, PlayingSuffixes);
        bool audible = beforeMemory.Length != caption.Length;
        caption = StripMemoryLabel(RemoveSuffix(beforeMemory, MutedSuffixes));
        string withoutAudio = RemoveSuffix(caption, PlayingSuffixes);
        audible |= withoutAudio.Length != caption.Length;
        caption = StripNotificationCount(RemoveSuffix(withoutAudio, MutedSuffixes));
        string title = RemoveSuffix(caption, PageSuffixes);
        return (title, title.Length != caption.Length && title.Length > 0, audible);
    }

    internal static int TitleScore(string caption, string title) =>
        ScoreTab(caption, title, ParseTabLabel(caption));

    private static int ScoreTab(string caption, string title, (string Title, bool YouTubeVideo, bool Audible) tab)
    {
        string expected = StripNotificationCount(RemoveSuffix(NormalizeTitle(title), PageSuffixes));
        if (expected.Length == 0 || !string.Equals(tab.Title, expected, StringComparison.OrdinalIgnoreCase)) return 0;
        return tab.Audible ? 3 : string.Equals(caption.Trim(), title.Trim(), StringComparison.OrdinalIgnoreCase) ? 2 : 1;
    }

    private static string NormalizeTitle(string title)
    {
        var output = new StringBuilder(title.Length);
        bool pendingSpace = false;
        foreach (char character in title)
        {
            if (character is '\u200E' or '\u200F' or '\u202A' or '\u202B' or '\u202C' or '\u2066' or '\u2067' or '\u2068' or '\u2069')
                continue;
            if (char.IsWhiteSpace(character))
            {
                pendingSpace = output.Length > 0;
                continue;
            }
            if (pendingSpace) output.Append(' ');
            pendingSpace = false;
            output.Append(character is '–' or '—' ? '-' : character);
        }
        return output.ToString();
    }

    private static string RemoveSuffix(string title, string[] suffixes)
    {
        foreach (string suffix in suffixes)
        {
            string marker = " - " + suffix;
            if (title.EndsWith(marker, StringComparison.OrdinalIgnoreCase)) return title[..^marker.Length];
        }
        return title;
    }

    private static string StripMemoryLabel(string title)
    {
        string[] prefixes = ["Uso da memória em ", "Memory usage for ", "Memory usage in "];
        foreach (string prefix in prefixes)
        {
            if (!title.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) continue;
            int separator = title.LastIndexOf(": ", StringComparison.Ordinal);
            if (separator <= prefix.Length) break;
            string amount = title[(separator + 2)..];
            int unitStart = amount.LastIndexOf(' ');
            if (unitStart > 0 && amount[0] is >= '0' and <= '9' &&
                amount[(unitStart + 1)..].ToUpperInvariant() is "B" or "KB" or "MB" or "GB" or "TB" or "KIB" or "MIB" or "GIB")
                return title[prefix.Length..separator];
            break;
        }
        return title;
    }

    private static string StripNotificationCount(string title)
    {
        if (!title.StartsWith('(')) return title;
        int end = title.IndexOf(") ", StringComparison.Ordinal);
        return end > 1 && end < 8 && title[1..end].TrimEnd('+').All(char.IsAsciiDigit)
            ? title[(end + 2)..] : title;
    }

    internal static Tab? FindBrowserTab(Window[] windows, string title, bool isAdvertisement)
    {
        if (string.IsNullOrWhiteSpace(title) && !isAdvertisement) return null;
        var budget = Stopwatch.StartNew();
        Tab? best = null;
        int bestScore = 0;
        int audibleCount = 0, videoCount = 0;
        bool scanComplete = true;
        Tab? audibleVideo = null, onlyVideo = null;
        var cache = new CacheRequest { TreeScope = TreeScope.Element };
        cache.Add(AutomationElement.ControlTypeProperty);
        cache.Add(AutomationElement.NameProperty);
        cache.Add(SelectionItemPattern.Pattern);
        cache.Add(InvokePattern.Pattern);

        foreach (var window in windows)
        {
            if (budget.ElapsedMilliseconds > 2400)
            {
                scanComplete = false;
                break;
            }
            try
            {
                var root = AutomationElement.FromHandle(window.Handle).GetUpdatedCache(cache);
                var queue = new Queue<AutomationElement>();
                var tree = TreeWalker.ControlViewWalker;
                queue.Enqueue(root);
                for (int visited = 0; queue.Count > 0 && visited < 700 && budget.ElapsedMilliseconds < 2400; visited++)
                {
                    var element = queue.Dequeue();
                    var type = element.Cached.ControlType;
                    if (type == ControlType.Document || type == ControlType.Menu) continue;
                    if (type == ControlType.TabItem)
                    {
                        string caption = element.Cached.Name;
                        var label = ParseTabLabel(caption);
                        if (label.Audible) audibleCount++;
                        if (label.YouTubeVideo) videoCount++;

                        if (element.TryGetCachedPattern(SelectionItemPattern.Pattern, out _) ||
                            element.TryGetCachedPattern(InvokePattern.Pattern, out _))
                        {
                            var candidate = new Tab(window, element);
                            int score = ScoreTab(caption, title, label);
                            if (score > bestScore)
                            {
                                best = candidate;
                                bestScore = score;
                                if (score == 3) return best;
                            }
                            if (label.YouTubeVideo)
                            {
                                onlyVideo = candidate;
                                if (label.Audible) audibleVideo = candidate;
                            }
                        }
                        continue;
                    }

                    for (var child = tree.GetFirstChild(element, cache); child is not null &&
                        budget.ElapsedMilliseconds < 2400; child = tree.GetNextSibling(child, cache))
                    {
                        if (queue.Count >= 700)
                        {
                            scanComplete = false;
                            break;
                        }
                        queue.Enqueue(child);
                    }
                }

                scanComplete &= queue.Count == 0 && budget.ElapsedMilliseconds < 2400;
            }
            catch (Exception ex) when (ex is COMException or ElementNotAvailableException or InvalidOperationException or ArgumentException or UnauthorizedAccessException)
            {
                scanComplete = false;
            }
        }

        if (best is not null || !scanComplete) return best;
        // During an ad, the browser's media title can describe the advertiser instead of the video.
        if (audibleCount == 1 && audibleVideo is not null) return audibleVideo;
        return isAdvertisement && audibleCount == 0 && videoCount == 1 ? onlyVideo : null;
    }

    internal static void SelectTab(Tab tab)
    {
        try
        {
            if (tab.Element.TryGetCurrentPattern(SelectionItemPattern.Pattern, out var pattern))
            {
                var selection = (SelectionItemPattern)pattern;
                if (!selection.Current.IsSelected) selection.Select();
                if (selection.Current.IsSelected) return;
            }
        }
        catch (Exception ex) when (ex is COMException or ElementNotAvailableException or InvalidOperationException or UnauthorizedAccessException) { }

        try
        {
            if (tab.Element.TryGetCurrentPattern(InvokePattern.Pattern, out var pattern))
                ((InvokePattern)pattern).Invoke();
        }
        catch (Exception ex) when (ex is COMException or ElementNotAvailableException or InvalidOperationException or UnauthorizedAccessException) { }
    }

    internal static bool IsMinimized(Window target) => IsIconic(target.Handle);

    internal static bool BringForward(Window target)
    {
        if (IsIconic(target.Handle)) ShowWindowAsync(target.Handle, 9);
        return SetForegroundWindow(target.Handle);
    }

    private static bool MatchesId(string source, string id) =>
        string.Equals(source, id, StringComparison.OrdinalIgnoreCase) ||
        source.StartsWith(id + ".", StringComparison.OrdinalIgnoreCase) ||
        source.StartsWith(id + "_", StringComparison.OrdinalIgnoreCase) ||
        source.StartsWith(id + "!", StringComparison.OrdinalIgnoreCase);

    private static bool MatchesProcess(uint pid, string source, string? processName)
    {
        if (pid == 0 || pid == Environment.ProcessId) return false;
        if (processName is not null)
        {
            try
            {
                using var process = Process.GetProcessById((int)pid);
                if (string.Equals(process.ProcessName, processName, StringComparison.OrdinalIgnoreCase)) return true;
            }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception) { }
        }

        nint processHandle = OpenProcess(0x1000, false, pid);
        if (processHandle == 0) return false;
        try
        {
            uint length = 256;
            var id = new StringBuilder((int)length);
            return GetApplicationUserModelId(processHandle, ref length, id) == 0 &&
                string.Equals(id.ToString(), source, StringComparison.OrdinalIgnoreCase);
        }
        finally { CloseHandle(processHandle); }
    }

    private delegate bool EnumWindow(nint window, nint parameter);
    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindow callback, nint parameter);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(nint window);
    [DllImport("user32.dll")] private static extern nint GetWindow(nint window, uint command);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(nint window, StringBuilder title, int capacity);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint window, out uint process);
    [DllImport("user32.dll")] private static extern bool IsIconic(nint window);
    [DllImport("user32.dll")] private static extern bool ShowWindowAsync(nint window, int command);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(nint window);
    [DllImport("kernel32.dll")] private static extern nint OpenProcess(uint access, bool inherit, uint pid);
    [DllImport("kernel32.dll")] private static extern bool CloseHandle(nint handle);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int GetApplicationUserModelId(nint process, ref uint length, StringBuilder id);
}








