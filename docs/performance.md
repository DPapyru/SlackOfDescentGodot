# Normal Gameplay Performance Checks

The target is 30 logic updates per second and at least 30 rendered frames per
second during normal gameplay. Rain retains its original resolution; only
`objDark4` uses a quarter-width, quarter-height texture with linear enlargement.
Weather, collision accuracy, GM event order, and game speed are preserved.
Loading and modal dialogs are excluded.

Use the normal game loop for acceptance. The `--animation-audit rain` entry point
advances one logic frame per process callback and is not equivalent to normal
gameplay scheduling.

Start the Debug build with script profiling enabled, keeping stdout connected to
a terminal or redirecting it to a file:

```zsh
env SLACK_PERF=1 SLACK_PERF_SCRIPTS=1 \
  /home/dpapyru/公共/Godot/Godot_v4.7.2-stable_mono_linux_x86_64/Godot_v4.7.2-stable_mono_linux.x86_64 \
  --path /home/dpapyru/ccgame --debug > /tmp/ccgame-normal-perf.log 2>&1
```

Performance JSONL files are created under
`~/.local/share/SlackOfDescentGodot/logs/`. `SLACK_USER_ROOT` overrides the user
directory. Each measured second produces a `cadence` record even if the room
started below the target frame rate.

- `logicFps` measures completed logic frames over elapsed wall time.
- `processFps` retains the existing process-callback frequency measurement.
- `completedRenderFps` counts intervals between Godot `FramePostDraw` callbacks.
- `process` summarizes time spent in the host's process callback.
- `renderInterval` summarizes intervals between completed render callbacks. This
  includes logic stalls and waits; it is not GPU execution time or physical
  display presentation time.
- `catchUpProcesses` and `maxStepsPerProcess` expose logic catch-up work.
- `graphics.triangleCacheHits`, `triangleCacheMisses`, `triangleCacheEntries`, and `triangleCacheBytes`
  expose the coverage cache. Coverage is keyed by dimensions and exact vertices,
  with at most 8192 entries and 8388608 stored integers (32 MiB of coverage data,
  excluding object overhead). Room changes and Surface release clear it.
- `graphics.effects` lists each targeted Surface's backend, logical and actual
  width/height, batch count, and triangle count. Rain is full resolution;
  `objDark4` is `ceil(width / 4)` by `ceil(height / 4)`, including 212 by 120
  for 848 by 477. The padded last row is filled but final composition covers
  only the logical rectangle. Other darkness controllers remain on the CPU.
- `graphics.cpuFallbacks` counts GPU Surfaces replayed into software after a
  capture or software-target draw. Replay uses recorded vertices, textures,
  colours and blend modes and does not execute scripts or consume randomness.
- `collision.snapshotRebuilds`, `candidateVisits`, and `exactTests` are cumulative
  event-dispatch counters. Immutable type snapshots are shared until membership
  changes; each traversal intersects nearby grid members in snapshot order.
  Nested queries cannot hide geometry changes from the outer traversal.
- `dynamicDark` and `darkness` record the current GM global settings.

`PERF` lines report logic phases and Surface operations. The `draw` phase measures
draw preparation, not the entire rendering pipeline. `PERF_SCRIPTS` provides
inclusive script timings when script profiling is enabled; nested script costs
must not be added together as independent work.
Waiting scripts are timed on the asynchronous path; synchronous delegation is
timed once. These inclusive times can include a modal wait.
`effect_cpu_submit` measures CPU preparation/submission of offscreen Canvas
commands, not GPU execution. No GPU timestamp measurements are reported.

For user acceptance, move to Playground near `(8007, 1860)` and `(7997, 1024)`.
Record at least 30 seconds at each position, then include movement into the area.
Review per-second logic FPS (29-31), completed-render FPS (at least 30), and P95
render intervals against the approximately 33.3 ms frame budget. A good average
alone does not establish stable cadence. Script profiling adds overhead and is
intended to identify remaining hotspots before a final normal-play check.

The normal scheduler still permits up to two logic steps per process callback.
GM events, draws and random calls still execute for every catch-up step. A full
opaque base rectangle replaces the targeted Surface's recorded version, so only
the last complete version is submitted for the current rendered frame. GPU
textures are sampled directly by the main Canvas; normal effects do not use CPU
pixel composition, the triangle coverage cache, or full-texture uploads.

Functional checks (no performance scenarios):

```zsh
dotnet build -c Debug
dotnet run --project Tests/RuntimeTests.csproj
env SLACK_PERF=0 \
  /home/dpapyru/公共/Godot/Godot_v4.7.2-stable_mono_linux_x86_64/Godot_v4.7.2-stable_mono_linux.x86_64 \
  --path /home/dpapyru/ccgame --rendering-method gl_compatibility \
  --audio-driver Dummy -- --audit --graphics-audit
```

The graphics audit checks software blend references, full-resolution rain,
quarter-resolution darkness, exact blocker boundaries, both aspect ratios,
multiple recorded versions before rendering, and software replay. Its additional
GM-event checks cover rain animation and dynamic blockers, darkness random-call
order, flash, repeated draws while paused, and room-end cleanup. RGB differences
must be at most 2/255. The reduced darkness reference rounds after each blend,
matching RGBA8 GPU storage; the original-resolution reference retains the legacy
software truncation. Audit frame advancement is solely a functional check.

No automated performance scenario is part of this change. Build and functional
checks do not establish the 30 FPS acceptance target. User acceptance still
requires normal gameplay at both listed positions for at least 30 seconds each,
camera movement, entering/leaving darkness, and fluid-heavy scenes.
