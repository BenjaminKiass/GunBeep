# GunBeep — notes for Claude

User talks in Thai; answer in Thai. This folder is the user's kept copy (made 2026-10-14) for reinstalling
Windows. README.md is the user-facing guide; this file is the technical context.

## What it is
- `recoil.lua` — Logitech G Hub Lua script (PUBG recoil compensation, time-based integration of a `segs` table,
  3 gun presets with `base`/`x`, live tuning, console dump of the `GUNS` table). Embedded into GunBeep.exe as
  resource `recoil.lua`. Header line `GunBeep-Script-Version: N` — bump N whenever the script changes so
  GunBeep offers the update.
- `GunBeepApp/` — WPF app (.NET 10, net10.0-windows, WPF + WinForms for NotifyIcon). Files:
  - `MainWindow.xaml(.cs)` — UI: power button, 3 gun cards (rename by double-click), hotkey capture,
    sound / topmost switches, G Hub status card + "ตั้งค่าอัตโนมัติ" setup flow.
  - `KeyHook.cs` — WH_KEYBOARD_LL hook; own injected keys tagged with dwExtraInfo 0x47424545 and ignored.
  - `GHub.cs` — detect install (registry DisplayName "G HUB"), find/verify installer (WinVerifyTrust +
    signer O=Logitech), list/pick scripts, install script (merge), lock PUBG profile, stop/start G Hub.
  - `Support.cs` — Settings (JSON in %APPDATA%\GunBeep and mirrored to `<exe dir>\saved\settings.json`),
    synthesized sound effects, tray icon drawing.
- `build.ps1` — `dotnet publish` → self-contained FOLDER (GunBeep.exe + ~250 runtime files, ~160 MB),
  copied next to the docs. Single-file publish was dropped on 2026-10-19: after adding the slider panel the
  single-file exe crashed at startup with 0xC0000005 on the user's PC (Malwarebytes is installed); the same
  code as a normal folder publish runs fine. Debug/framework-dependent also fine.
- UI is landscape (960 px wide, 3 columns: power | gun cards + base/x sliders | settings + G Hub).
  Sliders write to the script file at once and are pushed LIVE into G Hub 500 ms after the last move
  (`applied` = what G Hub has in memory, `vals` = wanted). Fallback when no ack: restart G Hub.
- Live channel GunBeep → Lua (script v5+, `LiveLink.cs` / `rx_poll()` in recoil.lua): Right Ctrl state
  change = clock (first change after >150 ms idle = frame start), Caps Lock toggled before a clock = bit 1.
  24-bit frame: `10 | gun(2) | base*2 (8) | x*2+128 (8) | (gun+base+x)%16 (4)`, 25 ms gaps (~1.3 s/gun).
  Lua acks with F18; Caps Lock is restored afterwards. Avoided Ctrl+Shift (language hotkey) and Shift
  alone (Sticky Keys). Verified 2026-10-19 on the user's PC: base and negative x applied without restart.

## GitHub + self-update
- Repo: github.com/BenjaminKiass/GunBeep (public). Working copy = the user's install folder
  `%USERPROFILE%\Downloads\มาโครพับจี\GunBeep` (.gitignore whitelists only source/docs; runtime files,
  `saved\` and the Logitech installer are never committed — the installer is proprietary).
- `.github/workflows/release.yml`: push to main (GunBeepApp/**, recoil.lua) → windows-latest, .NET 10,
  `dotnet publish -p:Version=1.0.<run_number>` → `GunBeep-win-x64.zip` → `gh release create v1.0.<run>`.
- `Updater.cs`: GET releases/latest, compare with assembly version, download zip to %TEMP%, PowerShell
  waits for GunBeep to exit, robocopy over the app folder excluding `saved\`, relaunches. UI: "⬆ อัปเดต"
  button in the title bar; check at startup and every 6 h. Local builds are 1.0.0 → always older.
- Commits are authored with the user's GitHub noreply email.

## GunBeep ⇄ G Hub protocol (G Hub Lua can only read lock-key LEDs)
- State on Scroll Lock / Num Lock: off/off = macro OFF, Scroll = gun 1, Num = gun 2, both = gun 3.
  When switching, turn ON the needed LED first, then turn off the other (never passes through OFF).
- On/off hotkey lives in GunBeep (default F8). Caps Lock is NOT used anymore.
- Mouse Back (button 4) handled in Lua, only while ON: plain = next gun (Lua writes LEDs itself, then
  ignores LED reads for 300 ms); Ctrl/Alt = base ±0.5 (sends F21 / F22); Shift+Ctrl/Alt = x ±0.5
  (sends F19 / F20); scope cycle sends F23 (only when UNIFIED_SENS = false). Script version 4+.
- GunBeep mirrors base/x per gun: reads the `GUNS` lines from the G Hub script at startup / after setup /
  whenever the `lghub_agent` PID changes (G Hub restarted → script reloaded from file), applies ±0.5 on
  F19–F22, shows them on the gun cards, and writes them back into script.lua + saved\guns.lua (600 ms
  debounce). No G Hub restart needed: G Hub already has those values in memory.
- GunBeep debounces LED changes 150 ms before decoding (G Hub toggles two LEDs in a row).
- GunBeep forces OFF at startup and on exit.
- Do NOT use GetKeyState to re-sync lock state on window activation: it lags one toggle. Read once at
  startup, then track from hook events.

## G Hub internals (found on v2021.11.1775)
- Scripts: `%LOCALAPPDATA%\LGHUB\scripts\<guid>\script.lua` + `config.json` ({name, description, fileName}).
- Settings: `%LOCALAPPDATA%\LGHUB\settings.db` (SQLite, table `data(_id, _date_created, file)`; `file` is
  one big JSON). Locked while G Hub runs → read a copy (FileShare.ReadWrite).
  - `applications.applications[]` (PUBG found by "BATTLEGROUNDS" in the entry), `profiles.profiles[]`
    (`applicationId`, `id`, `activeForApplication`, `scriptId`), `persistent.id` = locked profile id.
  - Profile `scriptId` (16 hex) does NOT match the script folder guid; mapping unknown. So GunBeep cannot
    create/bind a script itself — the user must "Create new Lua script" once; GunBeep then picks the
    script folder (GunBeep-marked first, else most recently modified) and overwrites `script.lua`.
- G Hub does NOT hot-reload script.lua on file change. Killing `lghub` + `lghub_agent` and starting
  `"C:\Program Files\LGHUB\lghub.exe" --background` reloads it (~8 s). Verified with an F24 test.
- Script install keeps the user's tuned section: regex `^-- ===== ปืน ... ^local gun_idx = 1` is copied from
  the old script (or from `saved\guns.lua` on a fresh machine) into the new template.
- Backups before any write: `%APPDATA%\GunBeep\backups\script-*.lua`, `settings-*.db`.

## Status (2026-10-14)
- Tested on the user's PC: key hook, gun cards, LED signalling (G Hub console showed gun=gun1/2/3),
  status detection, script install + merge + G Hub restart.
- Not yet exercised for real: installing G Hub from the installer, and LockPubgProfile (settings.db write)
  — profile was already locked.
- No Lua interpreter on the machine; recoil.lua was only checked by G Hub loading it.
- Tray/window icon = GunBeep logo (gradient + crosshair, grey when OFF) with a green/red status dot.
- base/x mirroring + auto-save added 2026-10-14 evening; built, not yet tried in-game by the user.
