// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Baba0bab — Vibe SSH Monitoring

using System.Windows.Markup;
using VibeSSHMonitoring.Core;

namespace VibeSSHMonitoring.UI;

/// <summary>
/// Translated text in XAML: <c>Text="{ui:Tr Servers.Title}"</c>. The text is read when the element
/// is built, so a language change takes effect after a restart (see <see cref="Loc"/>).
/// </summary>
[MarkupExtensionReturnType(typeof(string))]
public sealed class TrExtension : MarkupExtension
{
    public TrExtension()
    {
    }

    public TrExtension(string key) => Key = key;

    [ConstructorArgument("key")]
    public string Key { get; set; } = "";

    public override object ProvideValue(IServiceProvider serviceProvider) => Loc.T(Key);
}
