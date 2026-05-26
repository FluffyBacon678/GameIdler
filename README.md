# Steam Card Idler (GameIdler)

A professional Windows desktop tool that idles Steam games to farm Trading Card drops.  
Built with C# WinForms (.NET 4.x), fully owner-drawn dark UI.

![Platform](https://img.shields.io/badge/platform-Windows-blue)
![Framework](https://img.shields.io/badge/.NET-4.x-purple)
![Language](https://img.shields.io/badge/language-C%23%205-green)

---

## Features

| Feature | Description |
|---------|-------------|
| **Cookie Login** | Log in via embedded browser — shows only games with remaining card drops |
| **API Key Mode** | Load your full Steam library via the Web API (no drop-count data) |
| **Quick Idle** | Type any AppID instantly — no account or login needed |
| **Per-game timers** | Live elapsed time shown on each idling game card |
| **Idle All Drops** | One click to start idling every game with remaining drops |
| **System tray** | Minimizes to tray while idling; balloon notifications |
| **Sleep prevention** | Keeps the PC awake while any game is idling |
| **Game thumbnails** | Downloads and caches capsule art from the Steam CDN |
| **Search / filter** | Real-time filter across your loaded library |
| **Process monitoring** | Auto-detects if steam-idle.exe exits unexpectedly and cleans up |

---

## Requirements

- **Windows 10 / 11**
- **.NET Framework 4.x** — already installed on most Windows machines  
  Download if needed: https://dotnet.microsoft.com/download/dotnet-framework
- **Steam** must be running and logged in before you start idling
- The four bundled binaries (see below) must stay in the same folder as `SteamIdler.exe`

---

## Bundled third-party files

| File | Purpose | Source |
|------|---------|--------|
| `steam-idle.exe` | Registers a game as running with Steam | [nicklvsa/go-steam-idle](https://github.com/nicklvsa/go-steam-idle) or equivalent |
| `CSteamworks.dll` | C wrapper for the Steamworks SDK | Steamworks SDK |
| `steam_api.dll` | Valve Steam API | Steamworks SDK |
| `Steamworks.NET.dll` | .NET bindings for Steamworks | [Steamworks.NET](https://steamworks.github.io) |
| `HtmlAgilityPack.dll` | HTML parsing (badge scraper) | [HtmlAgilityPack](https://html-agility-pack.net) |
| `Newtonsoft.Json.dll` | JSON parsing (API mode) | [Newtonsoft.Json](https://www.newtonsoft.com/json) |

> **Important:** `steam-idle.exe` is a pre-built third-party binary. Keep it in the same  
> folder as `SteamIdler.exe` at all times — it is copied per-game to isolated working  
> directories under `%AppData%\SteamCardIdler\running\` when idling starts.

---

## Build

```batch
build.bat
```

This runs `C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe` (the C# 5 compiler  
shipped with .NET Framework 4.x) and produces `SteamIdler.exe` in the project folder.

**For CI / scripting** (suppresses the interactive pause and propagates the exit code):

```batch
build.bat -ci
```

**Requirements for building:**
- .NET Framework 4.x Developer Pack (includes the C# compiler)
- All DLL dependencies present in the project folder

---

## Usage

### First run

1. Run `SteamIdler.exe` — Settings opens automatically if no auth is configured.
2. Choose an auth mode (see below) and click **Save**.
3. Click **Refresh** to load your library.
4. Check the games you want to idle and click **Run Selected**, or click **Idle All Drops**.

### Auth modes

#### A) Browser Cookie Login *(recommended for card farming)*
- Click **Open Browser — Log in to Steam**
- Complete the Steam login in the embedded browser
- The window closes automatically when login is detected
- Your library shows **only games with remaining card drops**

#### B) Steam Web API Key
- Get a key at https://steamcommunity.com/dev/apikey
- Your **SteamID64** is detected automatically from the running Steam client  
  (or find it at https://www.steamidfinder.com)
- Your **full library** is loaded, sorted by least played
- Note: the API does not return card-drop counts — use Cookie Login for drop-focused farming

#### C) Manual / Quick Idle *(no login needed)*
- Use the **Quick Idle** bar at the bottom
- Type any AppID (visible in a game's store URL, e.g. `431960` for Wallpaper Engine)
- Separate multiple IDs with commas: `431960, 570, 1091500`
- Press **Enter** or click **Start**

### Stopping

- Click **Stop All** in the action bar or in the system tray right-click menu
- Right-click any running game card → **Stop Idling**
- Closing the window while games are running sends the app to the tray
- **Exit** via the tray icon to stop all games and quit

---

## Data stored on your machine

All data is stored under `%AppData%\SteamCardIdler\`:

| Path | Contents |
|------|----------|
| `config.txt` | Auth mode, API key, SteamID64, Steam session cookies |
| `imgcache\` | Cached game capsule images (`.jpg`) |
| `running\<appid>\` | Temporary per-game working directories (auto-cleaned on stop) |

> **Privacy note:** Your Steam session cookies and API key are stored in **plain text** in  
> `config.txt`. This file is in your user AppData folder and is not accessible to other  
> Windows users, but it is not encrypted. Do not share this file.

---

## Architecture

The entire application is a single C# 5 source file (`SteamIdler.cs`, ~2000 lines).

Key classes:

| Class | Role |
|-------|------|
| `Pal` / `Fnt` | Design tokens (colours, fonts) |
| `Anim` | Shared 50 ms animation clock |
| `Draw` | GDI+ helpers (rounded rects, colour math) |
| `SystemHelper` | Sleep prevention via `SetThreadExecutionState` |
| `TimedWebClient` | `WebClient` subclass with a 30 s hard timeout |
| `DarkMenuRenderer` | Dark theme for `ContextMenuStrip` |
| `SteamButton` | Fully owner-drawn button (hover / press / disabled states) |
| `GameCard` | 90 px owner-drawn game row with live idle timer |
| `AppConfig` | Config persistence in `%AppData%` |
| `BadgeScraper` | Async multi-page badge page HTML scraper |
| `CookieLoginDialog` | Embedded `WebBrowser` Steam login + `InternetGetCookieEx` |
| `SettingsForm` | 3-tab settings dialog |
| `MainForm` | Main window; orchestrates all logic |
| `Program` | Entry point; single-instance mutex + global exception handlers |

---

## Known limitations

- **Cookie login uses the IE/MSHTML engine** (`WebBrowser` control). Steam's login page is  
  a React SPA — if Valve breaks IE compatibility, cookie capture will stop working. As of  
  2026 this still functions correctly.
- **API mode does not show card-drop counts** — this data is not available through the  
  Steam Web API. Use Cookie Login if you want to see which games have drops remaining.
- **No automated card-drop detection** — the tool idles games; it does not know when a  
  card actually drops. Check your Steam inventory manually.

---

## Changelog

### v1.0.0
- Initial release
- Three auth modes: Cookie Login, API Key, Manual/Quick Idle
- Owner-drawn dark UI with game thumbnails, per-game timers, search
- System tray, sleep prevention, process crash detection
- Single-instance mutex, global exception handlers, 30 s network timeout
- Progress indicator during multi-page badge scraping
