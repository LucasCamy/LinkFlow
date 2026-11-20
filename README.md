# LinkFlow

[Português](README.pt-BR.md) · **English**

Fan, pump, RGB and LCD control for **Corsair iCUE LINK**, with CPU/GPU monitoring (LibreHardwareMonitor) and RGB sync for the graphics card (ASUS Aura), the motherboard (MSI Mystic Light), Corsair DDR5 memory and Logitech G peripherals.

## Highlights

- **Light and responsive**: all hardware access runs on background queues; the interface never waits on USB or I²C.
- **Windows service**: control keeps running with no window open, starts before sign-in and restarts on its own if it fails. The interface doesn't need administrator rights.
- **Tray**: closing or minimizing keeps LinkFlow running; the icon shows the CPU temperature; the menu has the fan profiles.
- **Automatic curve** with smoothing, hysteresis and a draggable editor; **safe mode** (100% at a critical temperature or if the pump fails).
- **Fan calibration**: measures each fan from 100% down to 20%, finds the minimum power that keeps it spinning and warns when a fan starts spinning well below normal (dust or a worn bearing).
- **Multiple selection** of fans (Ctrl/Shift+click), bulk actions, groups, renaming, saved profiles and **Undo (Ctrl+Z)**.
- **Shortcuts**, local and global (they work in games).
- **Dashboard with details**: click CPU, GPU, coolant, fans or memory to see up to 10 minutes of history, min/max/average, clocks, power, VRAM and the device list.
- **Full color picker**: saturation × brightness square with a hue bar, hex code and fine RGB tuning.
- **Customizable LCD**: build the layout (main value, up to 4 secondary values, title, ring, background photo) or show photos and GIFs as a slideshow, with rotation and an overlaid metric. The preview uses the same renderer as the display.
- **Hardware** shows a filterable inventory of CPU, GPUs, motherboard, BIOS, Windows version, memory, disks, network, monitors and peripherals, plus what each LinkFlow adapter supports.
- **Windows performance** lets you switch power plans, set the screen-off and sleep timeouts and toggle interface animations, undo the last change of the session and remove optional apps from your account after confirmation.
- **Drivers** checks the device drivers Windows Update offers when you click **Search for updates**, so you can review and install each one. **Manufacturers** compares GeForce drivers and recognized AMD AM4/AM5 chipset packages with the official catalogs.
- **Configurable floating widget** with CPU, GPU, fans, network, disks and other sensors in a row, column or grid. It can stay on top, let clicks pass through and hide in full screen on its own monitor only.
- **In-game overlay (RTSS)** in **Widget → Behavior**: install and open [RivaTuner Statistics Server](https://www.guru3d.com/download/rtss-rivatuner-statistics-server-download/), enable the OSD for the game and turn the option on with the widget showing. LinkFlow sends the selected readings up to once per second.
- **Curves per group**: a group can follow its own curve and sensor (e.g. the radiator following the coolant).
- **Automatic profiles**: per game/app and quiet hours; goes back to the previous profile on its own.
- **English and Portuguese**: the interface follows the Windows language. To change it, go to **Settings → General → Language** (Automatic, Português or English); the interface restarts and, in service mode, the fans don't stop. The LCD display is drawn in the chosen language too. Logs and the diagnostic package stay in Portuguese.
- **Backup**: export everything (profiles, curve, names, lighting, LCD with its images, limits, preferences) to a `.linkflow` file and import the parts you choose.

## Install

Download **`LinkFlow-Setup-<version>.exe`** from [Releases](https://github.com/LucasCamy/LinkFlow/releases) and run it. You don't need to install .NET.

- The installer copies LinkFlow to `Program Files`, installs the **Windows service** (recommended) and, if you want, opens LinkFlow in the tray when you sign in.
- Windows may show "Windows protected your PC" (the installer isn't signed): click **More info → Run anyway**.
- **Close iCUE** before using it: both can't control the hub at the same time.
- Optional: **Corsair DDR5 memory RGB** needs [PawnIO](https://pawnio.eu/) installed; **Logitech peripherals** need G HUB open with "Allow games & applications to control illumination" turned on.
- To remove it, use **Windows Settings → Apps**.

There is also a **portable** version (`LinkFlow-<version>-portable.zip`): extract it and open `LinkFlow.App.exe`.

### Updating

Install version 2.3.0 or later over the previous one once to get the built-in updater. Settings stay in `%ProgramData%\LinkFlow` and interface preferences in `%AppData%\LinkFlow`.

LinkFlow checks the stable GitHub releases at startup and periodically while it's open, including in the tray. You can turn this off or use **Check for updates** in **Settings → Updates**.

- In the installed version, choose **Download update**. LinkFlow checks the size and the SHA-256 published by GitHub. Then **Install and restart** opens the installer, which may ask for administrator permission. The interface closes during the update and the running service is paused; when it finishes, the service starts again and the interface reopens.
- In the portable version, choose **Download in the browser** and extract the ZIP to a new folder.

## Architecture

```
LinkFlow.App (WPF, regular user)           LinkFlow.Service (Windows Service, SYSTEM)
  Interface, tray, shortcuts                  LinkFlowEngine  ── iCUE LINK hub (HID)
        │                                          │          ── sensors (LibreHardwareMonitor)
        └──── named pipe "LinkFlow.Engine.v1" ─────┘          ── LCD, ASUS Aura, MSI Mystic Light
              (one JSON per line, ACL: interactive/admin/SYSTEM)
```

- **Service mode** (recommended): the interface connects to the service through the pipe.
- **Local mode**: without the service, the engine runs inside the app itself (as administrator to read the CPU temperature).
- `LinkFlow.Core`: engine, hub protocol, configuration (`%ProgramData%\LinkFlow\config.json`), IPC and simulated hardware.

## Build and publish

Requirements: .NET SDK 10.

```powershell
dotnet build LinkFlow.slnx
dotnet test tests\LinkFlow.Tests
.\scripts\publish.ps1                 # creates bin\LinkFlow_v2 with LinkFlow.App.exe + LinkFlow.Service.exe
```

`Atualizar-E-Executar.bat` builds, stops the service (if installed), updates `bin\LinkFlow` and reopens the app.

### Preparing a release

Use the same `X.Y.Z` version in the `LinkFlow.App` and `LinkFlow.Service` projects. Push the `vX.Y.Z` tag to GitHub pointing at that version's commit. GitHub Actions runs the tests on Windows and attaches the x64 installer, the portable ZIP and `SHA256SUMS.txt` to a draft in **Releases**. Review the draft and click **Publish release**.

To build the same files locally, run `.\scripts\release.ps1` with [Inno Setup 6](https://jrsoftware.org/isinfo.php) installed. The files go to `artifacts\release`.

### Translation

Interface texts live in `src/LinkFlow.App/Resources/Strings.resx` (Portuguese) and `Strings.en.resx` (English). Texts the service produces (alerts, status, calibration, LCD) live in `src/LinkFlow.Core/Resources/EngineStrings.resx` and `EngineStrings.en.resx`, by code. Tests fail if a screen has hardcoded text in XAML or if the two languages have different keys.

## Installing the service

Open LinkFlow → **Settings → Service and backup → Install service** (asks for UAC once). To remove it, use **Remove service** in the same place.

Manually (PowerShell as administrator):

```powershell
.\LinkFlow.App.exe --install-service      # creates, sets automatic restart and starts it
.\LinkFlow.App.exe --uninstall-service
```

## Shortcuts

| Shortcut | Action |
|---|---|
| F1 … F5 | Profiles: Silent, Balanced, Performance, Maximum, Smart curve |
| Ctrl+Alt+1 … 5 | Profiles from anywhere (global) |
| Ctrl+Alt+L / Ctrl+Alt+M | Open/hide LinkFlow / mini widget (global) |
| Ctrl+1 … 9 | Pages (Ctrl+5 = Widget, Ctrl+6 = Settings, Ctrl+7 = Hardware, Ctrl+8 = Windows, Ctrl+9 = Drivers) |
| Ctrl+↑ / Ctrl+↓ | Global power ±5% |
| Ctrl+Z | Undo |
| Ctrl+A / Esc | Select all / clear selection (Fans) |
| Ctrl+R | Rename the selected card (also: pencil, double-click or right-click) |
| Ctrl+M / Ctrl+L | Mini widget / send frame to the LCD |
| Esc, Ctrl+W / Ctrl+Q | Hide to the tray / quit |

## Command line

| Argument | Effect |
|---|---|
| `--tray` | Starts hidden in the tray (used by autostart) |
| `--exit` | Closes the running instance gracefully |
| `--simulate` | Simulated hardware (tests/demo; never touches the real configuration) |
| `--lang pt-BR` / `--lang en` | Interface language for this run only, without changing the saved one |
| `--screenshot file.png [--page N] [--size WxH] [--detail cpu] [--select N]` | Renders the window to a PNG and exits (visual test) |

`LinkFlow.Service.exe --simulate` runs the service in a console with simulated hardware. The `LINKFLOW_PIPE`, `LINKFLOW_DATA_DIR` and `LINKFLOW_DEBUG=1` variables isolate tests and turn on detailed logging.

## Files

- Configuration: `%ProgramData%\LinkFlow\config.json`
- Logs: `%ProgramData%\LinkFlow\logs\linkflow-{app|service}-YYYY-MM-DD.log`
- Interface preferences: `%AppData%\LinkFlow\ui.json`
- LCD images/GIFs: `%ProgramData%\LinkFlow\media` (copied when added, so the service can read them)
