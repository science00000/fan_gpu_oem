#!/bin/bash
# fanstatus: one-shot view of mi50fan state (install: sudo cp fanstatus.sh /usr/local/bin/fanstatus)
echo "service: $(systemctl is-active mi50fan)"
g=$(grep -l amdgpu /sys/class/hwmon/*/name 2>/dev/null | head -1 | xargs -r dirname)
s=$(grep -l nct677 /sys/class/hwmon/*/name 2>/dev/null | head -1 | xargs -r dirname)
if [ -n "$g" ]; then
  t=$(( $(cat $g/temp1_input) / 1000 )); ht=$(( $(cat $g/temp2_input 2>/dev/null || echo 0) / 1000 ))
  p=$(cat $g/power1_input 2>/dev/null); echo "gpu: ${t}C hotspot ${ht}C $(( ${p:-0} / 1000000 ))W"
fi
if [ -n "$s" ]; then
  for i in 1 2 3 4 5; do
    pwm=$(cat $s/pwm${i}_input 2>/dev/null); rpm=$(cat $s/fan${i}_input 2>/dev/null)
    [ -n "$rpm" ] && [ "$rpm" != "0" ] && echo "hdr$i: $(( ${pwm:-0} * 100 / 255 ))%  ${rpm} RPM"
  done
fi
l=$(journalctl -u mi50fan -n 1 --no-pager -o cat 2>/dev/null); [ -n "$l" ] && echo "last: $l"
