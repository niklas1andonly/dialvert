using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Dialvert.App.Interop;
using Dialvert.Core.Settings;

namespace Dialvert.App.Views;

/// <summary>Click, then press a key (combination) to bind it.</summary>
sealed class KeyCaptureBox : Button
{
    HotKey _binding = new(0);
    bool _capturing;

    /// <summary>Global hotkeys need a modifier or a function key; wheel mode keys may be anything.</summary>
    public bool RequireModifier { get; init; }

    public HotKey Binding
    {
        get => _binding;
        set { _binding = value; Render(); }
    }

    public KeyCaptureBox()
    {
        Style = (Style)Application.Current.FindResource("Btn");
        MinWidth = 120;
        FontFamily = (FontFamily)FindResource("Mono");
        BorderThickness = new Thickness(1, 1, 1, 3); // reads as a key
        Click += (_, _) => { _capturing = true; Content = "Press a key …"; BorderBrush = (Brush)FindResource("Signal"); Focus(); };
        LostKeyboardFocus += (_, _) => { _capturing = false; Render(); };
        PreviewKeyDown += OnKey;
    }

    void OnKey(object sender, KeyEventArgs e)
    {
        if (!_capturing) return;
        e.Handled = true;
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (key == Key.Escape) { _capturing = false; Render(); return; }

        var isModifier = key is Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt
                              or Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin;
        var mods = KeyNames.FromWpf(Keyboard.Modifiers);
        if (isModifier)
        {
            if (RequireModifier) return; // wait for the actual key
            // A bare modifier as mode key (e.g. Shift): store the generic VK without self-modifier.
            var vk = key switch
            {
                Key.LeftCtrl or Key.RightCtrl => Native.VK_CONTROL,
                Key.LeftAlt or Key.RightAlt => Native.VK_MENU,
                Key.LeftShift or Key.RightShift => Native.VK_SHIFT,
                _ => Native.VK_LWIN,
            };
            _binding = new HotKey(vk, vk == Native.VK_SHIFT ? KeyModifiers.Shift : vk == Native.VK_CONTROL ? KeyModifiers.Control
                                        : vk == Native.VK_MENU ? KeyModifiers.Alt : KeyModifiers.Win);
        }
        else
        {
            var isFunctionKey = key is >= Key.F1 and <= Key.F24;
            if (RequireModifier && mods == KeyModifiers.None && !isFunctionKey) { Content = "Combine with Ctrl/Alt/Shift…"; return; }
            _binding = new HotKey(KeyInterop.VirtualKeyFromKey(key), mods);
        }
        _capturing = false;
        Render();
    }

    void Render()
    {
        Content = _binding.VirtualKey == 0 ? "(none)" : KeyNames.Describe(_binding);
        BorderBrush = (Brush)FindResource("StrokeHi");
    }
}
