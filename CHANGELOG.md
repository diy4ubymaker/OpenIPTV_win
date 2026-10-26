# Changelog

The version is set in `Directory.Build.props` and shown in the title bar and on the Settings page.

## 1.0.0 — 2026-10-26
First release of OpenIPTV for Windows: a standalone C#/.NET 10 WPF app with a LibVLC player.

- Sync agent for the five iptv-org sources (API, database CSV, index.m3u, PLAYLISTS.md playlists, repository streams), on a configurable interval and on demand.
- HLS-aware stream checks honouring Referer/User-Agent; RTMP/RTSP reachability checks; per-host rate limit.
- Local JSON catalog with atomic writes; data folder in `%LOCALAPPDATA%\OpenIPTV`, portable `data` folder, or `OPENIPTV_DATA_DIR`.
- Channel browser: name search, multi-select Category/Language/Country filters with search boxes, status filter (default Active), two-level sort, click a column header to sort by it (click again to reverse; sorts all pages), per-status counts.
- Paged channel list: first/previous/next/last, jump to page, 25–500 channels per page (remembered); Ctrl+Page Up/Down; export covers all pages.
- VLC playback (HLS, DASH, HTTP, RTMP, RTSP, UDP), source picker, pause/stop/volume/mute (the mute button turns orange while muted), full screen (F11 / double-click / Esc).
- Check source now (with cooldown), copy source URL, M3U and CSV export.
- Sources, Settings and Sync history pages; live log; Clear log (live log + log file) and Clear history buttons, with confirmation; UI state remembered: search text, Category/Language/Country selections, status, sort, page size and volume.
- Theme selection (System / Light / Dark) in Settings, applied immediately; System follows Windows light/dark live.
- Start-up splash screen showing loading progress (data folder, channel catalog, VLC engine); slow steps run in the background.
- Default TV picture for channels without a logo, with an SVG logo or with a broken logo link (also shown while a logo downloads).
- Version number shown as a badge in the header, in the title bar, on the splash screen, under Settings → About and in the exe's file properties (set once in `Directory.Build.props`).
- App icon (orange TV tile) for the exe, title bar, taskbar, header and splash; sizes 16–256 px, with a simplified design at 32 px and smaller.
- Single-file executable build (`build.bat`), VS Code configuration, offline (24) and live tests.
- Repository files for GitHub: `.gitignore` (build output, app data, logs) and `.gitattributes` (CRLF for Windows scripts).
