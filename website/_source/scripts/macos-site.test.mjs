import test from "node:test";
import assert from "node:assert/strict";
import { readFile } from "node:fs/promises";
import { JSDOM } from "jsdom";
import { suggestedPlatform, validMacFeed, macReleaseMarkup } from "../src/platform.js";

test("built Mac page discloses ad-hoc trust and keeps unpublished downloads absent", async () => {
  const document = new JSDOM(await readFile(new URL("../dist/macos/index.html", import.meta.url), "utf8")).window.document;
  assert.equal(document.querySelector('link[rel="canonical"]').href, "https://airanluo-dot.github.io/DropSpace/macos/");
  assert.match(document.querySelector('[data-i18n="mac.integrity"]').textContent, /No Developer ID signature/);
  assert.match(document.querySelector('[data-i18n="mac.step3"]').textContent, /Open Anyway/);
  const feed = JSON.parse(await readFile(new URL("../dist/api/macos/v1/beta.json", import.meta.url), "utf8"));
  if (!feed.release) assert.equal(document.querySelector('[data-mac-release] a'), null);
  const home = new JSDOM(await readFile(new URL("../dist/index.html", import.meta.url), "utf8")).window.document;
  assert.match(home.querySelector('[data-download="installer"]').href, /DropSpaceSetup\.exe$/);
  assert.match(home.querySelector('[data-platform-option="macos"]').href, /\/macos\/$/);
  assert.ok(home.querySelector('[data-platform-option="windows"]'));
});

test("platform suggestion does not mislabel iPad or promise a Mac chip", () => {
  assert.equal(suggestedPlatform({ platform: "MacIntel", maxTouchPoints: 0 }), "macos");
  assert.equal(suggestedPlatform({ platform: "MacIntel", maxTouchPoints: 5 }), null);
  assert.equal(suggestedPlatform({ userAgentData: { platform: "Windows" } }), "windows");
  assert.equal(suggestedPlatform({ platform: "Linux aarch64" }), null);
});

test("Mac live feed rejects foreign downloads, mismatched channels and malformed integrity", async () => {
  const feed = JSON.parse(await readFile(new URL("../dist/api/macos/v1/releases.json", import.meta.url), "utf8"));
  assert.equal(validMacFeed(feed), true);
  assert.ok(feed.beta, "Published Mac fixture is required for this test");
  for (const mutate of [
    x => { x.releases[0].manifest.installer.url = 'https://example.com/DropSpace.dmg'; },
    x => { x.releases[0].manifest.installer.sha256 = '0'; },
    x => { x.beta.manifest.tag = 'macos-v99.0.0-beta.1'; },
    x => { x.releases.push(x.releases[0]); },
  ]) {
    const changed = structuredClone(feed); mutate(changed);
    assert.equal(validMacFeed(changed), false);
  }
  const document = new JSDOM(macReleaseMarkup(feed.beta, key => key)).window.document;
  assert.equal(document.querySelectorAll('a').length, 4);
});
