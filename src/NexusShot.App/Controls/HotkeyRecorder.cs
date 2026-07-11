using System.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using NexusShot.App.Models;
using Windows.System;
using Windows.UI.Core;

namespace NexusShot.App.Controls;

/// <summary>
/// A shortcut field: click it, press the new combination, done. Single keys (F9, PrtScn, …) are
/// valid gestures — modifiers are optional, like the system snipping tool's PrtScn binding.
/// Escape cancels the recording; Backspace restores the action's default.
/// </summary>
public sealed class HotkeyRecorder : Button
{
    private HotkeyBinding _binding = new();
    private HotkeyBinding _defaultBinding = new();
    private bool _isRecording;

    public HotkeyRecorder()
    {
        MinWidth = 148;
        Click += (_, _) => StartRecording();
        LostFocus += (_, _) => StopRecording();
        PreviewKeyDown += OnPreviewKeyDown;
        ShowText();
    }

    /// <summary>Raised when the user commits a new gesture (including a reset to default).</summary>
    public event EventHandler<HotkeyBinding>? GestureChanged;

    /// <summary>What Backspace restores while recording.</summary>
    public HotkeyBinding DefaultBinding
    {
        get => _defaultBinding;
        set => _defaultBinding = value.Clone();
    }

    public HotkeyBinding Binding
    {
        get => _binding;
        set
        {
            _binding = value.Clone();
            if (!_isRecording) ShowText();
        }
    }

    private void StartRecording()
    {
        _isRecording = true;
        Content = "Press keys…";
    }

    private void StopRecording()
    {
        _isRecording = false;
        ShowText();
    }

    private void OnPreviewKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (!_isRecording) return;
        e.Handled = true;

        switch (e.Key)
        {
            case VirtualKey.Escape:
                StopRecording();
                return;

            case VirtualKey.Back:
                Commit(_defaultBinding.Clone());
                return;

            // A modifier alone is not a gesture; keep recording until a real key arrives.
            case VirtualKey.Control or VirtualKey.Shift or VirtualKey.Menu
                or VirtualKey.LeftWindows or VirtualKey.RightWindows
                or VirtualKey.LeftControl or VirtualKey.RightControl
                or VirtualKey.LeftShift or VirtualKey.RightShift
                or VirtualKey.LeftMenu or VirtualKey.RightMenu:
                return;
        }

        Commit(new HotkeyBinding { Modifiers = CurrentModifiers(), Key = (uint)e.Key });
    }

    private void Commit(HotkeyBinding binding)
    {
        _binding = binding;
        StopRecording();
        GestureChanged?.Invoke(this, _binding.Clone());
    }

    /// <summary>Win32 RegisterHotKey modifier flags for whatever is held right now.</summary>
    private static uint CurrentModifiers()
    {
        uint modifiers = 0;
        if (IsDown(VirtualKey.Menu)) modifiers |= 0x0001;
        if (IsDown(VirtualKey.Control)) modifiers |= 0x0002;
        if (IsDown(VirtualKey.Shift)) modifiers |= 0x0004;
        if (IsDown(VirtualKey.LeftWindows) || IsDown(VirtualKey.RightWindows)) modifiers |= 0x0008;
        return modifiers;
    }

    private static bool IsDown(VirtualKey key) => Microsoft.UI.Input.InputKeyboardSource
        .GetKeyStateForCurrentThread(key)
        .HasFlag(CoreVirtualKeyStates.Down);

    private void ShowText() => Content = Format(_binding);

    /// <summary>Human-readable gesture, e.g. "Ctrl + Shift + S" or "F9".</summary>
    public static string Format(HotkeyBinding binding)
    {
        var text = new StringBuilder();
        if ((binding.Modifiers & 0x0002) != 0) text.Append("Ctrl + ");
        if ((binding.Modifiers & 0x0001) != 0) text.Append("Alt + ");
        if ((binding.Modifiers & 0x0004) != 0) text.Append("Shift + ");
        if ((binding.Modifiers & 0x0008) != 0) text.Append("Win + ");
        text.Append(KeyName(binding.Key));
        return text.ToString();
    }

    private static string KeyName(uint key) => (VirtualKey)key switch
    {
        0 => "None",
        VirtualKey.Snapshot => "PrtScn",
        >= VirtualKey.Number0 and <= VirtualKey.Number9 => ((char)key).ToString(),
        >= VirtualKey.A and <= VirtualKey.Z => ((char)key).ToString(),
        VirtualKey.Left => "←",
        VirtualKey.Right => "→",
        VirtualKey.Up => "↑",
        VirtualKey.Down => "↓",
        var named when Enum.IsDefined(named) => named.ToString(),
        _ => $"Key 0x{key:X2}",
    };
}
