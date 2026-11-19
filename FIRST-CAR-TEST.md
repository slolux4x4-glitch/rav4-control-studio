# First test with a real adapter and car

Do this in order. Stop at the first step that fails and note what you see.

**Guided version:** double-click `tools\FirstCarTest.cmd` and run it beside the app. It asks pass, fail or skip for each step below, shows what to try when one fails, checks that the PC can reach the Wi-Fi adapter, and saves your answers to `Documents\RAV4 Control Studio\Logs\car-test-....txt`. It never talks to the car itself.

## Before you go to the car
1. Open RAV4 Control Studio and use **OBD2 Connection > Demo > Test adapter**. Every step should pass.

## In the car (engine off, ignition on)
2. Plug the adapter into the OBD-II port under the dashboard (driver's side). Turn the ignition ON; do not start the engine yet.
3. Join the adapter's Wi-Fi network in Windows (for example `WiFi_OBDII`). "No internet" is normal.
   *Bluetooth adapter instead:* pair it in Windows Settings (PIN usually 1234 or 0000), then press **Refresh** on the connection page; it appears as a COM port labelled *Bluetooth*.
4. In the app choose **Wi-Fi**, address `192.168.0.10`, port `35000` (or press **Find adapter**). Leave the protocol on **ISO 15765-4 CAN 11-bit, 500 kbaud** and click **Test adapter**.
   - *Open link fails*: the PC is not on the adapter's network, or the address or port is wrong. (Bluetooth/USB: wrong COM port or baud rate; try 115200, then 9600, 38400 again.)
   - *Voltage low*: ignition is off or the adapter is not fully seated.
   - *ECU answers fails*: ignition off, or try protocol **Automatic**.
5. Click **Connect ECU**. The badge should say **Vehicle Mode**. Check the VIN on ECU Information, a sensible coolant temperature and battery voltage (about 12 V engine off).
6. Open **Diagnostics** and press **Read codes**. Compare with what a trusted scan tool or the dash lights show.

## Engine running
7. Start the engine. On **Metrics**: about 600 to 900 rpm at idle, speed 0, coolant rising, battery about 13.5 to 14.5 V.
8. Start **Data Logging** for a minute, then stop it and open the CSV to confirm rows were saved.
9. Create a report from the **Reports** page.
10. On **OBD2 Connection** press **Save session log** (even if everything passed), then **Disconnect**.

## What to send back if something is wrong
- **OBD2 Connection > Save session log**. Press it straight after the problem (before closing the app). It saves every command sent to the car and the exact reply to `Documents\RAV4 Control Studio\Logs\session-....txt` and shows the file. This is the most useful thing to send.
- The `car-test-....txt` file if you used the guided version.
- The result list from **Test adapter** (copy it from the screen).
- **About > Copy details**.
- The newest file from `Documents\RAV4 Control Studio\Logs` if logging was on.
- The adapter model or brand, and whether it is Bluetooth, USB or Wi-Fi.

## Safety
The app is read-only: it never clears codes or controls the vehicle. Do not drive while operating the laptop; let a passenger run it.
