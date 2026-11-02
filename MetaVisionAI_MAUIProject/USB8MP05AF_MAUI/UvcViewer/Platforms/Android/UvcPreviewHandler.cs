using Android.Graphics;
using Android.Views;
using Microsoft.Maui.Handlers;
using UvcViewer.Views;

namespace UvcViewer.Platforms.Android;

public sealed class UvcPreviewHandler : ViewHandler<UvcPreview, TextureView>
{
    public UvcCameraSession? Session { get; private set; }
    public static IPropertyMapper<UvcPreview, UvcPreviewHandler> Mapper =
    new PropertyMapper<UvcPreview, UvcPreviewHandler>(
        ViewHandler.ViewMapper);

    public UvcPreviewHandler() : base(Mapper)
    {
    }
    protected override TextureView CreatePlatformView() => new(Context);

    protected override void ConnectHandler(TextureView platformView)
    {
        base.ConnectHandler(platformView);
        Session = new UvcCameraSession(Context, platformView);
        platformView.SurfaceTextureListener = Session;
    }

    protected override void DisconnectHandler(TextureView platformView)
    {
        platformView.SurfaceTextureListener = null;
        if (Session is { } current) _ = current.StopAsync();
        Session = null;
        base.DisconnectHandler(platformView);
    }
}
