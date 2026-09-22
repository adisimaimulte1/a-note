using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;

namespace ANote.Controls;

public sealed class NoClearTextBox : TextBox
{
    public bool SuppressPenHandwriting { get; set; }

    protected override void OnPointerPressed(PointerRoutedEventArgs e)
    {
        if (SuppressPenHandwriting && e.Pointer.PointerDeviceType == PointerDeviceType.Pen)
        {
            // WinUI 3 has no IsHandwritingViewEnabled API. Stop the pen event before
            // TextBox's editor receives it; focus exactly once from the real press so
            // keyboard entry and the native caret remain available.
            if (FocusState == FocusState.Unfocused)
                Focus(FocusState.Pointer);
            e.Handled = true;
            return;
        }

        base.OnPointerPressed(e);
    }
}
