import argparse
import json
import os
from pathlib import Path
import subprocess

parser = argparse.ArgumentParser()
parser.add_argument("--godot", required=True)
parser.add_argument("--output", default="/tmp/ccgame-mud-profile")
args = parser.parse_args()
project = Path(__file__).resolve().parent.parent
output = Path(args.output).resolve()
output.mkdir(parents=True, exist_ok=True)
reports = []
for mode in ("full", "no-sprites", "no-state"):
    report = output / f"{mode}.json"
    env = dict(os.environ, SLACK_MUD_EXPERIMENT=mode, SLACK_MUD_REPORT=str(report), SLACK_PERF="0", SLACK_USER_ROOT=str(output / f"user-{mode}"))
    with (output / f"{mode}.log").open("w") as log:
        subprocess.run([args.godot, "--headless", "--log-file", str(output / f"{mode}-engine.log"), "--path", str(project), "--", "--audit", "--room", "10", "--frames", "120", "--dialog-choice", "1", "--input-text", "Nana", "--mud-profile"], env=env, stdout=log, stderr=subprocess.STDOUT, check=True, timeout=180)
    data = json.loads(report.read_text())
    if data["room"] != 11 or any(w["surfacesMean"] == 0 or w["touchingFrames"] == 0 for w in data["windows"]):
        raise RuntimeError(f"Invalid mud scenario: {mode}")
    reports.append(data)
    windows = data["windows"]
    print(mode, json.dumps({
        "frameMeanMs": sum(w["stepMeanMs"] for w in windows) / len(windows),
        "allocatedBytesPerFrame": sum(w["allocatedBytesPerFrame"] for w in windows) / len(windows),
        "activeNodesMean": sum(w["activeNodesMean"] for w in windows) / len(windows),
        "gen0Collections": sum(w["gcCollections"][0] for w in windows),
    }), flush=True)
(output / "combined.json").write_text(json.dumps(reports, indent=2))
print(f"Reports: {output}")
