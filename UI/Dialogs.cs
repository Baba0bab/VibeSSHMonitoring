// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Baba0bab — Vibe SSH Monitoring

using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;

namespace VibeSSHMonitoring.UI;

/// <summary>Boîtes de dialogue au thème de l'application (confirmation, erreur, mot de passe).</summary>
public static class Dialogs
{
    public static bool Confirm(Window? owner, string title, string message, string confirmText = "Confirmer",
        bool danger = false, string? detail = null, string cancelText = "Annuler")
    {
        var dialog = new DialogWindow(owner, title, message, detail, danger ? "" : "",
            danger ? "WarningBrush" : "AccentLightBrush");
        dialog.AddButton(cancelText, isDefault: false, style: null, result: false);
        dialog.AddButton(confirmText, isDefault: true, style: danger ? "DangerAccentButton" : "AccentButton", result: true);
        return dialog.ShowDialog() == true;
    }

    public static void Info(Window? owner, string title, string message, bool error = false, string? detail = null)
    {
        var dialog = new DialogWindow(owner, title, message, detail, error ? "" : "",
            error ? "DangerBrush" : "AccentLightBrush");
        dialog.AddButton("OK", isDefault: true, style: "AccentButton", result: true);
        dialog.ShowDialog();
    }

    /// <summary>
    /// Saisie d'un mot de passe (deux champs si <paramref name="confirm"/>). <paramref name="validate"/>
    /// renvoie null si la saisie est acceptée, sinon le message d'erreur ; il peut être lent (Argon2).
    /// </summary>
    public static string? Password(Window? owner, string title, string message, bool confirm,
        Func<string, Task<string?>>? validate = null, string okText = "Valider", string cancelText = "Annuler")
    {
        var dialog = new DialogWindow(owner, title, message, null, "", "AccentLightBrush");
        var first = dialog.AddPassword(confirm ? "Mot de passe" : null);
        var second = confirm ? dialog.AddPassword("Confirmation") : null;
        string? accepted = null;

        dialog.AddButton(cancelText, isDefault: false, style: null, result: false);
        dialog.AddButton(okText, isDefault: true, style: "AccentButton", result: null, onClick: async () =>
        {
            string value = first.Password;
            if (value.Length == 0)
            {
                dialog.ShowError("Saisissez un mot de passe.");
                return;
            }
            if (confirm)
            {
                if (value.Length < 8)
                {
                    dialog.ShowError("8 caractères minimum.");
                    return;
                }
                if (value != second!.Password)
                {
                    dialog.ShowError("Les deux mots de passe ne correspondent pas.");
                    return;
                }
            }
            if (validate is not null)
            {
                dialog.SetBusy(true);
                string? error = await validate(value);
                dialog.SetBusy(false);
                if (error is not null)
                {
                    dialog.ShowError(error);
                    first.SelectAll();
                    first.Focus();
                    return;
                }
            }
            accepted = value;
            dialog.DialogResult = true;
        });

        dialog.Loaded += (_, _) => first.Focus();
        return dialog.ShowDialog() == true ? accepted : null;
    }

    /// <summary>Fenêtre active de l'application, pour centrer les dialogues dessus.</summary>
    public static Window? ActiveWindow() =>
        Application.Current.Windows.OfType<Window>().FirstOrDefault(w => w.IsActive && w.IsVisible)
        ?? Application.Current.Windows.OfType<Window>().FirstOrDefault(w => w.IsVisible && w is not DialogWindow);
}

internal sealed class DialogWindow : Window
{
    private readonly StackPanel _body;
    private readonly StackPanel _buttons;
    private readonly TextBlock _error;
    private readonly Control _spinner;

    public DialogWindow(Window? owner, string title, string message, string? detail, string glyph, string glyphBrush)
    {
        Title = title;
        Background = (Brush)FindResource("BaseBrush");
        Foreground = (Brush)FindResource("TextBrush");
        FontFamily = (FontFamily)FindResource("TextFont");
        FontSize = 14;
        UseLayoutRounding = true;
        ResizeMode = ResizeMode.NoResize;
        SizeToContent = SizeToContent.Height;
        Width = 480;
        ShowInTaskbar = owner is null;
        if (owner is { IsVisible: true })
        {
            Owner = owner;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
        }
        else
        {
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
        }
        SourceInitialized += (_, _) => ApplyDarkFrame(this);

        var root = new Grid { Margin = new Thickness(24, 20, 24, 20) };
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        root.ColumnDefinitions.Add(new ColumnDefinition());

        var icon = new TextBlock
        {
            Style = (Style)FindResource("Icon"),
            Text = glyph,
            FontSize = 22,
            Foreground = (Brush)FindResource(glyphBrush),
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, 2, 16, 0)
        };
        root.Children.Add(icon);

        _body = new StackPanel();
        Grid.SetColumn(_body, 1);
        root.Children.Add(_body);

        _body.Children.Add(new TextBlock
        {
            Text = title,
            FontFamily = (FontFamily)FindResource("DisplayFont"),
            FontSize = 18,
            FontWeight = FontWeights.SemiBold,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 8)
        });
        _body.Children.Add(new TextBlock
        {
            Text = message,
            TextWrapping = TextWrapping.Wrap,
            Foreground = (Brush)FindResource("TextSecondaryBrush"),
            Margin = new Thickness(0, 0, 0, 12)
        });
        if (detail is not null)
        {
            _body.Children.Add(new Border
            {
                Background = (Brush)FindResource("InsetBrush"),
                BorderBrush = (Brush)FindResource("StrokeBrush"),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(6),
                Padding = new Thickness(12, 10, 12, 10),
                Margin = new Thickness(0, 0, 0, 12),
                Child = new TextBox
                {
                    Text = detail,
                    IsReadOnly = true,
                    BorderThickness = new Thickness(0),
                    Background = Brushes.Transparent,
                    Foreground = (Brush)FindResource("TextBrush"),
                    FontFamily = (FontFamily)FindResource("MonoFont"),
                    FontSize = 12,
                    TextWrapping = TextWrapping.Wrap,
                    Template = PlainTextBoxTemplate()
                }
            });
        }

        _error = new TextBlock
        {
            Foreground = (Brush)FindResource("DangerBrush"),
            FontSize = 13,
            TextWrapping = TextWrapping.Wrap,
            Visibility = Visibility.Collapsed,
            Margin = new Thickness(0, 0, 0, 8)
        };
        _body.Children.Add(_error);

        var footer = new Grid { Margin = new Thickness(0, 8, 0, 0) };
        _spinner = new Control
        {
            Style = (Style)FindResource("Spinner"),
            HorizontalAlignment = HorizontalAlignment.Left,
            Visibility = Visibility.Collapsed
        };
        footer.Children.Add(_spinner);
        _buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        footer.Children.Add(_buttons);
        _body.Children.Add(footer);

        Content = root;
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape && _buttons.IsEnabled)
                DialogResult = false;
        };
    }

    public PasswordBox AddPassword(string? label)
    {
        if (label is not null)
            _body.Children.Insert(_body.Children.IndexOf(_error), new TextBlock
            {
                Text = label,
                Style = (Style)FindResource("Caption"),
                Margin = new Thickness(0, 0, 0, 4)
            });
        var box = new PasswordBox { Margin = new Thickness(0, 0, 0, 10) };
        _body.Children.Insert(_body.Children.IndexOf(_error), box);
        return box;
    }

    public void AddButton(string text, bool isDefault, string? style, bool? result, Func<Task>? onClick = null)
    {
        var button = new Button
        {
            Content = text,
            MinWidth = 100,
            IsDefault = isDefault,
            Margin = new Thickness(8, 0, 0, 0)
        };
        if (style is not null)
            button.Style = (Style)FindResource(style);
        button.Click += async (_, _) =>
        {
            if (onClick is not null)
                await onClick();
            else
                DialogResult = result;
        };
        _buttons.Children.Add(button);
    }

    public void ShowError(string message)
    {
        _error.Text = message;
        _error.Visibility = Visibility.Visible;
    }

    public void SetBusy(bool busy)
    {
        _buttons.IsEnabled = !busy;
        _spinner.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        if (busy)
            _error.Visibility = Visibility.Collapsed;
        foreach (var box in _body.Children.OfType<PasswordBox>())
            box.IsEnabled = !busy;
    }

    private static ControlTemplate PlainTextBoxTemplate()
    {
        var template = new ControlTemplate(typeof(TextBox));
        var host = new FrameworkElementFactory(typeof(ScrollViewer)) { Name = "PART_ContentHost" };
        template.VisualTree = host;
        return template;
    }

    /// <summary>Barre de titre sombre, de la couleur du fond (Windows 11).</summary>
    internal static void ApplyDarkFrame(Window window)
    {
        var hwnd = new WindowInteropHelper(window).Handle;
        var baseColor = (Color)window.FindResource("BaseColor");
        var textColor = (Color)window.FindResource("TextColor");
        Dwm.SetAttribute(hwnd, Dwm.UseImmersiveDarkMode, 1);
        Dwm.SetAttribute(hwnd, Dwm.CaptionColor, Dwm.ToColorRef(baseColor.R, baseColor.G, baseColor.B));
        Dwm.SetAttribute(hwnd, Dwm.TextColor, Dwm.ToColorRef(textColor.R, textColor.G, textColor.B));
    }
}
