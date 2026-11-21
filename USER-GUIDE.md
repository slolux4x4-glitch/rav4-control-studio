# RAV4 Control Studio: user guide

A short guide to everyday use. For what each page shows, see the [README](README.md). For the first time with a real car, follow [FIRST-CAR-TEST.md](FIRST-CAR-TEST.md).

The app only reads from the car. It cannot clear codes, reset lights or change any setting in the vehicle.

## 1. Connect

1. Plug the ELM327 into the OBD-II port under the dashboard, driver's side.
2. Turn the ignition **on**. The engine can be off or running.
3. Connect the PC to the adapter:
   - **Wi-Fi:** join the adapter's network in Windows (for example `WiFi_OBDII`). Windows may say "No internet"; that is normal.
   - **Bluetooth:** pair it once in Windows Settings (PIN usually `1234` or `0000`).
   - **USB:** plug it in.
4. In the app, open **OBD2 Connection**:
   - Wi-Fi: choose Wi-Fi, address `192.168.0.10`, port `35000` (or press **Find adapter**).
   - Bluetooth or USB: press **Refresh** and pick the COM port.
   - Leave the protocol on **ISO 15765-4 CAN 11-bit, 500 kbaud**.
5. Press **Connect ECU**. The status bar turns green and the Overview fills in.

If it fails, press **Test adapter**. It checks each step and says whether the problem is the adapter, the link or the car.

If the link drops, the app tries to reconnect up to six times by itself.

## 2. Check a warning light

1. Open **Diagnostics (DTCs)** and press **Read codes**.
2. Stored codes turned the light on; pending codes have been seen once and may turn it on later; permanent codes stay until the car has confirmed the repair itself.
3. Select a code to see its meaning, likely causes and symptoms.
4. The **freeze frame** shows the engine conditions at the moment the fault was stored.
5. Save a **Report** before you visit a garage.

You can also type any code in the search box, without a car, to look it up.

**Toyota codes.** The generic codes come with the app. Toyota-specific codes come from the lists in `Documents\RAV4 Control Studio\Codes`, which currently holds the OBDcodex Toyota list. Those have a title only. A code missing from every list shows "not in the database".

## 3. Watch live values

- **Overview:** the main gauges, a three-minute graph and the code summary.
- **Metrics:** large gauges with session peaks.
- **Live Data:** every value the engine supports. **Save baseline** while the car is healthy; later the tiles show how far each value has moved from it.
- **Trends:** graph up to six values you choose.
- **Fuel and O2:** fuel trims and oxygen sensors, with plain-language hints.

Switch metric or imperial units from the status bar.

## 4. Record a drive

1. Open **Data Logging** and press **Start Logging**.
2. Drive. A passenger should handle the laptop.
3. Press **Stop Logging**. The CSV file is in `Documents\RAV4 Control Studio\Logs` and opens in Excel.
4. Choose a saved log and press **Replay** to play it back through the whole app at home.

When you disconnect, a trip summary shows distance, time and estimated fuel economy.

## 5. Phone view

Settings > Phone view starts a read-only web page on the PC. Join the phone to the same network, then open the address shown, which includes a 10-character code. Only devices on the local network can connect, and only with that code. Stop it in Settings when you are done.

With a Wi-Fi adapter the PC's Wi-Fi is taken by the adapter, so the phone needs to reach the PC another way (for example through a wired network).

## 5a. Laptop bridge (use the adapter from another PC)

RAV4 Bridge lets the app on one PC (for example your desktop) use the adapter that a laptop at the car is connected to. Both PCs must be on the same network (for example the laptop on Ethernet, its Wi-Fi on the adapter). It stays read-only: the laptop checks every command and refuses anything that is not a read.

**On the laptop (next to the car):**

1. Open RAV4 Control Studio. On **OBD2 Connection**, choose how the laptop reaches the adapter (**Wi-Fi** or **Bluetooth / USB**) and fill in its details. Do not connect.
2. Go to **Settings** > **Laptop bridge** and press **Start RAV4 Bridge**. If Windows asks, allow it on private networks.
3. The RAV4 Bridge window shows the laptop's addresses, the port (35100) and an access code.

**On the other PC:**

1. On **OBD2 Connection**, choose **Laptop bridge**.
2. Enter one of the laptop's addresses (the one on the shared network), the port and the access code.
3. Press **Connect ECU**. Everything then works as if the adapter were plugged into this PC.

Only one PC can use the bridge at a time, and the laptop's own copy of the app must stay disconnected while the bridge runs. Press **New code** in Settings to change the code (then restart RAV4 Bridge). Close the RAV4 Bridge window to stop sharing.

### Bridge web page (phone, tablet or any PC)

RAV4 Bridge also serves a web page in the same style as the app. The bridge window lists its address, for example `http://192.168.1.50:35100/#t=yourcode`; open it in a browser on any device on the same network (the code after `#t=` logs you in, or type it when asked).

- **Live**: speed, engine speed, coolant and battery dials, plus throttle and engine load. Switch Metric / Imperial at the top. **Full screen** shows only the dials (tap anywhere to exit) and keeps the screen awake; on an iPhone, add the page to the Home Screen first to hide Safari's bars.
- **Codes**: press **Read codes** for stored, pending and permanent codes and the readiness monitors.
- **Status**: the adapter, which PC is using the bridge, how many commands were passed on or refused, and recent events.

While a PC is connected through the bridge, the page shows the values that PC is reading (nothing extra is asked of the car), and codes are read on that PC instead. With no PC connected, the bridge reads the car itself while the page is open. The page is read-only too.



Set limits for coolant temperature, battery voltage and engine speed in **Settings**. The app warns you when a value passes its limit.

## Where your files are

Everything lives in `Documents\RAV4 Control Studio`:

| Folder | Contents |
|---|---|
| `Logs` | Recorded drives (CSV) |
| `Reports` | Saved reports |
| `Codes` | Extra trouble-code lists (CSV or JSON) |
| `Parameters` | Custom read-only parameters (JSON) |

Back this folder up together with the app. Settings are stored in the same place.

## Problems

| What you see | Try |
|---|---|
| Can't reach the Wi-Fi adapter | Check the PC is on the adapter's network; turn off VPN; press **Find adapter** |
| "NO DATA" or no ECU answer | Ignition on? Try protocol **Automatic** |
| Battery reads about 12 V with engine running | The charging system may not be charging; have it checked |
| Values freeze | Unplug the adapter for ten seconds, then reconnect |
| Windows SmartScreen warns on start | The app is not code-signed. Choose **More info > Run anyway** |
