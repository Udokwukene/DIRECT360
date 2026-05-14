# DIRECT360

<p align="center">
  <img src="https://raw.githubusercontent.com/Udokwukene/DIRECT360/main/icon.ico" width="120" alt="DIRECT360 Logo">
</p>

<p align="center"><b>Universal DirectInput-to-XInput Controller Remapper for PC Gaming</b></p>

<p align="center"><i>Plug in any controller. Map it once. Play everything.</i></p>

---

## What is DIRECT360?

**DIRECT360** is a lightweight, console-based controller remapper for Windows that converts virtually any DirectInput gamepad into a virtual **Xbox 360 controller** — the standard that nearly every PC game supports out of the box.

Whether you have all cheap generic USB joystick controller showing up as DirectInput, DIRECT360 makes it work with your games without touching a single config file.

---

## Features

| Feature | Description |
|---------|-------------|
| **Universal Compatibility** | Works with all generic USB controllers both wired and wireless |
| **Interactive Wizard** | Step-by-step button & axis mapping — no manual editing, no XML, no hassle |
| **Multi-Controller Setups** | Create persistent, named setups for 2–4 players. Save, load, and manage them independently |
| **Smart Profiles** | Per-controller layouts with favorites ★, notes, last-used memory, and quick-load |
| **3 Polling Modes** | Eco (~100Hz), Balanced (~200Hz), Performance (~500Hz) — pick your trade-off |
| **Game Launcher** | Attach a game EXE to any layout or multi-setup and launch it automatically |
| **Auto-Reconnect** | Controller unplugged mid-game? Waits and resumes automatically |
| **Live Validation** | Loading a layout validates it against the real controller and warns about missing/duplicate mappings |
| **Export / Import** | Share your layouts with friends or back them up as JSON |
| **Crash Logger** | Unhandled exceptions are caught, cleaned up, and saved to `crash.log` |

---

## Requirements

- **Windows 10 or 11** (64-bit)
- **[ViGEmBus Driver](https://github.com/nefarius/ViGEmBus/releases/latest)** — install and reboot

> The release build is **fully self-contained**. No .NET runtime needed on the target machine.

---

## Download

Head to the [**Releases**](https://github.com/Udokwukene/DIRECT360/releases) page and grab the latest `DIRECT360.zip`. Extract and run `DIRECT360.exe`.

---

## Building from Source

### Prerequisites
- [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0)

### Build

```bash
git clone https://github.com/Udokwukene/DIRECT360.git
cd DIRECT360
```

**Option A — Full rebuild (verbose output):**
```bash
rebuild.bat
```

**Option B — Quick build & run:**
```bash
play.bat
```

**Option C — Manual CLI:**
```bash
dotnet publish DIRECT360.csproj -c Release -r win-x64 --self-contained true ^
  -p:PublishSingleFile=true ^
  -p:EnableCompressionInSingleFile=true ^
  -p:ReadyToRun=false ^
  -o dist
```

The compiled binary will be in the `dist/` folder.

---

## Quick Start

1. **Install ViGEmBus** and reboot.
2. **Plug in your controller(s).**
3. **Run `DIRECT360.exe`.**
4. Choose your mode:
   - `[1]` **Single Controller** — one pad, one profile.
   - `[2]` **Multi-Controller** — create or load a named setup for multiple pads.
5. Follow the on-screen wizard to map your buttons and axes.
6. Save your layout and select it from the menu.
7. Launch your game and play.

### Controls While Running
| Key | Action |
|-----|--------|
| `M` | Return to menu |
| `Ctrl + C` | Exit DIRECT360 |

---

## Mode Selection

When you start DIRECT360 you’ll see:

```
  [1] Single Controller Mode
  [2] Multi-Controller Mode
  [S] Settings
```

- **`1`** — Pick one controller and manage its layouts.
- **`2`** — Create persistent **Multi-Setups** for couch co-op / local multiplayer.
- **`S`** — Change global polling mode (Eco / Balanced / Performance).

---

## Single Controller Mode

### Layout Menu

```
  [N]  New layout (wizard)
  [1-n] Load a saved layout
  [L]  Quick load last used

  [E]  Edit layout
  [T]  Test layout
  [A]  Adjust sensitivity
  [Z]  Adjust deadzone
  [G]  Set game launcher
  [X]  Export layout
  [I]  Import layout
  [R]  Rename layout
  [D]  Delete layout

  [S]  Settings
  [B]  Back
```

**Favorites & Duplicates**
- Favorited layouts are marked with `★` and sorted to the top.
- If two mappings share the same physical button index, a `[!]` warning appears.
- When loading, the app validates the layout against the real controller and reports errors/warnings. You can still force-load if you choose.

---

## Multi-Controller Mode

DIRECT360 can run **up to 4 controllers simultaneously**, each with its own independent profile.

### Multi-Setup Menu

```
  [N] New Setup
  [1-n] Load Setup
  [B] Back to Main Menu
```

### Creating a Setup

1. Choose `[N]` — all currently plugged-in controllers are detected.
2. For each controller, choose:
   - `[N]` Create a new layout via the wizard.
   - `[E]` Use an existing layout already saved for that controller.
3. The setup is saved as a named JSON file in `profiles/MultiSetups/`.

### Managing a Setup

```
  [R] Run Now
  [E] Edit Controller
  [T] Test Controller
  [A] Adjust Sensitivity
  [Z] Adjust Deadzone
  [G] Set Game
  [D] Delete Setup
  [B] Back
```

- **Run** — launches all configured controllers in parallel with a shared virtual bus.
- **Edit / Test / Adjust** — per-controller maintenance without leaving the session.
- **Set Game** — attach an EXE to the whole setup; you’ll be prompted to launch it before remapping starts.

> ⚠️ If a controller from the setup is missing when you load it, a warning is shown and that player is skipped until reconnected.

---

## Setup Wizard

The wizard walks you through every input:

```
  1/18 [ A ]     → press the matching button on your controller
  ...
  11/18 [ LEFT STICK UP ]    → move stick UP, release
  ...
```

### Wizard Controls
| Key | Action |
|-----|--------|
| `B` | Go back to the previous step |
| `R` | Redo the current step |

If your controller disconnects during the wizard, reconnect it and the wizard will **resume from where you left off**. Press `Enter` to abort instead.

---

## Edit Layout

Inside `[E] Edit Layout` you can:

| Key | Action |
|-----|--------|
| `1-n` | Remap that entry by pressing a new button on the controller |
| `S` | Swap two entries (enter two numbers, e.g. `1 2`) |
| `F` | Toggle favorite on/off |
| `N` | Edit a text note for the layout |
| `Enter` | Back |

Duplicate mappings are highlighted with `[!dup]` so you can spot conflicts instantly.

---

## Test Layout

`[T] Test Layout` opens a live view:

```
  btn  2 → Xbox [ A ]
  btn  5 → Xbox [ LB ]
  ...
```

Press buttons on your physical controller to see which Xbox mapping fires. Press `Escape` or `M` to exit.

---

## Polling Modes

| Mode | Rate | CPU Usage | Best For |
|------|------|-----------|----------|
| **Eco** | ~100 Hz | Minimal | Laptops, older PCs, long sessions |
| **Balanced** | ~200 Hz | Low | Most gaming (default) |
| **Performance** | ~500 Hz | Moderate | Competitive play, fast PCs |

Change modes anytime via `[S] Settings`.

---

## Stick Sensitivity & Deadzone

### Sensitivity (`[A]`)
Controls how much of the outer stick range is used. A higher value means the stick reaches full tilt sooner.

| Setting | Range | Feel |
|---------|-------|------|
| **Light** | 12,000 – 16,000 | Slightest touch registers |
| **Normal** | 8,000 – 11,999 | Balanced (default: 10,000) |
| **Firm** | 4,000 – 7,999 | Needs deliberate push |

### Inner Deadzone (`[Z]`)
Eliminates stick drift by ignoring small inputs near the center.

- **Range:** 0 – 30%
- **Default:** 8%
- Set per stick (Left / Right) independently.

---

## Project Structure

```
DIRECT360/
├── Program.cs              # Main application (single-file)
├── DIRECT360.csproj        # Project file
├── rebuild.bat             # Full rebuild
├── play.bat                # Quick build & run
├── icon.ico                # Application icon
├── profiles/               # Saved data (auto-created)
│   ├── _settings.json      # Global poll mode
│   ├── _recent.json        # Recent layouts
│   ├── MultiSetups/        # Named multi-controller sessions
│   │   └── <SetupName>.json
│   └── <ControllerName>/
│       ├── _last_used.txt
│       └── <LayoutName>.json
└── crash.log               # Auto-generated on crash
```

---

## Troubleshooting

| Problem | Solution |
|---------|----------|
| `ERROR: ViGEmBus driver not found!` | Install [ViGEmBus](https://github.com/nefarius/ViGEmBus/releases/latest) and reboot |
| Controller not detected | Unplug and reconnect, then press `Enter` at the "No controllers" prompt |
| Buttons feel stuck or unresponsive | Make sure you are using the latest build; buffer-reuse and COM-race issues were fixed in recent releases |
| High CPU usage / laptop getting hot | Switch to **Eco** mode in Settings |
| Multi-controller crashes | Ensure all controllers are plugged in before starting; avoid hot-plugging during active remap |
| Build fails with `CS1061` | You are using an outdated source file. Pull the latest `Program.cs` from this repo |
| Layout shows `[!]` or `ERR` on load | The profile has duplicate or out-of-range button indices. Use `[E] Edit` to fix |

---

## Tech Stack

| Component | Purpose |
|-----------|---------|
| [.NET 10](https://dotnet.microsoft.com/) | Runtime & build platform |
| [SharpDX.DirectInput](https://github.com/sharpdx/SharpDX) | DirectInput device enumeration and polling |
| [Nefarius.ViGEm.Client](https://github.com/nefarius/ViGEmSDK) | Virtual Xbox 360 controller creation |
| Windows Forms (`System.Windows.Forms`) | Save/Open file dialogs |
| `winmm.dll` (`timeBeginPeriod`) | High-resolution timer (Performance mode only) |

---

## Contributing

Contributions, issues, and feature requests are welcome.

1. Fork the repository
2. Create a feature branch: `git checkout -b feature/YourFeature`
3. Commit your changes: `git commit -m "Add YourFeature"`
4. Push to the branch: `git push origin feature/YourFeature`
5. Open a Pull Request

Please keep code style consistent with the existing project. The app is intentionally console-based — avoid adding heavy GUI dependencies unless discussed first.

---

## License

This project is licensed under the [MIT License](LICENSE).

---

## Acknowledgments

- **[Nefarius](https://github.com/nefarius)** — for the [ViGEmBus](https://github.com/nefarius/ViGEmBus) driver that makes virtual controllers possible
- **[SharpDX](https://github.com/sharpdx/SharpDX)** — for the DirectInput managed wrapper
- The open-source community — for feedback, testing, and patience

---

_Made with frustration over games that only support Xbox controllers._

**Now everything just works.**
