import assert from "node:assert/strict";
import test from "node:test";
import worker, { ShareUsageCoordinator } from "../src/index.js";

const shareId = "00112233445566778899aabbccddeeff";
const fileId = "11112222333344445555666677778888";
const metadataKey = "shares/" + shareId + "/meta.json";

function createEnvironment() {
  const storage = new Map();
  const objects = new Map();
  let pending = Promise.resolve();
  const coordinator = new ShareUsageCoordinator({
    storage: {
      async get(key) { return structuredClone(storage.get(key)); },
      async put(key, value) { storage.set(key, structuredClone(value)); },
    },
    blockConcurrencyWhile(callback) {
      const operation = pending.then(callback);
      pending = operation.catch(() => {});
      return operation;
    },
  });
  const env = {
    PUBLIC_ORIGIN: "https://share.invalid",
    UPLOAD_TOKEN_SECRET: "test-secret-".repeat(4),
    SHARE_CREATION_LIMITER: {
      idFromName: name => name,
      get: () => ({ fetch: async () => new Response("{}") }),
    },
    SHARE_COORDINATOR: {
      idFromName: name => name,
      get: () => ({ fetch: (url, init) => coordinator.fetch(new Request(url, init)) }),
    },
    SHARES: {
      async get(key) {
        const object = objects.get(key);
        return object ? {
          text: async () => object.value,
          body: object.value,
          httpMetadata: object.httpMetadata,
        } : null;
      },
      async head(key) { return objects.has(key) ? {} : null; },
      async put(key, value, options = {}) { objects.set(key, { value, ...options }); },
      async delete(key) { objects.delete(key); },
    },
  };
  return { env, objects, storage };
}

function createRequest() {
  return new Request("https://share.invalid/v1/shares", {
    method: "POST",
    body: JSON.stringify({
      shareId,
      expiresAtUtc: new Date(Date.now() + 3_600_000).toISOString(),
      itemCount: 1,
      totalBytes: 256,
    }),
  });
}

test("invalid public origin leaves the share ID available for a corrected retry", async () => {
  for (const origin of ["http://share.invalid", "not-a-url", "https://share.invalid/path"]) {
    const { env, objects, storage } = createEnvironment();
    env.PUBLIC_ORIGIN = origin;
    const rejected = await worker.fetch(createRequest(), env);
    assert.equal(rejected.status, 500);
    assert.equal((await rejected.json()).error, "origin-invalid");
    assert.equal(objects.has(metadataKey), false, "a rejected creation must not store metadata");
    assert.equal(storage.has("state"), false, "a rejected creation must not claim the share ID");
    env.PUBLIC_ORIGIN = "https://share.invalid";
    assert.equal((await worker.fetch(createRequest(), env)).status, 200);
  }
});

test("public share objects cannot render the uploader's active content type", async () => {
  for (const [objectName, contentType, payload] of [
    ["manifest.bin", "text/html", "<script>globalThis.compromised = true</script>"],
    [fileId + ".0.bin", "image/svg+xml", '<svg xmlns="http://www.w3.org/2000/svg" onload="alert(1)"/>'],
  ]) {
    const { env, objects } = createEnvironment();
    const created = await worker.fetch(createRequest(), env);
    assert.equal(created.status, 200);
    const session = await created.json();
    const bytes = new TextEncoder().encode(payload);
    const uploaded = await worker.fetch(new Request(session.uploadBaseUrl + objectName, {
      method: "PUT",
      headers: {
        authorization: session.uploadAuthorization,
        "content-type": contentType,
        "content-length": String(bytes.length),
      },
      body: bytes,
    }), env);
    assert.equal(uploaded.status, 201);
    const stored = objects.get("shares/" + shareId + "/" + objectName);
    assert.equal(stored.httpMetadata.contentType, "application/octet-stream");
    // Objects uploaded before the fix may still retain the sender's active MIME type.
    stored.httpMetadata.contentType = contentType;
    const downloaded = await worker.fetch(new Request(session.uploadBaseUrl + objectName), env);
    assert.equal(downloaded.status, 200);
    assert.equal(downloaded.headers.get("content-type"), "application/octet-stream");
    assert.equal(downloaded.headers.get("x-content-type-options"), "nosniff");
    assert.deepEqual(new Uint8Array(await downloaded.arrayBuffer()), bytes);
  }
});
