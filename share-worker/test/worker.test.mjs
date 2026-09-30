import assert from "node:assert/strict";
import test from "node:test";
import worker, { ShareCreationLimiter, ShareUsageCoordinator } from "../src/index.js";

const shareId = "00112233445566778899aabbccddeeff";
const fileOne = "11112222333344445555666677778888";
const fileTwo = "9999aaaabbbbccccddddeeeeffff0000";

function createCoordinator() {
  const values = new Map();
  let running = Promise.resolve();
  const state = {
    storage: {
      async get(key) {
        return structuredClone(values.get(key));
      },
      async put(key, value) {
        values.set(key, structuredClone(value));
      },
    },
    blockConcurrencyWhile(callback) {
      const result = running.then(callback);
      running = result.catch(() => {});
      return result;
    },
  };
  return new ShareUsageCoordinator(state);
}

async function invoke(coordinator, operation, payload = {}) {
  const response = await coordinator.fetch(new Request("https://coordinator/" + operation, {
    method: "POST",
    headers: { "content-type": "application/json" },
    body: JSON.stringify({ operation, shareId, ...payload }),
  }));
  return { status: response.status, body: await response.json() };
}

test("share creation limiter enforces a bounded window", async () => {
  const values = new Map();
  const limiter = new ShareCreationLimiter({
    storage: {
      async get(key) { return values.get(key); },
      async put(key, value) { values.set(key, value); },
    },
    async blockConcurrencyWhile(callback) { return callback(); },
  });
  const request = () => new Request("https://limiter/admit", {
    method: "POST",
    headers: { "content-type": "application/json" },
    body: JSON.stringify({ limit: 2, windowMs: 60_000 }),
  });
  assert.equal((await limiter.fetch(request())).status, 200);
  assert.equal((await limiter.fetch(request())).status, 200);
  const limited = await limiter.fetch(request());
  assert.equal(limited.status, 429);
  assert.equal((await limited.json()).error, "creation-rate-limited");
});

test("source-rejected creation does not consume global admission", async () => {
  const admitted = [];
  const env = {
    SHARE_CREATION_LIMITER: {
      idFromName: name => name,
      get: key => ({ fetch: async () => {
        admitted.push(key);
        return key.startsWith("source-")
          ? new Response(JSON.stringify({ error: "creation-rate-limited" }), { status: 429 })
          : new Response("{}");
      } }),
    },
  };
  const response = await worker.fetch(new Request("https://share.invalid/v1/shares", {
    method: "POST",
    headers: { "cf-connecting-ip": "192.0.2.1", "content-type": "application/json" },
    body: "{}",
  }), env);
  assert.equal(response.status, 429);
  assert.equal(admitted.length, 1);
  assert.ok(admitted[0].startsWith("source-"));
});

test("the coordinator reserves concurrent plaintext byte usage atomically", async () => {
  const coordinator = createCoordinator();
  const expiresAt = Date.now() + 60 * 60 * 1000;
  assert.equal((await invoke(coordinator, "init", {
    expiresAt,
    itemCount: 2,
    totalBytes: 10,
  })).status, 200);

  const manifest = await invoke(coordinator, "reserve", {
    objectName: "manifest.bin",
    kind: "manifest",
    plainBytes: 0,
  });
  assert.equal(manifest.status, 200);
  assert.equal((await invoke(coordinator, "commit", {
    reservationId: manifest.body.reservationId,
  })).status, 200);

  const [first, second] = await Promise.all([
    invoke(coordinator, "reserve", {
      objectName: fileOne + ".0.bin",
      kind: "chunk",
      plainBytes: 5,
      fileId: fileOne,
      index: 0,
    }),
    invoke(coordinator, "reserve", {
      objectName: fileTwo + ".0.bin",
      kind: "chunk",
      plainBytes: 5,
      fileId: fileTwo,
      index: 0,
    }),
  ]);
  assert.equal(first.status, 200);
  assert.equal(second.status, 200);
  assert.equal((await invoke(coordinator, "commit", {
    reservationId: first.body.reservationId,
  })).status, 200);
  assert.equal((await invoke(coordinator, "commit", {
    reservationId: second.body.reservationId,
  })).status, 200);

  const overBudget = await invoke(coordinator, "reserve", {
    objectName: fileOne + ".1.bin",
    kind: "chunk",
    plainBytes: 1,
    fileId: fileOne,
    index: 1,
  });
  assert.equal(overBudget.status, 413);
  assert.equal(overBudget.body.error, "byte-limit-exceeded");
});

test("pending first chunks consume the item quota", async () => {
  const coordinator = createCoordinator();
  const expiresAt = Date.now() + 60 * 60 * 1000;
  assert.equal((await invoke(coordinator, "init", {
    expiresAt,
    itemCount: 1,
    totalBytes: 10,
  })).status, 200);

  const [first, second] = await Promise.all([
    invoke(coordinator, "reserve", {
      objectName: fileOne + ".0.bin",
      kind: "chunk",
      plainBytes: 5,
      fileId: fileOne,
      index: 0,
    }),
    invoke(coordinator, "reserve", {
      objectName: fileTwo + ".0.bin",
      kind: "chunk",
      plainBytes: 5,
      fileId: fileTwo,
      index: 0,
    }),
  ]);
  assert.equal(first.status, 200);
  assert.equal(second.status, 413);
  assert.equal(second.body.error, "item-limit-exceeded");
});

test("revocation closes the coordinator before object deletion completes", async () => {
  const coordinator = createCoordinator();
  const expiresAt = Date.now() + 60 * 60 * 1000;
  assert.equal((await invoke(coordinator, "init", {
    expiresAt,
    itemCount: 1,
    totalBytes: 5,
  })).status, 200);
  assert.equal((await invoke(coordinator, "revoke")).status, 200);

  const rejected = await invoke(coordinator, "reserve", {
    objectName: fileOne + ".0.bin",
    kind: "chunk",
    plainBytes: 5,
    fileId: fileOne,
    index: 0,
  });
  assert.equal(rejected.status, 410);
  assert.equal(rejected.body.error, "share-expired");
});


test("receiver page uses nonce CSP and bounded streaming download fallback", async () => {
  const response = await worker.fetch(
    new Request("https://share.example.invalid/s/" + shareId),
    {
      PUBLIC_ORIGIN: "https://share.example.invalid",
      SHARE_COORDINATOR: { idFromName: name => name, get: () => ({ fetch: async () => new Response("{}") }) },
      SHARES: {
        async get(key) {
          if (key === "shares/" + shareId + "/meta.json") {
            return new Response(JSON.stringify({
              shareId,
              expiresAt: Date.now() + 60 * 60 * 1000,
              itemCount: 1,
              totalBytes: 5,
            }));
          }
          return null;
        },
      },
    },
  );
  const html = await response.text();
  assert.equal(response.status, 200);
  assert.match(html, /script-src 'nonce-[^']+'/);
  assert.doesNotMatch(html, /unsafe-inline/);
  assert.match(html, /showSaveFilePicker/);
  assert.match(html, /class Sha256/);
  assert.match(html, /this.words = new Uint32Array\(64\)/);
  assert.doesNotMatch(html, /const words = new Uint32Array\(64\)/);
  assert.match(html, /fallbackDownloadActive = true/);
  assert.match(html, /fallbackReleaseScheduled/);
  assert.match(html, /URL\.revokeObjectURL\(url\)/);
  assert.match(html, /button.disabled=true/);
  assert.match(html, /256 \* 1024 \* 1024/);
});

test("worker translates asynchronous route failures into JSON with CORS", async () => {
  for (const [method, path] of [["POST", "/v1/shares"], ["PUT", "/v1/shares/" + shareId + "/objects/manifest.bin"], ["GET", "/v1/shares/" + shareId + "/objects/manifest.bin"], ["DELETE", "/v1/shares/" + shareId], ["GET", "/s/" + shareId]]) {
    const response = await worker.fetch(new Request("http://share.invalid" + path, { method }), {});
    assert.equal(response.status, 400);
    assert.equal((await response.json()).error, "https-required");
    assert.equal(response.headers.get("access-control-allow-origin"), "*");
  }
});

test("asynchronous coordinator errors retain their policy status", async () => {
  const coordinator = createCoordinator();
  await invoke(coordinator, "init", { expiresAt: Date.now() + 3600000, itemCount: 1, totalBytes: 5 });
  const result = await invoke(coordinator, "reserve", { objectName: "../bad", kind: "chunk", plainBytes: 1 });
  assert.equal(result.status, 400);
  assert.equal(result.body.error, "coordinator-object-invalid");
});

test("duplicate creation cannot mint a token, overwrite metadata or revoke the original", async () => {
  const coordinator = createCoordinator();
  const objects = new Map();
  const mutations = [];
  const env = {
    UPLOAD_TOKEN_SECRET: "test-secret-".repeat(4),
    SHARE_CREATION_LIMITER: { idFromName: name => name, get: () => ({ fetch: async () => new Response("{}") }) },
    SHARE_COORDINATOR: { idFromName: name => name, get: () => ({ fetch: (url, init) => coordinator.fetch(new Request(url, init)) }) },
    SHARES: {
      async put(key, value) { mutations.push(["put", key]); objects.set(key, value); },
      async delete(key) { mutations.push(["delete", key]); objects.delete(key); },
    },
  };
  const body = { shareId, expiresAtUtc: new Date(Date.now() + 3600000).toISOString(), itemCount: 1, totalBytes: 5 };
  const request = data => new Request("https://share.invalid/v1/shares", { method: "POST", headers: { "content-type": "application/json" }, body: JSON.stringify(data) });
  assert.equal((await worker.fetch(request(body), env)).status, 200);
  const original = objects.get("shares/" + shareId + "/meta.json");
  for (const data of [body, { ...body, totalBytes: 10 }]) {
    const duplicate = await worker.fetch(request(data), env);
    assert.equal(duplicate.status, 409);
    assert.equal((await duplicate.json()).error, "coordinator-conflict");
  }
  assert.equal(objects.get("shares/" + shareId + "/meta.json"), original);
  assert.equal(mutations.length, 1);
  assert.equal((await invoke(coordinator, "reserve", { objectName: "manifest.bin", kind: "manifest", plainBytes: 0 })).status, 200);
});

test("receiver decrypts and downloads a real encrypted file", async () => {
  const { runInNewContext } = await import("node:vm");
  const { createHash } = await import("node:crypto");
  const master = crypto.getRandomValues(new Uint8Array(32));
  const salt = Uint8Array.from([0x33,0x22,0x11,0,0x55,0x44,0x77,0x66,0x88,0x99,0xaa,0xbb,0xcc,0xdd,0xee,0xff]);
  const enc = text => new TextEncoder().encode(text);
  const derive = async info => crypto.subtle.deriveKey({ name: "HKDF", hash: "SHA-256", salt, info: enc(info) }, await crypto.subtle.importKey("raw", master, "HKDF", false, ["deriveKey"]), { name: "AES-GCM", length: 256 }, false, ["encrypt"]);
  const bytes = enc("DropSpace encrypted download regression");
  const prefix = crypto.getRandomValues(new Uint8Array(8));
  const nonce = new Uint8Array(12); nonce.set(prefix);
  const ciphertext = await crypto.subtle.encrypt({ name: "AES-GCM", iv: nonce, additionalData: enc(`DropSpaceShare:v1\n${shareId}\n${fileOne}\n0\n${bytes.length}`) }, await derive("file:" + fileOne), bytes);
  const item = { fileId: fileOne, displayName: "regression.txt", mimeType: "text/plain", plainLength: bytes.length, chunkCount: 1, noncePrefix: Buffer.from(prefix).toString("base64"), sha256: createHash("sha256").update(bytes).digest("hex") };
  const manifestNonce = crypto.getRandomValues(new Uint8Array(12));
  const encryptedManifest = await crypto.subtle.encrypt({ name: "AES-GCM", iv: manifestNonce, additionalData: enc(`DropSpaceShare:v1\n${shareId}`) }, await derive("manifest"), enc(JSON.stringify({ shareId, items: [item] })));
  const packedManifest = new Uint8Array(12 + encryptedManifest.byteLength); packedManifest.set(manifestNonce); packedManifest.set(new Uint8Array(encryptedManifest), 12);
  const page = await worker.fetch(new Request("https://share.invalid/s/" + shareId), { SHARE_COORDINATOR: { idFromName: name => name, get: () => ({ fetch: async () => new Response("{}") }) }, SHARES: { async get() { return new Response(JSON.stringify({ shareId, expiresAt: Date.now() + 3600000, itemCount: 1, totalBytes: bytes.length })); } } });
  const script = (await page.text()).match(/<script nonce="[^"]+">([\s\S]*)<\/script>/)[1];
  const buttons = [], alerts = [], fetched = [];
  let downloaded;
  const status = {};
  const context = {
    crypto, TextEncoder, TextDecoder, Uint8Array, Uint32Array, DataView, Blob, atob, btoa,
    location: { hash: "#k=" + Buffer.from(master).toString("base64url") }, window: {},
    document: { getElementById: id => id === "status" ? status : { appendChild() {} }, createElement: kind => kind === "button" ? (buttons.push({}), buttons.at(-1)) : { appendChild() {}, click() {} } },
    fetch: async path => { fetched.push(path); return new Response(path.endsWith("manifest.bin") ? packedManifest : ciphertext); },
    URL: { createObjectURL(blob) { downloaded = blob; return "blob:download"; }, revokeObjectURL() {} },
    alert: error => alerts.push(error), setTimeout: callback => callback(),
  };
  await runInNewContext(script, context);
  assert.equal(buttons.length, 1);
  await buttons[0].onclick();
  assert.deepEqual(alerts, []);
  assert.equal(fetched[1], "/v1/shares/" + shareId + "/objects/" + fileOne + ".0.bin");
  assert.equal(await downloaded.text(), new TextDecoder().decode(bytes));
});


test("concurrent initialization grants ownership to exactly one creator", async () => {
  const coordinator = createCoordinator();
  const metadata = { expiresAt: Date.now() + 3600000, itemCount: 1, totalBytes: 5 };
  const results = await Promise.all([invoke(coordinator, "init", metadata), invoke(coordinator, "init", metadata)]);
  assert.deepEqual(results.map(result => result.status).sort(), [200, 409]);
});

async function createWorkerShare() {
  const coordinator = createCoordinator();
  const objects = new Map();
  const env = {
    UPLOAD_TOKEN_SECRET: "test-secret-".repeat(4),
    SHARE_CREATION_LIMITER: { idFromName: name => name, get: () => ({ fetch: async () => new Response("{}") }) },
    SHARE_COORDINATOR: { idFromName: name => name, get: () => ({ fetch: (url, init) => coordinator.fetch(new Request(url, init)) }) },
    SHARES: {
      async get(key) {
        const value = objects.get(key);
        return value === undefined ? null : { text: async () => value, body: value };
      },
      async head(key) { return objects.has(key) ? {} : null; },
      async put(key, value) {
        objects.set(key, value instanceof ReadableStream ? new Uint8Array(await new Response(value).arrayBuffer()) : value);
      },
      async delete(key) { objects.delete(key); },
      async list() { return { objects: [...objects.keys()].map(key => ({ key })), truncated: false }; },
    },
  };
  const response = await worker.fetch(new Request("https://share.invalid/v1/shares", {
    method: "POST", body: JSON.stringify({ shareId, expiresAtUtc: new Date(Date.now() + 3600000).toISOString(), itemCount: 1, totalBytes: 5 }),
  }), env);
  assert.equal(response.status, 200);
  return { env, objects, session: await response.json() };
}

test("upload verifies actual length before consuming the quota", async () => {
  for (const actualLength of [20, 22]) {
    const { env, objects, session } = await createWorkerShare();
    const request = bytes => new Request(session.uploadBaseUrl + fileOne + ".0.bin", {
      method: "PUT", headers: { authorization: session.uploadAuthorization, "content-length": "21" }, body: new Uint8Array(bytes),
    });
    const invalid = await worker.fetch(request(actualLength), env);
    assert.equal(invalid.status, 400);
    assert.equal((await invalid.json()).error, "object-length-mismatch");
    assert.equal(objects.size, 1);
    assert.equal((await worker.fetch(request(21), env)).status, 201);
  }
});

test("partial revocation fails closed and retains metadata for retry", async () => {
  const { env, objects, session } = await createWorkerShare();
  const objectName = fileOne + ".0.bin";
  const key = "shares/" + shareId + "/" + objectName;
  objects.set(key, new Uint8Array(21));
  const originalDelete = env.SHARES.delete;
  env.SHARES.delete = async candidate => {
    if (candidate === key) throw new Error("temporary storage failure");
    return originalDelete(candidate);
  };
  const revoke = () => worker.fetch(new Request(session.revokeUrl, { method: "DELETE", headers: { authorization: session.uploadAuthorization } }), env);
  assert.equal((await revoke()).status, 500);
  assert.equal(objects.has("shares/" + shareId + "/meta.json"), true);
  assert.equal((await worker.fetch(new Request(session.uploadBaseUrl + objectName), env)).status, 410);
  assert.equal((await worker.fetch(new Request("https://share.invalid/s/" + shareId), env)).status, 410);
  env.SHARES.delete = originalDelete;
  assert.equal((await revoke()).status, 204);
  assert.equal(objects.size, 0);
});

test("freshly created upload authorization authenticates and rejects tampering", async () => {
  const { env, session } = await createWorkerShare();
  const upload = authorization => worker.fetch(new Request(session.uploadBaseUrl + "manifest.bin", {
    method: "PUT", headers: { authorization, "content-length": "32" }, body: new Uint8Array(32),
  }), env);
  assert.equal((await upload(session.uploadAuthorization + "x")).status, 401);
  assert.equal((await upload(session.uploadAuthorization)).status, 201);
});
