# NMST Modded Save Cleaner

A companion app to **No Modded Save Tag** (NMST) for Cyberpunk 2077. It has one job: remove the "modded" tag from
saves you've already made, the flag behind the modded icon and warning on PC and the broken-trophy icon on consoles
(via Cross Progression).

The plugin stops *new* saves from being tagged. This app cleans the ones that already are.

## What it does

- Lists your saves grouped by playthrough, the same way the game's load menu does, with the quest, location,
  level, playtime and screenshot of each one
- Shows which saves are tagged, including the hidden "sticky" flag inside `sav.dat` that tags a save again the
  next time it's saved
- Cleans one save or hundreds at once. Saves that are already clean are always skipped.
- **Backs up every save before changing it**, and checks the result after writing. If the check fails, it puts the
  backup back by itself.
- Restores any backup, comparing several side by side. The current save is backed up before a restore, too.
- Refuses to change anything while the game is running

## Requirements

- Windows 10 or 11, 64-bit. Nothing else to install: the release is one self-contained `.exe` with .NET inside.
  Its native parts (graphics, text, audio) only use DLLs that ship with Windows; no Visual C++ runtime needed.
- Music needs a sound output. Without one (e.g. Remote Desktop without audio) the app runs silently.
- The `.exe` isn't code-signed, so SmartScreen may warn on first launch: **More info → Run anyway**.

## Using it

1. Close Cyberpunk 2077.
2. Start the app and read the disclaimer. It's shown on every launch.
3. Pick a tagged save (red or orange dot) and press **Clean tag**, or tick several (or **Tagged** to select all of
   them) and run **Clean modded tag**.

### What the dots mean

| Dot | State | Meaning |
| --- | --- | --- |
| 🔴 red | **Modded** | `isModded` is set: the game shows the modded icon/warning for this save. |
| 🟠 orange | **Sticky flag** | Looks clean in the menus, but the hidden `ModdingSystem` flag in `sav.dat` is still set, so the next time this save is saved it gets tagged again. Clean it too. |
| 🟢 green | **Clean** | Neither flag is set. Nothing to do; batch cleaning skips these. |
| ⚫ grey | **Checking** | Shown briefly at startup while the app reads each save's `sav.dat`. |

"Tagged" (the selection shortcut and the counts) includes both red and orange saves.

Saves are read from `%USERPROFILE%\Saved Games\CD Projekt Red\Cyberpunk 2077` (change it with **Change folder**).
The app keeps its own files in `%LOCALAPPDATA%\Cyberpunk 2077 - NMST Modded Save Cleaner\`:

| | |
| --- | --- |
| `Backups\<save>\<date-time>\` | full copies of saves taken before every change |
| `Logs\ModdedSaveCleaner.log` | everything the app did |
| `settings.json` | music mute and volume |

Backups are never deleted automatically. Delete old ones from a save's panel when you no longer need them.

## How the tag works

Each save marks itself as modded in two places:

1. `metadata.9.json` → `isModded`, which the menus read
2. `sav.dat` → the `ModdingSystem` node (a u32 plus a u8 flag). The game ORs this back into `isModded` on every
   save, so editing only the JSON doesn't last.

The cleaner clears both. In `sav.dat` it recompresses only the LZ4 chunk that holds the flag, then re-reads the
file and checks that nothing but that byte changed.

## Building

Requires the .NET 10 SDK.

```
dotnet build NmstSaveCleaner.sln
dotnet test tests/Nmst.SaveCleaner.Tests
dotnet run --project src/Nmst.SaveCleaner.Avalonia
```

| Project | |
| --- | --- |
| `src/Nmst.SaveCleaner.Core` | save reading/writing, cleaning, backups, settings; no UI |
| `src/Nmst.SaveCleaner.ViewModels` | all screen logic (MVVM), shared by any UI |
| `src/Nmst.SaveCleaner.Avalonia` | the Avalonia desktop app |
| `tests/` | tests, run against copies of real saves when `SAVEBROWSER_TEST_SAVES` or the default saves folder exists |
| `tools/Snapshots` | renders the UI off-screen to PNGs |
| `tools/DragProbe` | off-screen check that the save list's scrollbar thumb can be dragged at several window sizes |
| `tools/IconGen` | builds `app.ico` from `design/icon-app-*.svg` |
| `tools/music` | the synth engine and render/encode scripts for the background music |
| `tools/build-names.js` | rebuilds the quest/location name table from the game files |

## Credits

Made by **strifehart**: [strifehart.com](https://strifehart.com/) · [Nexus Mods](https://www.nexusmods.com/profile/strifehart)

Original synthwave soundtrack synthesised for the app. Fonts: Orbitron and Rajdhani (SIL Open Font License).
See `licenses/` for third-party notices.

This is an unofficial fan tool. It isn't made, supported or endorsed by CD PROJEKT RED. Use it at your own risk and
keep your own copies of saves you care about.

## Licence

Released under the [MIT License](LICENSE). You're free to use, modify and share it, as long as the copyright notice
and licence text come with it. The bundled fonts and libraries keep their own licences (see `licenses/`).

The software is provided as is, without warranty. Back up your saves.
