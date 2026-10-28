import argparse
import fcntl
import json
import os
from pathlib import Path
import signal
import subprocess

parser = argparse.ArgumentParser(description="Run an unpaced fixed-frame GmWorld mud/quicksand benchmark; this does not perform the planned wall-clock cadence test.")
parser.add_argument("--godot", required=True)
parser.add_argument("--output", default="/tmp/ccgame-mud-matrix")
parser.add_argument("--warmup-frames", type=int, default=450)
parser.add_argument("--sample-frames", type=int, default=1800)
parser.add_argument("--repeats", type=int, default=3)
parser.add_argument("--seed", type=int, default=0)
parser.add_argument("--case-limit", type=int)
parser.add_argument("--condition", help="Run one kind:mode:root-count condition.")
parser.add_argument("--display-driver", choices=["x11", "wayland"])
parser.add_argument("--timeout", type=float, default=900)
parser.add_argument("--window", action="store_true", help="Keep a real Godot window for presentation timing.")
args = parser.parse_args()
if args.warmup_frames < 0 or args.sample_frames <= 0 or args.repeats <= 0 or args.timeout <= 0 or (args.case_limit is not None and args.case_limit <= 0):
    parser.error("Durations, repeats, and case limit must be positive; warmup can be zero.")
lock = open("/tmp/ccgame-mud-matrix.lock", "w")
try:
    fcntl.flock(lock, fcntl.LOCK_EX | fcntl.LOCK_NB)
except BlockingIOError:
    parser.error("A mud matrix benchmark is already running.")
project = Path(__file__).resolve().parent.parent
output = Path(args.output).resolve()
output.mkdir(parents=True, exist_ok=True)
report = output / "matrix.json"
env = dict(os.environ, SLACK_MUD_MATRIX_REPORT=str(report), SLACK_PERF="0", SLACK_USER_ROOT=str(output / "user"))
log = output / "matrix.log"
mode = [] if args.window else ["--headless"]
command = [args.godot, *mode, "--log-file", str(output / "engine.log"), "--path", str(project), "--", "--audit", "--room", "10", "--frames", "0", "--dialog-choice", "1", "--input-text", "Nana", "--mud-matrix", "--matrix-warmup", str(args.warmup_frames), "--matrix-samples", str(args.sample_frames), "--matrix-repeats", str(args.repeats), "--matrix-seed", str(args.seed)]
if args.condition:
    command.extend(["--matrix-condition", args.condition])
if args.case_limit is not None:
    command.extend(["--matrix-limit", str(args.case_limit)])
if args.display_driver:
    command[1:1] = ["--display-driver", args.display_driver]
def terminate(signum, frame):
    raise KeyboardInterrupt(f"Received signal {signum}")
signal.signal(signal.SIGTERM, terminate)
with log.open("w") as stream:
    process = subprocess.Popen(command, env=env, stdout=stream, stderr=subprocess.STDOUT, start_new_session=True)
    print(f"MUD_MATRIX_PROCESS pid={process.pid} pgid={process.pid} report={report} log={log}", flush=True)
    try:
        status = process.wait(timeout=args.timeout)
    finally:
        if process.poll() is None:
            os.killpg(process.pid, signal.SIGTERM)
            try:
                process.wait(timeout=10)
            except subprocess.TimeoutExpired:
                os.killpg(process.pid, signal.SIGKILL)
                process.wait()
    if status:
        raise subprocess.CalledProcessError(status, command)
data = json.loads(report.read_text())
if not data.get("complete"):
    raise RuntimeError("Godot exited before completing the matrix; the report contains partial samples.")
conditions = {(case["kind"], case["mode"], case["rootEntities"]) for case in data["cases"]}
expected_samples = (1 if args.condition else 21) * args.repeats
if args.case_limit is not None:
    expected_samples = min(expected_samples, args.case_limit)
elif not args.condition and (data.get("conditions") != 21 or len(conditions) != 21):
    raise RuntimeError(f"Expected 21 unique mud matrix conditions, got {data.get('conditions')} / {len(conditions)}")
if data.get("samples") != expected_samples:
    raise RuntimeError(f"Expected {expected_samples} repeated samples, got {data.get('samples')}")
invalid = [case for case in data["cases"] if not case.get("valid")]
print(json.dumps({"conditions": len(conditions), "samples": data["samples"], "repeats": data["repeats"], "fixtureChecksPassed": data["samples"] - len(invalid), "invalidSamples": len(invalid), "performanceAcceptanceComplete": False, "presentationAvailable": data["presentationAvailable"], "report": str(report)}, indent=2))
if invalid:
    raise RuntimeError(f"{len(invalid)} samples failed fixture checks; inspect invalidReasons in {report}.")
