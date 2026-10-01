import assert from "node:assert/strict";
import test from "node:test";
import worker, { ShareUsageCoordinator } from "../src/index.js";
import { RESERVATION_TTL_MS } from "../src/policy.js";

const shareId = "00112233445566778899aabbccddeeff";
const fileId = "11112222333344445555666677778888";
const objectName = fileId + ".0.bin";

for (const staleWriteFails of [false, true]) {
  test("expired upload cannot destroy a committed retry" + (staleWriteFails ? " when storage fails" : ""), async () => {
    const originalNow = Date.now;
    let now = originalNow();
    Date.now = () => now;
    let releaseFirst;
    let firstReached;
    const firstReady = new Promise(resolve => { firstReached = resolve; });
    const firstRelease = new Promise(resolve => { releaseFirst = resolve; });
    const values = new Map();
    const objects = new Map();
    let running = Promise.resolve();
    let dataPuts = 0;
    const coordinator = new ShareUsageCoordinator({
      storage: {
        async get(key) { return structuredClone(values.get(key)); },
        async put(key, value) { values.set(key, structuredClone(value)); },
      },
      blockConcurrencyWhile(callback) {
        const pending = running.then(callback);
        running = pending.catch(() => {});
        return pending;
      },
    });
    const env = {
      UPLOAD_TOKEN_SECRET: "test-secret-".repeat(4),
      SHARE_CREATION_LIMITER: { idFromName: name => name, get: () => ({ fetch: async () => new Response("{}") }) },
      SHARE_COORDINATOR: {
        idFromName: name => name,
        get: () => ({ fetch: (url, init) => coordinator.fetch(new Request(url, init)) }),
      },
      SHARES: {
        async get(key) {
          const object = objects.get(key);
          return object ? { text: async () => object.value, body: object.value } : null;
        },
        async head(key) { return objects.get(key) || null; },
        async put(key, value, options = {}) {
          if (key.endsWith(".bin") && ++dataPuts === 1) {
            firstReached();
            await firstRelease;
            if (staleWriteFails) throw new Error("storage failed before writing");
          }
          // R2 evaluates the create-only precondition atomically at the actual write.
          if (options.onlyIf?.etagDoesNotMatch === "*" && objects.has(key)) return null;
          const object = { value, ...options };
          objects.set(key, object);
          return object;
        },
        async delete(key) { objects.delete(key); },
      },
    };
    coordinator.env = env;
    try {
      const created = await worker.fetch(new Request("https://share.invalid/v1/shares", {
        method: "POST",
        body: JSON.stringify({ shareId, expiresAtUtc: new Date(now + 3_600_000).toISOString(), itemCount: 1, totalBytes: 5 }),
      }), env);
      assert.equal(created.status, 200);
      const session = await created.json();
      const upload = byte => worker.fetch(new Request(session.uploadBaseUrl + objectName, {
        method: "PUT",
        headers: { authorization: session.uploadAuthorization, "content-length": "21" },
        body: new Uint8Array(21).fill(byte),
      }), env);
      const stale = upload(1);
      await firstReady;
      now += RESERVATION_TTL_MS + 1;
      assert.equal((await upload(2)).status, 201);
      releaseFirst();
      assert.notEqual((await stale).status, 201);
      const downloaded = await worker.fetch(new Request(session.uploadBaseUrl + objectName), env);
      assert.equal(downloaded.status, 200, "cleanup from the expired reservation must preserve the committed retry");
      assert.deepEqual(new Uint8Array(await downloaded.arrayBuffer()), new Uint8Array(21).fill(2));
    } finally {
      releaseFirst();
      Date.now = originalNow;
    }
  });
}

test("cleanup cannot delete a retry created after storage lifecycle expiry", async () => {
  const values = new Map();
  const objects = new Map();
  let running = Promise.resolve();
  let pauseCleanup;
  let cleanupReached;
  const cleanupReady = new Promise(resolve => { cleanupReached = resolve; });
  const cleanupRelease = new Promise(resolve => { pauseCleanup = resolve; });
  let retryReserveReached;
  const retryReserved = new Promise(resolve => { retryReserveReached = resolve; });
  let cleanupActive = false;
  let dataPuts = 0;
  let dataHeads = 0;
  const coordinator = new ShareUsageCoordinator({
    storage: {
      async get(key) { return structuredClone(values.get(key)); },
      async put(key, value) { values.set(key, structuredClone(value)); },
    },
    blockConcurrencyWhile(callback) {
      const pending = running.then(callback);
      running = pending.catch(() => {});
      return pending;
    },
  });
  const env = {
    UPLOAD_TOKEN_SECRET: "test-secret-".repeat(4),
    SHARE_CREATION_LIMITER: { idFromName: name => name, get: () => ({ fetch: async () => new Response("{}") }) },
    SHARE_COORDINATOR: {
      idFromName: name => name,
      get: () => ({ fetch: (url, init) => {
        if (cleanupActive && JSON.parse(init.body).operation === "reserve") retryReserveReached();
        return coordinator.fetch(new Request(url, init));
      } }),
    },
    SHARES: {
      async get(key) {
        const object = objects.get(key);
        return object ? { text: async () => object.value, body: object.value } : null;
      },
      async head(key) {
        const current = objects.get(key) || null;
        if (key.endsWith(".bin") && ++dataHeads === 2) {
          cleanupActive = true;
          cleanupReached();
          await cleanupRelease;
        }
        return current;
      },
      async put(key, value, options = {}) {
        if (options.onlyIf?.etagDoesNotMatch === "*" && objects.has(key)) return null;
        const object = { value, ...options };
        objects.set(key, object);
        if (key.endsWith(".bin") && ++dataPuts === 1) throw new Error("storage wrote bytes before reporting failure");
        return object;
      },
      async delete(key) { objects.delete(key); },
    },
  };
  coordinator.env = env;
  const created = await worker.fetch(new Request("https://share.invalid/v1/shares", {
    method: "POST",
    body: JSON.stringify({ shareId, expiresAtUtc: new Date(Date.now() + 3_600_000).toISOString(), itemCount: 1, totalBytes: 5 }),
  }), env);
  const session = await created.json();
  const upload = byte => worker.fetch(new Request(session.uploadBaseUrl + objectName, {
    method: "PUT",
    headers: { authorization: session.uploadAuthorization, "content-length": "21" },
    body: new Uint8Array(21).fill(byte),
  }), env);
  const stale = upload(1);
  await cleanupReady;
  // R2 lifecycle expiry is independent of the active share/coordinator lifetime.
  objects.delete("shares/" + shareId + "/" + objectName);
  const retry = upload(2);
  await retryReserved;
  // Drain the retry's storage path if it is not protected by cleanup.
  await new Promise(resolve => setImmediate(resolve));
  pauseCleanup();
  assert.equal((await stale).status, 500);
  assert.equal((await retry).status, 201);
  const downloaded = await worker.fetch(new Request(session.uploadBaseUrl + objectName), env);
  assert.equal(downloaded.status, 200);
  assert.deepEqual(new Uint8Array(await downloaded.arrayBuffer()), new Uint8Array(21).fill(2));
});

test("partial storage writes are cleaned before the same object is retried", async () => {
  const values = new Map();
  const objects = new Map();
  let running = Promise.resolve();
  let dataPuts = 0;
  const env = {};
  const coordinator = new ShareUsageCoordinator({
    storage: {
      async get(key) { return structuredClone(values.get(key)); },
      async put(key, value) { values.set(key, structuredClone(value)); },
    },
    blockConcurrencyWhile(callback) {
      const pending = running.then(callback);
      running = pending.catch(() => {});
      return pending;
    },
  }, env);
  Object.assign(env, {
    UPLOAD_TOKEN_SECRET: "test-secret-".repeat(4),
    SHARE_CREATION_LIMITER: { idFromName: name => name, get: () => ({ fetch: async () => new Response("{}") }) },
    SHARE_COORDINATOR: { idFromName: name => name, get: () => ({ fetch: (url, init) => coordinator.fetch(new Request(url, init)) }) },
    SHARES: {
      async get(key) { const object = objects.get(key); return object ? { text: async () => object.value, body: object.value } : null; },
      async head(key) { return objects.get(key) || null; },
      async put(key, value, options = {}) {
        if (options.onlyIf?.etagDoesNotMatch === "*" && objects.has(key)) return null;
        const object = { value, ...options };
        objects.set(key, object);
        if (key.endsWith(".bin") && ++dataPuts === 1) throw new Error("partial storage write");
        return object;
      },
      async delete(key) { objects.delete(key); },
    },
  });
  const created = await worker.fetch(new Request("https://share.invalid/v1/shares", {
    method: "POST", body: JSON.stringify({ shareId, expiresAtUtc: new Date(Date.now() + 3_600_000).toISOString(), itemCount: 1, totalBytes: 5 }),
  }), env);
  const session = await created.json();
  const upload = byte => worker.fetch(new Request(session.uploadBaseUrl + objectName, {
    method: "PUT", headers: { authorization: session.uploadAuthorization, "content-length": "21" }, body: new Uint8Array(21).fill(byte),
  }), env);
  assert.equal((await upload(1)).status, 500);
  assert.equal(objects.has("shares/" + shareId + "/" + objectName), false);
  assert.equal((await upload(2)).status, 201);
});

for (const lifecycleDeletes of [false, true]) {
test("cleanup protects an in-flight retry and clears expired orphan: lifecycle=" + lifecycleDeletes, async () => {
  const originalNow = Date.now;
  let now = originalNow();
  Date.now = () => now;
  const values = new Map();
  const objects = new Map();
  let running = Promise.resolve();
  let releaseWrite;
  let writeReached;
  let writeFinished;
  const writeReady = new Promise(resolve => { writeReached = resolve; });
  const writeRelease = new Promise(resolve => { releaseWrite = resolve; });
  const writeComplete = new Promise(resolve => { writeFinished = resolve; });
  let cleanupHeads = 0;
  const env = {};
  const coordinator = new ShareUsageCoordinator({
    storage: {
      async get(key) { return structuredClone(values.get(key)); },
      async put(key, value) { values.set(key, structuredClone(value)); },
    },
    blockConcurrencyWhile(callback) {
      const pending = running.then(callback);
      running = pending.catch(() => {});
      return pending;
    },
  }, env);
  Object.assign(env, {
    UPLOAD_TOKEN_SECRET: "test-secret-".repeat(4),
    SHARE_CREATION_LIMITER: { idFromName: name => name, get: () => ({ fetch: async () => new Response("{}") }) },
    SHARE_COORDINATOR: { idFromName: name => name, get: () => ({ fetch: (url, init) => coordinator.fetch(new Request(url, init)) }) },
    SHARES: {
      async get(key) { const object = objects.get(key); return object ? { text: async () => object.value, body: object.value } : null; },
      async head(key) {
        const snapshot = objects.get(key) || null;
        if (snapshot && key.endsWith(".bin") && lifecycleDeletes) {
          cleanupHeads++;
          // Lifecycle expiry removes A while a previously reserved B can write.
          objects.delete(key);
          await writeComplete;
        }
        return snapshot;
      },
      async put(key, value, options = {}) {
        if (key.endsWith(".bin")) { writeReached(); await writeRelease; }
        if (options.onlyIf?.etagDoesNotMatch === "*" && objects.has(key)) return null;
        const object = { value, ...options };
        objects.set(key, object);
        if (key.endsWith(".bin")) writeFinished();
        return object;
      },
      async delete(key) { objects.delete(key); },
    },
  });
  const operate = (operation, body) => coordinator.fetch(new Request("https://coordinator/" + operation, {
    method: "POST", body: JSON.stringify({ operation, shareId, ...body }),
  }));
  try {
    const created = await worker.fetch(new Request("https://share.invalid/v1/shares", {
      method: "POST", body: JSON.stringify({ shareId, expiresAtUtc: new Date(now + 3_600_000).toISOString(), itemCount: 1, totalBytes: 5 }),
    }), env);
    const session = await created.json();
    const stale = await (await operate("reserve", { objectName, kind: "chunk", plainBytes: 5, fileId, index: 0 })).json();
    now += RESERVATION_TTL_MS + 1;
    const retry = worker.fetch(new Request(session.uploadBaseUrl + objectName, {
      method: "PUT", headers: { authorization: session.uploadAuthorization, "content-length": "21" }, body: new Uint8Array(21).fill(2),
    }), env);
    await writeReady;
    const key = "shares/" + shareId + "/" + objectName;
    // A's expired storage operation completes after B has reserved and checked R2.
    objects.set(key, { value: new Uint8Array(21).fill(1), customMetadata: { uploadReservationId: stale.reservationId } });
    const cleanup = operate("rollback", { reservationId: stale.reservationId, objectName, cleanupStoredObject: true });
    await new Promise(resolve => setImmediate(resolve));
    if (lifecycleDeletes) objects.delete(key); // Independent lifecycle cleanup is optional.
    releaseWrite();
    assert.equal((await cleanup).status, 200);
    assert.equal((await retry).status, lifecycleDeletes ? 201 : 409);
    if (!lifecycleDeletes) {
      const nextRetry = await worker.fetch(new Request(session.uploadBaseUrl + objectName, {
        method: "PUT", headers: { authorization: session.uploadAuthorization, "content-length": "21" }, body: new Uint8Array(21).fill(2),
      }), env);
      assert.equal(nextRetry.status, 201, "rollback must clear the expired predecessor without manual lifecycle deletion");
    }
    const downloaded = await worker.fetch(new Request(session.uploadBaseUrl + objectName), env);
    assert.equal(downloaded.status, 200);
    assert.equal(cleanupHeads, 0, "pending ownership must skip cleanup before reading storage");
    assert.deepEqual(new Uint8Array(await downloaded.arrayBuffer()), new Uint8Array(21).fill(2));
  } finally {
    releaseWrite();
    Date.now = originalNow;
  }
});

}
