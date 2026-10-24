import argparse
import json
import os
from pathlib import Path
import subprocess
import tempfile

parser = argparse.ArgumentParser()
parser.add_argument("--godot", required=True)
parser.add_argument("--frames", type=int, default=600)
args = parser.parse_args()
if args.frames < 300:
    parser.error("--frames must be at least 300")
project = Path(__file__).resolve().parent.parent
output = Path(tempfile.mkdtemp(prefix="ccgame-mud-window-"))
print(output, flush=True)
results = []
for mode in ("full", "no-sprites", "no-state"):
    user = output / mode
    env = dict(os.environ, SLACK_MUD_EXPERIMENT=mode, SLACK_USER_ROOT=str(user), SLACK_PERF="1", SLACK_PERF_OBJECTS="1")
    with (output / f"{mode}.log").open("w") as log:
        subprocess.run([args.godot, "--path", str(project), "--log-file", str(output / f"{mode}-engine.log"),
                        "--", "--audit", "--room", "10", "--frames", "120", "--dialog-choice", "1",
                        "--input-text", "Nana", "--mud-window", "--live-frames", str(args.frames)],
                       env=env, stdout=log, stderr=subprocess.STDOUT, check=True, timeout=180)
    records = [json.loads(line) for path in (user / "logs").glob("performance-*.jsonl") for line in path.read_text().splitlines()]
    samples = [r for r in records if r["type"] == "cadence" and r["room"] == 11][5:]
    if not samples or "LIVE_PERF room=11" not in (output / f"{mode}.log").read_text():
        raise RuntimeError(f"Incomplete window test: {mode}")
    if any(r["completedRenderFps"] == 0 for r in samples):
        raise RuntimeError(f"No completed rendering: {mode}; keep the test window visible")
    seconds = sum(r["seconds"] for r in samples)
    allocated = sum(r["gc"]["allocatedBytes"] for r in samples)
    pause_ms = sum(r["gc"]["pauseMs"] for r in samples)
    result = dict(mode=mode, windows=len(samples),
                  renderFps=sum(r["completedRenderFps"] for r in samples) / len(samples),
                  logicFps=sum(r["logicFps"] for r in samples) / len(samples),
                  catchUpProcesses=sum(r["catchUpProcesses"] for r in samples),
                  maxStepsPerProcess=max(r["maxStepsPerProcess"] for r in samples),
                  allocatedMBPerSecond=allocated / seconds / 1e6,
                  allocatedMBPerLogicStep=allocated / sum(r["logicSteps"] for r in samples) / 1e6,
                  gcCollections=[sum(r["gc"]["collections"][g] for r in samples) for g in range(3)],
                  gcPauseMs=pause_ms, gcPausePercent=pause_ms / (seconds * 1000) * 100)
    results.append(result)
    print(json.dumps(result), flush=True)
(output / "summary.json").write_text(json.dumps(results, indent=2))
