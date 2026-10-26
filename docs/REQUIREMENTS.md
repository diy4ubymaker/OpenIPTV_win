# OpenIPTV for Windows — Requirements Specification

| Item | Value |
|---|---|
| Document | SRS v1.1 |
| Product version | 1.0.0 |
| Date | 2026-10-26 |
| Author | Desmond |
| Status | Release 1.0.0 |

## 1. Purpose and scope

OpenIPTV for Windows is a free, standalone desktop application. It aggregates the public IPTV channel lists of the [iptv-org](https://github.com/iptv-org) project into a local catalog, tests which streams are playable, and lets the user search, filter, sort and watch channels. It is the Windows member of the OpenIPTV family (Android, iOS) and offers the same channel features as the IPTV Portal web application, without a server, users or login.

Out of scope (1.0): EPG/programme guide, recording, favourites, multiple user profiles, casting, macOS/Linux builds, automatic updates.

## 2. Definitions

| Term | Meaning |
|---|---|
| Channel | A TV channel identified by its iptv-org ID (e.g. `Channel8.sg`), or `custom:<slug>` for playlist entries without an ID. |
| Stream / source | One URL of a channel. A channel may have several. |
| Agent | Background worker that synchronises sources and tests streams. |
| Active | A stream is active if its last test could fetch media. A channel is active if any stream is active; inactive if all were tested and none is; unchecked otherwise. |

## 3. Data sources (defaults, editable)

| # | Kind | Default URL |
|---|---|---|
| S1 | `iptv_api` | `https://iptv-org.github.io/api` |
| S2 | `database_csv` | `https://github.com/iptv-org/database/tree/master/data` |
| S3 | `m3u` | `https://iptv-org.github.io/iptv/index.m3u` |
| S4 | `playlists_md` | `https://github.com/iptv-org/iptv/blob/master/PLAYLISTS.md` (follows category, language and country playlists) |
| S5 | `github_repo` | `https://github.com/iptv-org/iptv` (`streams/*.m3u` via the GitHub API) |

## 4. Functional requirements

### 4.1 Synchronisation

| ID | Requirement | Priority |
|---|---|---|
| FR-1.1 | The agent shall harvest all enabled sources and create, update and synchronise the local catalog while the app runs. | Must |
| FR-1.2 | Each channel shall have name, categories, languages, country name and ISO 3166-1 alpha-2 code, logo URL (if any), one or more stream URLs, and a status (Active / Inactive / Unchecked). | Must |
| FR-1.3 | Each stream shall keep title, quality, labels, required Referer/User-Agent, origin sources, last check time and result. | Should |
| FR-1.4 | Sources shall be merged per channel and per (channel, URL) with priority API > CSV > repository/M3U > PLAYLISTS.md for single values and union for lists. Stream status shall survive re-syncs. | Must |
| FR-1.5 | Data shall be removed only after a sync in which every enabled source succeeded; a failing source never deletes data. | Must |
| FR-1.6 | Channels on the iptv-org blocklist are excluded; adult (NSFW) channels are excluded unless enabled. | Must |
| FR-1.7 | During each sync the agent shall test streams (least recently checked first, optional per-sync cap). | Must |
| FR-1.8 | Each sync shall be recorded (time, trigger, status, counts, errors) and shown on the Sync history page with a live log. | Should |
| FR-1.9 | The user can clear the sync history and the log (live log and log file), each after confirmation. Clearing does not change the sync schedule. | Should |

### 4.2 Scheduling

| ID | Requirement | Priority |
|---|---|---|
| FR-2.1 | The agent shall sync every N minutes (default 60), configurable in Settings, effective within 30 s without restart. | Must |
| FR-2.2 | The user can start a sync immediately (Sync now / F5); only one sync runs at a time. | Must |
| FR-2.3 | Sync on start-up, check concurrency, timeout, per-sync cap and TLS verification are configurable. | Should |

### 4.3 Source configuration

| ID | Requirement | Priority |
|---|---|---|
| FR-3.1 | The user can add, edit, enable/disable, remove and restore default sources (name, kind, URL, options), with validation. | Must |
| FR-3.2 | Source changes take effect on the next sync. | Must |

### 4.4 Browsing

| ID | Requirement | Priority |
|---|---|---|
| FR-4.1 | Search by channel name (case-insensitive; also alternative names and IDs). | Must |
| FR-4.2 | Filter by any combination of Category, Language and Country (multi-select, each with a search box), and by status. The status filter defaults to **Active**. | Must |
| FR-4.3 | Sort by up to two of Name, Category, Language, Country, Code, Sources, Status, ascending or descending. Clicking a column header sorts by that column; clicking it again reverses the order. Sorting applies to all matching channels, not only the visible page. | Must |
| FR-4.4 | A one-line summary shows total, active, inactive and unchecked channels for the current filters and the range shown (e.g. "showing 101–200 of 5,754"). | Should |
| FR-4.5 | The list stays responsive with ~15,000 channels (virtualised list, logos loaded in the background). | Must |
| FR-4.6 | The list is paged: 25, 50, 100 (default), 200 or 500 channels per page; first/previous/next/last, a page box, and Ctrl+Page Up/Down. Changing filters, search or sort returns to page 1; a data refresh keeps the page and selection; changing the page size keeps the first visible channel in view. | Must |
| FR-4.7 | The search text, Category/Language/Country selections, status, sort, page size and volume are restored at the next start. Clear filters resets them. | Should |
| FR-4.8 | A channel without a usable logo (none, SVG, or a broken link) shows a default TV picture. | Should |

### 4.5 Watching

| ID | Requirement | Priority |
|---|---|---|
| FR-5.1 | The user can select a channel and one of its streams and play it in the app (double-click, Enter or Play). | Must |
| FR-5.2 | Playback shall support HLS, DASH, HTTP progressive, RTMP, RTSP and UDP, and send each stream's required Referer/User-Agent. | Must |
| FR-5.3 | Pause, stop, volume, mute and full screen (F11, double-click, Esc) shall be available; the mute button is highlighted while muted; failures shall show a clear message. | Should |
| FR-5.4 | The original source URL is shown with a Copy button, with required headers and the last check result. | Should |

### 4.6 Re-check

| ID | Requirement | Priority |
|---|---|---|
| FR-6.1 | **Check source now** tests all streams of the selected channel immediately and updates the catalog and the screen. | Must |
| FR-6.2 | Re-checks are rate-limited per channel (default 30 s). | Should |

### 4.7 Export and operation

| ID | Requirement | Priority |
|---|---|---|
| FR-7.1 | Export every channel matching the current filters (all pages) as an M3U playlist (best stream each, with header lines) or CSV (one row per stream, Excel-friendly). | Should |
| FR-7.2 | The version, set once in `Directory.Build.props`, is shown in the title bar, a header badge, the splash screen, Settings → About and the exe's file properties. | Should |
| FR-7.3 | A build script produces a single self-contained executable (optionally an app folder). | Must |
| FR-7.4 | Theme selection: System (follows Windows light/dark, live), Light or Dark; applied immediately and remembered. | Should |
| FR-7.5 | A splash screen shows loading progress at start-up; slow steps run in the background. | Should |
| FR-7.6 | The app has its own icon on the exe, window, taskbar, header and splash. | Should |

## 5. Non-functional requirements

| ID | Category | Requirement |
|---|---|---|
| NFR-1 | Platform | Windows 10/11 x64; .NET 10; WPF UI; developed in VS Code. |
| NFR-2 | Dependencies | Core and view-model layers use only the .NET base library; the app adds only LibVLCSharp.WPF and VideoLAN.LibVLC.Windows. |
| NFR-3 | Storage | Local JSON files with atomic writes; data folder `%LOCALAPPDATA%\OpenIPTV`, portable `data` folder, or `OPENIPTV_DATA_DIR`. A corrupt file is set aside and defaults are used. |
| NFR-4 | Performance | Search/filter over ~15k channels < 100 ms; catalog load < 1 s; a full sync of ~18k streams within the default 60-minute interval. |
| NFR-5 | Politeness | Per-host concurrency limit (4) for stream tests; configurable global concurrency. |
| NFR-6 | Resilience | Network errors never crash the app; the agent continues on schedule; unhandled UI errors are logged and reported. |
| NFR-7 | Testability | Core and view-model logic covered by an offline test runner; optional live test against iptv-org. |
| NFR-8 | Legal | Only lists links published by iptv-org; honours the blocklist; stores no media. |

## 6. Acceptance criteria

1. First start: the app opens, syncs automatically, and within one sync shows channels with name, category, language, country name/code and logo.
2. The status filter starts at Active; the summary line shows correct per-status counts that change with the Country filter but not with the Status filter.
3. Search "news" + Language English + Country United Kingdom + sort Country then Name shows only matching channels in that order.
4. Double-clicking an active HLS channel plays it; a stream needing a Referer plays; an RTMP or UDP stream plays if reachable.
5. **Check source now** updates the status and last-check time of the channel's sources; a second press within 30 s is refused with a message.
6. Changing the sync interval to 5 minutes starts the next sync ≤ 5 minutes after the previous one, without restart.
7. Disabling a source whose fetch fails never removes channels.
8. **Copy** puts the original URL on the clipboard; **Export M3U** produces a playlist that VLC opens.
9. `build.bat` produces one executable that runs on a Windows PC without .NET installed and creates its data folder.
10. With 5,000+ matching channels, the list shows 100 per page; clicking the Country header sorts all pages by country, and clicking it again reverses the order.
11. After choosing Country Singapore, searching "news" and restarting the app, the same filters and results are shown.
12. Clear history empties the Sync history table after confirmation; Clear log empties the live log and `openiptv.log`.

### Verification status (1.0.0)

| Criteria | Verified by |
|---|---|
| 1–3, 5–7, 10–12 (logic) | Offline test runner (24 tests) and a live sync against iptv-org on .NET 10, 2026-10-26. |
| 1, 4 | Windows build run by the product owner during 1.0.0 testing (channel list and HLS playback confirmed). |
| 8, 10–12 (UI), remaining UI | Product owner's final pass on Windows before release. |
| 9 | To confirm with the release build on a second PC without .NET installed. |
