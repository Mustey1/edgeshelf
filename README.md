# Edge Shelf

Edge Shelf is a lightweight Windows 10/11 utility that opens from the left
edge of the screen. It keeps frequently used files, clipboard history, text
snippets, and upcoming calendar meetings in one compact panel.

## Features

- Animated edge panel with a configurable global shortcut.
- Shelf for files, folders, and screenshots with drag and drop in both directions.
- Local clipboard history for text, links, images, and file paths.
- Reusable text snippets with search and categories.
- Google Calendar through a private iCal feed or read-only OAuth.
- Tray controls, startup option, hover timing, and history settings.
- Local persistence under `%LOCALAPPDATA%\EdgeShelf`.

## Requirements

- Windows 10 or Windows 11.
- .NET 8 SDK with Windows desktop support to build from source.

The installer and portable release are self-contained, so end users do not
need to install .NET separately.

## Install

Download `EdgeShelf-Setup-<version>.exe` from the latest GitHub release. The
installer uses the current Windows account and does not require administrator
access. Its **Start Edge Shelf when I sign in to Windows** option is enabled by
default and can later be changed from the tray menu or Edge Shelf settings.

A portable ZIP is provided alongside the installer. Portable users can enable
startup from the tray menu after placing the application in its permanent
folder.

## Build and run

```powershell
dotnet restore .\EdgeShelf\EdgeShelf.csproj --configfile .\NuGet.Config
dotnet run --project .\EdgeShelf\EdgeShelf.csproj
```

To create a self-contained portable ZIP and installer:

```powershell
.\scripts\build-release.ps1 -Version 0.1.0
```

This requires Inno Setup 6. Output is written to `artifacts/`. Passing
`-SkipInstaller` builds only the portable ZIP.

## Use

Hover over the handle at the center of the primary display's left edge, or
press `Ctrl + Shift + Space`. The tray icon provides **Open**,
**Start with Windows**, **Settings**, and **Quit**.

### Shelf

Drop files, folders, or bitmap images onto the panel. Drag an item out to send
it to another application. Double-click to open it. Image cards use a larger
preview and expose a delete button on hover.

### Clipboard

Clipboard history records copied text, links, images, and file paths. Click an
entry to copy it again, or drag it into another compatible application. Hover
over a card to delete it. **Clear** removes the complete history, including
pinned entries.

### Snippets

Store a title, value, and optional category. Click a snippet to copy its value.
The context menu supports editing, duplication, and deletion.

### Calendar

The simplest connection uses a private Google Calendar iCal URL:

1. Open Google Calendar settings.
2. Select a calendar under **Settings for my calendars**.
3. Open **Integrate calendar**.
4. Copy **Secret address in iCal format** into Edge Shelf.

OAuth is also available with a Google OAuth Client ID of type **Desktop app**.
The app requests read-only calendar access and listens on a random loopback
port for the authorization response.

## Privacy and local data

Edge Shelf has no project server or analytics. Shelf metadata, clipboard
history, snippets, and settings remain under `%LOCALAPPDATA%\EdgeShelf`.
Calendar credentials are encrypted for the current Windows user with DPAPI.
The private iCal URL is stored in `calendar-ical.bin`; OAuth tokens are stored
in `calendar-token.bin`.

Never commit calendar URLs, OAuth tokens, or files copied from the local data
directory.

## Checks

```powershell
dotnet run --project .\EdgeShelf.Smoke\EdgeShelf.Smoke.csproj
dotnet run --project .\EdgeShelf.UiSmoke\EdgeShelf.UiSmoke.csproj
```

GitHub Actions runs the build and both smoke test projects on Windows.
Pushing a version tag such as `v0.1.0` creates a GitHub Release containing the
installer, portable ZIP, and SHA-256 checksums.

The generated binaries are currently unsigned. Windows SmartScreen may show a
warning until releases are signed with a trusted code-signing certificate.

## License

Edge Shelf is available under the [MIT License](LICENSE). See the
[third party notices](THIRD_PARTY_NOTICES.md) for included dependencies and build tools.
