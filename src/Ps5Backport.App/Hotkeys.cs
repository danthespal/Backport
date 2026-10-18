using Avalonia;
using Avalonia.Input;

namespace Ps5Backport.App;

/// <summary>The platform's command key: Ctrl on Windows and Linux, ⌘ on macOS.</summary>
static class Hotkeys
{
    public static KeyModifiers Command =>
        Application.Current?.PlatformSettings?.HotkeyConfiguration.CommandModifiers ?? KeyModifiers.Control;

    public static KeyGesture Gesture(Key key) => new(key, Command);

    /// <summary>A hint written with "Ctrl+" in the view, in the platform's terms ("Ctrl+K" -> "⌘K" on macOS).</summary>
    public static string Text(string hint) => Command == KeyModifiers.Control ? hint
        : System.Text.RegularExpressions.Regex.Replace(hint, @"Ctrl\+(\w+)", m => m.Groups[1].Length == 1
            ? Gesture(Enum.Parse<Key>(m.Groups[1].Value)).ToString("p", null) : "⌘+" + m.Groups[1].Value);
}
