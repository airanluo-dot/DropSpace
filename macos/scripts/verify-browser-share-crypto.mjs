// Execute the actual frozen receiver script against public native test vectors.
// DOM/file-picker adapters here are a protocol harness, not browser UI evidence.
import assert from 'node:assert/strict';
import { readFile } from 'node:fs/promises';
import { execFileSync } from 'node:child_process';
import vm from 'node:vm';
import { webcrypto } from 'node:crypto';
import { fileURLToPath } from 'node:url';
const root=fileURLToPath(new URL('../..',import.meta.url));
const baseline='1cd4620dd58a67b0963b73487743137813b8864a';
const fixed=path=>execFileSync('git',['show',baseline+':'+path],{cwd:root,encoding:'utf8'});
const source=fixed('share-worker/src/index.js');
const protocol=fixed('share-worker/src/protocol.js').replace(/^export /gm,'');
const receiver=source.slice(source.indexOf('function receiverScript('),source.indexOf('async function loadMeta('));
const fixture=JSON.parse(await readFile(new URL('../Tests/DropSpaceInfrastructureTests/Fixtures/share-crypto-baseline.json',import.meta.url),'utf8'));
const native=JSON.parse(await readFile(process.argv[2],'utf8'));
const share=fixture.share.replaceAll('-',''); const file=fixture.file.replaceAll('-','');
const script=vm.runInNewContext(protocol+'\n'+receiver+'\nreceiverScript("https://fixture.invalid",'+JSON.stringify(share)+')');
let notifyStatus; const finished=new Promise(resolve=>{notifyStatus=resolve;});
const status={set textContent(value){notifyStatus(value);}};
const buttons=[],output=[]; let closed=false;
const document={getElementById:id=>id==='status'?status:{appendChild(){}},createElement:tag=>{
 const element={appendChild(){}};if(tag==='button')buttons.push(element);return element;
}};
const context=vm.createContext({document,location:{hash:'#k='+fixture.fragment},TextEncoder,TextDecoder,crypto:webcrypto,atob,
 window:{showSaveFilePicker:async()=>({createWritable:async()=>({write:async bytes=>output.push(Buffer.from(bytes)),close:async()=>{closed=true;},abort:async()=>{throw Error('Unexpected receiver abort');}})})},
 alert:message=>{throw Error(message);},
 fetch:async path=>{
  assert(!path.includes(fixture.fragment),'Fragment key must not enter request path');
  let bytes;
  if(path==='/v1/shares/'+share+'/objects/manifest.bin')bytes=Buffer.from(native.manifestWire,'base64');
  else if(path==='/v1/shares/'+share+'/objects/'+file+'.0.bin')bytes=Buffer.from(native.chunkWire,'base64');
  else throw Error('Unexpected object path: '+path);
  return {ok:true,status:200,arrayBuffer:async()=>Uint8Array.from(bytes).buffer};
 }});
vm.runInContext(script,context,{timeout:5000});
let timer;
try { assert.equal(await Promise.race([finished,new Promise((_,reject)=>{timer=setTimeout(()=>reject(Error('Receiver manifest timeout')),5000);})]),'The manifest was decrypted in this browser. Files remain encrypted until download.'); }
finally {clearTimeout(timer);}
assert.equal(buttons.length,1); await buttons[0].onclick();
assert(closed); assert.deepEqual(Buffer.concat(output),Buffer.from(fixture.chunks[0].plain,'base64'));
console.log('Frozen browser receiver decrypted native manifest/chunk, checked SHA-256, and wrote the exact public fixture bytes. VM harness only; no browser UI or deployed backend claim.');
