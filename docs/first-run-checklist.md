# First run checklist

First real run of milestone 1 on the owner's PC (Windows 11 Pro 10.0.26300, branch `first-build`).

| # | Check | Result | Notes |
|---|---|---|---|
| 1 | Win key opens Porta; Win+E and other combos still work | pending | |
| 2 | Pinned grid and All apps show real icons | pending | |
| 3a | Search finds apps | pass (ranking) | Fixed: fuzzy matching was too loose ("note" returned GIGABYTE Control Center). Against this PC's 179 apps, "note" now gives Notepad, Release Notes, Git Release Notes; "calc" gives only Calculator; "phts" still finds Photos; "calculater" still finds Calculator. Spell check and text suggestions turned off in the search box. On-screen check pending |
| 3b | Shorthands: "rdp", "cmd" | pass (ranking) | "rdp" gives only Remote Desktop Connection; "cmd" gives Command Prompt then Git CMD. On-screen check pending |
| 3e | Steam store results below installed items, 3+ characters, no adult games | pass (live API) | Moved to their own "From the Steam store" section. Adult filter uses Steam content descriptors 1, 3, 4: an explicit test search showed 0 results; "red dead" showed 3. The Witcher 3 is hidden too (Steam tags it nudity). On-screen check pending |
| 3c | Maths in search | not built | Planned for milestone 4 (README roadmap); no maths code in milestone 1 |
| 3d | `> ping 1.1.1.1` runs; Ctrl+Shift+Enter runs as admin | pending | |
| 4a | Steam games appear and launch | pending | 8 games installed. Fixed: covers were only found for 1 of 8 (Steam's newer hashed cache folders) |
| 4b | Epic games | can't test | Epic launcher not installed on this PC |
| 4c | Battle.net games (launch codes unverified) | can't test | Battle.net not installed on this PC |
| 4d | Xbox app games (detection unverified) | can't test | `C:\XboxGames` has no games, only `GameSave` |
| 5 | Microsoft account picture at top right | pending | Picture found in the registry path the code reads |
| 6 | Hide from All apps; still appears in search | pending | |
| 7 | Tray: Back up / Restore settings with a `.portabackup` file | pending | |
| 8 | Second launch opens the menu in the first copy | pass | Second copy exited with code 0; first copy's menu window became visible and foreground |
