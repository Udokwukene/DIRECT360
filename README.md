# DIRECT360

<p align="center">
  <img src="https://img.shields.io/badge/Windows-10%2F11-0078D6?style=flat-square&logo=windows" alt="Windows">
  <img src="https://img.shields.io/badge/.NET-10.0-512BD4?style=flat-square&logo=dotnet" alt=".NET 10">
  <img src="https://img.shields.io/badge/Architecture-x64-2ea44f?style=flat-square" alt="x64">
  <img src="https://img.shields.io/badge/License-MIT-yellow?style=flat-square" alt="License">
</p>

<p align="center">
  <b>Universal DirectInput-to-XInput Controller Remapper for PC Gaming</b><br>
  <i>Plug in any controller. Map it once. Play everything.</i>
</p>

---

## What is DIRECT360?

**DIRECT360** is a lightweight, console-based controller remapper for Windows that converts virtually any DirectInput gamepad into a virtual **Xbox 360 controller** — the standard that nearly every PC game supports out of the box.

Whether you have a cheap generic USB joystick, a PlayStation 3/4/5 controller, DIRECT360 makes it work with your games without touching a single config file.

---

## Features

| Feature | Description |
|---------|-------------|
| **Universal Compatibility** | Works with Xbox, PS3, PS4, PS5 (wired & Bluetooth), and generic USB joysticks |
| **Interactive Wizard** | Step-by-step button mapping — no manual editing, no XML, no hassle |
| **Multi-Controller** | Run up to 4 controllers simultaneously, each with independent profiles |
| **Smart Profiles** | Per-controller layouts with favorites, notes, recent history, and quick-load |
| **3 Polling Modes** | Eco (~100Hz), Balanced (~200Hz), Performance (~500Hz) — pick your trade-off |
| **Game Launcher** | Auto-launch your game EXE when loading a layout |
| **Auto-Reconnect** | Controller unplugged mid-game? It waits and resumes automatically |
| **Export / Import** | Share your layouts with friends or back them up |
| **Crash Logger** | Unhandled exceptions are caught and saved to `crash.log` |

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
4. Follow the on-screen wizard to map your buttons and axes.
5. Save your layout and select it from the menu.
6. Launch your game and play.

### Controls While Running
| Key | Action |
|-----|--------|
| `M` | Return to menu |
| `Ctrl + C` | Exit DIRECT360 |

---

## Multi-Controller Setup

When multiple controllers are detected, DIRECT360 asks which ones to use:

```
  [A] All  |  [S] Choose specific  |  [1-4] One only
```

- **`A`** — Use all detected controllers
- **`S`** — Pick specific ones (e.g. `1 3` for Players 1 and 3)
- **`1-4`** — Use only that controller

Each player gets their own profile slot. If all selected controllers have a "last used" layout, you can quick-load everything with **`L`**.

---

## Wizard Controls

During the setup wizard, you can navigate with keyboard shortcuts:

| Key | Action |
|-----|--------|
| `B` | Go back to the previous step |
| `R` | Redo the current step |

If your controller disconnects during the wizard, reconnect it and the wizard will resume from where you left off. Press `Enter` to abort instead.

---

## Profile Menu Options

```
  [N]  New layout (wizard)
  [P]  PS smart default (DS4/DS5 preset)
  [L]  Quick load last used
  [1-n] Load a saved layout
  [Recent1-3] Load recent layout

  [E]  Edit layout
  [T]  Test layout
  [A]  Adjust sensitivity
  [G]  Set game launcher
  [X]  Export layout
  [I]  Import layout
  [R]  Rename layout
  [D]  Delete layout

  [S]  Settings
```

---

## Project Structure

```
DIRECT360/
├── Program.cs              # Main application
├── DIRECT360.csproj        # Project file
├── rebuild.bat             # Full rebuild
├── play.bat                # Quick build & run
├── icon.ico                # Application icon
├── profiles/               # Saved layouts (auto-created)
│   ├── _settings.json
│   ├── _recent.json
│   └── <ControllerName>/
│       └── <LayoutName>.json
└── crash.log               # Auto-generated on crash
```

---

## Polling Modes

| Mode | Rate | CPU Usage | Best For |
|------|------|-----------|----------|
| **Eco** | ~100 Hz | Minimal | Laptops, older PCs, long sessions |
| **Balanced** | ~200 Hz | Low | Most gaming (default) |
| **Performance** | ~500 Hz | Moderate | Competitive play, fast PCs |

Change modes anytime via **`[S] Settings`** in the layout menu.

---

## Stick Sensitivity

During the wizard or via **`[A] Adjust sensitivity`**, you can tune how responsive your analog sticks feel:

| Setting | Range | Feel |
|---------|-------|------|
| **Light** | 12,000 – 16,000 | Slightest touch registers |
| **Normal** | 8,000 – 11,999 | Balanced (default: 10,000) |
| **Firm** | 4,000 – 7,999 | Needs deliberate push |

You can also set a custom **inner deadzone** (0–30%) to eliminate stick drift.

---

## Troubleshooting

| Problem | Solution |
|---------|----------|
| `ERROR: ViGEmBus driver not found!` | Install [ViGEmBus](https://github.com/nefarius/ViGEmBus/releases/latest) and reboot |
| Controller not detected | Unplug and reconnect, then press `Enter` at the "No controllers" prompt |
| Buttons feel stuck or unresponsive | Make sure you are using the latest build; SharpDX buffer reuse was fixed in v2.0 |
| High CPU usage / laptop getting hot | Switch to **Eco** mode in Settings |
| Multi-controller crashes | Ensure all controllers are plugged in before starting; avoid hot-plugging during active remap |
| Build fails with `CS1061` | You are using an outdated source file. Pull the latest `Program.cs` from this repo |

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

<p align="center">
  <i>Made with frustration over games that only support Xbox controllers.</i><br>
  <b>Now everything just works.</b>
</p>
