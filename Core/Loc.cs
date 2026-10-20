// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Baba0bab — Vibe SSH Monitoring

using System.Globalization;
using System.Reflection;
using System.Text.Json;

namespace VibeSSHMonitoring.Core;

/// <summary>
/// User interface translations. Strings live in Localization/{code}.json (embedded resources,
/// flat "Area.Key" → text). English is the reference: any key missing from another language
/// falls back to English, and a key missing everywhere shows as the key itself.
/// </summary>
public static class Loc
{
    public const string SystemLanguage = "system";
    public const string DefaultLanguage = "en";

    /// <summary>Supported languages, with their name in their own language (for the language picker).</summary>
    public static readonly IReadOnlyList<(string Code, string NativeName)> Languages = new[]
    {
        ("en", "English"),
        ("fr", "Français"),
        ("de", "Deutsch"),
        ("es", "Español")
    };

    private static Dictionary<string, string>? _strings;
    private static Dictionary<string, string>? _fallback;

    // Windows display language and regional format, read before Initialize replaces the
    // thread cultures: "System" must keep meaning Windows' language, not the chosen one
    private static readonly CultureInfo WindowsUICulture = CultureInfo.CurrentUICulture;
    private static readonly CultureInfo WindowsCulture = CultureInfo.CurrentCulture;

    /// <summary>Language in use (two-letter code).</summary>
    public static string Language { get; private set; } = DefaultLanguage;

    /// <summary>Culture used for numbers and dates, matching <see cref="Language"/>.</summary>
    public static CultureInfo Culture { get; private set; } = CultureInfo.GetCultureInfo(DefaultLanguage);

    /// <summary>Language for a setting value: "system" (or empty) follows the Windows display language.</summary>
    public static string Resolve(string? setting)
    {
        string code = string.IsNullOrWhiteSpace(setting) || setting == SystemLanguage
            ? WindowsUICulture.TwoLetterISOLanguageName
            : setting;
        return Languages.Any(l => l.Code == code) ? code : DefaultLanguage;
    }

    /// <summary>
    /// Loads the language and makes it the current culture of every thread. Call once at startup,
    /// before any window is created: the interface reads its texts when it is built.
    /// </summary>
    public static void Initialize(string? setting)
    {
        Language = Resolve(setting);
        _fallback = Load(DefaultLanguage);
        _strings = Language == DefaultLanguage ? _fallback : Load(Language);

        // Keep the user's regional format (fr-CA, de-AT…) when it matches the chosen language
        Culture = WindowsCulture.TwoLetterISOLanguageName == Language ? WindowsCulture : CultureInfo.GetCultureInfo(Language);
        CultureInfo.DefaultThreadCurrentCulture = Culture;
        CultureInfo.DefaultThreadCurrentUICulture = Culture;
        CultureInfo.CurrentCulture = Culture;
        CultureInfo.CurrentUICulture = Culture;
    }

    /// <summary>Translated text for <paramref name="key"/>.</summary>
    public static string T(string key)
    {
        if (_strings is null)
            Initialize(DefaultLanguage);   // tests and tools that never called Initialize
        if (_strings!.TryGetValue(key, out var text) || _fallback!.TryGetValue(key, out text))
            return text;
        return key;
    }

    /// <summary>Translated text with {0}, {1}… placeholders, formatted with the current culture.</summary>
    public static string T(string key, params object?[] args) => string.Format(Culture, T(key), args);

    /// <summary>Raw strings of a language (tests: every language must have the same keys).</summary>
    public static Dictionary<string, string> Load(string code)
    {
        string resource = $"VibeSSHMonitoring.Localization.{code}.json";
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(resource)
            ?? throw new InvalidOperationException($"Missing translation resource: {resource}");
        return JsonSerializer.Deserialize<Dictionary<string, string>>(stream) ?? new();
    }
}
