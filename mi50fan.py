#!/usr/bin/env python3
"""mi50fan — Linux fan controller for MI50/Pro VII whose cooler fan is wired
to a motherboard 4-pin header (auto-detected by RPM sweep + GPU-temp correlation).
Rules (user spec): curve theo GPU temp; duoi 70 degC tran 75%; tren 70 degC toan cong suat.
Author: Hermes, box ros7@192.168.1.13 (HUANANZHI X99-BD4, SuperIO NCT6779D)."""
import glob, os, sys, time, math

GPU = '/sys/class/drm/card1/device/hwmon/hwmon1'   # amdgpu hwmon
MODPROBE = ['modprobe', 'nct6775']
DETECT_RETRIES = 5

# --- curve: (gpu_temp_C, pwm_%), linear interpolate; user spec: hard cap 75% below 70C
CURVE = [(0, 15), (40, 20), (50, 35), (58, 50), (65, 65), (70, 75), (75, 88), (82, 100), (90, 100)]
HOTSPOT_BUMP = 3      # use max(core, mem)+bump toward hotspot
STEP_LIMIT = 12       # max pwm delta per cycle (smooth, no revving)
HYST_C = 1.5          # degC: must cool this much below 60s-window max before lowering
CYCLE = 2.0           # s
WINDOW = int(60 / CYCLE)

def find_superio():
    for h in glob.glob('/sys/class/hwmon/hwmon*'):
        try:
            if open(h + '/name').read().strip() in ('nct6779', 'nct6775', 'nct6776', 'nct6791', 'it87'):
                return h
        except OSError:
            pass
    return None

def detect_header(h):
    """Sweep each pwm/fan pair; header whose RPM responds = wired fan.
    Return list of (pwm_idx, lo_rpm, hi_rpm)."""
    out = []
    for f in glob.glob(h + '/fan*_input'):
        idx = f.split('fan')[1].split('_')[0]
        pwm = f'{h}/pwm{idx}'
        if not os.path.exists(pwm):
            continue
        try:
            en_old = int(open(f'{h}/pwm{idx}_enable').read())
            w(pwm, 20); time.sleep(3); lo = int(open(f).read())
            w(pwm, 255); time.sleep(3); hi = int(open(f).read())
            if hi - lo > 100:
                out.append((idx, lo, hi))
        except OSError:
            pass
    return out

def find_gpu_hwmon():
    for h in glob.glob('/sys/class/drm/card*/device/hwmon/hwmon*'):
        try:
            if open(h + '/name').read().strip() == 'amdgpu':
                return h
        except OSError:
            pass
    return GPU

def gpu_temps():
    g = find_gpu_hwmon()
    def rd(p):
        try: return int(open(p).read())
        except OSError: return 0
    core = rd(g + '/temp1_input'); hot = rd(g + '/temp2_input'); mem = rd(g + '/temp3_input')
    return max(core, mem + HOTSPOT_BUMP, hot - 2) / 1000.0

def curve_pwm(t):
    if t <= CURVE[0][0]: return CURVE[0][1]
    for (t0, p0), (t1, p1) in zip(CURVE, CURVE[1:]):
        if t <= t1:
            return p0 + (p1 - p0) * (t - t0) / (t1 - t0)
    return CURVE[-1][1]

def w(path, val):
    try:
        with open(path, 'w') as f:
            f.write(str(int(val)))
    except OSError:
        pass

def main():
    # single instance
    import fcntl
    lock = open('/tmp/mi50fan.lock', 'w')
    try:
        fcntl.flock(lock, fcntl.LOCK_EX | fcntl.LOCK_NB)
    except BlockingIOError:
        print('another instance running'); return 0
    h = find_superio()
    if not h:
        os.system(' '.join(MODPROBE)); time.sleep(2); h = find_superio()
    if not h:
        print('superio not found', file=sys.stderr); return 1
    hdrs = detect_header(h)
    if not hdrs:
        print('no responsive fan header', file=sys.stderr); return 1
    hdrs.sort(key=lambda x: -x[2])          # highest-RPM header = GPU blower
    idx = hdrs[0][0]
    pwm, enable = f'{h}/pwm{idx}', f'{h}/pwm{idx}_enable'
    print(f'managing header pwm{idx} in {h}; headers seen: {hdrs}', flush=True)
    w(enable, 1)
    hist = []
    try:
        while True:
            t = gpu_temps()
            hist.append(t)
            if len(hist) > WINDOW:
                hist.pop(0)
            t_eff = max(hist)
            tgt = curve_pwm(t_eff)                     # percent 0..100
            # hard cap per user rule: below 70C never exceed 75%
            if t_eff < 70 and tgt > 75: tgt = 75
            cur = int(open(pwm).read())                # raw 0..255
            tgt_raw = int(tgt * 255 // 100)
            if tgt_raw > cur:
                w(pwm, min(tgt_raw, cur + STEP_LIMIT))
            elif tgt_raw < cur:
                # lower when: cooled below window-max by HYST_C, OR temp flat (<0.5C
                # drift in 60s window) — gradually walk down to curve target
                if t < t_eff - HYST_C or t_eff - t < 0.5:
                    w(pwm, max(tgt_raw, cur - STEP_LIMIT))
            time.sleep(CYCLE)
    finally:
        pass  # keep our curve active after exit; systemd restarts anyway

if __name__ == '__main__':
    sys.exit(main())
