namespace UvcViewer;

public partial class LaunchSplashPage : ContentPage
{
    private bool _navigationStarted;

    public LaunchSplashPage()
    {
        InitializeComponent();
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();

        if (_navigationStarted)
            return;

        _navigationStarted = true;

        // Show the branded page briefly, then open the existing scanner.
        // Delay runs while the app is active; no background service is created.
        await Task.Delay(1700);

        // Window is normally available in OnAppearing. If the page was
        // replaced before the delay ended, do not navigate again.
        var window = Window;
        if (window is not null && ReferenceEquals(window.Page, this))
            window.Page = new RoleSelectionPage();
    }
}
