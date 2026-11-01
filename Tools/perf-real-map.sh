#!/bin/bash
# Benchmarks the real game room on the real GPU (outside the DSH sandbox) via the user systemd manager.
# Usage: Tools/perf-real-map.sh <label> [room=11] [liveFrames=600]
set -euo pipefail
root="${PERF_ROOT:-$(cd "$(dirname "$0")/.." && pwd)}"
godot="${GODOT_BIN:-/home/dpapyru/公共/Godot/Godot_v4.7.2-stable_mono_linux_x86_64/Godot_v4.7.2-stable_mono_linux.x86_64}"
label="${1:?usage: perf-real-map.sh <label> [room] [frames]}"
menu="${2:-Playground}"
frames="${3:-600}"
mud="${MUD:+--mud-window}"
out="$root/.playtest/real-map/$label"
rm -rf "$out"; mkdir -p "$out"
cd "$root"
dotnet build -p:IncludeDiagnostics=true -v:q 2>&1 | grep -E ': error' && exit 1 || true
run="$out/run.sh"
printf '#!/bin/sh\nexec %q --display-driver x11 --log-file %q --path %q -- --audit --menu %s --frames 300 %s --dialog-choice 1 --input-text Nana --live-frames %s\n' "$godot" "$out/engine.log" "$root" "$menu" "$mud" "$frames" > "$run"
chmod +x "$run"
systemd-run --user --wait --collect --unit="ccgame-map-$(date +%s)-$$" \
  --setenv=XDG_RUNTIME_DIR="${XDG_RUNTIME_DIR:-/run/user/$(id -u)}" \
  --setenv=WAYLAND_DISPLAY="${WAYLAND_DISPLAY:-wayland-1}" \
  --setenv=DISPLAY="${DISPLAY:-:1}" \
  -- /bin/sh "$run" > "$out/systemd.log" 2>&1 || true
grep -h 'LIVE_PERF\|AUDIT room' "$out/engine.log" | tail -3