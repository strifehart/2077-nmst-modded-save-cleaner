NMST MODDED SAVE CLEANER  ·  test build {VERSION}
by strife//art  ·  https://strifehart.com/  ·  https://www.nexusmods.com/profile/strifehart

What it does
  Removes the "modded" tag from Cyberpunk 2077 saves you've already made: the flag behind the modded
  icon/warning on PC and the broken-trophy icon on consoles (Cross Progression). Companion to the
  No Modded Save Tag plugin, which stops new saves from being tagged.

Requirements
  Windows 10 or 11, 64-bit. Nothing else to install: .NET and everything the app needs is inside the exe.
  Music needs a sound output; without one the app simply runs silently.
  Windows SmartScreen may warn on first launch because the exe isn't code-signed:
  click "More info" -> "Run anyway".

How to use
  1. Close Cyberpunk 2077 (the app won't change saves while the game runs).
  2. Run NMSTSaveCleaner.exe. Nothing to install; it's one self-contained file.
  3. Read and accept the disclaimer (shown every launch).
  4. Pick a tagged save (red or orange dot) and press "Clean tag", or tick several (or "Tagged" to
     select all of them) and run "Clean modded tag".

What the dots mean
  red      Modded       The game shows the modded icon/warning for this save.
  orange   Sticky flag  Looks clean in the menus, but a hidden flag inside sav.dat is still set, so the
                        next time this save is saved it gets tagged again. Clean it too.
  green    Clean        Nothing to do; batch cleaning skips these.
  grey     Checking     Shown briefly at startup while the app reads each save.
  "Tagged" (the selection shortcut and the counts) means red + orange.

Every save is backed up before it's changed, and can be restored from its panel. The app keeps its files in
  %LOCALAPPDATA%\Cyberpunk 2077 - NMST Modded Save Cleaner\
    Backups\   full copies taken before every change (never deleted automatically)
    Logs\      everything the app did
    settings.json   music mute/volume and a saves folder you picked

Saves are found automatically (Windows: Saved Games\CD Projekt Red\Cyberpunk 2077). If yours are somewhere
else, use "Change folder"; the choice is remembered.

This is a TEST BUILD. Back up saves you care about yourself as well, and please report anything odd along with
the log file. Unofficial fan tool, not affiliated with or endorsed by CD PROJEKT RED. Use at your own risk.
Third-party licences (fonts, libraries) are in the licenses folder.
