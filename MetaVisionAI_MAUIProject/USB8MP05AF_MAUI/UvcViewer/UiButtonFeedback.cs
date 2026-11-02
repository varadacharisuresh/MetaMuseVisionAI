using System;
using System.Threading.Tasks;
using Microsoft.Maui.Controls;

namespace UvcViewer;

/// <summary>Unified press/release animation, including icon-button containers.</summary>
internal static class UiButtonFeedback
{
    private static VisualElement? Target(object sender)
    {
        if (sender is not Button button || !button.IsEnabled)
            return null;

        // Most icon controls draw the real icon behind a transparent Button.
        // Animate the wrapping Grid so the icon AND its background respond.
        if (button.Text == string.Empty && button.Parent is Grid parent)
            return parent;

        return button;
    }

    public static void Press(object sender)
    {
        var target = Target(sender);
        if (target is not null)
            _ = Animate(target, 0.91, 0.75, 85);
    }

    public static void Release(object sender)
    {
        var target = Target(sender);
        if (target is not null)
            _ = BounceBack(target);
    }

    // Even very quick taps receive an observable press-and-return animation.
    private static async Task BounceBack(VisualElement element)
    {
        await Animate(element, 0.94, 0.82, 55);
        await Animate(element, 1.0, 1.0, 155);
    }

    private static async Task Animate(VisualElement element, double scale,
                                      double opacity, uint milliseconds)
    {
        try
        {
            element.CancelAnimations();
            await Task.WhenAll(
                element.ScaleToAsync(scale, milliseconds, Easing.CubicOut),
                element.FadeToAsync(opacity, milliseconds, Easing.CubicOut));
        }
        catch (ObjectDisposedException) { /* Navigation can dispose a tapped control. */ }
        catch (InvalidOperationException) { /* A page may detach during navigation. */ }
    }

    public static async Task TapAsync(VisualElement element)
    {
        await Animate(element, 0.97, 0.85, 70);
        await Animate(element, 1.0, 1.0, 110);
    }
}
