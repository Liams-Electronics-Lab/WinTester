
<img width="900" height="450" alt="Splash" src="https://github.com/user-attachments/assets/f3f5bee8-3a77-4d48-806f-8bf721316929" />


# WinTester

**Hardware diagnostics and stress-testing suite for computer repair and refurbishment shops.**

WinTester is a Windows desktop application that runs CPU, GPU, memory, storage and power tests, it can be set to loop unattended.
It produces a clear PASS/FAIL verdict and a professional, self-contained HTML report with sensor graphs. Every customer-facing element (name, colors, logo, splash, report header) is driven by a runtime brand profile, so one build can be re-skinned per store without recompiling.

---
<img width="1127" height="738" alt="main" src="https://github.com/user-attachments/assets/b2b40f03-fbad-455d-9307-6038a6c723ba" />

## Contents

- [Key Capabilities](#key-capabilities)
- [Tab 1: Automated Test Suite](#tab-1-automated-test-suite)
- [Tab 2: Standalone Tools](#tab-2-standalone-tools)
- [Reporting](#reporting)
- [White-Label Branding](#white-label-branding)
- [Architecture](#architecture)
- [Requirements](#requirements)
- [Build](#build)
- [Repository Layout](#repository-layout)


---
## Key Capabilities

- Five automated tests (CPU, GPU, Memory, Storage, Power) can run individually or in a fixed sequence with one click.
- Live telemetry during runs: load, temperature, clocks, power, memory and battery state.
- Aggregate PASS/FAIL popup with per-test results and failure reasons, plus optional abort on first failure function.
- Self contained offline HTML report with interactive charts, plus an 80 mm thermal receipt print option (handy for attaching to machines on the outgoing queue).
- Crash resilient logging: every sample is written to disk as it is captured.
- Standalone diagnostic and deployment tools for batteries, input devices, audio, USB, drivers, disks, licensing and OEM branding.
- Full whitelabel support through a single `brand.json` file.

---

## Tab 1: Automated Test Suite

Tests run in a fixed order: **CPU, GPU, Memory, Storage, Power**. Select any combination with the tile checkboxes, or run a single test directly from its tile. A full window progress overlay with a live telemetry grid is shown during runs.

### CPU
[Image](https://github.com/Liams-Electronics-Lab/WinTester/blob/4b3d69ee7302f4fcd99235211a7dd300e16ef35d/images/cputest.png)
- Intel and AMD supported through the same engine set.
- Uses Intel Processor Diagnostic Tool compute modules (`dgemm`, `fma3`, `avx`, `math_primenumber`, `math_fp`) run as isolated, hidden processes.
- Multi module concurrent load across all logical cores, exercising FPU, cache and wide SIMD paths.
- Pass/fail from each module's exit code contract; early module exits are surfaced live.
- Records package temperature, clocks, load and power for the report.

### GPU
[Image1](https://github.com/Liams-Electronics-Lab/WinTester/blob/4b3d69ee7302f4fcd99235211a7dd300e16ef35d/images/gputest.png)
[Image2](https://github.com/Liams-Electronics-Lab/WinTester/blob/main/images/gputest_2.png)
- FurMark 2 OpenGL baseline pass; the baseline is the pass/fail-bearing result.
- Per-GPU Vulkan sweep, one adapter at a time, on hybrid-graphics systems (integrated and discrete); results are additional data.
- GravityMark sweep per detected GPU with automatic API fallback ordering for integrated graphics.
- VRAM test (thanks to Furmark).
- Artifact scanning enabled by default.
- Captures FPS (not graphed in current build), per-GPU temperature, usage, and clocks.

### Memory

- In process pattern test engine (8 patterns) that works through RAM in bounded windows.
- Test size derived from currently free physical memory, with a reserve for the OS.
- Any detected bit error fails the test, with address and pattern detail in the report.
- Memory is returned to the operating system when the test ends or is cancelled.

### Storage
[Image](https://github.com/Liams-Electronics-Lab/WinTester/blob/4b3d69ee7302f4fcd99235211a7dd300e16ef35d/images/storage.png)
- fio based sequential 1 MiB Q8T1 read and write test, one physical disk at a time.
- Tests against a 4 GiB file on the disk's volume (no raw device access); averaged across multiple loops.
- Live fullscreen speed visual with gradient read and write bars.
- Reports averaged read and write throughput per drive.

### Power

- **Battery Health** (laptops): current versus design capacity, health percentage, voltage, charge state, cycle count, chemistry, degradation level and estimated idle battery life. Multiple sources are cross-checked and the more conservative value is used. Shown as not applicable on machines with no battery interface.
- **PSU / Charger Recommendation** (every machine): estimates peak system draw from measured CPU and GPU power plus a component inventory (storage by type, RAM modules, optical drives, fans, PCI cards, network adapters, motherboard and USB, screen, webcam, card reader, modem, etc).
- Desktops: 20% headroom, rounded up to a standard PSU tier.
- Laptops: measured platform power where available, rounded up to the next standard charger size.
- Informational only.

**Power supply calculation from report** (Gaming laptop, shipped with 230w psu)

<img width="410" height="66" alt="psu_budget" src="https://github.com/user-attachments/assets/0a686863-bee1-4f36-b2bf-5c45d73018b0" />

---

## Tab 2: Standalone Tools

Tools launch individually from tiles and are independent of the Tab 1 queue and its verdict.

### System

| Tool | Description | Image |
|---|---|---|
| **Devices** | Lists devices Windows flags as problematic, grouped by category (driver missing or failed, device errors, disabled) and by device class. Shows the error code and a plain English explanation. Double click opens the device's properties. | [Link](https://github.com/Liams-Electronics-Lab/WinTester/blob/4b3d69ee7302f4fcd99235211a7dd300e16ef35d/images/devices.png) |
| **Sensors** | Tree of all LibreHardwareMonitor and HWiNFO (place HWiNFO executable in its vendor folder for this function) readings with current, minimum, maximum and average values. | [Link](https://github.com/Liams-Electronics-Lab/WinTester/blob/4b3d69ee7302f4fcd99235211a7dd300e16ef35d/images/sensors.png) |
| **Hardware Errors** | Reads WHEA hardware error events from the System log, groups repeats with occurrence times, decodes error records, and builds a web lookup per error. Can export to text. | [Link](https://github.com/Liams-Electronics-Lab/WinTester/blob/4b3d69ee7302f4fcd99235211a7dd300e16ef35d/images/whea_1.png) [Link](https://github.com/Liams-Electronics-Lab/WinTester/blob/4b3d69ee7302f4fcd99235211a7dd300e16ef35d/images/whea_2.png) |
| **Disk Info** | S.M.A.R.T. health, key drive counters and TRIM state for a selected physical disk (SATA and NVMe), plus a Disk Management style partition map. Plain-language explanations for each data point. Read-only. | [Link](https://github.com/Liams-Electronics-Lab/WinTester/blob/4b3d69ee7302f4fcd99235211a7dd300e16ef35d/images/disk%20info.png) |
| **Restore Points** | Create, restore and delete System Restore points: enable or disable protection per drive, set disk space, interval and retention; view automatic restore point status. | [Link](https://github.com/Liams-Electronics-Lab/WinTester/blob/4b3d69ee7302f4fcd99235211a7dd300e16ef35d/images/restore%20points.png) |
| **BitLocker** | Shows volume encryption status and TPM presence and version. Enable or disable BitLocker with a forced recovery key backup. | [Link](https://github.com/Liams-Electronics-Lab/WinTester/blob/4b3d69ee7302f4fcd99235211a7dd300e16ef35d/images/bitlocker.png) |
| **Windows Activation** | Activation status, product key backup and change, with a system identity header on the saved backup. | [Link](https://github.com/Liams-Electronics-Lab/WinTester/blob/4b3d69ee7302f4fcd99235211a7dd300e16ef35d/images/activation.png) |
| **Fan Control** | Fan RPM display and manual override with automatic reset on close. (In development). | [Link](https://github.com/Liams-Electronics-Lab/WinTester/blob/4b3d69ee7302f4fcd99235211a7dd300e16ef35d/images/fans.png) |

### Power

| Tool | Description | Image |
|---|---|---|
| **Battery Drain** | Long running discharge test under selectable CPU and/or GPU load. Persecond and per 10 second logging, live graph, automatic stop on AC reconnect, and an HTML report with summary blocks, cross-checks and full logged data. | [Link](https://github.com/Liams-Electronics-Lab/WinTester/blob/4b3d69ee7302f4fcd99235211a7dd300e16ef35d/images/batt_drain.png) |
| **Battery Charge** | Charge curve test with three modes (Wide Sweep, Quick Test, Peak Sweep). Automatic discharge preparation, idle-only logging, and a condition rating (Good, OK, Bad or Inconclusive) based on energy accepted against a state of charge aware reference curve. HTML report with multi series chart, quick stats and logged data. | [Link](https://github.com/Liams-Electronics-Lab/WinTester/blob/4b3d69ee7302f4fcd99235211a7dd300e16ef35d/images/batt_chrg.png) |

### HID and Peripherals

| Tool | Description | Image |
|---|---|---|
| **Keyboard Tester** | Visual per-key test across full, TKL, laptop and 60% layouts. Distinguishes left and right modifiers and numpad keys. Live telemetry (hold time, rollover, percent tested) and an exportable report. | [Link](https://github.com/Liams-Electronics-Lab/WinTester/blob/4b3d69ee7302f4fcd99235211a7dd300e16ef35d/images/keybaord.png) |
| **Mouse and Touch Tester** | Fullscreen grid test for buttons, scroll wheel, multi-touch and screen coverage. Color coded input feedback. Closes automatically when complete. | [Link](https://github.com/Liams-Electronics-Lab/WinTester/blob/4b3d69ee7302f4fcd99235211a7dd300e16ef35d/images/touch.png) |
| **Audio Tester** | Plays a test track with a spectrum and waveform visualizer. Multi-channel speaker sweep (2.0 through 7.1) with a diagram of the active speaker. | [Link](https://github.com/Liams-Electronics-Lab/WinTester/blob/4b3d69ee7302f4fcd99235211a7dd300e16ef35d/images/audio.png) |
| **Webcam and Mic** | Camera and microphone check. | [Link](https://github.com/Liams-Electronics-Lab/WinTester/blob/4b3d69ee7302f4fcd99235211a7dd300e16ef35d/images/webcam.png) |
| **USB Validator** | Physical-port test: one tile per hub port, lit as devices are plugged in, with a PASS/FAIL verdict and HTML export. Ports without a user-accessible connector are hidden. | [Link](https://github.com/Liams-Electronics-Lab/WinTester/blob/4b3d69ee7302f4fcd99235211a7dd300e16ef35d/images/usb.png) |

### Deployment

| Tool | Description | Image |
|---|---|---|
| **OEM Branding** | Sets Windows OEM support information (manufacturer, model, phone, hours, URL) and logo. Optional hook to reapply branding after a Push-Button Reset. Backup and load of settings as a zip. | [Link](https://github.com/Liams-Electronics-Lab/WinTester/blob/4b3d69ee7302f4fcd99235211a7dd300e16ef35d/images/branding.png) |
| **Recovery Provisioning** | Wizard that captures the current install with USMT into a custom recovery package so Reset this PC restores the deployed state. Includes app discovery, per app rules, drivers, services and startup options. User accounts are not saved, a reset will kick back to the OOBE but keep apps etc | [Link](https://github.com/Liams-Electronics-Lab/WinTester/blob/4b3d69ee7302f4fcd99235211a7dd300e16ef35d/images/recovery_provision.png) |

---

## Reporting
---
<img width="1141" height="1257" alt="report1" src="https://github.com/user-attachments/assets/f11e7d5f-9545-4641-8eae-72e11ac6735d" />
---

[Example Report](https://htmlpreview.github.io/?https://github.com/Liams-Electronics-Lab/WinTester/blob/main/Sample_Test_report.html)

- Single self contained HTML file.
- Report name: `Report-<session id>.html` (suffixed per run for multi run sessions).
- Overall and per-test PASS/FAIL with failure reasons.
- Machine identification: CPU, GPU, RAM, storage, board and BIOS.
- Charts (vendored Chart.js): temperature, load, clocks, power, fan speed, RAM, VRAM, battery.
- Per test summary statistics (minimum, maximum, average).
- Collapsible raw-log appendix for each engine.
- Power and charger recommendation tile and component breakdown.
- Business Details block (name, address, free form label and value rows) at the top of every report, with independent switches for the full report and the thermal view.
- Thermal receipt view for 80 mm printers, available on the main report and the battery reports.
- Optional HWiNFO sourced data when HWiNFO is bundled: CPU and GPU throttle and limiter events, PROC_HOT, PCH temperature, PCIe link capability and error counters, and Windows WHEA errors. Omitted when unavailable.

---

## White-Label Branding

All branding is read from `brand.json` at startup. No brand strings, colors or images are hardcoded.

- Application name, window title and taskbar title
- Company name and logo
- Primary and accent colors
- Splash image
- About dialog contents and support contact
- Report footer and disclaimer text
- Business details block for report headers

Reskinning requires only editing the config file and replacing image assets.

---

## Architecture

- **Platform:** C#, .NET 8, WPF.
- **Process isolation:** vendored test engines (Intel PDT modules, FurMark 2, GravityMark, fio) run as separate child processes.
- **Sensors:** LibreHardwareMonitorLib behind an `ISensorService` abstraction, polled on a fixed cadence.
- **Session data store:** append only JSON Lines written to disk continuously; reports are rendered from this store.
- **Extensibility:** new auto run tests implement `ITestModule`; new standalone tools register in the Tab 2 composition list. No unrelated code changes required.

---

## Requirements

- Windows 10 or Windows 11 (64-bit)
- Administrator privileges (the application manifest requests elevation, required for full sensor access)
- .NET 8 runtime 
- VISUAL C++ 2019 RUNTIME 
- PawnIO driver for full low-level sensor coverage 
- Vendored engine binaries placed under `vendor/` (Furmark,HWINFO, Gravity mark)

---

## Build

```
dotnet build WinTester.sln
```

Vendored binaries are not built by the solution and must be supplied under `vendor/`: Intel PDT modules, FurMark 2, GravityMark, fio, and optionally HWiNFO. The project file copies `vendor/**` to the build and publish output.

---

## Repository Layout

```
src/
  WinTester.App              WPF shell, views, view-models
  WinTester.Core             Contracts, session store, shared models
  WinTester.Branding         Brand profile loader
  WinTester.Sensors          LibreHardwareMonitor and HWiNFO readers
  WinTester.Reporting        HTML report generator and thermal view
  WinTester.Tests.*          Cpu, Gpu, Memory, Storage, Power modules
  WinTester.Tools.*          Standalone Tab 2 tools
vendor/                      Third-party binaries and licenses
scratch/                     Logic-check projects
```

---
