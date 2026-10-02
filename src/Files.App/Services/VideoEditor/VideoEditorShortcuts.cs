// Copyright (c) Files Community. Licensed under the MIT License.
using Windows.System;
using Microsoft.UI.Input;
using Windows.UI.Core;
namespace Files.App.Services.VideoEditor;
public sealed record VideoEditorShortcut(string Action, VirtualKey Key, VirtualKeyModifiers Modifiers);
public static class VideoEditorShortcuts
{
    public static IReadOnlyList<VideoEditorShortcut> Defaults { get; } = [
        new("Play", VirtualKey.Space, VirtualKeyModifiers.None),
        new("Split", VirtualKey.S, VirtualKeyModifiers.None),
        new("Start", VirtualKey.Home, VirtualKeyModifiers.None),
        new("Open", VirtualKey.O, VirtualKeyModifiers.Control),
        new("Reset", VirtualKey.R, VirtualKeyModifiers.Control | VirtualKeyModifiers.Shift),
        new("Export", VirtualKey.E, VirtualKeyModifiers.Control | VirtualKeyModifiers.Shift),
        new("Close", VirtualKey.X, VirtualKeyModifiers.Control | VirtualKeyModifiers.Shift),
        new("ZoomIn", (VirtualKey)187, VirtualKeyModifiers.Control),
        new("ZoomOut", (VirtualKey)189, VirtualKeyModifiers.Control),
        new("Fit", VirtualKey.F, VirtualKeyModifiers.Control | VirtualKeyModifiers.Shift) ];

    public static List<VideoEditorShortcut> Load(string value)
    {
        var result = Defaults.ToList();
        foreach (var entry in (value ?? string.Empty).Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = entry.Split('=', 2);
            if (parts.Length != 2) continue;
            var keys = parts[1].Split(',');
            if (keys.Length != 2 || !int.TryParse(keys[0], out var key) || !int.TryParse(keys[1], out var modifiers)) continue;
            var index = result.FindIndex(item => item.Action == parts[0]);
            var shortcut = new VideoEditorShortcut(parts[0], (VirtualKey)key, (VirtualKeyModifiers)modifiers);
            if (index >= 0 && (key == 0 || IsSupported(shortcut))) result[index] = shortcut;
        }
        return result;
    }
    public static string Save(IEnumerable<VideoEditorShortcut> items)
        => string.Join(";", items.Select(item => $"{item.Action}={(int)item.Key},{(int)item.Modifiers}"));
    public static VirtualKeyModifiers CurrentModifiers()
    {
        var result = VirtualKeyModifiers.None;
        if (InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Control).HasFlag(CoreVirtualKeyStates.Down)) result |= VirtualKeyModifiers.Control;
        if (InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Shift).HasFlag(CoreVirtualKeyStates.Down)) result |= VirtualKeyModifiers.Shift;
        if (InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Menu).HasFlag(CoreVirtualKeyStates.Down)) result |= VirtualKeyModifiers.Menu;
        if (InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.LeftWindows).HasFlag(CoreVirtualKeyStates.Down) ||
            InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.RightWindows).HasFlag(CoreVirtualKeyStates.Down)) result |= VirtualKeyModifiers.Windows;
        return result;
    }
    public static bool IsSupported(VideoEditorShortcut item)
    {
        if ((int)item.Key <= 0 || (int)item.Key > 255 || item.Key is VirtualKey.Control or VirtualKey.Shift or VirtualKey.Menu or VirtualKey.LeftWindows or VirtualKey.RightWindows or VirtualKey.Tab or VirtualKey.Escape) return false;
        if ((item.Modifiers & ~(VirtualKeyModifiers.Control | VirtualKeyModifiers.Shift | VirtualKeyModifiers.Menu)) != 0) return false;
        if (item.Modifiers.HasFlag(VirtualKeyModifiers.Menu) && item.Key is VirtualKey.F4 or VirtualKey.Left or VirtualKey.Right) return false;
        if (item.Modifiers.HasFlag(VirtualKeyModifiers.Control) && item.Key is VirtualKey.T or VirtualKey.W or VirtualKey.L) return false;
        return true;
    }
    public static string Display(VideoEditorShortcut item)
    {
        if ((int)item.Key == 0) return Strings.VideoEditorShortcutDisabled.GetLocalizedResource();
        var keys = new List<string>();
        if (item.Modifiers.HasFlag(VirtualKeyModifiers.Control)) keys.Add("Ctrl");
        if (item.Modifiers.HasFlag(VirtualKeyModifiers.Menu)) keys.Add("Alt");
        if (item.Modifiers.HasFlag(VirtualKeyModifiers.Shift)) keys.Add("Shift");
        keys.Add((int)item.Key switch { 187 => "+", 189 => "−", >= 48 and <= 57 => ((char)(int)item.Key).ToString(), _ => item.Key.ToString() });
        return string.Join(" + ", keys);
    }
}
