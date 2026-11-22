# LinkFlow

[Português](README.pt-BR.md) · **English**

Fan, pump, RGB and LCD control for **Corsair iCUE LINK**, with CPU/GPU monitoring (LibreHardwareMonitor) and RGB sync for the graphics card (ASUS Aura), the motherboard (MSI Mystic Light), Corsair DDR5 memory and Logitech G peripherals.

This README describes **3.2.4**. Development artifacts and official signed distributions are separate channels.

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

## Control and history

**Dashboard → Control & history** shows protection status, reading quality, active/saved configuration revisions, storage status, and requested/controller-accepted/measured fan control. Each sensor carries its source, last successful observation, age and validity: valid, stale, unavailable, invalid or unsupported. Missing RPM is unknown, not a measured stopped fan; LCD modules do not offer speed control.

CPU/GPU temperature readings from the supported LibreHardwareMonitor provider must be assigned during the current update. A retained positive value does not renew the successful-observation timestamp; assigning the same temperature again does. This check uses managed provider state without extra hardware queries. It does not independently calibrate physical sensors or prove a native acquisition for every other provider metric.

GPU RGB diagnostics retain up to eight native failure contexts, with explicit counts for contexts not yet received and details evicted from that temporary window. Healthy worker replies do not repeat the history. These records help investigate NVAPI refusals without extra SDK calls; accepted output still does not prove emitted light. See [3.2.4 release notes](docs/releases/3.2.4.md) for retention and evidence limits.

- **Independent protection** checks acquisition freshness even when a sensor reader fails or stalls. Initialization has its own watchdog coverage; partial loss of hub RPM is tracked separately from temperatures.
- **Explicit curve fallback** is configured in **Fans** for the global or group curve. The default uses protective speed and reports a missing selected source. Optional CPU/GPU or CPU/GPU/coolant substitution has a protective minimum and a visible indication. Safety mode takes priority.
- **Command receipts** distinguish pending, service acceptance, application, non-verifiable delivery, rejection, expiration and revision conflict. Software/controller confirmation does not prove physical readback. Save status is separate: pending, saved or failed. Concurrent clients cannot silently overwrite a configuration revision they have not read.
- **Continuous local history** belongs to the engine, including while the window is hidden or no interface is connected. The operational view queries that history, plots timestamped gaps, and shows min/max/average/P95, valid coverage and time above the warning threshold. Select a fan, disk or volume for its individual history; compare sessions or export CSV/JSON with UTC acquisition times, units, quality, source and expected cadence.
- **Bounded storage** defaults to a 1-second collection interval, a maximum age of 30 days and a 64 MiB disk budget in 2 MiB segments. These are upper retention limits: the oldest segments are removed when either budget is reached. The memory tail is limited to 7,200 frames and 2,000 events. Expensive disk readings remain demand-driven; an inactive collection is not evidence of disk health. Resource metrics use a 30-second cadence, and cached acquisitions are not counted as new readings.
- **Timeline** records configuration changes and origin, alert opening/escalation/resolution, device loss/recovery, command results and inventory changes. Storage errors, corrupt records and dropped writes remain visible. Inventory compares against its last valid baseline; Windows maintenance and driver operations distinguish process exit, reported result, restart required and unconfirmed tracking.
- **Diagnostics** include the current snapshot with quality metadata, recent events, thermal history, Windows-operation records and the interactive Logitech sender's cached status. SDK acceptance and physical light verification remain separate. User/profile/computer names are sanitized with the same rules as the logs; review an export before sharing it.

Quiet hours and automatic-profile decisions run in the engine; the interface supplies session/app observations. Rule priority and time-limited manual overrides are explicit. Hardware ownership is exclusive across processes. When an installed service is unavailable, the interface waits in read-only mode instead of starting a competing local controller. Stale IPC snapshots also disable new control actions until recovery.

## Install

Download **`LinkFlow-Setup-<version>.exe`** from [Releases](https://github.com/LucasCamy/LinkFlow/releases) and run it. You don't need to install .NET.

- The installer copies LinkFlow to `Program Files`, installs the **Windows service** (recommended) and, if you want, opens LinkFlow in the tray when you sign in.
- Development packages are explicitly named `LinkFlow-Dev-Setup-<version>.exe` / `LinkFlow-<version>-dev-portable.zip`; they do not claim a verified publisher. Official packages require the signing configuration described below.
- **Close iCUE** before using it: both can't control the hub at the same time.
- Optional: **Corsair DDR5 memory RGB** needs [PawnIO](https://pawnio.eu/) installed; **Logitech peripherals** need G HUB open with "Allow games & applications to control illumination" turned on.
- To remove it, use **Windows Settings → Apps**.

There is also a **portable** version (`LinkFlow-<version>-portable.zip`): extract it and open `LinkFlow.App.exe`.

### Updating

Install version 2.3.0 or later over the previous one once to get the built-in updater. Settings stay in `%ProgramData%\LinkFlow` and interface preferences in `%AppData%\LinkFlow`.

LinkFlow checks the stable GitHub releases at startup and periodically while it's open, including in the tray. You can turn this off or use **Check for updates** in **Settings → Updates**.

- In the installed version, choose **Download update**. LinkFlow checks size and SHA-256. The official channel also requires a trusted Authenticode signature matching its embedded publisher and certificate SHA-256; a development build labels publisher verification as unavailable. **Install and restart** opens the installer and may ask for administrator permission. The previous configuration is journaled, and the reopened app checks version/protocol, service connectivity and required readings. A failed health check is reported and offers configuration restoration when it can be confirmed. This does not automatically downgrade installed binaries.
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
- **Local mode**: without an installed service, the engine runs inside the app itself (as administrator to read the CPU temperature). An installed but unreachable service remains the owner; the app reconnects as a read-only client.
- `LinkFlow.Core`: engine, hub protocol, configuration (`%ProgramData%\LinkFlow\config.json`), IPC and simulated hardware.
- The pipe name remains `LinkFlow.Engine.v1` for discovery, while the negotiated protocol is **v2**. It carries identified commands/receipts and bounded history/events/sessions/comparison/CSV/JSON RPCs. Responses are limited to 1 MiB; RPC queries are capped at 1,200 points or 200 events. Incompatible peers cannot write; configuration schemas newer than the supported version are rejected.

## Paired computers (Fleet)

**Dashboard → Computers** can observe up to 32 explicitly paired agents, query history and apply an authorized profile. Each target retains its own engine and safety rules without a network connection. Fleet is **off until provisioned**; provisioning defaults to loopback and observation only. Network listening and remote control are separate opt-ins. Connections use TLS 1.2/1.3, an explicitly paired certificate SHA-256 and scoped random credentials. Remote control cannot change local safety limits or fallback policies; operations that require the local session are refused.

On the **target computer**, in an elevated PowerShell from the published LinkFlow directory:

```powershell
.\LinkFlow.Cli.exe fleet init --name "Desktop"
# Optional LAN observation + control on an explicit local address:
# .\LinkFlow.Cli.exe fleet init --name "Desktop" --bind 192.168.1.10 --allow-network --allow-control
```

`init` creates a private `fleet\agent.json` and `agent.pfx`, prints the certificate pin and observer credential (plus a separate control credential only when requested), and refuses to replace an existing pairing. Keep this one-time credential output private. Restart the LinkFlow service through Settings to load the agent. The script does not open firewall ports; configure the intended network access separately.

On the **monitoring computer**, pair through the Computers tab or use a normal-user console:

```powershell
.\LinkFlow.Cli.exe fleet add-peer --peer desktop.json --name "Desktop" --host 192.168.1.10 --pin <CERTIFICATE_SHA256>
# Enter the credential at the hidden prompt. It can also be supplied through stdin, never a command-line argument.
.\LinkFlow.Cli.exe fleet monitor --peer desktop.json
.\LinkFlow.Cli.exe fleet history --peer desktop.json --metric cpu.temp --minutes 60
.\LinkFlow.Cli.exe fleet profile --peer desktop.json --profile Equilibrado
```

The profile command requires a control-scoped credential. Built-in profile keys remain `Silencioso`, `Equilibrado`, `Desempenho`, `Máximo` and `Automático`; UI labels follow the selected language. `fleet command --peer desktop.json --file command.json` submits an identified `EngineCommandRequest` subject to the same revision, expiry and protection checks. An initial accepted receipt is not proof that all targets have physically reached their requested state.

Peer credentials are protected with **DPAPI for the saving Windows user**. The UI's peer list and individual CLI peer files are separate; copying either to another account does not transfer usable credentials. The agent stores token hashes, and its certificate password is protected for the machine in a directory restricted to administrators/SYSTEM. Re-pair explicitly when changing the target certificate. The locally generated Fleet TLS certificate is separate from the Authenticode identity used to sign official releases.

## Build and publish

Requirements: Windows and .NET SDK 10. The scripts below use PowerShell; the soak runner requires PowerShell 7.2 or later.

```powershell
dotnet build LinkFlow.slnx --configuration Release -warnaserror
dotnet test tests\LinkFlow.Tests --configuration Release --no-build
.\scripts\publish.ps1                 # framework-dependent App + Service + CLI in bin\LinkFlow_v2
.\scripts\soak.ps1 -Repeat 5 -NoBuild
# Or run repeated isolated integration checks for at least ten minutes:
# .\scripts\soak.ps1 -DurationSeconds 600 -NoBuild
```

`Atualizar-E-Executar.bat` builds, stops the service (if installed), updates `bin\LinkFlow` and reopens the app.

### Preparing a release

Use the same `X.Y.Z` version in the App, Core, Service and CLI projects. Development packaging is the default and needs [Inno Setup 6](https://jrsoftware.org/isinfo.php):

```powershell
.\scripts\release.ps1 -Distribution Development
# -> artifacts\development: self-contained dev installer, portable ZIP, hashes and release-manifest.json
```

Official packaging requires a valid code-signing certificate with private key in `Cert:\CurrentUser\My`, SignTool from the Windows SDK, and the exact expected publisher/certificate identity:

```powershell
.\scripts\release.ps1 -Distribution Official `
    -CertificateThumbprint <STORE_THUMBPRINT> `
    -ExpectedPublisher "Your verified publisher" `
    -ExpectedCertificateSha256 <CERTIFICATE_SHA256>
# -> artifacts\release; application binaries, setup and uninstaller are signed and verified
```

There is no bundled official certificate or private key. Missing signing inputs block official packaging rather than producing an unsigned official artifact. The signing selectors can also come from `LINKFLOW_SIGNING_THUMBPRINT`, `LINKFLOW_SIGNING_PUBLISHER` and `LINKFLOW_SIGNING_CERT_SHA256`. `-TimestampServer` accepts an HTTPS timestamp endpoint.

Pushing a `vX.Y.Z` tag runs the official signed release workflow. It requires secrets `LINKFLOW_SIGNING_PFX_BASE64` and `LINKFLOW_SIGNING_PFX_PASSWORD`, plus repository variables `LINKFLOW_SIGNING_PUBLISHER` and `LINKFLOW_SIGNING_CERT_SHA256`. Review the signed artifacts, `SHA256SUMS.txt` and `release-manifest.json` before publishing the draft.

An unsigned release requires an explicit manual workflow run with `distribution=Development` and `release_tag=vX.Y.Z`. It remains a Development build even when published as a stable GitHub release: the installer and manifest disclose that the publisher is unverified. Compatibility aliases `LinkFlow-Setup-X.Y.Z.exe` and `LinkFlow-X.Y.Z-portable.zip` contain the same bytes as the canonical Development assets and are covered by `SHA256SUMS.txt`. This lets older updaters locate the installer; it does not bypass signature verification in Official builds. A missing certificate never silently changes an Official request into Development.

### Repeatable reliability checks

`scripts\soak.ps1` runs only `ReliabilityIntegrationTests` in fresh child processes with isolated data, temporary directories and pipe names. It checks repeated sensor/RPM faults, safety recovery, command truth, persistence, conflicts and queue bounds. Each iteration must execute a nonzero, unchanged test set with no failures or skips. `-IterationTimeoutSeconds` defaults to 120; owned process trees are terminated on timeout. TRX files, stdout/stderr, bounded runtime logs and `summary.json` remain under `artifacts\soak` or `-ResultsDirectory`.

These are repeated simulated integration checks with real local IPC/filesystem work. They do not certify uninterrupted 72-hour operation on physical hardware, driver compatibility or thermal performance; those require equipment-specific validation.

### Translation

Interface texts live in `src/LinkFlow.App/Resources/Strings.resx` (Portuguese) and `Strings.en.resx` (English). Texts the service produces live in `src/LinkFlow.Core/Resources/EngineStrings.resx` and `EngineStrings.en.resx`, by code. The operational, reliability and Fleet UI helpers also provide both languages. Tests check resource parity and presentation behavior.

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
| `--peripheral-rgb-diagnostics` | Opt-in diagnostics of the interactive Logitech lighting sender |
| `--lang pt-BR` / `--lang en` | Interface language for this run only, without changing the saved one |
| `--screenshot file.png [--page N] [--size WxH] [--detail cpu] [--select N]` | Renders the window to a PNG and exits (visual test) |

`LinkFlow.Cli.exe status` reads the installed service without opening hardware. `--diagnose` explicitly opens direct hardware diagnostics and requires exclusive ownership with no running service. `LinkFlow.Cli.exe --help` lists Fleet commands.

`LinkFlow.Service.exe --simulate --no-fleet` runs a console host with simulated actuators and network-agent startup disabled. Set **both** `LINKFLOW_DATA_DIR` and a unique `LINKFLOW_PIPE` before manual simulation so it cannot share production files or the production endpoint; `LINKFLOW_DEBUG=1` enables detailed logging. The soak script sets isolation automatically. `--detail operations` selects the operational tab for app screenshot tests; `--detail fleet` selects Computers.
For device-control screenshots without a selection, use `--page 1 --detail devices`; add `--select 2` to compare the enabled state.

When starting a new interface instance, `--peripheral-rgb-diagnostics` writes one bounded current-status file at `%AppData%\LinkFlow\diagnostics\peripheral-rgb-<PID>.json`, at most once per second and outside the sending thread. It records calculated colors, call progress and SDK results; it does not verify physical light or acquire another controller session. Simulation and screenshot runs do not enable this sender.

Publication atomically replaces the existing file and retains the last write failure's detail, time, stage and HResult after recovery. Diagnostic file errors are separate from RGB sending results. Concurrent readers should allow `FileShare.ReadWrite | FileShare.Delete`, copy the bytes and close the file before parsing JSON.

## Files

- Configuration: `%ProgramData%\LinkFlow\config.json`
- Logs: `%ProgramData%\LinkFlow\logs\linkflow-{app|service}-YYYY-MM-DD.log`
- Interface preferences: `%AppData%\LinkFlow\ui.json`
- LCD images/GIFs: `%ProgramData%\LinkFlow\media` (copied when added, so the service can read them)
- Timestamped history/events: `%ProgramData%\LinkFlow\history\lfh-*.jsonl`
- Inventory baseline and automation state: `%ProgramData%\LinkFlow\inventory.json`, `automation-state.json`
- Optional private Fleet agent: `%ProgramData%\LinkFlow\fleet\agent.json`, `agent.pfx`
- User peer list and Windows-operation journal: `%AppData%\LinkFlow\fleet-peers.json`, `system-operations.json`
- Update recovery: `%AppData%\LinkFlow\updates\update-recovery.json`
