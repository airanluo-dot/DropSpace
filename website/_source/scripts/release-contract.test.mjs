import assert from "node:assert/strict";
import test from "node:test";
import { normalizeGitHubReleases, validateReleaseApi } from "./release-contract.mjs";

const valid = {
  tag_name: "v0.2.0-preview.2",
  name: "DropSpace v0.2.0-preview.2",
  body: "- Fix",
  draft: false,
  prerelease: true,
  published_at: "2026-08-12T00:00:00Z",
  html_url: "https://github.com/airanluo-dot/DropSpace/releases/tag/v0.2.0-preview.2",
  assets: [{
    name: "DropSpaceSetup.exe",
    size: 42,
    browser_download_url: "https://github.com/airanluo-dot/DropSpace/releases/download/v0.2.0-preview.2/DropSpaceSetup.exe"
  }]
};

test("normalizes the public GitHub contract without arbitrary URLs", () => {
  const api = normalizeGitHubReleases([valid, { ...valid, draft: true }], "2026-08-12T00:00:00Z");
  assert.equal(api.schemaVersion, 1);
  assert.equal(api.releases.length, 1);
  assert.equal(api.releases[0].assets[0].size, 42);
  assert.equal(validateReleaseApi(api), api);
});

test("rejects mismatched release, asset and schema identities", () => {
  assert.throws(() => normalizeGitHubReleases([{ ...valid, html_url: "https://attacker.invalid/release" }]));
  assert.throws(() => normalizeGitHubReleases([{ ...valid, assets: [{ ...valid.assets[0], browser_download_url: "https://attacker.invalid/update.exe" }] }]));
  assert.throws(() => validateReleaseApi({ schemaVersion: 2, releases: [] }));
});
