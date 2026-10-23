# Porta

A modern Start menu for Windows 11. WinUI 3, .NET 8.
© 2026 Stephen Stirling

Porta is free. If it saves you time, you can support it at [buymeacoffee.com/stephenstirling](https://buymeacoffee.com/stephenstirling) ☕

## Run it

You need Visual Studio 2022 (17.10 or newer) with the **Windows application development** workload,
or the .NET 8 SDK on its own.

- **Visual Studio:** open `src/Porta/Porta.csproj`, set the platform to **x64**, and press F5.
- **Command line:** `cd src/Porta` then `dotnet run`

Porta starts in the background. Tap the **Win key** (or click the door icon in the system tray) to open it.
The tray menu also has a **Buy me a coffee** link. To quit, right-click the tray icon and choose **Exit Porta**; the normal Windows Start menu comes straight back.

Settings are saved to `%LocalAppData%\Porta\settings.json`.

The app icon is the Keyhole (`Assets/Porta.ico`, with the source in `Assets/Porta.svg`). The tray uses a single-colour version that switches between white and black to match the taskbar.

## What milestone 1 does

- Win key opens Porta instead of the Windows Start menu. Win+E, Win+R and other shortcuts still work.
- Frosted Acrylic menu, centred above the taskbar on whichever screen your pointer is on, in light or dark to match Windows.
- Pinned apps (drag to reorder; right-click to pin, unpin, or run as administrator). A starter set is pinned on first run.
- All apps, grouped A–Z, with real icons for both desktop and Store apps.
- Search that forgives: initials ("vsc"), letters in order ("phts"), and one typo all match. Arrow keys and Enter work.
- Recent files, folder shortcuts, Windows Settings, Run, Lock, and a power menu (Sleep, Sign out, Restart, Shut down).
- Only one copy ever runs; starting Porta again just opens the menu.
- Games in search from **Steam, Epic Games, Battle.net and the Xbox app**, read from each launcher's own files
  on this PC. Typing "red dead" or "rdr2" finds Red Dead Redemption 2 with a Play button that starts it through
  its launcher. Steam store results for games you don't have appear too (online; `SearchSteamStore` in settings
  turns them off), plus quick links to search the Steam, Epic Games, Battle.net and Microsoft stores.
- **Quick answers:** type a sum, a percentage or a conversion and the answer is the top row; Enter copies it.
  `2+2*3`, `15% of 240`, `72 f in c`, `5 km to mi`, `1 gb in mb`, and (with its own switch) `5 usd in eur`.
  A **Search the web** row is always last and opens your browser on Google (or DuckDuckGo or Bing, your choice).
- **Wikipedia in search:** up to 3 articles with a thumbnail and short description, from the Wikipedia in your
  Windows language (English if that has no match). Open goes to the article in your browser. Turn it off with
  the "Search Wikipedia" switch (in the glass-strength pop-up for now).
- Your account picture **breaks out of the top edge** of the side panel; a glass-strength slider (palette button
  beside Lock and Power) sets how frosted the menu is.
- **Run commands from search:** type `>` then a command, like `> ping 1.1.1.1` or `> notepad notes.txt`.
  Enter runs it; Ctrl+Shift+Enter runs it as administrator.
- **Shorthand names:** "rdp" finds Remote Desktop, "cmd" Command Prompt, "regedit" Registry Editor, and so on
  (`SearchService.Synonyms`).
- **Hide apps from All apps** (right-click). Hidden apps still show in search, where right-click shows them again.
- **Back up and restore settings** to a `.portabackup` file from the tray menu: pins, folders, profiles,
  hidden apps and preferences. A restore keeps your previous settings as `settings.before-restore.json`.
- Your **Windows account picture and name** (your Microsoft account picture, as on the lock screen) at the
  top of the side panel. A new picture shows up the next time the menu opens.

## Privacy

Porta has no telemetry. These search features go online, and only while they're switched on:

- **Wikipedia** (`SearchWikipedia`, on by default): what you type in search (3 or more characters) is sent to
  Wikipedia (`<language>.wikipedia.org`) to find articles.
- **Wikipedia pictures** (`WikipediaPictures`, on by default, "Wikipedia pictures" switch under "Search Wikipedia", saved and
  included in settings backups; does nothing while Search Wikipedia is off): the same request also asks Wikipedia for
  each article's small picture, and Porta downloads the ones for the articles on screen from
  `upload.wikimedia.org` (nothing else is ever fetched), with the same `Porta/<version>` User-Agent. They are kept
  in memory and in `%LOCALAPPDATA%\Porta\WikiPictures` (about 20 MB at most). The pictures can include non-free covers
  (album, film and game covers, logos); they are shown only as small thumbnails next to the article link. For an
  article that comes back without a picture, Porta makes one more small request to that article's summary to look for
  its lead picture (at most the 3 rows on screen, never for disambiguation pages). Switching the pictures off stops
  all picture requests, deletes that folder, and the rows go back to letter tiles.
- **Steam store results** (`SearchSteamStore`, on by default, only when Steam is installed): what you type
  (3 or more characters) is sent to the Steam store to find games you don't own.

- **Online currency rates** (`OnlineCurrencyRates`, **off** by default, "Online currency rates" switch in the glass-strength
  pop-up, saved and included in settings backups; does nothing while "Quick answers" is off): only when you type a
  currency conversion such as `5 usd in eur`, Porta asks the free Frankfurter service (`api.frankfurter.dev`, European
  Central Bank reference rates, no account or key) for the day's rates, with the `Porta/<version>` User-Agent. The request
  contains no part of what you typed. The rates are kept in memory for 12 hours, forgotten when the switch is turned
  off, and a failed request is not retried for a minute. With the switch off, a currency query just shows a hint row.

Everything else, including your apps, games and files, stays on your PC. `>` commands are never sent anywhere.

**Epic library** (`SearchEpicLibrary`, on by default, "Epic library" switch in the glass-strength pop-up):
Porta reads only the Epic Games Launcher's local catalog cache
(`C:\ProgramData\Epic\EpicGamesLauncher\Data\Catalog\catcache.bin`), read-only, to show Epic games in search
labelled "Epic". It never goes online for this, never writes to the file, and never reads Epic login, token or
account files. The cache can include games you don't own; Open hands the game to the Epic launcher.

**Epic cover images** (`EpicCoverImages`, on by default, "Epic cover images" switch next to the Epic library one,
saved and included in settings backups): the Epic cache lists a web link to each game's cover picture. When an Epic
result is on screen, Porta downloads that one picture from Epic's image server (`cdn1.epicgames.com`), which sees
your IP address and a `Porta/<version>` User-Agent, nothing else. Only results being shown are fetched, never the
whole library. Pictures are kept in `%LOCALAPPDATA%\Porta\EpicCovers` (at most 40 MB, deleted after 30 days) and a
failed download is ignored. With the switch off nothing is downloaded and the tile shows the game's icon or a
letter. The small Epic badge is the icon of your installed Epic Games Launcher, read from your PC when Porta runs;
Porta contains no Epic logo, and without the launcher only the text label shows.

**Quick answers** (`ShowQuickAnswers`, on by default, "Quick answers" switch in the glass-strength pop-up, saved and
included in settings backups): maths (`+ - * / ^`, brackets, percentages) and unit conversions (length, weight,
temperature, volume, speed, data size) are worked out on your PC and never sent anywhere. Only text that is clearly a
calculation gets an answer, so "portal 2", "3d", "1-800" or a date show nothing. KB, MB, GB and TB are decimal
(1 GB = 1000 MB); KiB, MiB and GiB are binary; volumes are US measures.

**Search the web** (`SearchTheWeb`, on by default, "Search the web" switch, and `WebSearchEngine`: Google by default,
or DuckDuckGo or Bing, chosen in the same pop-up; a saved choice is always kept): the last row of search results, for
2 or more characters. Porta sends nothing itself. Only when you click the row (or press Enter with nothing else to
open) does your default browser open the search page of the engine, and **that engine (Google unless you picked
another) then receives the words you typed**, under that company's own privacy policy. Until you click, nothing is sent.

**People in Ocula** (`SearchOculaPeople`, **off** by default, "Search people in Ocula" switch in the glass-strength
pop-up, saved and included in settings backups): when switched on, Porta reads the names Ocula keeps in
`%LocalAppData%\Stirling\Ocula\people-index.json` (format version 1) so you can find a person from search. A packaged
Ocula (Store or sideloaded) has its writes there redirected by Windows into its own package folder, so Porta also looks in
`%LocalAppData%\Packages\StephenStirling.Ocula_*\LocalCache\Local\Stirling\Ocula\` and reads whichever of the places has
the most recently written file. Only the fields `id`, `name`, `aliases`, `description`, `photoCount` and `thumbnail` are read. The file is only read, never
written, and is read again only when its timestamp changes. Face thumbnails are loaded from the local picture
files the index points to (network paths are ignored). Nothing about people is ever sent online. "Open" starts
Ocula with an `ocula:person?id=...` link; if Ocula isn't installed the row shows without it. Turning the switch
off forgets the list and the file isn't read again.

## Roadmap

| Milestone | Contents |
|---|---|
| 1 ✅ | Core menu, Win key, search, pinned, all apps, recent, power |
| 2 | Start button replacement (overlay), Porta Settings window, accent colour, start with Windows |
| 3 | Folders in Pinned, Profiles |
| 4 | Smart search: maths, unit conversion, Everything file search, Windows settings pages |
| 5 | Ocula photo strip, open animation, compact mode, MSIX packaging for the Store |
| 1.1 | Pinned tabs (your profiles as tabs, switching by time of day) and a classic single-column style; designs on the canvas |

## Code map

| Path | Job |
|---|---|
| `Program.cs` | Entry point; keeps Porta to a single running copy |
| `App.xaml.cs` | Starts everything: settings, keyboard hook, tray icon, app list |
| `MenuWindow.xaml(.cs)` | The menu itself |
| `Interop/KeyboardHook.cs` | Catches a lone Win key press and stops Windows' Start from opening |
| `Interop/Native.cs` | Win32 calls (input, windows, monitors, power) |
| `Services/AppCatalog.cs` | Reads every installed app from `shell:AppsFolder` |
| `Services/ShellIcons.cs`, `IconLoader.cs` | Real Windows icons, loaded in the background |
| `Services/SearchService.cs` | Search ranking |
| `Services/SettingsStore.cs` | Saving, pinning, profiles |
| `Services/Games/` | Installed games from Steam, Epic, Battle.net and Xbox; Steam store search |
| `Services/AccountPicture.cs` | Windows / Microsoft account picture and name |
| `Services/CommandRunner.cs` | Runs `>` commands typed in search |
| `Models/` | Data types |

## Things to check on a real PC

- **Battle.net:** games are found from their uninstall entries and launched with
  `Battle.net.exe --exec="launch <product code>"`. Product codes differ per game; if one doesn't start, note the
  code shown in the uninstall entry and adjust `BattleNetLibrary`.
- **Xbox app games:** found through each drive's `.GamingRoot` file and `XboxGames\<game>\Content\MicrosoftGame.config`.
  Check a Game Pass game shows with the Xbox label.
- **Account name:** for a Microsoft account, the full name needs the `userAccountInformation` capability in the
  MSIX manifest (added when Porta moves to MSIX). Until then Porta shows the Windows sign-in name.

## Known limits in milestone 1

- Clicking the Windows Start **button** still opens the Windows menu; the overlay that replaces it arrives in milestone 2.
- x64 only for now; ARM64 is a one-line change once we package for the Store.
