# USB8MP05AF — Clean open-source UVC preview (Android, .NET 10 MAUI)

> **Historical UVC-only prototype notes:** This file documents an earlier camera-preview milestone, not the current completed Meta Muse Vision AI app. The current project includes Meta Muse Spark visual analysis, barcode workflows, and role-specific Android functionality. For the current project description, screenshots, 3D design files, APK link, and setup guidance, see the **[main project README](../../../README.md)**. The older instructions below are preserved as engineering history.

This is a **fresh, single-project** replacement for the old `DroidUvcBridge` solution. It has *no VisioForge, no paid SDK, no Java source binding project, no generated `Com.Usb8mp05af` C# classes*, and no compiled output from the previous project.

**Important validation statement:** The project structure, all XML, namespace/XAML relationships, JNI signatures against published Kotlin source, and solution Deploy configuration have been checked. This environment has **no dotnet/MAUI Android toolchain or USB8MP05AF hardware**; an actual build or device run could NOT be verified. Consequently, *no one can guarantee an error-free build on your Windows installation without running it there.*

## Requirements
- Visual Studio 2026 with **.NET 10 MAUI Android** workload installed.
- Android phone with USB OTG/USB Host support and 64-bit ARM processor.
- USB8MP05AF UVC camera, USB-C OTG cable/hub; enough power.
- First build needs internet to restore NuGet + open-source Maven Central dependencies.
- Connect phone to Windows for deployment with USB debugging, or use wireless ADB if its port is needed by the USB camera.

## Open and build
1. Completely close the old solution / Visual Studio.
2. Extract this project into a NEW folder, e.g. `C:\UVCNew`. Do **not** copy old `bin` or `obj` files or put it inside `C:\UVC`.
3. Open `C:\UVCNew\UvcViewer.sln` (if you extract without a wrapper folder).
4. Set `UvcViewer` as startup project and select **Debug | Any CPU**; select your Android device.
5. Restore dependencies, build and deploy. Grant Android **Camera** permission and then the separate USB-device prompt.
6. Connect USB camera. Tap **Start USB Camera**. Tap **Stop** to close.

## Implementation
- The app has ONE .NET 10 MAUI Android project; the `.sln` includes **Deploy** for it.
- Droid UVC 0.2.1 is a real open-source Maven AAR. The .csproj uses `<AndroidMavenLibrary Bind="false">` and calls the published `UvcCamera` Kotlin JVM API via JNI using the Android app classloader.
- Android USB Host locates the external UVC interface and requests USB permission. It keeps the USB connection and Android Surface alive while the camera is streaming, then closes in reverse order.
- The 1280x720@30 request is **negotiated**, so the camera may select a different supported mode.
- No barcode processing, Muse AI, recording, or other features.

## Previous errors this project addresses
- `CS0246` `Com.*` namespace: eliminated by removing the custom binding project and all `Com.*` references.
- `CS1061` missing `StartCamera`: eliminated. This project calls actual `UvcCamera.open/start/stop/close` JVM API by JNI.
- `XARDF7024`: This error indicates a .NET Android SDK directory deletion problem, not necessarily project source. The new project has no old generated `obj` folders or binding project, reducing causes; **it may still occur on a faulty SDK, corrupt path or environment and cannot be guaranteed fixed without its diagnostic log**.

## If XARDF7024 persists
Close Visual Studio, delete the NEW project's `bin`, `obj` and `.vs` folders only, and build with a diagnostic binary log:

```powershell
dotnet build C:\UVCNew\UvcViewer\UvcViewer.csproj -f net10.0-android -c Debug -m:1 -bl:C:\UVCNew\android-build.binlog
```

The exact `RemoveDirFixed` input path is shown in the `.binlog` with MSBuild Structured Log Viewer. Do not change the installed SDK `.targets` file, ignore the error, or copy the old `DroidUvcBridge` project into this solution.

## License
Library: `io.github.mikimn:droiduvc:0.2.1` (Apache 2.0). Native deps include libusb (LGPL-2.1+), libuvc (BSD-3-Clause), libjpeg-turbo (IJG/BSD/zlib). Review native library obligations before distributing a closed-source app.

Upstream: https://github.com/mikimn/droiduvc
Docs: https://mikimn.github.io/droiduvc/
Maven: https://central.sonatype.com/artifact/io.github.mikimn/droiduvc
