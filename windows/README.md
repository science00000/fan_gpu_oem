# Mi50FanWin — Windows 10/11 version

Same behavior as the Linux `mi50fan`: controls the **motherboard 4-pin header**
the MI50 / Radeon Pro VII mod-card cooler fan is plugged into, with the same
auto-detect (PWM sweep → tach response → highest RPM = GPU blower) and the same
curve — **hard cap 75 % below 70 °C**, up to 100 % above 70 °C.

Built on [LibreHardwareMonitor](https://github.com/LibreHardwareMonitor/LibreHardwareMonitor)
(Nuvoton/IT87xx SuperIO + AMD/NVIDIA GPU sensors). C# / .NET 8, self-contained
single-file exe — no runtime install needed.

## Use

1. `Mi50FanWin.exe --detect` — list the board's fan headers and show which ones
   respond to PWM (safe: only revs fans for ~6 s each).
2. `Mi50FanWin.exe --temp` — print GPU temperature 5 times (sensor check).
3. `Mi50FanWin.exe` — run the controller (prints `gpu=..C -> ..% fan=..RPM` every 2 s).
4. `install_task.bat` — install as a scheduled task that starts at logon with
   administrator rights (needs Ring0 driver for SuperIO access).

**Must run as Administrator**, otherwise the SuperIO chip can't be accessed.

## Build

```
dotnet publish -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true
```

Output: `bin/Release/net8.0/win-x64/publish/Mi50FanWin.exe` (~70 MB).

## Notes

- Verified on ASUS ProArt B650 (GPU temp read + board enumeration). SuperIO fan
  control requires the board's hardware monitor to be exposed by
  LibreHardwareMonitor — HUANANZHI X99 boards (NCT6779) are supported; some
  newer boards hide fans until "SuperIO monitoring" is enabled in BIOS.
- If nothing responds to the sweep, check the 4-pin wiring and BIOS fan control
  mode (PWM, not DC) for that header.
