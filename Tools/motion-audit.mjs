import path from 'node:path';
import { mkdirSync } from 'node:fs';
import { prepareDiagnostics, runDiagnostic } from './diagnostics.mjs';

const project=path.resolve(import.meta.dirname,'..');
const godot=process.env.GODOT_BIN ?? '/home/dpapyru/公共/Godot/Godot_v4.7.2-stable_mono_linux_x86_64/Godot_v4.7.2-stable_mono_linux.x86_64';
const output=path.resolve(process.env.SLACK_MOTION_OUTPUT ?? path.join(project,'.playtest','motion'));
mkdirSync(output,{recursive:true});
prepareDiagnostics(project);
const result=runDiagnostic(godot,[...(process.argv.includes('--headless')?['--headless']:[]),'--path',project,'--audio-driver','Dummy','--log-file',path.join(output,'godot.log'),'res://Tools/NativeMotionAudit.tscn'],{project,timeout:300000,env:{...process.env,SLACK_MOTION_PERF:path.join(output,'performance.json'),SLACK_MOTION_SHOTS:output}}).toString();
process.stdout.write(result);
if(!result.includes('MOTION_AUDIT_OK')||!result.includes('MOTION_EDITOR_AUDIT_OK'))throw new Error('Motion runtime/editor checks did not finish');
