import assert from "node:assert/strict";
import test from "node:test";
import worker from "../src/index.js";

test("an existing share rejects an upload without its bearer token", async () => {
  const shareId = "00112233445566778899aabbccddeeff";
  const metadata = { shareId, expiresAt: Date.now() + 60_000, itemCount: 1, totalBytes: 32 };
  const storage = { get: async () => Response.json(metadata) };
  const request = new Request(`https://share.invalid/v1/shares/${shareId}/objects/manifest.bin`, {
    method: "PUT",
    body: new Uint8Array(32),
    headers: { "content-length": "32" },
  });

  const response = await worker.fetch(request, { SHARES: storage });
  assert.equal(response.status, 401);
  assert.equal((await response.json()).error, "not-authorized");
});
