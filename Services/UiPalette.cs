using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Media;

namespace VNotch.Services;

public record PinnThemeDefinition(
    string Id,
    string DisplayName,
    Color NotchColor,
    Color CardColor,
    Color BorderColor,
    Color AccentColor,
    Color PrimaryText,
    Color SecondaryText
);

internal static class UiPalette
{
    public static readonly PinnThemeDefinition SakuraPastel = new(
        "SakuraPastel",
        "Sakura Pastel 🌸",
        Color.FromRgb(31, 21, 27),
        Color.FromRgb(45, 30, 39),
        Color.FromRgb(74, 40, 56),
        Color.FromRgb(255, 183, 197),
        Color.FromRgb(255, 240, 245),
        Color.FromRgb(255, 209, 220)
    );

    public static readonly PinnThemeDefinition LavenderDream = new(
        "LavenderDream",
        "Lavender Dream 💜",
        Color.FromRgb(23, 21, 36),
        Color.FromRgb(34, 31, 51),
        Color.FromRgb(54, 47, 82),
        Color.FromRgb(195, 177, 225),
        Color.FromRgb(248, 248, 255),
        Color.FromRgb(230, 230, 250)
    );

    public static readonly PinnThemeDefinition MatchaLatte = new(
        "MatchaLatte",
        "Matcha Latte 🍵",
        Color.FromRgb(20, 26, 22),
        Color.FromRgb(28, 38, 31),
        Color.FromRgb(43, 61, 48),
        Color.FromRgb(169, 223, 191),
        Color.FromRgb(244, 251, 247),
        Color.FromRgb(212, 239, 223)
    );

    public static readonly PinnThemeDefinition CatppuccinMocha = new(
        "CatppuccinMocha",
        "Catppuccin Mocha 🐱",
        Color.FromRgb(30, 30, 46),
        Color.FromRgb(49, 50, 68),
        Color.FromRgb(69, 71, 90),
        Color.FromRgb(245, 194, 231),
        Color.FromRgb(205, 214, 244),
        Color.FromRgb(186, 194, 222)
    );

    public static readonly PinnThemeDefinition ClassicBlack = new(
        "ClassicBlack",
        "Classic Black 🖤",
        Colors.Black,
        Color.FromRgb(20, 20, 20),
        Color.FromRgb(38, 38, 38),
        Colors.White,
        Colors.White,
        Color.FromArgb(0xB3, 255, 255, 255)
    );

    public static readonly IReadOnlyList<PinnThemeDefinition> AllThemes = new[]
    {
        SakuraPastel,
        LavenderDream,
        MatchaLatte,
        CatppuccinMocha,
        ClassicBlack
    };

    public static Color PrimaryColor = Colors.White;
    public static Color SecondaryColor = Color.FromArgb(0xB3, 255, 255, 255);
    public static Color TertiaryColor = Color.FromArgb(0x73, 255, 255, 255);
    public static SolidColorBrush PrimaryBrush = CreateBrush(PrimaryColor);
    public static SolidColorBrush SecondaryBrush = CreateBrush(SecondaryColor);
    public static SolidColorBrush TertiaryBrush = CreateBrush(TertiaryColor);
    public static SolidColorBrush IconBrush = TertiaryBrush;
    public static SolidColorBrush AccentBrush = CreateBrush(SakuraPastel.AccentColor);

    public static PinnThemeDefinition CurrentTheme { get; private set; } = SakuraPastel;

    public static void ApplyTheme(string themeId, ResourceDictionary? resources = null)
    {
        var theme = themeId switch
        {
            "LavenderDream" => LavenderDream,
            "MatchaLatte" => MatchaLatte,
            "CatppuccinMocha" => CatppuccinMocha,
            "ClassicBlack" => ClassicBlack,
            _ => SakuraPastel
        };

        CurrentTheme = theme;
        PrimaryColor = theme.PrimaryText;
        SecondaryColor = theme.SecondaryText;
        TertiaryColor = Color.FromArgb(0x73, theme.PrimaryText.R, theme.PrimaryText.G, theme.PrimaryText.B);

        PrimaryBrush = CreateBrush(PrimaryColor);
        SecondaryBrush = CreateBrush(SecondaryColor);
        TertiaryBrush = CreateBrush(TertiaryColor);
        IconBrush = TertiaryBrush;
        AccentBrush = CreateBrush(theme.AccentColor);

        if (resources != null)
        {
            resources["NotchGradient"] = CreateBrush(theme.NotchColor);
            resources["CardGradient"] = CreateBrush(theme.CardColor);
            resources["NotchBorderBrush"] = CreateBrush(theme.BorderColor);
            resources["AccentBrush"] = AccentBrush;
        }

        if (Application.Current?.Resources != null)
        {
            Application.Current.Resources["NotchGradient"] = CreateBrush(theme.NotchColor);
            Application.Current.Resources["CardGradient"] = CreateBrush(theme.CardColor);
            Application.Current.Resources["NotchBorderBrush"] = CreateBrush(theme.BorderColor);
            Application.Current.Resources["AccentBrush"] = AccentBrush;
        }
    }

    private static SolidColorBrush CreateBrush(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }
}
