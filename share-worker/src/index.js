const MAX_ITEMS = 100;
const MAX_BYTES = 2 * 1024 * 1024 * 1024;
const MAX_OBJECT_BYTES = 6 * 1024 * 1024;
const MAX_TTL_SECONDS = 7 * 24 * 60 * 60;
const MIN_TTL_SECONDS = 60;

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
      if (receiverMatch && request.method === "GET") return receiverPage(env, receiverMatch[1]);
      return json({ error: "not-found" }, 404);
    } catch (error) {
      // Do not log request URLs: the key is carried in a fragment in normal use, but never put
      // secrets or request bodies in a provider log if a platform-level request logger is enabled.
      return json({ error: error instanceof HttpError ? error.code : "request-failed" }, error instanceof HttpError ? error.status : 400);
    }
  },
};

async function createShare(request, env) {
  requireHttps(request);
  const body = await readJson(request, 16 * 1024);
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
  await env.SHARES.put(metaKey(shareId), JSON.stringify(meta), { httpMetadata: { contentType: "application/json", cacheControl: "no-store" } });
  const origin = String(env.PUBLIC_ORIGIN || new URL(request.url).origin).replace(/\/$/, "");
  return json({
    uploadBaseUrl: `${origin}/v1/shares/${shareId}/objects/`,
    downloadBaseUrl: origin,
    uploadAuthorization: `Bearer ${token}`,
  });
}

async function putObject(request, env, shareId, objectName) {
  requireHttps(request);
  const meta = await loadMeta(env, shareId);
  const claims = await verifyBearer(request, env.UPLOAD_TOKEN_SECRET);
  if (claims.shareId !== shareId || claims.expiresAt !== meta.expiresAt) throw new HttpError("not-authorized", 401);
  const length = Number(request.headers.get("content-length"));
  if (!Number.isSafeInteger(length) || length < 1 || length > MAX_OBJECT_BYTES) throw new HttpError("object-too-large", 413);
  const existing = await env.SHARES.head(objectKey(shareId, objectName));
  if (existing) throw new HttpError("object-exists", 409);
  await env.SHARES.put(objectKey(shareId, objectName), request.body, {
    httpMetadata: { contentType: request.headers.get("content-type") || "application/octet-stream", cacheControl: "no-store" },
    customMetadata: { expiresAt: String(meta.expiresAt), shareId },
  });
  return cors(new Response(null, { status: 201 }));
}

async function getObject(request, env, shareId, objectName) {
  const meta = await loadMeta(env, shareId);
  if (meta.expiresAt <= Date.now()) throw new HttpError("share-expired", 410);
  const object = await env.SHARES.get(objectKey(shareId, objectName));
  if (!object) throw new HttpError("not-found", 404);
  const headers = new Headers({ "Cache-Control": "no-store", "Content-Type": object.httpMetadata?.contentType || "application/octet-stream", "X-Content-Type-Options": "nosniff" });
  return cors(new Response(object.body, { headers }));
}

async function revokeShare(request, env, shareId) {
  const meta = await loadMeta(env, shareId);
  const claims = await verifyBearer(request, env.UPLOAD_TOKEN_SECRET);
  if (claims.shareId !== shareId || claims.expiresAt !== meta.expiresAt) throw new HttpError("not-authorized", 401);
  const listed = await env.SHARES.list({ prefix: `shares/${shareId}/` });
  await Promise.all(listed.objects.map(object => env.SHARES.delete(object.key)));
  await env.SHARES.delete(metaKey(shareId));
  return cors(new Response(null, { status: 204 }));
}

async function receiverPage(env, shareId) {
  const meta = await loadMeta(env, shareId);
  if (meta.expiresAt <= Date.now()) throw new HttpError("share-expired", 410);
  const origin = String(env.PUBLIC_ORIGIN || "").replace(/\/$/, "");
  const script = receiverScript(origin, shareId);
  const html = `<!doctype html><html lang="en"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1"><meta http-equiv="Content-Security-Policy" content="default-src 'none'; script-src 'unsafe-inline'; style-src 'unsafe-inline'; connect-src ${origin}; img-src blob:;"><title>DropSpace Secure Share</title><style>body{font:16px system-ui;max-width:48rem;margin:3rem auto;padding:0 1rem}li{margin:.75rem 0}small{color:#666}</style></head><body><h1>DropSpace Secure Share</h1><p id="status">Decrypting the encrypted manifest locally…</p><ul id="files"></ul><script>${script}</script></body></html>`;
  return cors(new Response(html, { headers: { "Content-Type": "text/html; charset=utf-8", "Cache-Control": "no-store", "X-Content-Type-Options": "nosniff" } }));
}

function receiverScript(origin, shareId) {
  return `(async()=>{const status=document.getElementById('status'),list=document.getElementById('files');const keyText=location.hash.startsWith('#k=')?location.hash.slice(3):'';if(!keyText){status.textContent='The decryption key is missing from the URL fragment.';return;}try{const key=fromB64(keyText),manifestBin=await get('/v1/shares/${shareId}/objects/manifest.bin'),nonce=manifestBin.slice(0,12),tag=manifestBin.slice(-16),cipher=manifestBin.slice(12,-16),manifestKey=await hkdf(key,uuidBytes('${shareId}'),'manifest'),plain=await crypto.subtle.decrypt({name:'AES-GCM',iv:nonce,additionalData:enc('DropSpaceShare:v1\\n${shareId}')},manifestKey,concat(cipher,tag)),manifest=JSON.parse(new TextDecoder().decode(plain));if(manifest.shareId.replaceAll('-','')!=='${shareId}')throw Error('share mismatch');for(const item of manifest.items){const li=document.createElement('li'),button=document.createElement('button');button.textContent='Download '+item.displayName+' ('+item.plainLength+' bytes)';button.onclick=()=>download('${shareId}',key,item).catch(e=>alert(e.message));li.appendChild(button);list.appendChild(li)}status.textContent='The manifest was decrypted in this browser. Files remain encrypted until download.';}catch(e){status.textContent='Unable to decrypt or validate this share: '+e.message;}})();
async function download(id,master,item){const out=[];for(let i=0;i<item.chunkCount;i++){const packed=await get('/v1/shares/'+id+'/objects/'+item.fileId.replaceAll('-','')+'.'+i+'.bin'),cipher=packed.slice(0,-16),tag=packed.slice(-16),fileKey=await hkdf(master,uuidBytes(id),'file:'+item.fileId.replaceAll('-','')),nonce=new Uint8Array(12);nonce.set(fromB64(item.noncePrefix),0);new DataView(nonce.buffer).setUint32(8,i,false);const plain=await crypto.subtle.decrypt({name:'AES-GCM',iv:nonce,additionalData:enc('DropSpaceShare:v1\\n'+id+'\\n'+item.fileId.replaceAll('-','')+'\\n'+i+'\\n'+Math.min(5*1024*1024,item.plainLength-i*5*1024*1024))},fileKey,concat(cipher,tag));out.push(new Uint8Array(plain));}const blob=new Blob(out,{type:item.mimeType});const digest=await crypto.subtle.digest('SHA-256',await blob.arrayBuffer());if(hex(digest)!==item.sha256.toLowerCase())throw Error('integrity check failed');const a=document.createElement('a');a.href=URL.createObjectURL(blob);a.download=item.displayName;a.click();setTimeout(()=>URL.revokeObjectURL(a.href),60000)}
async function get(path){const r=await fetch(path,{cache:'no-store'});if(!r.ok)throw Error('share object unavailable ('+r.status+')');return new Uint8Array(await r.arrayBuffer())}function enc(s){return new TextEncoder().encode(s)}function concat(a,b){const x=new Uint8Array(a.length+b.length);x.set(a);x.set(b,a.length);return x}function fromB64(s){const p=s.replaceAll('-','+').replaceAll('_','/')+'='.repeat((4-s.length%4)%4);const raw=atob(p),x=new Uint8Array(raw.length);for(let i=0;i<raw.length;i++)x[i]=raw.charCodeAt(i);return x}function uuidBytes(h){const b=fromHex(h);return new Uint8Array([b[3],b[2],b[1],b[0],b[5],b[4],b[7],b[6],b[8],b[9],b[10],b[11],b[12],b[13],b[14],b[15]])}function fromHex(s){const x=new Uint8Array(s.length/2);for(let i=0;i<x.length;i++)x[i]=parseInt(s.slice(i*2,i*2+2),16);return x}function hex(b){return [...new Uint8Array(b)].map(x=>x.toString(16).padStart(2,'0')).join('')}async function hkdf(master,salt,info){const k=await crypto.subtle.importKey('raw',master,'HKDF',false,['deriveKey']);return crypto.subtle.deriveKey({name:'HKDF',hash:'SHA-256',salt,info:enc(info)},k,{name:'AES-GCM',length:256},false,['decrypt'])}`;
}

async function loadMeta(env, shareId) {
  const object = await env.SHARES.get(metaKey(shareId));
  if (!object) throw new HttpError("not-found", 404);
  const meta = JSON.parse(await object.text());
  if (meta.shareId !== shareId || !Number.isSafeInteger(meta.expiresAt)) throw new HttpError("metadata-invalid", 500);
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
  const claims = JSON.parse(new TextDecoder().decode(fromB64(encoded)));
  if (!claims || claims.expiresAt <= Date.now()) throw new HttpError("share-expired", 410);
  return claims;
}

async function sign(claims, secret) {
  const encoded = toB64(new TextEncoder().encode(JSON.stringify(claims)));
  return `${encoded}.${await hmac(secret, encoded)}`;
}

async function hmac(secret, text) {
  const key = await crypto.subtle.importKey("raw", new TextEncoder().encode(secret), { name: "HMAC", hash: "SHA-256" }, false, ["sign"]);
  return toB64(new Uint8Array(await crypto.subtle.sign("HMAC", key, new TextEncoder().encode(text))));
}

function constantTime(a, b) { const x = fromB64(a), y = fromB64(b); if (x.length !== y.length) return false; let d = 0; for (let i = 0; i < x.length; i++) d |= x[i] ^ y[i]; return d === 0; }
function toB64(bytes) { let s = ""; for (const b of bytes) s += String.fromCharCode(b); return btoa(s).replaceAll("+", "-").replaceAll("/", "_").replaceAll("=", ""); }
function metaKey(id) { return `shares/${id}/meta.json`; }
function objectKey(id, name) { return `shares/${id}/${name}`; }
function requireHttps(request) { if (new URL(request.url).protocol !== "https:") throw new HttpError("https-required", 400); }
async function readJson(request, maximum) { const text = await request.text(); if (text.length > maximum) throw new HttpError("body-too-large", 413); try { return JSON.parse(text); } catch { throw new HttpError("json-invalid", 400); } }
function json(value, status = 200) { return cors(new Response(JSON.stringify(value), { status, headers: { "Content-Type": "application/json", "Cache-Control": "no-store" } })); }
function cors(response) { response.headers.set("Access-Control-Allow-Origin", "*"); response.headers.set("Access-Control-Allow-Methods", "GET,POST,PUT,DELETE,OPTIONS"); response.headers.set("Access-Control-Allow-Headers", "Authorization,Content-Type"); return response; }
class HttpError extends Error { constructor(code, status) { super(code); this.code = code; this.status = status; } }
