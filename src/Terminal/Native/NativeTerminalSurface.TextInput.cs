using System.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace Resesh.Terminal.Native;

/// <summary>
/// Text input through a hidden <see cref="TextBox"/> parked on the cursor cell, as xterm.js
/// parks a textarea. XAML only connects the IME (and the emoji panel, dictation, the touch
/// keyboard) to text controls, so keyboard focus lives in this box: keys the terminal encodes
/// are handled in the surface's PreviewKeyDown before the box sees them; text the box
/// receives is sent and cleared. While an IME composes, the box shows the composition over
/// the cursor in the terminal font and the keys belong to the IME.
/// </summary>
public sealed partial class NativeTerminalSurface
{
    private readonly TextBox _textInput = new();
    private readonly SolidColorBrush _textInputForeground = new();
    private readonly SolidColorBrush _textInputBackground = new();
    private bool _composing;
    private bool _flushQueued;

    private void ConfigureTextInput()
    {
        var box = _textInput;
        AutomationProperties.SetAutomationId(box, "TerminalTextInput");
        AutomationProperties.SetAccessibilityView(box, AccessibilityView.Raw);
        box.IsSpellCheckEnabled = false;
        box.IsTextPredictionEnabled = false; // no suggestion popups over the terminal
        box.AcceptsReturn = false;
        box.TextWrapping = TextWrapping.NoWrap;
        box.ContextFlyout = null;
        box.SelectionFlyout = null;
        box.UseSystemFocusVisuals = false;
        box.IsHitTestVisible = false;
        box.BorderThickness = new Thickness(0);
        box.Padding = new Thickness(0);
        box.MinWidth = 0;
        box.MinHeight = 0;
        box.HorizontalAlignment = HorizontalAlignment.Left;
        box.VerticalAlignment = VerticalAlignment.Top;
        box.Opacity = 0;
        // Composition text takes the terminal's colors in every visual state.
        foreach (var state in new[] { "", "PointerOver", "Focused", "Disabled" })
        {
            box.Resources["TextControlForeground" + state] = _textInputForeground;
            box.Resources["TextControlBackground" + state] = _textInputBackground;
            box.Resources["TextControlBorderBrush" + state] = _textInputBackground;
        }
        box.Loaded += (_, _) => HideDeleteButton(box);
        // TextChanging, not TextChanged: the latter is raised late and not for every change
        // (a UI Automation SetValue never raises it). The box cannot be edited inside the
        // event, so the flush is queued; it still runs before the next key message.
        box.TextChanging += (_, e) =>
        {
            if (e.IsContentChanging && !_composing && !_flushQueued)
            {
                _flushQueued = true;
                DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.High, FlushTextInput);
            }
        };
        box.TextCompositionStarted += (_, _) =>
        {
            _composing = true;
            PlaceTextInput();
            box.Opacity = 1;
        };
        box.TextCompositionEnded += (_, _) =>
        {
            _composing = false;
            box.Opacity = 0;
            // The committed text lands after this event.
            _flushQueued = true;
            DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.High, FlushTextInput);
        };
        GotFocus += (_, args) =>
        {
            // The surface itself is only a way in (tab order, automation): the box takes focus.
            if (ReferenceEquals(args.OriginalSource, this))
                _textInput.Focus(FocusState == FocusState.Unfocused ? FocusState.Programmatic : FocusState);
        };
        Children.Add(box);
        ApplyTextInputStyle();
    }

    /// <summary>The single-line TextBox shows a clear button once it holds text, which a
    /// composition does; its visual states toggle Visibility, so it is sized away instead.</summary>
    private static void HideDeleteButton(DependencyObject root)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is FrameworkElement { Name: "DeleteButton" } button)
            {
                button.Width = 0;
                button.MaxWidth = 0;
                return;
            }
            HideDeleteButton(child);
        }
    }

    private void ApplyTextInputStyle()
    {
        _textInput.FontFamily = new FontFamily(_fontFamily);
        _textInput.FontSize = EffectiveFontSize;
        _textInputForeground.Color = ToColor(_theme.Foreground);
        _textInputBackground.Color = ToColor(_theme.Background);
        PlaceTextInput();
    }

    /// <summary>Keeps the box on the cursor cell, so a composition (and the IME's candidate
    /// window, which follows the box's caret) starts where the text will go.</summary>
    private void PlaceTextInput()
    {
        if (_fontScale <= 0)
            return;
        var row = Math.Clamp(_lastInfo.CursorY, 0, Math.Max(0, Rows - 1));
        var column = Math.Clamp(_lastInfo.CursorX, 0, Math.Max(0, Columns - 1));
        var left = (_renderer.OriginX + column * _renderer.CellWidth) / _fontScale;
        var top = row * _renderer.CellHeight / _fontScale;
        var margin = new Thickness(left, top, 0, 0);
        if (_textInput.Margin != margin)
            _textInput.Margin = margin;
        var height = _renderer.CellHeight / _fontScale;
        if (_textInput.Height != height)
            _textInput.Height = height;
    }

    /// <summary>Sends what the box received (typed text, a committed composition, an emoji
    /// panel pick, dictation) and empties it.</summary>
    private void FlushTextInput()
    {
        _flushQueued = false;
        if (_composing)
            return;
        var text = _textInput.Text;
        if (text.Length == 0)
            return;
        _textInput.Text = "";
        // A key the terminal already encoded can still leave its character behind.
        if (_disposed || !_inputEnabled || _term == IntPtr.Zero || !_connected || _suppressCharactersForKey != 0)
            return;
        SendUserInput(Encoding.UTF8.GetBytes(text));
    }
}
