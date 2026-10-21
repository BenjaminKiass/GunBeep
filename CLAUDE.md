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

## Branding / overlay
- Name shown: "all@Ben-GunBeep" (user's brand). Theme = black/navy + electric blue #1D6BFF → ice #5CE1FF,
  gold #C9A54A accents (from the user's all@Ben logo: ice letters, gold crown). Window border gradient
  rotates (`EdgeSpin`), open/minimize animate (`AnimateIn`/`AnimateOut`), KeyBtn hover glow.
- Logo: `Branding` (Support.cs) loads `<exe dir>\logo.png` first, else embedded resource `logo.png`
  (csproj embeds `..\logo.png` if it exists; .gitignore whitelists `/logo.png`). Used for title-bar image
  (circle clip) and tray icon (grey when OFF). Fallback = gold ♛ tile / crosshair tray icon.
- `Overlay.cs`: click-through topmost tool window (WS_EX_TRANSPARENT|TOOLWINDOW|NOACTIVATE), primary
  screen corner, modes off / pop 2.5 s / always (`cfg.OsdMode`, `cfg.OsdCorner`). Re-asserts HWND_TOPMOST
  every 1.5 s while shown. Not visible over exclusive fullscreen. App uses ShutdownMode.OnMainWindowClose.

## Presets (Presets.cs)
- `Preset` JSON {App, Format, Name, Saved, Guns[{Name, Base, X}]} in `saved\presets\<name>.json`; Parse validates
  (1-3 guns, base 0..60, x -30..30, snapped to 0.5). `cfg.Preset` = last used name; button shows " •" when
  current names/values differ. Loading writes vals + names, then the normal save + live path (resets liveFailed).
- Preset hotkey `cfg.PresetKey` (default F9 0x78, 0 = off) → `CyclePreset()`: no dialogs; unsaved current values are
  saved first as preset "ก่อนสลับ (อัตโนมัติ)" (excluded from the cycle); applies base/x live only, segs differences
  are reported on the overlay (needs restart). Capture shares `CaptureKey` with the on/off hotkey (`capturePreset`).
- `InputDialog` = dark themed name prompt. Menus are code-built ContextMenus using the implicit dark
  ContextMenu/MenuItem/Separator styles in MainWindow.xaml (assigned explicitly via TryFindResource).
- Bug fixed 2026-10-21: live frames for several guns were sent back-to-back; the script only starts a new frame
  after >150 ms of Right Ctrl silence, so the 2nd frame was lost. Now `LiveLink.FRAME_GAP_MS` (250) between frames.
  Verified with a standalone sender: ~20 ms gap → 1 ack of 2, 250 ms → 2 acks.

## Recoil graph (segs) — SegsEditor.cs
- `GunVals.Steps` = per-10 ms values expanded from the gun's `segs` table (ReadGunValues follows each GUNS line's
  `segs = NAME`). `GHub.WriteSegs(steps[])` replaces all segs tables in the gun section with run-length tables
  (identical guns share one: SEGS_MAIN, SEGS_2, SEGS_3), patches `segs = X, sname = "X"`, backs up script.lua.
  Segs can't go over the live channel → `ApplyRestartAsync(segs)` writes + restarts G Hub.
- Editor: bars of 100 ms (10 steps); painting a bar sets its 10 steps to one value (snap 0.25); untouched bars
  keep their original per-step values. Tools: shift all ±0.5, 1-2-1 smooth, copy from gun, default
  (embedded template), undo stack.
- Presets store `Segs` as [value, count] pairs (optional; old files have none and ignore segs in Matches).
- Verified 2026-10-21: +0.5 on gun 1 → SEGS_MAIN/SEGS_2 written, G Hub restarted and acked a live frame;
  −0.5 restored identical values (only line wrapping differs). Mouse painting not yet tried by the user.

## Live graph (script v6, 2026-10-21)
- Timing measured on the user's PC: the script sees Right Ctrl changes every ~16 ms (12 ms/bit lost frames,
  18 ms passed 10/10). Default Windows timer rounds Sleep to 15.6 ms, so `LiveLink.Send` uses timeBeginPeriod(1),
  ONE sleep per bit (Caps toggle + edge, then sleep) and BIT_MS = 28 → base/x frame ~0.7 s.
- Graph frame `11 | gun(2) | runs(5) | runs × (value*4 (7) + bars (5)) | sum%256 (8)`; bar = 10 steps (100 ms).
  Lua `rx_poll` grows `rx_need` after bit 2 (type 11 → 9) and bit 9 (→ 9 + runs*12 + 8); `rx_apply_segs` rebuilds
  GUNS[g].rc/count and acks F18. `rx_poll` is also called inside `spray()` (new graph used by the next spray).
- Edited/loaded graphs are quantized (`LiveLink.Quantize`, bar average, 0.25 steps, max 31.75); comparisons use
  `SameGraph` (quantized). `appliedSteps` = what G Hub has; `SegsPending` feeds HasPending; after a live send the
  graph is written to script.lua (`WriteSegs`). Script < v6 → `NeedRestart` → restart path.
- `SendFrameAsync` retries a frame once if no ack. Verified: graph +0.5 on gun 1 sent live in ~4 s, same
  lghub_agent PID, file updated; then restored the detailed original from the backup.

## Auto resolution (Display.cs)
- `cfg.AutoRes/GameResW/GameResH`; MainWindow polls `TslGame` every 1.5 s; on start → `Display.Switch` with
  CDS_FULLSCREEN (temporary, same refresh rate if available), on exit / GunBeep close → `Display.Restore()`.
  Game already running when GunBeep starts → no switch. `cfg.ResAlways` = stretch from GunBeep start until exit.
  `KeepRes()` re-applies (≤ every 10 s) if something else changed the mode while it is wanted; the menu lists
  resolutions ≤ `Display.Desktop()` (registry mode, not the temporary one). User's PC has 1728x1080@300 (custom NVIDIA mode).
  Purpose: stretched 1728x1080 in Borderless so the overlay still shows (overlay can't draw over exclusive
  fullscreen; DX hooking/injection into PUBG is refused — BattlEye).

## Recoil recorder (Calibrator.cs + RawMouse.cs)
- Raw Input (RIDEV_INPUTSINK, hidden HwndSource) gives physical mouse counts even while the game has focus —
  same unit as G Hub MoveMouseRelative (assumed 1:1, not yet verified in game). Spray = left down → up, ≥500 ms,
  macro must be OFF (sprays while ON are ignored). Bucketed per 10 ms; `Analyze`: drop sprays whose early total
  differs >40 % from median (n≥3), average steps covered by ≥ half the sprays, quantize to 100 ms bars,
  base = floor(min bar to 0.5), graph = bars − base (≤31.75), x = mean dx per 10 ms (0.5 steps).
- Deliberately no screen capture / image analysis of the game.
- Fine-tune mode (2026-10-22): sprays fired with the macro ON for the calibrator's gun are `Tuned`. RawMouse now
  passes hDevice; the device that sent left-down = the hand. Expected macro motion per 10 ms = applied base +
  appliedSteps (`MainWindow.InMacro`, null while a send is pending). If other devices moved ≥40 % of expected →
  G Hub moves arrive separately: hand stream = pure correction, ratio = other/expected converts units. Else
  merged: correction = hand stream − expected. Spray Y = expected + correction (same shape as a manual spray),
  so `Analyze` is unchanged. Verdict "นิ่งแล้ว" when |mean corr| < 0.3 and RMS of 100 ms correction bars < 0.75.
  "ใช้ค่านี้" clears sprays and starts the next round. Not yet tried in game.
- Auto-tune (▶ ออโต้จูน): follows the macro's active gun; every 3 tuned sprays → `Analyze` → `Blend(lastUsed,
  fit, 0.7)` (move 70 % of the way in total-pull space, then re-split into base/graph; if rounding leaves it
  unchanged, use the fit) → apply live → next round. Verdict good twice in a row = done; stops after 12 rounds.
  Simulated (hand gain 0.8–1.2 + noise, start base 7 / no graph): converged to the true curve in ~3 rounds.
- `LiveLink.Fit`: graphs with >31 runs (every recorded bar differs slightly) could not be sent live; the user's
  first recording failed with a misleading "G Hub ไม่ตอบรับ". Fit merges the adjacent runs with the least
  |Δv|·min(n) until ≤31; used by Calibrator, presets, SegsEditor results and ApplyLiveAsync.

## CPU
- The window uses AllowsTransparency + blur/glow effects, so every animated frame re-renders the whole layered
  window. Endless animations (EdgeSpin border, LogoGlow, power Glow pulse) ran at ~60 fps → ~3.9 % of the
  20-thread CPU with the window open (user saw ~10 %). `UpdateAmbient()` now runs them only while the window is
  visible AND active, at 24 fps (`Slow`) → measured 0.8 % active, 0.02 % inactive/hidden (2026-10-21).

## Stability (2026-10-22)
- All script.lua read-modify-writes (`WriteGunValues`, `WriteSegs`, `InstallScript`, `ReadGunValues`) hold
  `GHub.ScriptLock`. Before this, the slider save (saveVals → Task.Run) and the live-graph save (WriteSegs →
  Task.Run) could run at once and the later one wrote back the older text → file reverted to old base/x, and
  the next G Hub restart (agent PID change → LoadGunValues) loaded the stale values. Best explanation found
  for the one-off "base 20" seen earlier (gun 1 used to be 20).
- MainWindow queues background writes on `fileWork` (`QueueFile`) in order; OnClosing waits up to 4 s;
  `ApplyRestartAsync`/setup await it first; `LoadGunValues` keeps values that were changed but not yet saved.
- `SafeFile.Write` = write `.tmp` then `File.Move(overwrite)` (fallback: direct write if the target is locked).
  Used for script.lua, saved\guns.lua, settings.json, presets.
- `Log` → `saved\log.txt` (rolls to log.old.txt at 512 KB): start/exit, values read/written/sent live,
  PUBG start/stop, display switch, unhandled exceptions (UI exceptions are logged and handled, app keeps running).
- In game: `Notify()` defers tray balloons while TslGame runs (shown after the game closes); resolution
  switch success shows nothing, failures use the overlay. The in-app updater relaunches with `--tray` when the
  game is running or the window is hidden. Update balloon shown once per tag.

## Autostart / single instance
- "เปิดพร้อม Windows" switch = `AutoStart` (Support.cs): HKCU Run value `GunBeep` = `"<exe>" --tray`. The
  registry is the source of truth (not settings.json). At startup `AutoStart.Refresh()` rewrites the path if
  the folder moved.
- `--tray`: window is created but never shown; `StartInTray()` runs the Loaded work (`Started()`).
  OnClosing skips saving Left/Top when they are NaN (never shown).
- Second launch sets named event `GunBeep_show` → running instance calls `ShowFromTray()`.

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

## x slider (2026-10-22)
- `XSlider` uses `CenterSlider` (transparent track); the bar is drawn behind it in code (`UpdateXBar`): grey track,
  `XFill` from 0 to the value (left or right), `XMid` tick at 0. Range is kept symmetric (±max(5, |x|)).
  Label `XText`: "ซ้าย 1.5" / "ขวา 1.5" / "0". One net value only — the Lua script applies a single x.

## In-game cycle button (script v7, 2026-10-22)
- `cfg.CycleMouse` (3/4/5, 0 = none) → Lua `CYCLE_BTN`; `cfg.CycleKey` (keyboard vk, 0 = none) → handled by
  GunBeep (`CycleKeyPressed`: plain = next gun via lock LEDs, Ctrl/Alt = base ±0.5, Shift+Ctrl/Alt = x ±0.5,
  modifiers via GetAsyncKeyState, values pushed live). Choosing a key sets CycleMouse = 0 (Lua side off).
- Config frame `01 | kind(2) | value(8) | 0(8) | (kind+value)%16`, kind 0 = CYCLE_BTN; Lua `rx_apply_cfg` acks F18.
  rx_poll now routes to the graph handler only for `11`. `GHub.WriteCycleButton` rewrites `local CYCLE_BTN = N`
  in script.lua (also right after InstallScript, since the template resets it to 4).
- Lua `IsMouseButtonPressed` only covers buttons 1–5, so G-keys 6+ can't be offered (OnEvent never returns).
