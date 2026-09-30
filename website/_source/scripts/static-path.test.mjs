import assert from "node:assert/strict";
import test from "node:test";
import path from "node:path";
import { resolveStaticPath } from "./static-path.mjs";

const root = path.resolve("dist");
test("static server resolves root and project-prefixed pages", () => {
  for (const request of ["/en/index.html", "/DropSpace/en/index.html"]) {
    assert.equal(resolveStaticPath(root, request), path.join(root, "en", "index.html"));
  }
  assert.equal(resolveStaticPath(root, "/DropSpace"), root);
  assert.equal(resolveStaticPath(root, "/DropSpace/"), root);
});
test("static server rejects encoded escapes including siblings with the same prefix", () => {
  for (const request of ["/DropSpace/..%2Fdist-private/secret", "/..%2Fdist-private/secret", "/DropSpace/%2e%2e%2fpackage.json", "/DropSpace/%E0%A4%A"]) {
    assert.throws(() => resolveStaticPath(root, request));
  }
});
