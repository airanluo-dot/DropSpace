// Original eight-second ambient loop for the website illustration. No recordings
// or third-party melody. Its analytic amplitude envelope is mirrored by lyrics-demo.js.
import {writeFile} from 'node:fs/promises';
const sampleRate=24000, seconds=8, count=sampleRate*seconds;
const bytes=Buffer.alloc(44+count*2);
bytes.write('RIFF',0);bytes.writeUInt32LE(bytes.length-8,4);bytes.write('WAVEfmt ',8);
bytes.writeUInt32LE(16,16);bytes.writeUInt16LE(1,20);bytes.writeUInt16LE(1,22);
bytes.writeUInt32LE(sampleRate,24);bytes.writeUInt32LE(sampleRate*2,28);
bytes.writeUInt16LE(2,32);bytes.writeUInt16LE(16,34);bytes.write('data',36);bytes.writeUInt32LE(count*2,40);
for(let j=0;j<count;j++) {
 const t=j/sampleRate,beat=(.5+.5*Math.cos(t*Math.PI*2))**6,slow=.5+.5*Math.sin(t*Math.PI/2);
 const envelope=(.22+.50*beat+.18*slow)*Math.min(1,t/.12,(8-t)/.16);
 const sample=[220,277.1826,329.6276,415.3047].reduce((v,f)=>v+Math.sin(2*Math.PI*f*t+.12*Math.sin(t*Math.PI/2)),0)/4;
 bytes.writeInt16LE(Math.round(sample*envelope*.22*32767),44+j*2);
}
await writeFile(new URL('../src/assets/lyrics-demo.wav',import.meta.url),bytes);
