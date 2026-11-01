import json, glob, os, sys

root = sys.argv[1]
fail = 0
reports = glob.glob(os.path.join(root, "*", "matrix.json"))
for report in sorted(glob.glob(os.path.join(root, "*", "matrix.json"))):
    label = os.path.basename(os.path.dirname(report))
    try:
        data = json.load(open(report))
    except Exception as ex:
        print(label, "NO-REPORT", ex)
        fail = 1
        continue
    for case in data.get("cases", []):
        render = case.get("renderInterval", {})
        print(f"{label:24s} valid={case.get('valid')} render={render.get('meanMs', 0):6.2f} p95={render.get('p95Ms', 0):6.2f} "
              f"step={case.get('stepIncludingBuild', {}).get('meanMs', 0):6.2f} logic={case.get('logic', {}).get('meanMs', 0):5.2f} "
              f"build={case.get('build', {}).get('meanMs', 0):6.2f} wait={case.get('frameWait', {}).get('meanMs', 0):6.2f} "
              f"draw={case.get('drawCommands', {}).get('mean', 0):5.0f} allocMB={case.get('allocation', {}).get('meanBytes', 0)/1048576:5.2f} "
              f"reasons={case.get('invalidReasons')}")
        if case.get("valid") and render.get("meanMs", 0) > 33.4:
            fail = 1
    if not data.get("cases"):
        print(label, "EMPTY-REPORT")
        fail = 1
if not reports:
    print("NO-REPORTS", root)
    fail = 1
print("ACCEPTANCE", "FAIL" if fail else "PASS", "(30fps means renderInterval <= 33.4ms per case)")
sys.exit(1 if fail else 0)