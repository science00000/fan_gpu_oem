# mi50fan — GPU header fan control (Linux + Windows)

Linux fan controller for AMD Instinct **MI50 / Radeon Pro VII** cards whose cooler
fan is wired to a **motherboard 4-pin header** (the common Chinese mod-card setup).

Port of the concept from [kingbone2006/MI50-Fan-Control](https://github.com/kingbone2006/MI50-Fan-Control) (Windows) to Linux, using only kernel hwmon — no SuperIO vendor drivers needed. A Windows 10/11 version (`Mi50FanWin`, C#/.NET, LibreHardwareMonitor) lives in [`windows/`](windows/).

## What it does

1. **Auto-detects the SuperIO chip** (Nuvoton NCT6779D etc. via `nct6775` kernel module) and the GPU amdgpu hwmon by *name* (hwmon numbers change across boots).
2. **Detects which fan header the GPU fan is plugged into**: sweeps each PWM header 20% → 100% and keeps headers whose tach responds; the highest-RPM header is taken as the GPU blower. (Validated on HUANANZHI X99-BD4 by stop-the-fan / watch-GPU-temperature correlation: +6 °C in 90 s on the right header.)
3. **Curve control** from GPU core + HBM + hotspot temperatures, with smoothing and hysteresis (no revving):

   | GPU temp °C | fan % |
   |---|---|
   | 0 | 15 |
   | 40 | 20 |
   | 50 | 35 |
   | 58 | 50 |
   | 65 | 65 |
   | **70** | **75** |
   | 75 | 88 |
   | 82+ | 100 |

   Hard rule: **below 70 °C the fan never exceeds 75 %**; above 70 °C full authority up to 100 %.

## Install

```bash
sudo mkdir -p /opt/mi50fan
sudo cp mi50fan.py /opt/mi50fan/mi50fan.py
echo nct6775 | sudo tee /etc/modules-load.d/mi50fan.conf   # SuperIO module at boot
sudo cp mi50fan.service /etc/systemd/system/mi50fan.service
sudo systemctl enable --now mi50fan
```

Run once in the foreground to see detection results:

```bash
sudo python3 /opt/mi50fan/mi50fan.py
# managing header pwm1 in /sys/class/hwmon/hwmon0; headers seen: [('1', 1004, 4770)]
```

## Viewing status — `fanstatus`

The controller runs as a service, so running `sudo python3 /opt/mi50fan/mi50fan.py`
by hand prints **`another instance running`** (single-instance lock) — that is
normal, the service is already doing the job. To just *look* at it:

```bash
sudo cp fanstatus.sh /usr/local/bin/fanstatus && sudo chmod +x /usr/local/bin/fanstatus
fanstatus
# service: active
# gpu: 48C hotspot 49C 21W
# hdr1: 0%  1102 RPM      <- GPU blower header
# hdr2: 0%  1216 RPM
```

More detail when needed:

```bash
journalctl -u mi50fan --no-pager | tail -20   # service log
systemctl status mi50fan                      # alive?
sudo systemctl stop mi50fan && sudo python3 /opt/mi50fan/mi50fan.py   # manual foreground test
sudo systemctl start mi50fan                  # hand control back
```

## Notes

- The card-side `fan1_input` on these mod cards is a **phantom constant** (~4950) when the fan is on the motherboard header — ignore it; use the header tach.
- PWM writes are raw 0–255; the curve works in %.
- Tested on Ubuntu 22.04.5 / kernel 6.8, HUANANZHI X99-BD4 (NCT6779D), MI50 16G crossflashed to Radeon Pro VII VBIOS. Idle result: fan 780 RPM @ 38 °C (was 4800 RPM constant).
- MIT-style, same spirit as the upstream Windows project it is inspired by.
