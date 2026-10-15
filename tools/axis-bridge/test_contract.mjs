import assert from 'node:assert/strict';
import {readFile} from 'node:fs/promises';
const source = await readFile(new URL('axis_control.st', import.meta.url), 'utf8').catch(()=>'');
assert.match(source, /FUNCTION_BLOCK MC_Power/);
assert.match(source, /FUNCTION_BLOCK MC_Stop/);
assert.match(source, /AB_StopHeld/);
console.log('axis library contract exists');
