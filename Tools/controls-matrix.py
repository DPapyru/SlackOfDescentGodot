"""Enumerate original event handlers and dynamic key queries without guessing bindings."""
import json
from pathlib import Path
root = Path(__file__).resolve().parents[1]
manifest = json.loads((root/'GameData/manifest.json').read_text())
code = json.loads((root/'GameData/code.json').read_text())
rows=[]
queries={'keyboard_check','keyboard_check_pressed','keyboard_check_released','mouse_check_button','mouse_check_button_pressed','mouse_check_button_released','joystick_check_button','joystick_xpos','joystick_ypos','mouse_wheel_up','mouse_wheel_down'}
def walk(n, owner):
    if not isinstance(n,list):return
    if n and n[0]=='call' and n[1] in queries:
        rows.append({'context':owner,'input':n[1],'binding':json.dumps(n[2],ensure_ascii=False),'evidence':'待专项验证'})
    for child in n:walk(child,owner)
for obj in manifest['objects']:
    for event in obj['events']:
        if event['kind'] in [5,6,9,10]:
            rows.append({'context':obj['name'],'input':f"event:{event['kind']}:{event['sub']}",'binding':json.dumps(event['actions'],ensure_ascii=False),'evidence':'已枚举；待专项验证'})
for key,tree in code.items():walk(tree,key)
(root/'docs/controls-matrix.json').write_text(json.dumps({'source':'ai d996bab GameData manifest/code; runtime dynamic bindings are global.key[0..31]','entries':rows},ensure_ascii=False,indent=2)+'\n')
print(json.dumps({'eventAndQueryCount':len(rows),'output':'docs/controls-matrix.json'}))
