# First test with a real adapter and car

Do this in order. Stop at the first step that fails and note what you see.

## Before you go to the car
1. Open RAV4 Control Studio and use **OBD2 Connection > Demo > Test adapter**. Every step should pass.
2. Pair or plug in the ELM327 adapter on this PC first (Bluetooth: Windows Settings, PIN usually 1234 or 0000; Wi-Fi: join the adapter's network).
3. Press **Refresh** on the connection page. A Bluetooth adapter should appear as a COM port labelled *Bluetooth*; a USB one as *USB / wired*.

## In the car (engine off, ignition on)
4. Plug the adapter into the OBD-II port under the dashboard. Turn the ignition ON (do not start the engine yet).
5. Choose the adapter, leave the protocol on **ISO 15765-4 CAN 11-bit, 500 kbaud**, then click **Test adapter**.
   - *Open link / reset fail*: wrong COM port or baud rate. Try 115200, then 9600, 38400 again.
   - *Voltage low*: ignition is off or the adapter is not fully seated.
   - *ECU answers fails*: ignition off, or try protocol **Automatic**.
6. Click **Connect ECU**. Check the Overview shows the VIN (ECU Information page), a sensible coolant temperature and battery voltage (about 12 V engine off).
7. Open **Diagnostics** and press **Read codes**. Compare with what a trusted scan tool or the dash lights show.

## Engine running
8. Start the engine. RPM should read about 600 to 900 at idle; check **Metrics** (speed 0, coolant rising).
9. Start **Data Logging** for a minute, then stop it and open the CSV to confirm rows were saved.
10. Create a **Report** from the Reports page.

## What to send back if something is wrong
- **OBD2 Connection > Save session log**. Press it straight after the problem (before closing the app). It saves every command sent to the car and the exact reply to `Documents\RAV4 Control Studio\Logs\session-....txt` and shows the file. This is the most useful thing to send.
- The result list from **Test adapter** (copy it from the screen).
- **About > Copy details**.
- The newest file from `Documents\RAV4 Control Studio\Logs` if logging was on.
- The adapter model or brand, and whether it is Bluetooth, USB or Wi-Fi.

## Safety
The app is read-only: it never clears codes or controls the vehicle. Do not drive while operating the laptop; let a passenger run it.
