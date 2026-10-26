# OpenIPTV for Windows

<img src="src/OpenIPTV.App/Assets/openiptv.png" alt="OpenIPTV icon" width="96" align="right" />

A free, standalone Windows desktop app for the public IPTV channels published by the [iptv-org](https://github.com/iptv-org) project. It syncs the channel lists into a local catalog, tests which streams actually play, and lets you search, filter, sort and watch channels with a built-in VLC player. There is no server, no account and no login.

**Version 1.0.0** · C# / .NET 10 · WPF · LibVLC (VLC engine) · Windows 10/11 x64 · part of the OpenIPTV family (Android, iOS, Windows)

* Requirements: [`docs/REQUIREMENTS.md`](docs/REQUIREMENTS.md)
* Architecture: [`docs/ARCHITECTURE.md`](docs/ARCHITECTURE.md)
* Changes: [`CHANGELOG.md`](CHANGELOG.md)

## Features

### Channels
* **Sync agent:** pulls the five iptv-org sources and merges them per channel. The sources are the API, the database CSV, `index.m3u`, the playlists linked from PLAYLISTS.md, and the repository's `streams/*.m3u` files. It repeats every *N* minutes; the interval is configurable and applies without a restart. **Sync now** (F5) starts a sync immediately.
* **Channel data:** name, categories, languages, country name and two-letter code, logo, every stream URL, and an Active, Inactive or Unchecked status. Channels without a usable logo show a neutral TV picture.
* **Stream testing:** checks are HLS-aware (master playlist, then variant, then the first segment). Each check sends the Referer and User-Agent that stream needs and is rate-limited per host. RTMP and RTSP sources are tested for reachability.

### Browsing
* **Search** by channel name.
* **Filter** by Category, Language and Country (multi-select, each with its own search box), and by status. Status defaults to Active.
* **Sort** by clicking a column header (click again to reverse), or with the two-level **Sort by … then …** controls. The whole result is sorted, not just the visible page.
* **Pages** of 25, 50, 100, 200 or 500 channels, with first/previous/next/last buttons, a page box, and Ctrl+Page Up/Down.
* **Counts** on one line: total, active, inactive and unchecked channels, plus the range shown (e.g. "showing 101–200 of 5,754").
* **Remembers your view:** the search text, Category/Language/Country selections, status, sort, page size and volume come back the next time the app starts. **Clear filters** resets them.

### Watching
* **Player:** the VLC engine plays HLS, DASH, HTTP, RTMP, RTSP and UDP/multicast, sending each stream's required headers, so no proxy is needed.
* **Controls:** source picker, pause (Ctrl+Space), stop, volume, and mute (the button turns orange while muted).
* **Full screen:** F11 or double-click the video; Esc leaves.
* **Check source now:** re-tests every source of the selected channel immediately, with a 30-second cooldown.
* **Copy source URL:** copies the original stream URL; the required headers are shown next to it.

### Managing
* **Export:** saves every channel matching the current filters, across all pages, as an M3U playlist (for VLC, Kodi, TiviMate …) or as CSV for Excel.
* **Sources page:** add, edit, enable or disable sources, or restore the defaults.
* **Settings page:** sync interval, check parameters, player network caching, theme, data folder, and the About section.
* **Sync history page:** the last 100 syncs and a live log. **Clear history** and **Clear log** empty them after asking.
* **Look and feel:** System theme (follows Windows light/dark), Light or Dark. The window has orange accents, an app icon and a start-up splash screen with loading progress. The version is shown as a badge in the header.

### Keyboard shortcuts

| Key | Action |
|---|---|
| Enter / double-click | Play the selected channel |
| F5 | Sync now |
| F11 / Esc | Full screen on / off |
| Ctrl+Space | Pause / resume |
| Ctrl+Page Down / Ctrl+Page Up | Next / previous page |

## Download and run

Use a release build (`OpenIPTV-<version>-win-x64.exe`), or build it yourself as described below. The exe is self-contained: it needs no .NET installation and can be copied to any Windows 10/11 x64 PC.

Windows SmartScreen may warn about an unsigned exe. Choose **More info → Run anyway**, or sign the file.

On first start the app downloads the channel lists in the background; the first full sync with stream tests takes several minutes.

## Build from source

### Prerequisites
1. Windows 10/11 x64
2. [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0)
3. Optional: [Visual Studio Code](https://code.visualstudio.com/) with the **C# Dev Kit** extension

The first build downloads two NuGet packages, LibVLCSharp.WPF and VideoLAN.LibVLC.Windows (about 80 MB).

### Visual Studio Code
1. If the repository has a `vscode-config` folder instead of `.vscode`, rename it to **`.vscode`**.
2. Open the repository folder in VS Code.
3. Press **F5** to build and start the app in the debugger. In debug mode the app keeps its data in `data-dev\`, separate from your everyday data.

### Command line

```bat
dotnet build OpenIPTV.sln
dotnet run --project src\OpenIPTV.App
dotnet run --project tests\OpenIPTV.Tests
dotnet run --project tests\OpenIPTV.Tests -- --live
```

In order, these build everything, run the app, run the 24 offline tests, and run them plus a live sync against iptv-org.

### Release build

```bat
build.bat
build.bat --onedir
```

* **`build.bat`** runs the tests, then produces `dist\OpenIPTV-<version>-win-x64.exe`: one self-contained file with .NET, WPF and the VLC engine inside. The VLC engine is unpacked to a temporary folder at start.
* **`build.bat --onedir`** produces an app folder plus a zip, which starts faster.

If the VLC engine ends up beside the exe instead of inside it, the script ships `OpenIPTV.exe` and the `libvlc` folder together as a zip.

### Version number

The version is set once, in `Directory.Build.props` (`<Version>1.0.0</Version>`). It appears in the title bar, the header badge, the splash screen, Settings → About, the exe's file properties, the build file name, and the log.

## Where data is stored

The data folder holds `catalog.json`, `sources.json`, `settings.json`, `history.json` and `openiptv.log`.

| Situation | Data folder |
|---|---|
| Normal | `%LOCALAPPDATA%\OpenIPTV` |
| Portable (e.g. on a USB stick) | a folder named `data` next to `OpenIPTV.exe`, which you create once |
| Override | environment variable `OPENIPTV_DATA_DIR` |
| Debugging in VS Code | `data-dev\` in the repository (ignored by git) |

The full catalog of about 12,000 channels and 18,000 streams is about 8 MB. It loads in about 0.2 s, and a search over it takes 5–50 ms.

## Project layout

```
OpenIPTV.sln
Directory.Build.props        version and shared build settings
build.bat                    tests + release build
src/OpenIPTV.Core/           sync, parsers, merge, stream checker, storage, queries (no NuGet packages)
src/OpenIPTV.ViewModels/     MVVM screen logic (no WPF types; unit-tested)
src/OpenIPTV.App/            WPF windows, LibVLC player, logo loading, theme; Assets/ holds the icons
tests/OpenIPTV.Tests/        self-contained test runner (offline + --live)
docs/                        requirements and architecture
vscode-config/ or .vscode/   VS Code launch, build tasks and recommended extensions
```

## About the streams

OpenIPTV only lists links that the iptv-org project publishes; it hosts and stores no video. The streams belong to third parties: some are geo-blocked, some are not 24/7, and some stop working at any time. The app honours the iptv-org blocklist and hides adult channels unless you enable them in Settings.

## Credits

* Channel data: [iptv-org/iptv](https://github.com/iptv-org/iptv), [iptv-org/database](https://github.com/iptv-org/database) and the [iptv-org API](https://github.com/iptv-org/api)
* Playback: [VLC / LibVLC](https://www.videolan.org/vlc/libvlc.html) via [LibVLCSharp](https://github.com/videolan/libvlcsharp)

## License

No license has been chosen yet. Until a `LICENSE` file is added, the default copyright rules apply. Note that LibVLC is licensed under the LGPL 2.1 or later, and releases that bundle it must meet its terms.
