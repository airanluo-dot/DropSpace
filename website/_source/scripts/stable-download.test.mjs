import assert from "node:assert/strict";
import { readFile } from "node:fs/promises";
import test from "node:test";
import { JSDOM } from "jsdom";

test("homepage Windows download buttons open the stable installer", async () => {
  const releases = JSON.parse(await readFile(new URL("../data/releases.json", import.meta.url), "utf8"));
  const stable = releases.api.releases.find(release => !release.isDraft && !release.isPrerelease);
  const installer = stable.assets.find(asset => asset.name === "DropSpaceSetup.exe");
  const homepage = await readFile(new URL("../dist/index.html", import.meta.url), "utf8");
  const document = JSDOM.fragment(homepage);
  const buttons = Array.from(document.querySelectorAll('a[data-download="installer"]'));
  assert.ok(buttons.length > 0);
  for (const button of buttons) assert.equal(button.getAttribute("href"), installer.downloadUrl);
});
