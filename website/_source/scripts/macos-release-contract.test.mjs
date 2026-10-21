import assert from "node:assert/strict";
import test from "node:test";
import { MAC_ASSETS, createMacReleaseData, validateMacManifest, macChannelFeed } from "./macos-release-contract.mjs";

const tag = "macos-v0.1.0-beta.1";
const prefix = "https://github.com/airanluo-dot/DropSpace/releases/";
const asset = name => ({ name, size: 42, sha256: "a".repeat(64), url: `${prefix}download/${tag}/${name}` });
const manifest = { schemaVersion: 1, platform: "macos", architecture: "arm64", tag, channel: "beta",
  bundleID: "io.github.airanluo-dot.DropSpace.macOS", bundleVersion: "1", minimumOS: "26.0",
  windowsBaseline: "v0.3.2-beta.7", signing: "ad-hoc", notarized: false,
  archive: asset(MAC_ASSETS.archive), installer: asset(MAC_ASSETS.installer), releaseNotes: "- Native Mac Beta",
  releaseURL: `${prefix}tag/${tag}` };
const release = { tag_name: tag, html_url: manifest.releaseURL, prerelease: true, draft: false, published_at: "2026-10-20T00:00:00Z",
  assets: Object.values(MAC_ASSETS).map(name => ({ name, size: 42, browser_download_url: `${prefix}download/${tag}/${name}` })) };

test("Mac channels never select Windows or claim an unpublished download", () => {
  const empty = createMacReleaseData([{ tag_name: "v0.3.2-beta.7", draft: false }]);
  assert.equal(macChannelFeed(empty, "beta").release, null);
  const data = createMacReleaseData([release, { tag_name: "v99.0.0", draft: false }], new Map([[tag, manifest]]));
  assert.equal(macChannelFeed(data, "stable").release, null);
  assert.equal(macChannelFeed(data, "beta").release.tag, tag);
  assert.equal(data.releases.length, 1);
});

test("Mac publication rejects wrong bytes, trust claims, missing artifacts and cross-platform identity", () => {
  assert.throws(() => validateMacManifest({ ...manifest, notarized: true }));
  assert.throws(() => validateMacManifest({ ...manifest, architecture: "x64" }));
  assert.throws(() => validateMacManifest({ ...manifest, archive: { ...manifest.archive, url: `${prefix}download/v0.3.2-beta.7/DropSpace.exe` } }));
  assert.throws(() => createMacReleaseData([{ ...release, assets: release.assets.slice(1) }], new Map([[tag, manifest]])));
  assert.throws(() => createMacReleaseData([release], new Map([[tag, { ...manifest, archive: { ...manifest.archive, size: 43 } }]])));
});

test("Mac release truncation retains Stable behind a long Beta sequence", () => {
  const tags = [...Array.from({ length: 25 }, (_, i) => `macos-v0.2.0-beta.${i + 1}`), "macos-v0.1.0"];
  const manifests = new Map();
  const releases = tags.map(nextTag => {
    const nextManifest = JSON.parse(JSON.stringify(manifest).replaceAll(tag, nextTag));
    nextManifest.channel = nextTag.includes("-beta.") ? "beta" : "stable";
    manifests.set(nextTag, nextManifest);
    const nextRelease = JSON.parse(JSON.stringify(release).replaceAll(tag, nextTag));
    nextRelease.prerelease = nextManifest.channel === "beta";
    return nextRelease;
  });
  const data = createMacReleaseData(releases, manifests);
  assert.equal(data.releases.length, 20);
  assert.equal(data.stable.manifest.tag, "macos-v0.1.0");
  assert.equal(data.beta.manifest.tag, "macos-v0.2.0-beta.25");
});
