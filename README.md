# Zink

Zink is an all-in-one Windows media hub built with WinUI 3 and .NET 8. It brings music, video, radio, streaming shortcuts, Spotify controls, screen recording, social features, and calling tools into one desktop app.

## Features

- Music and video playback with local library pages.
- Internet radio playback with station artwork, metadata, liked songs, and a mini radio widget.
- Spotify login, controls, and widget support.
- WebView-powered pages for streaming, social, and gaming services such as YouTube, Netflix, Disney+, Twitch, Xbox, GeForce NOW, Amazon Music, and more.
- Screen recording, replay buffer, audio capture, FPS tools, and MP4 output services.
- Zink Connect social features, including login, profiles, friends, messages, calls, and screen sharing.
- Discord rich presence, tray integration, notifications, customization, themes, and background mode support.
- MSIX packaging for Windows desktop distribution.

## Tech Stack

- C# and XAML
- .NET 8
- WinUI 3 / Windows App SDK
- WebView2
- NAudio
- SIPSorcery / WebRTC-related calling services
- Microsoft Graphics Capture and Media Foundation helpers
- MSIX packaging

## Requirements

- Windows 10 version 1809 or later.
- Windows 10 SDK / Windows 11 SDK.
- Visual Studio 2022 or newer with:
  - .NET desktop development
  - Windows App SDK / WinUI tooling
  - MSIX Packaging Tools
- .NET 8 SDK.

## Getting Started

1. Clone the repository.

   ```powershell
   git clone https://github.com/tactical12/Zink.git
   cd Zink
   ```

2. Restore dependencies.

   ```powershell
   dotnet restore Zink.csproj
   ```

3. Open the solution in Visual Studio.

   ```powershell
   start Zink.sln
   ```

4. Select a Windows platform target such as `x64`.

5. Build and run the app from Visual Studio.

## Command Line Build

Build the app for x64:

```powershell
dotnet build Zink.csproj -c Debug -p:Platform=x64 -p:RuntimeIdentifier=win-x64
```

Build a release package:

```powershell
.\Build-ZinkAppPackages.ps1 -Configuration Release -Platforms x64
```

Build packages for multiple platforms:

```powershell
.\Build-ZinkAppPackages.ps1 -Configuration Release -Platforms x64,arm64 -CreateBundle
```

Build the Store package and a signed local install package at the same time:

```powershell
.\Build-ZinkAppPackages.ps1 -Configuration Release -Platforms x64,arm64 -CreateBundle -CreateInstallPackage
```

Package output is written under:

```text
%LOCALAPPDATA%\Zink\PackageOutput
```

## Installing a Local Package

After creating an MSIX or MSIX bundle, install it with:

```powershell
.\Install-ZinkPackage.ps1
```

When `-CreateInstallPackage` is used, run the copied `Install-ZinkPackage.ps1` from the generated `Install` folder so it can find the matching signed package and certificate.

You can also pass explicit paths:

```powershell
.\Install-ZinkPackage.ps1 -PackagePath "C:\Path\To\Zink.msixbundle" -CertificatePath "C:\Path\To\Zink_TemporaryKey.cer"
```

## Project Structure

```text
Assets/                 App icons, splash assets, audio, radio artwork, and UI images
CodecInstallers/        Optional codec installer bundles
Converters/             XAML value converters
Models/                 Shared app models
Pages/                  Main feature pages and social pages
Services/               Playback, recording, calling, social, search, notification, and app services
ViewModels/             MVVM view models
WebRTC/                 WebRTC helper page and script assets
Windows/                Additional window implementations
Zink.csproj             Main WinUI project
Zink.sln                Visual Studio solution
```

## Packaging Notes

The app uses MSIX packaging and includes full-trust desktop capabilities. The manifest identity is currently:

```text
FrancisHayle.Zink-MusicVideoPlayer
```

The project is configured for Windows desktop packaging, background media playback, global media control, graphics capture, microphone access, and startup task support.

## Development Notes

- Keep generated output out of source control. Build folders such as `bin/`, `obj/`, `x64/`, `BundleArtifacts/`, `Logs/`, and Visual Studio cache files are ignored.
- Use `Zink.csproj` as the source of truth for package references, app versioning, and MSIX settings.
- Some recording and native calling features depend on native DLLs being available for the selected platform.

## License

No license file is currently included. Add one before publishing or accepting external contributions.
