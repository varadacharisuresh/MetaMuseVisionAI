namespace UvcViewer;

public partial class App : Application
{
    public App() => InitializeComponent();

    // Show the branded full-screen launch page before starting the UVC viewer.
    // The launch page navigates to the EXISTING MainPage without changing its
    // camera, scanning, voice, or checkout logic.
    protected override Window CreateWindow(IActivationState? activationState)
        => new Window(new LaunchSplashPage());
}
