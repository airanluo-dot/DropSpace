import assert from "node:assert/strict";
import { readFile } from "node:fs/promises";
import test from "node:test";
import { stableRequirements } from "../src/release-requirements.js";

test("Stable v0.2.1 requirements follow its published release notes", async () => {
  const body = await readFile(new URL("../../../.github/release-notes/v0.2.1.md", import.meta.url), "utf8");
  const release = { tagName: "v0.2.1", body };
  assert.equal(stableRequirements(release, "en"), "Stable v0.2.1: 64-bit Windows 11 build 26100 or later");
  assert.equal(stableRequirements(release, "zh-CN"), "稳定版 v0.2.1: 需要 64 位 Windows 11 Build 26100 或更高版本");
});

test("another Stable release uses its own requirement rather than a fixed build", () => {
  const release = { tagName: "v0.9.0", body: "Requires 64-bit Windows build 28000 or later." };
  assert.equal(stableRequirements(release, "en"), "Stable v0.9.0: 64-bit Windows build 28000 or later");
  assert.equal(stableRequirements(release, "zh-CN"), "稳定版 v0.9.0: 需要 64 位 Windows Build 28000 或更高版本");
});

test("missing or conflicting requirements point to release notes without guessing a build", () => {
  for (const body of [
    "No system requirement has been published.",
    "Requires Windows 11 build 26100 or later.\nRequires Windows 11 build 28000 or later.",
    "Requires Windows 10 build 26100 or later.\nRequires Windows 11 build 26100 or later."
  ]) {
    const release = { tagName: "v0.9.0", body };
    assert.equal(stableRequirements(release, "en"), "Stable v0.9.0: 64-bit Windows · See release notes for system requirements");
    assert.equal(stableRequirements(release, "zh-CN"), "稳定版 v0.9.0: 64 位 Windows · 系统要求请查看发布说明");
  }
});
