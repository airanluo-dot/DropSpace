import assert from "node:assert/strict";
import test from "node:test";
import { createLatestChangeApi, createWebsiteReleaseData, normalizeGitHubReleases, validateLatestChangeApi, validateReleaseApi } from "./release-contract.mjs";

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

test("latest-change contract follows the newest published release with a variable highlight count", () => {
  const body = `# DropSpace v0.2.1-preview.1 — Release-driven website\n\n## Highlights\n\n- API-driven headline\n- Variable summary list\n- Build snapshot fallback\n\n## 中文说明\n\n- 标题由接口更新\n- 摘要数量可变`;
  const preview = {
    ...valid,
    tag_name: "v0.2.1-preview.1",
    name: "DropSpace v0.2.1-preview.1",
    body,
    published_at: "2026-08-20T00:00:00Z",
    html_url: "https://github.com/airanluo-dot/DropSpace/releases/tag/v0.2.1-preview.1",
    assets: valid.assets.map((asset) => ({
      ...asset,
      browser_download_url: "https://github.com/airanluo-dot/DropSpace/releases/download/v0.2.1-preview.1/DropSpaceSetup.exe"
    }))
  };
  const payload = createLatestChangeApi(normalizeGitHubReleases([valid, preview], "2026-08-20T00:01:00Z"));
  assert.equal(validateLatestChangeApi(payload), payload);
  assert.equal(payload.release.tagName, "v0.2.1-preview.1");
  assert.equal(payload.release.headline["zh-CN"], "最新预览版。");
  assert.equal(payload.release.title, "Release-driven website");
  assert.deepEqual(payload.release.highlights.en, ["API-driven headline", "Variable summary list", "Build snapshot fallback"]);
  assert.deepEqual(payload.release.highlights["zh-CN"], ["标题由接口更新", "摘要数量可变"]);
});

test("latest-change contract rejects unofficial identity and unbounded summaries", () => {
  const payload = createLatestChangeApi(normalizeGitHubReleases([valid]));
  assert.throws(() => validateLatestChangeApi({ ...payload, release: { ...payload.release, htmlUrl: "https://attacker.invalid/release" } }));
  assert.throws(() => validateLatestChangeApi({
    ...payload,
    release: { ...payload.release, highlights: { ...payload.release.highlights, en: Array(7).fill("Too many") } }
  }));
});

test("requires complete current Stable and Preview assets", () => {
  const assetNames = ["DropSpaceSetup.exe", "DropSpace.exe", "DropSpace-x64.msix", "SHA256SUMS.txt", "update-manifest.json"];
  const complete = (tag, prerelease) => ({
    ...valid,
    tag_name: tag,
    name: `DropSpace ${tag}`,
    prerelease,
    html_url: `https://github.com/airanluo-dot/DropSpace/releases/tag/${tag}`,
    assets: assetNames.map((name) => ({
      name,
      size: 42,
      browser_download_url: `https://github.com/airanluo-dot/DropSpace/releases/download/${tag}/${name}`
    }))
  });
  const stable = complete("v0.1.0", false);
  const preview = complete("v0.2.0-preview.5", true);
  assert.doesNotThrow(() => createWebsiteReleaseData([preview, stable]));
  assert.throws(() => createWebsiteReleaseData([{ ...preview, assets: preview.assets.slice(0, -1) }, stable]));
  assert.throws(() => createWebsiteReleaseData([preview, { ...stable, assets: stable.assets.slice(0, -1) }]));
  assert.throws(() => createWebsiteReleaseData([preview]));
  assert.throws(() => createWebsiteReleaseData([{ ...preview, html_url: "http://github.com/insecure" }, stable]));
});
