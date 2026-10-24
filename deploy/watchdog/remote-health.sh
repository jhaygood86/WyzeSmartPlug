#!/usr/bin/env bash
# Runs ON the monitored host (piped over SSH). Exit 0 = healthy, 1 = problems found.
# Optional env: DISK_MAX_PCT (default 95), MEM_MIN_MB (default 128), LOAD_FACTOR (default 4), SERVICES (space list).
problems=()

disk_max=${DISK_MAX_PCT:-95}
while read -r pct mount; do
  [ "${pct%\%}" -ge "$disk_max" ] && problems+=("disk ${mount} at ${pct}")
done < <(df -P -x tmpfs -x devtmpfs -x squashfs -x overlay 2>/dev/null | awk 'NR>1 {print $5, $6}')

mem_min=${MEM_MIN_MB:-128}
avail_mb=$(awk '/MemAvailable/ {print int($2/1024)}' /proc/meminfo)
[ "${avail_mb:-0}" -lt "$mem_min" ] && problems+=("low memory: ${avail_mb}MB available")

cores=$(nproc)
load1=$(awk '{print $1}' /proc/loadavg)
if awk -v l="$load1" -v c="$cores" -v f="${LOAD_FACTOR:-4}" 'BEGIN {exit !(l > c * f)}'; then
  problems+=("high load ${load1} on ${cores} cores")
fi

failed=$(systemctl --failed --no-legend --plain 2>/dev/null | awk '{print $1}' | paste -sd, -)
[ -n "$failed" ] && problems+=("failed units: ${failed}")

for svc in ${SERVICES:-}; do
  systemctl is-active --quiet "$svc" || problems+=("service ${svc} not active")
done

if [ "${#problems[@]}" -eq 0 ]; then
  echo "healthy host=$(hostname) load=${load1} mem_avail=${avail_mb}MB"
  exit 0
fi
printf '%s; ' "${problems[@]}"; echo
exit 1
