#!/bin/bash
# Benchmarks a real game room headless by default; HEADLESS=0 uses the x11 display directly.
# Usage: Tools/perf-real-map.sh <label> [room=11] [liveFrames=600]
set -euo pipefail
root="${PERF_ROOT:-$(cd "$(dirname "$0")/.." && pwd)}"
godot="${GODOT_BIN:-/home/dpapyru/公共/Godot/Godot_v4.7.2-stable_mono_linux_x86_64/Godot_v4.7.2-stable_mono_linux.x86_64}"
label="${1:?usage: perf-real-map.sh <label> [room] [frames]}"
room="${2:-Playground}"
frames="${3:-600}"
headless="${HEADLESS:-1}"
out="$root/.playtest/real-map/$label"
rm -rf "$out"; mkdir -p "$out"
cd "$root"
dotnet build -p:IncludeDiagnostics=true -v:q 2>&1 | grep -E ': error' && exit 1 || true
driver=(); [ "$headless" = "1" ] && driver=(--headless) || driver=(--display-driver x11)
mud=(); [ "$headless" != "1" ] && [ -n "${MUD:-}" ] && mud=(--mud-window)
# Room audits enter via the Playground menu first so the enemy registry globals exist.
audit=(); if [[ "$room" =~ ^[0-9]+$ ]]; then audit=(--audit --menu Playground --room "$room" --frames 300); else audit=(--audit --menu "$room" --frames 300); fi
run="$out/run.sh"
{
  echo '#!/bin/sh'
  printf 'exec %q' "$godot"
  for arg in "${driver[@]}" --log-file "$out/engine.log" --path "$root" -- "${audit[@]}" "${mud[@]}" --dialog-choice 1 --input-text Nana --live-frames "$frames"; do printf ' %q' "$arg"; done
  printf '\n'
} > "$run"
chmod +x "$run"
SLACK_PERF=1 SLACK_USER_ROOT="$out/user" timeout "${RUN_TIMEOUT:-180}" /bin/sh "$run" > "$out/stdout.log" 2>&1 || true
grep -h 'LIVE_PERF\|AUDIT room' "$out/engine.log" | tail -3
