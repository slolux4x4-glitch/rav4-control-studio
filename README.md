# RAV4 Control Studio

Read-only OBD-II diagnostics for the 2021 Toyota RAV4 (M20A-FKS / CVT) on Windows 10/11.
It talks to an ELM327 adapter over **Bluetooth, USB (COM port), Wi-Fi**, or a built-in **Demo** ECU.

## Launch

Run **`dist\RAV4ControlStudio\RAV4ControlStudio.exe`**. It is self-contained, so no .NET install is needed.

## Build the exe yourself

```bash
dotnet publish src/RAV4ControlStudio/RAV4ControlStudio.csproj -c Release -r win-x64 --self-contained true -p:DebugType=none -o dist/RAV4ControlStudio
```

Run tests:

```bash
dotnet test tests/Obd2.Core.Tests
```

## Pages

| Page | Purpose |
|---|---|
| Overview | Vehicle card, system status, live gauges, DTC summary, live graph, CAN bus monitor |
| OBD2 Connection | Choose Bluetooth/USB (COM), Wi-Fi (IP and port) or Demo; protocol; connect; **Test adapter** runs a step-by-step check (link, reset, setup, voltage, protocol, ECU, live data) and says whether the fault is the adapter or the vehicle |
| Live Data | Every supported Mode 01 parameter as value tiles with range bars; **Save baseline** to see how values differ from a reference; custom parameters you define |
| Metrics | Large Speed, Engine Speed (0-8000 rpm), Coolant (water) temperature and Battery voltage gauges with session peaks, six smaller stat tiles, and a metric/imperial switch (km/h, mph, °C, °F) |
| Trends | Graph up to six parameters of your choice over the last three minutes |
| Fuel and O2 | Fuel trims, O2 voltage, lambda and catalyst temperature, with plain-language hints |
| ECU Discovery | Read-only scan of which control units answer (3E 00 tester present) and their identification data (22 F1xx) |
| Diagnostics (DTCs) | Stored (Mode 03) and pending (Mode 07) codes, plus a searchable offline database of 9,500+ trouble codes with explanations, common causes and symptoms (select a code the ECU reports to jump to its entry); also permanent codes (Mode 0A) and the freeze frame (Mode 02) stored when a fault was set |
| Data Logging | Record live data to CSV; list and open saved logs |
| CAN Monitor | Greyed out, not yet available |
| ECU Information | VIN and its breakdown (year, maker, WMI, plant, serial sequence), ECU name, calibration ID and CVN (Mode 09), adapter description/serial/voltage, supported PIDs, copy buttons |
| Tests (Read-Only) | I/M readiness monitors |
| Reports | Save and list plain-text diagnostic reports |

**Units:** switch between metric (km/h, °C, kPa) and imperial (mph, °F, psi) from the status bar or Settings. It applies everywhere (gauges, Live Data, graph, freeze frame, reports). Connection choices, units and polling delay are remembered between launches.

Pages that need a live link show a "Not connected" banner with a Connect button. Minimum window size is 1300×800.

**More features**
- Auto-reconnect if the adapter drops (6 tries); Wi-Fi presets and a **Find adapter** button.
- Alerts (coolant, battery, rpm) with limits in Settings; a trip summary with estimated fuel economy when you disconnect.
- **Replay** a saved CSV log through the whole app (Data Logging page); PDF and text reports.
- Several vehicles, each with its own logs, reports and baseline; interface size (90% to 125%).
- Phone view: a read-only web page on port 8765 with a random code in the address (Settings).
- Custom parameters: define extra read-only values in `Documents\RAV4 Control Studio\Parameters\*.json` (Mode 01 or Mode 22), for example Toyota-specific data you have definitions for.

Header buttons: **About** (version, .NET and WPF runtime, libraries, data sources, standards, credits and file locations, with Copy details), **Projects** (opens the project folder), **Settings** (adapter defaults, polling delay, folders), **Help**.

## Connecting

- **Bluetooth:** pair the ELM327 in Windows Settings (PIN usually `1234` or `0000`). It appears as a COM port labelled *Bluetooth*. Press Refresh on the connection page.
- **USB:** plug in; the adapter appears as a COM port.
- **Wi-Fi:** join the adapter's network (for example `WiFi_OBDII`), then use its address (usually `192.168.0.10`, port `35000`).
- **Demo:** no hardware needed.

Turn the ignition on before connecting. Default protocol is ISO 15765-4 CAN 11-bit 500 kbaud, which the RAV4 uses.

## Read-only by design

Only OBD-II services 01 (live data), 02 (freeze frame), 03/07/0A (read codes), 09 (vehicle info), 22 (read data by identifier) and 3E 00 (tester present) plus ELM327 setup commands are sent. Clearing codes and actuator control are refused before they reach the adapter (covered by tests).

## Known limits

- **Not yet tested on a real car or adapter.** Verified with unit tests and the built-in simulator. Expect tuning of timeouts and baud rate on real hardware.
- Compression-ignition readiness mapping is best-effort. Spark ignition follows SAE J1979.
- The code database is the full generic SAE J2012 set from [OBDex](https://github.com/foerbsnavi/OBDex) (CC0). **Toyota-specific codes (most P1xxx and the body/chassis ranges) are proprietary and not included**, apart from a handful. To add your own Toyota list, press **Import codes...** on the Diagnostics page and put `.json` (`[{"dtc":"P1349","description":"..."}]`) or `.csv` (`P1349,description`) files in the folder, then press Reload. Only add data you are allowed to use.
- OBD-II has no standard ECU serial number. The app shows the VIN production sequence (last six characters) as the vehicle serial, and the calibration ID and CVN to identify the ECU software. The adapter serial comes from the ELM327 (AT@2) when it reports one.
- VIN decoding is offline and partial (check digit, model year, a small manufacturer table).
- The CAN monitor shows ELM327 request/response frames labelled with the standard OBD-II IDs (7DF request, 7E8 reply). It is not a raw bus sniffer.
- The vehicle photo, the Toyota emblem and the Projects/Help screens of the concept are replaced with neutral graphics.

## Project layout

```
src/Obd2.Core/              ELM327 transports (Bluetooth/USB serial, Wi-Fi TCP, simulator), OBD-II services, decoders
src/RAV4ControlStudio/      WPF app: MainWindow, Views/, ViewModels/, Controls/ (gauge, graph), Services/
tests/Obd2.Core.Tests/      xUnit tests
dist/RAV4ControlStudio/     Published, self-contained app (run RAV4ControlStudio.exe)
```

## Reference

The layout follows the RAV4 Control Studio concept. The protocol design is inspired by [OBDium](https://github.com/provrb/obdium), but it is an independent implementation from the public SAE J1979 and ELM327 documentation. No OBDium code is copied. OBDium is GPL-3.0; porting its code directly would bring GPL terms.
