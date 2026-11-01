#!/bin/bash
# Runs the Playground mud/quicksand matrix on the real GPU (outside the DSH sandbox)
# through the user's systemd manager. Usage: Tools/perf-real-gpu.sh <label> [condition|all ...]
# Each condition runs in its own Godot process so a compositor frame-pacing stall cannot
# contaminate the whole sweep.
set -euo pipefail
root="${PERF_ROOT:-$(cd "$(dirname "$0")/.." && pwd)}"
godot="${GODOT_BIN:-/home/dpapyru/公共/Godot/Godot_v4.7.2-stable_mono_linux_x86_64/Godot_v4.7.2-stable_mono_linux.x86_64}"
label="${1:?usage: perf-real-gpu.sh <label> [condition|all ...]}"
shift
conditions=("$@")
warmup="${WARMUP:-20}"
samples="${SAMPLES:-60}"
driver="${DISPLAY_DRIVER:-wayland}"
out="$root/.playtest/real-gpu/$label"
rm -rf "$out"; mkdir -p "$out"
cd "$root"
dotnet build -p:IncludeDiagnostics=true -v:q 2>&1 | grep -E ': error' && exit 1 || true
if [ ${#conditions[@]} -eq 0 ] || [ "${conditions[0]}" = "all" ]; then
  conditions=()
  for kind in none mud quicksand; do
    for mode in static contact moving; do
      if [ "$kind" = none ]; then roots_list=(0); else roots_list=(1 20 50); fi
      for roots in "${roots_list[@]}"; do conditions+=("$kind:$mode:$roots"); done
    done
  done
fi
for condition in "${conditions[@]}"; do
  name="${condition//:/_}"
  dir="$out/$name"; mkdir -p "$dir"
  run="$dir/run.sh"
  {
    echo '#!/bin/sh'
    printf 'exec %q --display-driver %s --log-file %q --path %q -- --audit --room 10 --frames 0 --dialog-choice 1 --input-text Nana --mud-matrix --matrix-warmup %d --matrix-samples %d --matrix-repeats 1 --matrix-seed 0 --matrix-condition %q\n' \
      "$godot" "$driver" "$dir/engine.log" "$root" "$warmup" "$samples" "$condition"
  } > "$run"
  chmod +x "$run"
  timeout "${RUN_TIMEOUT:-180}" systemd-run --user --wait --collect --unit="ccgame-perf-$(date +%s)-$$" \
    --setenv=XDG_RUNTIME_DIR="${XDG_RUNTIME_DIR:-/run/user/$(id -u)}" \
    --setenv=WAYLAND_DISPLAY="${WAYLAND_DISPLAY:-wayland-1}" \
    --setenv=DISPLAY="${DISPLAY:-:0}" \
    --setenv=SLACK_MUD_MATRIX_REPORT="$dir/matrix.json" \
    --setenv=SLACK_PERF="${SLACK_PERF:-0}" \
    --setenv=SLACK_USER_ROOT="$dir/user" \
    -- /bin/sh "$run" > "$dir/systemd.log" 2>&1 || true
  echo "done $condition" >&2
done
python3 "$root/Tools/perf-real-gpu-summary.py" "$out"