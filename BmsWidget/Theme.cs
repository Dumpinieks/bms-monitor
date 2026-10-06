using Microsoft.Win32;

namespace BmsWidget;

enum Level { Unknown, Good, Medium, Low }

/// <summary>Colors that fit the taskbar, which follows the Windows (not app) light/dark setting.</summary>
sealed record Theme(Color Background, Color Text, Color SecondaryText, Color Good, Color Medium, Color Low)
{
    static readonly Theme Dark = new(
        Color.FromArgb(32, 32, 32), Color.White, Color.FromArgb(190, 190, 190),
        Color.FromArgb(108, 203, 95), Color.FromArgb(252, 196, 25), Color.FromArgb(255, 99, 99));

    static readonly Theme Light = new(
        Color.FromArgb(238, 238, 238), Color.Black, Color.FromArgb(80, 80, 80),
        Color.FromArgb(16, 124, 16), Color.FromArgb(157, 93, 0), Color.FromArgb(196, 43, 28));

    public static Theme Current
    {
        get
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("SystemUsesLightTheme") is int and not 0 ? Light : Dark;
        }
    }

    public Color For(Level level) => level switch
    {
        Level.Good => Good,
        Level.Medium => Medium,
        Level.Low => Low,
        _ => SecondaryText,
    };
}
