// Copyright (c) Files Community. Licensed under the MIT License.
using System.Diagnostics;
using System.Runtime.InteropServices.Marshalling;
using System.Text.RegularExpressions;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.System.Com;
using Windows.Win32.UI.Accessibility;
using Windows.Win32.UI.WindowsAndMessaging;

namespace Files.App.Services.ResourceManager;

internal static class ChromeImageSourceService
{
    internal enum ReadStatus { Success, NoChrome, Unavailable, NoNumber, TimedOut }
    internal sealed record ReadResult(ReadStatus Status, string? Number = null);

    private static readonly SemaphoreSlim Reader = new(1, 1);

    public static async Task<ReadResult> ReadSourceNumberAsync()
    {
        if (!Reader.Wait(0)) return new(ReadStatus.Unavailable);
        var read = Task.Run(() =>
        {
            try { return ReadSourceNumber(); }
            catch (Exception) { return new ReadResult(ReadStatus.Unavailable); }
            finally { Reader.Release(); }
        });
        try { return await read.WaitAsync(TimeSpan.FromSeconds(5)); }
        catch (TimeoutException) { return new(ReadStatus.TimedOut); }
    }

    internal static string? ExtractSourceNumber(string address)
    {
        if (!Uri.TryCreate(address.Contains("://", StringComparison.Ordinal) ? address : "https://" + address, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)) return null;
        var match = Regex.Match(uri.AbsolutePath.TrimEnd('/'), @"(?<![0-9])([0-9]{5})$");
        return match.Success ? match.Groups[1].Value : null;
    }

    private static unsafe ReadResult ReadSourceNumber()
    {
        var initialized = PInvoke.CoInitializeEx(null, COINIT.COINIT_MULTITHREADED);
        if (initialized.Failed) return new(ReadStatus.Unavailable);
        try
        {
            ReadResult? result = null;
            var window = PInvoke.GetWindow(PInvoke.GetForegroundWindow(), GET_WINDOW_CMD.GW_HWNDFIRST);
            Span<char> className = stackalloc char[256];
            for (var index = 0; window != HWND.Null && index < 256; index++, window = PInvoke.GetWindow(window, GET_WINDOW_CMD.GW_HWNDNEXT))
            {
                if (!PInvoke.IsWindowVisible(window)) continue;
                var length = PInvoke.GetClassName(window, className);
                if (!className[..length].StartsWith("Chrome_WidgetWin_", StringComparison.Ordinal)) continue;
                PInvoke.GetWindowThreadProcessId(window, out var processId);
                using var process = Process.GetProcessById((int)processId);
                if (!string.Equals(process.ProcessName, "chrome", StringComparison.OrdinalIgnoreCase)) continue;
                result = ReadAddress(window);
                if (result.Status == ReadStatus.Success || result.Status == ReadStatus.NoNumber) return result;
            }
            return result ?? new(ReadStatus.NoChrome);
        }
        finally { PInvoke.CoUninitialize(); }
    }

    private static unsafe ReadResult ReadAddress(HWND window)
    {
        if (PInvoke.CoCreateInstance(typeof(CUIAutomation).GUID, null, CLSCTX.CLSCTX_INPROC_SERVER, out IUIAutomation? automation).Failed || automation is null) return new(ReadStatus.Unavailable);
        if (automation.ElementFromHandle(window, out var root).Failed) return new(ReadStatus.Unavailable);
        if (automation.get_ControlViewWalker(out var walker).Failed) return new(ReadStatus.Unavailable);
        var pending = new Queue<(IUIAutomationElement Element, int Depth)>();
        pending.Enqueue((root, 0));
        var elapsed = Stopwatch.StartNew();
        for (var visited = 0; pending.Count > 0 && visited < 256 && elapsed.Elapsed < TimeSpan.FromSeconds(4); visited++)
        {
            var (element, depth) = pending.Dequeue();
            if (element.get_CurrentControlType(out var type).Failed) continue;
            // The browser address bar is outside the web document.
            if (type == UIA_CONTROLTYPE_ID.UIA_DocumentControlTypeId) continue;
            if (type == UIA_CONTROLTYPE_ID.UIA_EditControlTypeId)
            {
                var name = ReadProperty(element, UIA_PROPERTY_ID.UIA_NamePropertyId);
                var id = ReadProperty(element, UIA_PROPERTY_ID.UIA_AutomationIdPropertyId);
                if (IsAddressBar(name, id))
                {
                    var address = ReadProperty(element, UIA_PROPERTY_ID.UIA_ValueValuePropertyId);
                    if (string.IsNullOrWhiteSpace(address)) return new(ReadStatus.Unavailable);
                    var number = ExtractSourceNumber(address);
                    return number is null ? new(ReadStatus.NoNumber) : new(ReadStatus.Success, number);
                }
            }
            if (depth >= 12 || walker.GetFirstChildElement(element, out var child).Failed) continue;
            for (var siblings = 0; child is not null && siblings < 64; siblings++)
            {
                pending.Enqueue((child, depth + 1));
                if (walker.GetNextSiblingElement(child, out child).Failed) break;
            }
        }
        return new(ReadStatus.Unavailable);
    }

    internal static bool IsAddressBar(string name, string id) =>
        name.Contains("\u5730\u5740", StringComparison.Ordinal)
        || name.Equals("Address and search bar", StringComparison.OrdinalIgnoreCase)
        || id.Contains("omnibox", StringComparison.OrdinalIgnoreCase);

    private static string ReadProperty(IUIAutomationElement element, UIA_PROPERTY_ID property)
    {
        if (element.GetCurrentPropertyValue(property, out var value).Failed) return string.Empty;
        using (value) return value.VarType == System.Runtime.InteropServices.VarEnum.VT_BSTR ? (value.As<string>() ?? string.Empty) : string.Empty;
    }
}
