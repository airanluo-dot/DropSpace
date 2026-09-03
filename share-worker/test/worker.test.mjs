import assert from "node:assert/strict";
import test from "node:test";
import { ShareUsageCoordinator } from "../src/index.js";

const shareId = "00112233445566778899aabbccddeeff";
const fileOne = "11112222333344445555666677778888";
const fileTwo = "9999aaaabbbbccccddddeeeeffff0000";

function createCoordinator() {
  const values = new Map();
  const state = {
    storage: {
      async get(key) {
        return values.get(key);
      },
      async put(key, value) {
        values.set(key, value);
      },
    },
    async blockConcurrencyWhile(callback) {
      return callback();
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
