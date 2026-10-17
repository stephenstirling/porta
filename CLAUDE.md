# Porta

Free Windows 11 Start menu replacement by Stephen Stirling. WinUI 3 on the Windows App SDK,
.NET 8, runs unpackaged (plain .exe, no MSIX yet).

## Build and run

Needs only the .NET 8 SDK (`winget install Microsoft.DotNet.SDK.8`). Visual Studio is not required.

```
dotnet build src/Porta -c Debug
src\Porta\bin\Debug\net8.0-windows10.0.19041.0\win-x64\Porta.exe
```

Porta takes over the Win key while running. To get the normal Start menu back, use the tray
icon's "Exit Porta", or end `Porta.exe` in Task Manager. Stop Porta before rebuilding, or the
build fails because the .exe is locked.

## Layout

- `src/Porta/Program.cs` - entry point; single-instance redirect via `AppInstance`
- `src/Porta/App.xaml(.cs)` - startup, tray icon, settings backup/restore
- `src/Porta/MenuWindow.xaml(.cs)` - the menu window (pinned, all apps, search, games)
- `src/Porta/Interop/` - Win32 P/Invoke and the low-level keyboard hook for the Win key
- `src/Porta/Services/` - app catalog, icons, search, `>` command runner, settings store,
  account picture, recent files
- `src/Porta/Services/Games/` - Steam, Epic, Battle.net and Xbox game discovery
- `src/Porta/Models/` - plain data types
- `design/` - HTML mockups of the intended UI (reference only, not built)
- `docs/` - checklists and notes

Settings live in `%LOCALAPPDATA%\Porta\settings.json`.

## Rules

- Commits are small and made after each working step.
- Never push to `main` without asking the owner first; work on branches.
- No paid code signing, no telemetry.
- Keep fixes minimal; don't redesign without being asked.
- The owner is not a developer: explain problems in plain words.
