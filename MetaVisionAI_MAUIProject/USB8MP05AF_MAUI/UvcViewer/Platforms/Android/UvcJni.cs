using Android.Content;
using Android.Runtime;
using Android.Views;

namespace UvcViewer.Platforms.Android;

public readonly record struct UvcFocusInfo(
    bool AutoSupported, bool ManualSupported,
    int Minimum, int Maximum, int Position, bool AutoEnabled);

public readonly record struct UvcVideoMode(int Width, int Height, int Fps, string Format)
{
    public override string ToString() => $"{Width} x {Height} @ {Fps} FPS ({Format})";
}

internal sealed class UvcJni : IDisposable
{
    private IntPtr _cameraClass;
    private IntPtr _companionClass;
    private IntPtr _camera;
    private readonly IntPtr _companionField;
    private readonly IntPtr _openMethod;
    private readonly IntPtr _startMethod;
    private readonly IntPtr _stopMethod;
    private readonly IntPtr _closeMethod;
    private readonly Context _context;

    public IReadOnlyList<UvcVideoMode> SupportedModes { get; private set; } = Array.Empty<UvcVideoMode>();

    public UvcJni(Context context)
    {
        _context = context;
        using var cameraClass = context.ClassLoader!.LoadClass("com.mikimn.droiduvc.UvcCamera")
            ?? throw new InvalidOperationException("Droid UVC is missing from the application.");
        using var companionClass = context.ClassLoader!.LoadClass("com.mikimn.droiduvc.UvcCamera$Companion")
            ?? throw new InvalidOperationException("Droid UVC companion class is missing.");
        _cameraClass = JNIEnv.NewGlobalRef(cameraClass.Handle);
        _companionClass = JNIEnv.NewGlobalRef(companionClass.Handle);
        try
        {
            _companionField = JNIEnv.GetStaticFieldID(_cameraClass, "Companion",
                "Lcom/mikimn/droiduvc/UvcCamera$Companion;");
            _openMethod = JNIEnv.GetMethodID(_companionClass, "open",
                "(I)Lcom/mikimn/droiduvc/UvcCamera;");
            _startMethod = JNIEnv.GetMethodID(_cameraClass, "start",
                "(Landroid/view/Surface;IIILcom/mikimn/droiduvc/StreamFormat;)Lcom/mikimn/droiduvc/StreamMode;");
            _stopMethod = JNIEnv.GetMethodID(_cameraClass, "stop", "()V");
            _closeMethod = JNIEnv.GetMethodID(_cameraClass, "close", "()V");
        }
        catch { Dispose(); throw; }
    }

    // Enumerate camera UVC descriptors from the actual connected device.
    // Note: an advertised mode can still fail because of USB bandwidth, power,
    // or decoder errors; UvcCameraSession also checks for delivered frames.
    private IReadOnlyList<UvcVideoMode> QuerySupportedModes()
    {
        var result = new List<UvcVideoMode>();
        var method = JNIEnv.GetMethodID(_cameraClass, "supportedModes", "()Ljava/util/List;");
        var list = JNIEnv.CallObjectMethod(_camera, method);
        if (list == IntPtr.Zero)
            return result;
        try
        {
            var listClass = JNIEnv.GetObjectClass(list);
            try
            {
                var sizeMethod = JNIEnv.GetMethodID(listClass, "size", "()I");
                var getMethod = JNIEnv.GetMethodID(listClass, "get", "(I)Ljava/lang/Object;");
                var count = JNIEnv.CallIntMethod(list, sizeMethod);
                for (var i = 0; i < count; i++)
                {
                    var item = JNIEnv.CallObjectMethod(list, getMethod, new JValue(i));
                    if (item == IntPtr.Zero) continue;
                    try
                    {
                        var itemClass = JNIEnv.GetObjectClass(item);
                        try
                        {
                            int width = JNIEnv.CallIntMethod(item,
                                JNIEnv.GetMethodID(itemClass, "getWidth", "()I"));
                            int height = JNIEnv.CallIntMethod(item,
                                JNIEnv.GetMethodID(itemClass, "getHeight", "()I"));
                            int fps = JNIEnv.CallIntMethod(item,
                                JNIEnv.GetMethodID(itemClass, "getFps", "()I"));
                            var formatObject = JNIEnv.CallObjectMethod(item,
                                JNIEnv.GetMethodID(itemClass, "getFormat",
                                    "()Lcom/mikimn/droiduvc/StreamFormat;"));
                            try
                            {
                                if (formatObject == IntPtr.Zero) continue;
                                var formatClass = JNIEnv.GetObjectClass(formatObject);
                                try
                                {
                                    int ordinal = JNIEnv.CallIntMethod(formatObject,
                                        JNIEnv.GetMethodID(formatClass, "ordinal", "()I"));
                                    var format = ordinal switch
                                    {
                                        0 => "MJPEG", 1 => "YUYV", _ => "OTHER"
                                    };
                                    if (format != "OTHER")
                                        result.Add(new UvcVideoMode(width, height, fps, format));
                                }
                                finally { JNIEnv.DeleteLocalRef(formatClass); }
                            }
                            finally
                            {
                                if (formatObject != IntPtr.Zero)
                                    JNIEnv.DeleteLocalRef(formatObject);
                            }
                        }
                        finally { JNIEnv.DeleteLocalRef(itemClass); }
                    }
                    finally { JNIEnv.DeleteLocalRef(item); }
                }
            }
            finally { JNIEnv.DeleteLocalRef(listClass); }
        }
        finally { JNIEnv.DeleteLocalRef(list); }
        return result.Distinct().OrderBy(m => m.Width * (long)m.Height)
            .ThenBy(m => m.Format).ThenBy(m => m.Fps).ToArray();
    }

    public string OpenAndStart(int fileDescriptor, Surface surface,
        int width, int height, int fps, string format = "MJPEG")
    {
        var companion = JNIEnv.GetStaticObjectField(_cameraClass, _companionField);
        try
        {
            var camera = JNIEnv.CallObjectMethod(companion, _openMethod,
                new JValue(fileDescriptor));
            if (camera == IntPtr.Zero)
                throw new InvalidOperationException("Droid UVC returned no camera instance.");
            try { _camera = JNIEnv.NewGlobalRef(camera); }
            finally { JNIEnv.DeleteLocalRef(camera); }
        }
        finally { JNIEnv.DeleteLocalRef(companion); }

        SupportedModes = QuerySupportedModes();
        // Don't rely on libuvc's nearest-mode fallback: it can select a slow
        // uncompressed mode and the user may see only a black preview.
        // Prefer MJPEG for USB2 bandwidth and use the advertised FPS closest
        // to the one the user requested (e.g. 1080p@25 and 8MP@15).
        var selected = SupportedModes
            .Where(m => m.Width == width && m.Height == height && m.Format == format)
            .OrderBy(m => Math.Abs(m.Fps - fps))
            .ThenByDescending(m => m.Fps)
            .FirstOrDefault();

        if (selected.Width == 0)
        {
            var available = string.Join(", ", SupportedModes
                .Select(m => $"{m.Width}x{m.Height}@{m.Fps} {m.Format}"));
            throw new NotSupportedException(
                $"{width}x{height}@{fps} {format} is not advertised. Available: {available}");
        }

        using var formatClass = _context.ClassLoader!.LoadClass(
            "com.mikimn.droiduvc.StreamFormat")
            ?? throw new InvalidOperationException("Droid UVC StreamFormat unavailable.");
        var formatField = JNIEnv.GetStaticFieldID(formatClass.Handle, selected.Format,
            "Lcom/mikimn/droiduvc/StreamFormat;");
        var preferredFormat = JNIEnv.GetStaticObjectField(formatClass.Handle, formatField);
        if (preferredFormat == IntPtr.Zero)
            throw new InvalidOperationException("Unable to select UVC pixel format.");

        IntPtr actualMode;
        try
        {
            actualMode = JNIEnv.CallObjectMethod(_camera, _startMethod,
                new JValue(surface.Handle), new JValue(selected.Width),
                new JValue(selected.Height), new JValue(selected.Fps),
                new JValue(preferredFormat));
        }
        finally { JNIEnv.DeleteLocalRef(preferredFormat); }
        if (actualMode == IntPtr.Zero)
            throw new InvalidOperationException("Native UVC streaming did not start.");
        try
        {
            var modeClass = JNIEnv.GetObjectClass(actualMode);
            try
            {
                int w = JNIEnv.CallIntMethod(actualMode, JNIEnv.GetMethodID(modeClass, "getWidth", "()I"));
                int h = JNIEnv.CallIntMethod(actualMode, JNIEnv.GetMethodID(modeClass, "getHeight", "()I"));
                int f = JNIEnv.CallIntMethod(actualMode, JNIEnv.GetMethodID(modeClass, "getFps", "()I"));
                if (w != selected.Width || h != selected.Height)
                    throw new InvalidOperationException(
                        $"Camera negotiated {w}x{h}, not selected {selected.Width}x{selected.Height}.");
                return $"USB LIVE: {w} x {h} @ {f} FPS ({selected.Format})";
            }
            finally { JNIEnv.DeleteLocalRef(modeClass); }
        }
        finally { JNIEnv.DeleteLocalRef(actualMode); }
    }

    // Kotlin CameraControls exposes getAutoFocus() and getFocus(). Both can be null.
    private IntPtr GetControl(string property)
    {
        var getControls = JNIEnv.GetMethodID(_cameraClass, "getControls",
            "()Lcom/mikimn/droiduvc/CameraControls;");
        var controls = JNIEnv.CallObjectMethod(_camera, getControls);
        if (controls == IntPtr.Zero) return IntPtr.Zero;
        try
        {
            var cls = JNIEnv.GetObjectClass(controls);
            try
            {
                var method = JNIEnv.GetMethodID(cls, property,
                    "()Lcom/mikimn/droiduvc/Control;");
                return JNIEnv.CallObjectMethod(controls, method);
            }
            finally { JNIEnv.DeleteLocalRef(cls); }
        }
        finally { JNIEnv.DeleteLocalRef(controls); }
    }

    private static int ReadInt(IntPtr instance, string getter)
    {
        var cls = JNIEnv.GetObjectClass(instance);
        try { return JNIEnv.CallIntMethod(instance, JNIEnv.GetMethodID(cls, getter, "()I")); }
        finally { JNIEnv.DeleteLocalRef(cls); }
    }

    private static void WriteControl(IntPtr control, int value)
    {
        var cls = JNIEnv.GetObjectClass(control);
        try { JNIEnv.CallVoidMethod(control, JNIEnv.GetMethodID(cls, "set", "(I)V"), new JValue(value)); }
        finally { JNIEnv.DeleteLocalRef(cls); }
    }

    public UvcFocusInfo ReadFocusInfo()
    {
        if (_camera == IntPtr.Zero) throw new InvalidOperationException("Camera is not open.");
        var auto = GetControl("getAutoFocus");
        var focus = GetControl("getFocus");
        try
        {
            var enabled = auto != IntPtr.Zero && ReadInt(auto, "getValue") == 1;
            if (focus == IntPtr.Zero)
                return new UvcFocusInfo(auto != IntPtr.Zero, false, 0, 0, 0, enabled);

            var focusClass = JNIEnv.GetObjectClass(focus);
            IntPtr range;
            try
            {
                range = JNIEnv.CallObjectMethod(focus,
                    JNIEnv.GetMethodID(focusClass, "getRange",
                        "()Lcom/mikimn/droiduvc/ControlRange;"));
            }
            finally { JNIEnv.DeleteLocalRef(focusClass); }
            if (range == IntPtr.Zero)
                return new UvcFocusInfo(auto != IntPtr.Zero, false, 0, 0, 0, enabled);
            try
            {
                var min = ReadInt(range, "getMin");
                var max = ReadInt(range, "getMax");
                return new UvcFocusInfo(auto != IntPtr.Zero, max > min,
                    min, max, ReadInt(focus, "getValue"), enabled);
            }
            finally { JNIEnv.DeleteLocalRef(range); }
        }
        finally
        {
            if (auto != IntPtr.Zero) JNIEnv.DeleteLocalRef(auto);
            if (focus != IntPtr.Zero) JNIEnv.DeleteLocalRef(focus);
        }
    }

    public void SetAutoFocus(bool enabled)
    {
        var control = GetControl("getAutoFocus");
        if (control == IntPtr.Zero)
            throw new NotSupportedException("Camera does not expose UVC autofocus control.");
        try { WriteControl(control, enabled ? 1 : 0); }
        finally { JNIEnv.DeleteLocalRef(control); }
    }

    public void SetManualFocus(int position)
    {
        var control = GetControl("getFocus");
        if (control == IntPtr.Zero)
            throw new NotSupportedException("Camera does not expose UVC manual focus control.");
        try { WriteControl(control, position); }
        finally { JNIEnv.DeleteLocalRef(control); }
    }

    public void StopAndClose()
    {
        if (_camera == IntPtr.Zero) return;
        try { JNIEnv.CallVoidMethod(_camera, _stopMethod); }
        finally
        {
            try { JNIEnv.CallVoidMethod(_camera, _closeMethod); }
            finally { JNIEnv.DeleteGlobalRef(_camera); _camera = IntPtr.Zero; }
        }
    }

    public void Dispose()
    {
        if (_camera != IntPtr.Zero)
            throw new InvalidOperationException("StopAndClose must be called before Dispose.");
        if (_cameraClass != IntPtr.Zero)
        { JNIEnv.DeleteGlobalRef(_cameraClass); _cameraClass = IntPtr.Zero; }
        if (_companionClass != IntPtr.Zero)
        { JNIEnv.DeleteGlobalRef(_companionClass); _companionClass = IntPtr.Zero; }
    }
}
