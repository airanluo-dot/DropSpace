const MAX_ITEMS = 100;
const MAX_BYTES = 2 * 1024 * 1024 * 1024;
const MAX_OBJECT_BYTES = 6 * 1024 * 1024;
const CHUNK_PLAIN_BYTES = 5 * 1024 * 1024;
const AUTH_TAG_BYTES = 16;
const MAX_MANIFEST_BYTES = 512 * 1024;
const MAX_TTL_SECONDS = 7 * 24 * 60 * 60;
const MIN_TTL_SECONDS = 60;
const RESERVATION_TTL_MS = 10 * 60 * 1000;
const MAX_CHUNK_INDEX = Math.ceil(MAX_BYTES / CHUNK_PLAIN_BYTES) - 1;
const MAX_COORDINATOR_OBJECTS = MAX_ITEMS + Math.ceil(MAX_BYTES / CHUNK_PLAIN_BYTES) + 1;

export default {
  async fetch(request, env) {
    const url = new URL(request.url);
    if (request.method === "OPTIONS") return cors(new Response(null, { status: 204 }));

    try {
      if (request.method === "POST" && url.pathname === "/v1/shares") return createShare(request, env);
      const objectMatch = url.pathname.match(/^\/v1\/shares\/([0-9a-f]{32})\/objects\/([A-Za-z0-9._-]{1,180})$/);
      if (objectMatch && request.method === "PUT") return putObject(request, env, objectMatch[1], objectMatch[2]);
      if (objectMatch && request.method === "GET") return getObject(request, env, objectMatch[1], objectMatch[2]);
      const shareMatch = url.pathname.match(/^\/v1\/shares\/([0-9a-f]{32})$/);
      if (shareMatch && request.method === "DELETE") return revokeShare(request, env, shareMatch[1]);
      const receiverMatch = url.pathname.match(/^\/s\/([0-9a-f]{32})$/);
      if (receiverMatch && request.method === "GET") return receiverPage(env, receiverMatch[1], request);
      return json({ error: "not-found" }, 404);
    } catch (error) {
      // Do not log request URLs: the key is carried in a fragment in normal use, but never put
      // secrets or request bodies in a provider log if a platform-level request logger is enabled.
      const status = error instanceof HttpError ? error.status : 500;
      return json({ error: error instanceof HttpError ? error.code : "request-failed" }, status);
    }
  },
};

async function createShare(request, env) {
  requireHttps(request);
  const body = await readJson(request, 16 * 1024);
  if (!body || typeof body !== "object" || Array.isArray(body)) throw new HttpError("request-invalid", 400);
  const shareId = String(body.shareId || "").replaceAll("-", "").toLowerCase();
  if (!/^[0-9a-f]{32}$/.test(shareId)) throw new HttpError("share-id-invalid", 400);
  const expiresAt = Date.parse(body.expiresAtUtc || "");
  const now = Date.now();
  if (!Number.isFinite(expiresAt) || expiresAt <= now + MIN_TTL_SECONDS * 1000 || expiresAt > now + MAX_TTL_SECONDS * 1000) throw new HttpError("expiry-invalid", 400);
  const itemCount = Number(body.itemCount);
  const totalBytes = Number(body.totalBytes);
  if (!Number.isInteger(itemCount) || itemCount < 1 || itemCount > MAX_ITEMS || !Number.isSafeInteger(totalBytes) || totalBytes < 1 || totalBytes > MAX_BYTES) throw new HttpError("limits-invalid", 400);
  const token = await sign({ shareId, expiresAt, itemCount, totalBytes }, env.UPLOAD_TOKEN_SECRET);
  const meta = { shareId, expiresAt, itemCount, totalBytes };
  try {
    await env.SHARES.put(metaKey(shareId), JSON.stringify(meta), { httpMetadata: { contentType: "application/json", cacheControl: "no-store" } });
    await coordinatorRequest(env, shareId, "init", meta);
  } catch (error) {
    await env.SHARES.delete(metaKey(shareId)).catch(() => {});
    await coordinatorRequest(env, shareId, "revoke").catch(() => {});
    throw error;
  }
  const origin = publicOrigin(env, request);
  return json({
    uploadBaseUrl: origin + "/v1/shares/" + shareId + "/objects/",
    downloadBaseUrl: origin,
    uploadAuthorization: "Bearer " + token,
    revokeUrl: origin + "/v1/shares/" + shareId,
  });
}


async function putObject(request, env, shareId, objectName) {
  requireHttps(request);
  const meta = await loadMeta(env, shareId);
  const claims = await verifyBearer(request, env.UPLOAD_TOKEN_SECRET);
  if (!sameShareClaims(claims, meta)) throw new HttpError("not-authorized", 401);
  const length = Number(request.headers.get("content-length"));
  if (!Number.isSafeInteger(length) || length < 1) throw new HttpError("object-length-invalid", 400);
  if (!request.body) throw new HttpError("body-missing", 400);
  const descriptor = describeUploadObject(objectName, length);
  const reservation = await coordinatorRequest(env, shareId, "reserve", descriptor);
  const key = objectKey(shareId, objectName);
  let putStarted = false;
  try {
    const existing = await env.SHARES.head(key);
    if (existing) throw new HttpError("object-exists", 409);
    putStarted = true;
    await env.SHARES.put(key, request.body, {
      httpMetadata: { contentType: request.headers.get("content-type") || "application/octet-stream", cacheControl: "no-store" },
      customMetadata: { expiresAt: String(meta.expiresAt), shareId },
    });
    await coordinatorRequest(env, shareId, "commit", { reservationId: reservation.reservationId });
    return cors(new Response(null, { status: 201 }));
  } catch (error) {
    await coordinatorRequest(env, shareId, "rollback", { reservationId: reservation.reservationId }).catch(() => {});
    if (putStarted) await env.SHARES.delete(key).catch(() => {});
    throw error;
  }
}

function describeUploadObject(objectName, length) {
  if (objectName === "manifest.bin") {
    if (length > MAX_MANIFEST_BYTES) throw new HttpError("manifest-too-large", 413);
    return { objectName, kind: "manifest", plainBytes: 0 };
  }
  const match = objectName.match(/^([0-9a-f]{32})\.([0-9]+)\.bin$/);
  if (!match) throw new HttpError("object-name-invalid", 400);
  const index = Number(match[2]);
  if (!Number.isSafeInteger(index) || index < 0 || index > MAX_CHUNK_INDEX) throw new HttpError("chunk-index-invalid", 400);
  if (length <= AUTH_TAG_BYTES || length > MAX_OBJECT_BYTES) throw new HttpError("chunk-size-invalid", 413);
  return { objectName, kind: "chunk", plainBytes: length - AUTH_TAG_BYTES, fileId: match[1], index };
}


async function getObject(request, env, shareId, objectName) {
  requireHttps(request);
  const meta = await loadMeta(env, shareId);
  if (meta.expiresAt <= Date.now()) throw new HttpError("share-expired", 410);
  const object = await env.SHARES.get(objectKey(shareId, objectName));
  if (!object) throw new HttpError("not-found", 404);
  const headers = new Headers({ "Cache-Control": "no-store", "Content-Type": object.httpMetadata?.contentType || "application/octet-stream", "X-Content-Type-Options": "nosniff" });
  return cors(new Response(object.body, { headers }));
}

async function revokeShare(request, env, shareId) {
  requireHttps(request);
  const meta = await loadMeta(env, shareId);
  const claims = await verifyBearer(request, env.UPLOAD_TOKEN_SECRET);
  if (!sameShareClaims(claims, meta)) throw new HttpError("not-authorized", 401);
  await coordinatorRequest(env, shareId, "revoke");
  let cursor;
  do {
    const listed = await env.SHARES.list({ prefix: "shares/" + shareId + "/", limit: 1000, ...(cursor ? { cursor } : {}) });
    await Promise.all(listed.objects.map(object => env.SHARES.delete(object.key)));
    if (listed.truncated && !listed.cursor) throw new HttpError("listing-incomplete", 503);
    cursor = listed.truncated ? listed.cursor : undefined;
  } while (cursor);
  await env.SHARES.delete(metaKey(shareId));
  return cors(new Response(null, { status: 204 }));
}


async function receiverPage(env, shareId, request) {
  requireHttps(request);
  const meta = await loadMeta(env, shareId);
  if (meta.expiresAt <= Date.now()) throw new HttpError("share-expired", 410);
  const origin = publicOrigin(env, request);
  const nonce = toB64(crypto.getRandomValues(new Uint8Array(16)));
  const script = receiverScript(origin, shareId);
  const html = "<!doctype html><html lang=\"en\"><head><meta charset=\"utf-8\"><meta name=\"viewport\" content=\"width=device-width,initial-scale=1\"><meta http-equiv=\"Content-Security-Policy\" content=\"default-src 'none'; script-src 'nonce-" + nonce + "'; style-src 'nonce-" + nonce + "'; connect-src " + origin + "; img-src blob:;\"><title>DropSpace Secure Share</title><style nonce=\"" + nonce + "\">body{font:16px system-ui;max-width:48rem;margin:3rem auto;padding:0 1rem}li{margin:.75rem 0}small{color:#666}</style></head><body><h1>DropSpace Secure Share</h1><p id=\"status\">Decrypting the encrypted manifest locally…</p><ul id=\"files\"></ul><script nonce=\"" + nonce + "\">" + script + "</script></body></html>";
  return cors(new Response(html, { headers: { "Content-Type": "text/html; charset=utf-8", "Cache-Control": "no-store", "X-Content-Type-Options": "nosniff", "Referrer-Policy": "no-referrer", "Cross-Origin-Resource-Policy": "same-origin" } }));
}


function receiverScript(origin, shareId) {
  return `(async()=>{const status=document.getElementById('status'),list=document.getElementById('files');const keyText=location.hash.startsWith('#k=')?location.hash.slice(3):'';if(!keyText){status.textContent='The decryption key is missing from the URL fragment.';return;}try{const key=fromB64(keyText),manifestBin=await get('/v1/shares/${shareId}/objects/manifest.bin'),nonce=manifestBin.slice(0,12),tag=manifestBin.slice(-16),cipher=manifestBin.slice(12,-16),manifestKey=await hkdf(key,uuidBytes('${shareId}'),'manifest'),plain=await crypto.subtle.decrypt({name:'AES-GCM',iv:nonce,additionalData:enc('DropSpaceShare:v1\\n${shareId}')},manifestKey,concat(cipher,tag)),manifest=JSON.parse(new TextDecoder().decode(plain));if(manifest.shareId.replaceAll('-','')!=='${shareId}')throw Error('share mismatch');for(const item of manifest.items){const li=document.createElement('li'),button=document.createElement('button');button.textContent='Download '+item.displayName+' ('+item.plainLength+' bytes)';button.onclick=async()=>{button.disabled=true;try{await download('${shareId}',key,item)}catch(e){alert(e.message)}finally{button.disabled=false}};li.appendChild(button);list.appendChild(li)}status.textContent='The manifest was decrypted in this browser. Files remain encrypted until download.';}catch(e){status.textContent='Unable to decrypt or validate this share: '+e.message;}})();
const SHA256_K = new Uint32Array([
  0x428a2f98,0x71374491,0xb5c0fbcf,0xe9b5dba5,0x3956c25b,0x59f111f1,0x923f82a4,0xab1c5ed5,
  0xd807aa98,0x12835b01,0x243185be,0x550c7dc3,0x72be5d74,0x80deb1fe,0x9bdc06a7,0xc19bf174,
  0xe49b69c1,0xefbe4786,0x0fc19dc6,0x240ca1cc,0x2de92c6f,0x4a7484aa,0x5cb0a9dc,0x76f988da,
  0x983e5152,0xa831c66d,0xb00327c8,0xbf597fc7,0xc6e00bf3,0xd5a79147,0x06ca6351,0x14292967,
  0x27b70a85,0x2e1b2138,0x4d2c6dfc,0x53380d13,0x650a7354,0x766a0abb,0x81c2c92e,0x92722c85,
  0xa2bfe8a1,0xa81a664b,0xc24b8b70,0xc76c51a3,0xd192e819,0xd6990624,0xf40e3585,0x106aa070,
  0x19a4c116,0x1e376c08,0x2748774c,0x34b0bcb5,0x391c0cb3,0x4ed8aa4a,0x5b9cca4f,0x682e6ff3,
  0x748f82ee,0x78a5636f,0x84c87814,0x8cc70208,0x90befffa,0xa4506ceb,0xbef9a3f7,0xc67178f2
]);

class Sha256 {
  constructor() {
    this.h = new Uint32Array([0x6a09e667,0xbb67ae85,0x3c6ef372,0xa54ff53a,0x510e527f,0x9b05688c,0x1f83d9ab,0x5be0cd19]);
    this.buffer = new Uint8Array(64);
    this.words = new Uint32Array(64);
    this.bufferLength = 0;
    this.bytes = 0;
  }

  update(input) {
    const data = input instanceof Uint8Array ? input : new Uint8Array(input);
    this.bytes += data.length;
    let offset = 0;
    if (this.bufferLength) {
      const needed = 64 - this.bufferLength;
      const copied = Math.min(needed, data.length);
      this.buffer.set(data.subarray(0, copied), this.bufferLength);
      this.bufferLength += copied;
      offset = copied;
      if (this.bufferLength === 64) {
        this.process(this.buffer, 0);
        this.bufferLength = 0;
      }
    }
    while (offset + 64 <= data.length) {
      this.process(data, offset);
      offset += 64;
    }
    if (offset < data.length) {
      this.buffer.set(data.subarray(offset));
      this.bufferLength = data.length - offset;
    }
    return this;
  }

  hex() {
    const paddedLength = this.bufferLength < 56 ? 64 : 128;
    const padded = new Uint8Array(paddedLength);
    padded.set(this.buffer.subarray(0, this.bufferLength));
    padded[this.bufferLength] = 0x80;
    const bitLength = this.bytes * 8;
    const view = new DataView(padded.buffer);
    view.setUint32(paddedLength - 8, Math.floor(bitLength / 0x100000000) >>> 0);
    view.setUint32(paddedLength - 4, bitLength >>> 0);
    for (let offset = 0; offset < padded.length; offset += 64) this.process(padded, offset);
    return [...this.h].map(value => value.toString(16).padStart(8, "0")).join("");
  }

  process(data, offset) {
    const words = this.words;
    for (let i = 0; i < 16; i++) {
      const p = offset + i * 4;
      words[i] = (data[p] << 24) | (data[p + 1] << 16) | (data[p + 2] << 8) | data[p + 3];
    }
    for (let i = 16; i < 64; i++) {
      const value = words[i - 15];
      const s0 = rotateRight(value, 7) ^ rotateRight(value, 18) ^ (value >>> 3);
      const prior = words[i - 2];
      const s1 = rotateRight(prior, 17) ^ rotateRight(prior, 19) ^ (prior >>> 10);
      words[i] = (words[i - 16] + s0 + words[i - 7] + s1) >>> 0;
    }
    let [a,b,c,d,e,f,g,h] = this.h;
    for (let i = 0; i < 64; i++) {
      const s1 = rotateRight(e, 6) ^ rotateRight(e, 11) ^ rotateRight(e, 25);
      const choose = (e & f) ^ (~e & g);
      const t1 = (h + s1 + choose + SHA256_K[i] + words[i]) >>> 0;
      const s0 = rotateRight(a, 2) ^ rotateRight(a, 13) ^ rotateRight(a, 22);
      const majority = (a & b) ^ (a & c) ^ (b & c);
      const t2 = (s0 + majority) >>> 0;
      h=g; g=f; f=e; e=(d+t1)>>>0; d=c; c=b; b=a; a=(t1+t2)>>>0;
    }
    this.h[0] = (this.h[0] + a) >>> 0; this.h[1] = (this.h[1] + b) >>> 0;
    this.h[2] = (this.h[2] + c) >>> 0; this.h[3] = (this.h[3] + d) >>> 0;
    this.h[4] = (this.h[4] + e) >>> 0; this.h[5] = (this.h[5] + f) >>> 0;
    this.h[6] = (this.h[6] + g) >>> 0; this.h[7] = (this.h[7] + h) >>> 0;
  }
}

const rotateRight = (value, bits) => (value >>> bits) | (value << (32 - bits));
let fallbackDownloadActive = false;

async function download(id,master,item){
  const plainLength = Number(item.plainLength);
  if (!Number.isSafeInteger(plainLength) || plainLength < 0) throw Error("invalid file length");
  const canStreamToFile = typeof window.showSaveFilePicker === "function";
  if (!canStreamToFile && plainLength > 256 * 1024 * 1024) throw Error("This browser cannot safely download files larger than 256 MiB. Use a browser with file streaming support.");
  if (!canStreamToFile) {
    if (fallbackDownloadActive) throw Error("Another in-memory download is already in progress. Wait for it to finish before starting another.");
    fallbackDownloadActive = true;
  }
  let writable = null;
  const out = canStreamToFile ? null : [];
  if (canStreamToFile) {
    const handle = await window.showSaveFilePicker({suggestedName:String(item.displayName || "download")});
    writable = await handle.createWritable();
  }
  const hash = new Sha256();
  let written = 0;
  try {
    for(let i=0;i<item.chunkCount;i++){
      const packed=await get("/v1/shares/"+id+"/objects/"+item.fileId.replaceAll("-","")+"."+i+".bin"),cipher=packed.slice(0,-16),tag=packed.slice(-16),fileKey=await hkdf(master,uuidBytes(id),"file:"+item.fileId.replaceAll("-","")),nonce=new Uint8Array(12);
      nonce.set(fromB64(item.noncePrefix),0);
      new DataView(nonce.buffer).setUint32(8,i,false);
      const plain=await crypto.subtle.decrypt({name:"AES-GCM",iv:nonce,additionalData:enc("DropSpaceShare:v1\\n"+id+"\\n"+item.fileId.replaceAll("-","")+"\\n"+i+"\\n"+Math.min(5*1024*1024,plainLength-i*5*1024*1024))},fileKey,concat(cipher,tag));
      const bytes=new Uint8Array(plain);
      written += bytes.length;
      if (written > plainLength) throw Error("decrypted file exceeds the declared length");
      hash.update(bytes);
      if (writable) await writable.write(bytes); else out.push(bytes);
    }
    if (written !== plainLength || hash.hex() !== item.sha256.toLowerCase()) throw Error("integrity check failed");
    if (writable) { await writable.close(); writable = null; }
    else { const blob=new Blob(out,{type:item.mimeType}); const a=document.createElement("a"); a.href=URL.createObjectURL(blob); a.download=item.displayName; a.click(); setTimeout(()=>URL.revokeObjectURL(a.href),60000); }
  } catch (error) {
    if (writable) await writable.abort().catch(()=>{});
    throw error;
  } finally {
    if (!canStreamToFile) fallbackDownloadActive = false;
  }
}
async function get(path){const r=await fetch(path,{cache:"no-store"});if(!r.ok)throw Error("share object unavailable ("+r.status+")");return new Uint8Array(await r.arrayBuffer())}
function enc(s){return new TextEncoder().encode(s)}function concat(a,b){const x=new Uint8Array(a.length+b.length);x.set(a);x.set(b,a.length);return x}function fromB64(s){const p=s.replaceAll('-','+').replaceAll('_','/')+'='.repeat((4-s.length%4)%4);const raw=atob(p),x=new Uint8Array(raw.length);for(let i=0;i<raw.length;i++)x[i]=raw.charCodeAt(i);return x}function uuidBytes(h){const b=fromHex(h);return new Uint8Array([b[3],b[2],b[1],b[0],b[5],b[4],b[7],b[6],b[8],b[9],b[10],b[11],b[12],b[13],b[14],b[15]])}function fromHex(s){const x=new Uint8Array(s.length/2);for(let i=0;i<x.length;i++)x[i]=parseInt(s.slice(i*2,i*2+2),16);return x}function hex(b){return [...new Uint8Array(b)].map(x=>x.toString(16).padStart(2,'0')).join('')}async function hkdf(master,salt,info){const k=await crypto.subtle.importKey('raw',master,'HKDF',false,['deriveKey']);return crypto.subtle.deriveKey({name:'HKDF',hash:'SHA-256',salt,info:enc(info)},k,{name:'AES-GCM',length:256},false,['decrypt'])}`;
}

async function loadMeta(env, shareId) {
  const object = await env.SHARES.get(metaKey(shareId));
  if (!object) throw new HttpError("not-found", 404);
  let meta;
  try {
    meta = JSON.parse(await object.text());
  } catch {
    throw new HttpError("metadata-invalid", 500);
  }
  if (!meta || meta.shareId !== shareId ||
      !Number.isSafeInteger(meta.expiresAt) ||
      !Number.isInteger(meta.itemCount) || meta.itemCount < 1 || meta.itemCount > MAX_ITEMS ||
      !Number.isSafeInteger(meta.totalBytes) || meta.totalBytes < 1 || meta.totalBytes > MAX_BYTES) {
    throw new HttpError("metadata-invalid", 500);
  }
  if (meta.expiresAt <= Date.now()) throw new HttpError("share-expired", 410);
  return meta;
}


async function verifyBearer(request, secret) {
  const header = request.headers.get("authorization") || "";
  if (!header.startsWith("Bearer ")) throw new HttpError("not-authorized", 401);
  const token = header.slice(7);
  const dot = token.indexOf(".");
  if (dot <= 0) throw new HttpError("not-authorized", 401);
  const encoded = token.slice(0, dot), signature = token.slice(dot + 1);
  const expected = await hmac(secret, encoded);
  if (!constantTime(expected, signature)) throw new HttpError("not-authorized", 401);
  let claims;
  try {
    claims = JSON.parse(new TextDecoder().decode(fromB64(encoded)));
  } catch {
    throw new HttpError("not-authorized", 401);
  }
  if (!claims || typeof claims.shareId !== "string" || !/^[0-9a-f]{32}$/.test(claims.shareId) ||
      !Number.isSafeInteger(claims.expiresAt) || !Number.isInteger(claims.itemCount) ||
      !Number.isSafeInteger(claims.totalBytes)) {
    throw new HttpError("not-authorized", 401);
  }
  if (claims.expiresAt <= Date.now()) throw new HttpError("share-expired", 410);
  return claims;
}


async function sign(claims, secret) {
  const encoded = toB64(new TextEncoder().encode(JSON.stringify(claims)));
  return `${encoded}.${await hmac(secret, encoded)}`;
}

async function hmac(secret, text) {
  if (typeof secret !== "string" || secret.length < 32) throw new HttpError("secret-unavailable", 503);
  const key = await crypto.subtle.importKey("raw", new TextEncoder().encode(secret), { name: "HMAC", hash: "SHA-256" }, false, ["sign"]);
  return toB64(new Uint8Array(await crypto.subtle.sign("HMAC", key, new TextEncoder().encode(text))));
}

function constantTime(a, b) { try { const x = fromB64(a), y = fromB64(b); if (x.length !== y.length) return false; let d = 0; for (let i = 0; i < x.length; i++) d |= x[i] ^ y[i]; return d === 0; } catch { return false; } }
function toB64(bytes) { let s = ""; for (const b of bytes) s += String.fromCharCode(b); return btoa(s).replaceAll("+", "-").replaceAll("/", "_").replaceAll("=", ""); }
function metaKey(id) { return `shares/${id}/meta.json`; }
function objectKey(id, name) { return `shares/${id}/${name}`; }
function requireHttps(request) { if (new URL(request.url).protocol !== "https:") throw new HttpError("https-required", 400); }
function publicOrigin(env, request) {
  const value = String(env.PUBLIC_ORIGIN || new URL(request.url).origin).replace(/\/$/, "");
  let origin;
  try { origin = new URL(value); } catch { throw new HttpError("origin-invalid", 500); }
  if (origin.protocol !== "https:" || origin.username || origin.password || origin.search || origin.hash || origin.pathname !== "/") throw new HttpError("origin-invalid", 500);
  return origin.origin;
}
async function readJson(request, maximum) { const text = await request.text(); if (text.length > maximum) throw new HttpError("body-too-large", 413); try { return JSON.parse(text); } catch { throw new HttpError("json-invalid", 400); } }
function json(value, status = 200) { return cors(new Response(JSON.stringify(value), { status, headers: { "Content-Type": "application/json", "Cache-Control": "no-store" } })); }
function cors(response) { response.headers.set("Access-Control-Allow-Origin", "*"); response.headers.set("Access-Control-Allow-Methods", "GET,POST,PUT,DELETE,OPTIONS"); response.headers.set("Access-Control-Allow-Headers", "Authorization,Content-Type"); return response; }
function sameShareClaims(claims, meta) {
  return claims.shareId === meta.shareId &&
    claims.expiresAt === meta.expiresAt &&
    claims.itemCount === meta.itemCount &&
    claims.totalBytes === meta.totalBytes;
}

async function coordinatorRequest(env, shareId, operation, payload = {}) {
  const binding = env.SHARE_COORDINATOR;
  if (!binding || typeof binding.idFromName !== "function" || typeof binding.get !== "function") {
    throw new HttpError("coordinator-unavailable", 503);
  }
  const stub = binding.get(binding.idFromName(shareId));
  const response = await stub.fetch("https://dropspace-coordinator/" + operation, {
    method: "POST",
    headers: { "Content-Type": "application/json" },
    body: JSON.stringify({ operation, shareId, ...payload }),
  });
  let result = {};
  try {
    result = await response.json();
  } catch {
    result = {};
  }
  if (!response.ok) {
    const status = response.status >= 400 && response.status <= 599 ? response.status : 503;
    throw new HttpError(result.error || "coordinator-failed", status);
  }
  return result;
}

function coordinatorJson(value, status = 200) {
  return new Response(JSON.stringify(value), {
    status,
    headers: { "Content-Type": "application/json", "Cache-Control": "no-store" },
  });
}

function cleanupCoordinatorState(state) {
  const now = Date.now();
  for (const [reservationId, reservation] of Object.entries(state.pending || {})) {
    if (!reservation || reservation.expiresAt <= now) delete state.pending[reservationId];
  }
  return state;
}

export class ShareUsageCoordinator {
  constructor(state) {
    this.state = state;
  }

  async fetch(request) {
    try {
      const body = await request.json();
      return await this.state.blockConcurrencyWhile(async () => {
        if (!body || typeof body !== "object" || Array.isArray(body) ||
            typeof body.operation !== "string" || typeof body.shareId !== "string" ||
            !/^[0-9a-f]{32}$/.test(body.shareId)) {
          throw new HttpError("coordinator-request-invalid", 400);
        }
        let current = await this.state.storage.get("state");
        if (body.operation === "init") {
          if (!Number.isSafeInteger(body.expiresAt) ||
              !Number.isInteger(body.itemCount) || body.itemCount < 1 || body.itemCount > MAX_ITEMS ||
              !Number.isSafeInteger(body.totalBytes) || body.totalBytes < 1 || body.totalBytes > MAX_BYTES) {
            throw new HttpError("coordinator-metadata-invalid", 400);
          }
          if (current) {
            if (current.shareId !== body.shareId ||
                current.expiresAt !== body.expiresAt ||
                current.itemCount !== body.itemCount ||
                current.totalBytes !== body.totalBytes) {
              throw new HttpError("coordinator-conflict", 409);
            }
            return coordinatorJson({ ok: true });
          }
          current = {
            shareId: body.shareId,
            expiresAt: body.expiresAt,
            itemCount: body.itemCount,
            totalBytes: body.totalBytes,
            committedPlainBytes: 0,
            manifestUploaded: false,
            objects: {},
            files: {},
            pending: {},
            revoked: false,
          };
          await this.state.storage.put("state", current);
          return coordinatorJson({ ok: true });
        }

        if (!current || current.shareId !== body.shareId) throw new HttpError("coordinator-not-found", 404);
        current = cleanupCoordinatorState(current);
        if (body.operation === "reserve") return this.reserve(current, body);
        if (body.operation === "commit") return this.commit(current, body);
        if (body.operation === "rollback") return this.rollback(current, body);
        if (body.operation === "revoke") return this.revoke(current);
        throw new HttpError("coordinator-operation-invalid", 400);
      });
    } catch (error) {
      const status = error instanceof HttpError ? error.status : 500;
      return coordinatorJson({ error: error instanceof HttpError ? error.code : "coordinator-failed" }, status);
    }
  }

  async reserve(current, body) {
    if (current.revoked || current.expiresAt <= Date.now()) throw new HttpError("share-expired", 410);
    const objectName = String(body.objectName || "");
    const kind = objectName === "manifest.bin" ? "manifest" : "chunk";
    const plainBytes = Number(body.plainBytes);
    if (objectName.length < 1 || objectName.length > 180 ||
        !/^[A-Za-z0-9._-]+$/.test(objectName) ||
        objectName === "." || objectName === ".." ||
        body.kind !== kind || !Number.isSafeInteger(plainBytes) || plainBytes < 0) {
      throw new HttpError("coordinator-object-invalid", 400);
    }
    if (kind === "manifest") {
      if (plainBytes !== 0 || current.manifestUploaded || Object.values(current.pending).some(item => item?.objectName === objectName)) {
        throw new HttpError("object-exists", 409);
      }
    } else {
      const match = objectName.match(/^([0-9a-f]{32})\.([0-9]+)\.bin$/);
      const index = Number(body.index);
      if (!match || body.fileId !== match[1] ||
          !Number.isSafeInteger(index) || index < 0 || index > MAX_CHUNK_INDEX ||
          String(index) !== match[2] ||
          plainBytes < 1 || plainBytes > MAX_OBJECT_BYTES - AUTH_TAG_BYTES) {
        throw new HttpError("coordinator-object-invalid", 400);
      }
      if (current.objects[objectName] || Object.values(current.pending).some(item => item?.objectName === objectName)) {
        throw new HttpError("object-exists", 409);
      }
      const file = current.files[match[1]];
      if ((!file && index !== 0) || (file && file.nextIndex !== index)) {
        throw new HttpError("chunk-order-invalid", 409);
      }
      const pendingFileIds = new Set(
        Object.values(current.pending)
          .filter(item => item?.kind === "chunk" && item.index === 0 && typeof item.fileId === "string")
          .map(item => item.fileId)
      );
      if (!file && !pendingFileIds.has(match[1]) &&
          Object.keys(current.files).length + pendingFileIds.size >= current.itemCount) {
        throw new HttpError("item-limit-exceeded", 413);
      }
    }
    const objectCount = Object.keys(current.objects).length + Object.keys(current.pending).length;
    if (objectCount >= MAX_COORDINATOR_OBJECTS) throw new HttpError("object-limit-exceeded", 413);
    const pendingBytes = Object.values(current.pending).reduce((sum, item) => sum + item.plainBytes, 0);
    if (current.committedPlainBytes + pendingBytes + plainBytes > current.totalBytes) {
      throw new HttpError("byte-limit-exceeded", 413);
    }
    const reservationId = crypto.randomUUID();
    current.pending[reservationId] = {
      objectName,
      kind,
      plainBytes,
      fileId: kind === "chunk" ? body.fileId : undefined,
      index: kind === "chunk" ? Number(body.index) : undefined,
      expiresAt: Math.min(current.expiresAt, Date.now() + RESERVATION_TTL_MS),
    };
    await this.state.storage.put("state", current);
    return coordinatorJson({ ok: true, reservationId });
  }

  async commit(current, body) {
    const reservationId = String(body.reservationId || "");
    const reservation = current.pending[reservationId];
    if (!reservation) throw new HttpError("reservation-missing", 409);
    if (reservation.expiresAt <= Date.now()) {
      delete current.pending[reservationId];
      await this.state.storage.put("state", current);
      throw new HttpError("reservation-expired", 409);
    }
    current.objects[reservation.objectName] = {
      kind: reservation.kind,
      plainBytes: reservation.plainBytes,
      fileId: reservation.fileId,
      index: reservation.index,
    };
    current.committedPlainBytes += reservation.plainBytes;
    if (reservation.kind === "manifest") {
      current.manifestUploaded = true;
    } else {
      const file = current.files[reservation.fileId] || { nextIndex: 0 };
      if (file.nextIndex !== reservation.index) throw new HttpError("chunk-order-invalid", 409);
      file.nextIndex += 1;
      current.files[reservation.fileId] = file;
    }
    delete current.pending[reservationId];
    await this.state.storage.put("state", current);
    return coordinatorJson({ ok: true });
  }

  async rollback(current, body) {
    const reservationId = String(body.reservationId || "");
    if (current.pending[reservationId]) delete current.pending[reservationId];
    await this.state.storage.put("state", current);
    return coordinatorJson({ ok: true });
  }

  async revoke(current) {
    current.revoked = true;
    current.pending = {};
    await this.state.storage.put("state", current);
    return coordinatorJson({ ok: true });
  }
}

class HttpError extends Error { constructor(code, status) { super(code); this.code = code; this.status = status; } }
