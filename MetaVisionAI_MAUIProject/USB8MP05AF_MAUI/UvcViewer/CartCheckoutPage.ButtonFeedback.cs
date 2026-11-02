using System;

namespace UvcViewer;

public partial class CartCheckoutPage
{
    private void OnUiButtonPressed(object sender, EventArgs e)
        => UiButtonFeedback.Press(sender);

    private void OnUiButtonReleased(object sender, EventArgs e)
        => UiButtonFeedback.Release(sender);
}
