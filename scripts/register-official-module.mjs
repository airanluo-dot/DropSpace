import fs from 'node:fs';
import {createHash} from 'node:crypto';

// Produce the two host/catalog copies from the same real immutable official ZIP.
const [archive,id,version] = process.argv.slice(2);
if (!/^[a-z][a-z0-9.-]{0,79}$/.test(id ?? '') || !/^\d+\.\d+\.\d+$/.test(version ?? ''))
  throw Error('Usage: node scripts/register-official-module.mjs <ZIP> <module-id> <version>');
const data = fs.readFileSync(archive);
const item = {id, version,
  url:`https://github.com/airanluo-dot/DropSpace/releases/download/dlc-${id}-${version}/${id}-${version}-win-x64.zip`,
  bytes:data.length, sha256:createHash('sha256').update(data).digest('hex')};
const file = 'modules/catalog.json';
const catalog = fs.existsSync(file) ? JSON.parse(fs.readFileSync(file,'utf8')) : {schemaVersion:1,packages:[]};
catalog.packages = catalog.packages.map(p => Object.fromEntries(Object.entries(p).map(([k,v]) => [k[0].toLowerCase()+k.slice(1),v])));
const previous = catalog.packages.find(p => p.id === id);
if (previous?.version === version && JSON.stringify(previous) !== JSON.stringify(item))
  throw Error('An existing module version must not be replaced with different bytes. Publish a new version.');
catalog.packages = catalog.packages.filter(p => p.id !== id).concat(item).sort((a,b) => a.id.localeCompare(b.id));
for (const destination of [file,'src/DropSpace.Infrastructure/Dlc/official-modules.json'])
  fs.writeFileSync(destination,JSON.stringify(catalog,null,2)+'\n');
console.log(JSON.stringify(item));
