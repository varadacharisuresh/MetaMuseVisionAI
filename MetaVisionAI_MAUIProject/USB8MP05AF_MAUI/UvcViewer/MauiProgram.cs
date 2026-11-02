using UvcViewer.Platforms.Android;
using UvcViewer.Views;

namespace UvcViewer;

public static class MauiProgram
{
    public static MauiApp CreateMauiApp()
    {
        var builder = MauiApp.CreateBuilder();
        builder.UseMauiApp<App>();
        builder.ConfigureMauiHandlers(handlers =>
            handlers.AddHandler<UvcPreview, UvcPreviewHandler>());
        return builder.Build();
    }
}
