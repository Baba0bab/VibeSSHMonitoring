// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Baba0bab — Vibe SSH Monitoring

using System.Windows;
using System.Windows.Media;

namespace VibeSSHMonitoring.UI;

/// <summary>An entry of the language picker (its name is also the UI Automation name).</summary>
public record LanguageChoice(string Code, string Name, ImageSource? Flag)
{
    public override string ToString() => Name;
}

/// <summary>
/// Flags of the interface languages, drawn as vectors (Windows shows flag emojis as two letters).
/// All in a 4:3 box (640 × 480), rounded and with a faint light outline so that dark edges stay
/// visible on the dark theme. Spain's coat of arms is left out: it is a blur at this size.
/// </summary>
public static class Flags
{
    private const double Width = 640, Height = 480;

    private static readonly Dictionary<string, ImageSource> Cache = new();

    /// <summary>Flag for a language code of <see cref="Core.Loc.Languages"/>, or null if there is none.</summary>
    public static ImageSource? For(string code)
    {
        if (Cache.TryGetValue(code, out var cached))
            return cached;
        var parts = Parts(code);
        if (parts == null)
            return null;

        var group = new DrawingGroup { ClipGeometry = new RectangleGeometry(new Rect(0, 0, Width, Height), 48, 48) };
        foreach (var (color, path) in parts)
            group.Children.Add(new GeometryDrawing(Brush(color), null, Geometry.Parse(path)));
        group.Children.Add(new GeometryDrawing(null, new Pen(Brush("#38FFFFFF"), 24),
            new RectangleGeometry(new Rect(0, 0, Width, Height), 48, 48)));

        var image = new DrawingImage(group);
        image.Freeze();
        return Cache[code] = image;
    }

    private static (string Color, string Path)[]? Parts(string code) => code switch
    {
        // United Kingdom (Union Jack, counterchanged red saltire)
        "en" => new[]
        {
            ("#012169", "M0 0h640v480H0z"),
            ("#FFFFFF", "M75 0l244 181L562 0h78v62L400 241l240 178v61h-80L320 301L81 480H0v-60l239-178L0 64V0z"),
            ("#C8102E", "M424 281l216 159v40L369 281z M240 301l6 35L54 480H0z M640 0v3L391 191l2-44L590 0z M0 0l239 176h-60L0 42z"),
            ("#FFFFFF", "M241 0v480h160V0z M0 160v160h640V160z"),
            ("#C8102E", "M0 193v96h640v-96z M273 0v480h96V0z")
        },
        "fr" => new[]
        {
            ("#FFFFFF", "M0 0h640v480H0z"),
            ("#000091", "M0 0h213.3v480H0z"),
            ("#E1000F", "M426.7 0H640v480H426.7z")
        },
        "de" => new[]
        {
            ("#FFCE00", "M0 0h640v480H0z"),
            ("#000000", "M0 0h640v160H0z"),
            ("#DD0000", "M0 160h640v160H0z")
        },
        "es" => new[]
        {
            ("#AA151B", "M0 0h640v480H0z"),
            ("#F1BF00", "M0 120h640v240H0z")
        },
        _ => null
    };

    private static SolidColorBrush Brush(string color)
    {
        var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(color));
        brush.Freeze();
        return brush;
    }
}
