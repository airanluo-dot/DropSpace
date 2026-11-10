import assert from "node:assert/strict";
import { readFile } from "node:fs/promises";
import test from "node:test";
import { JSDOM } from "jsdom";

test("the complete published archive follows the interface language offline", async () => {
  const history = JSON.parse(await readFile(new URL("../../../localization/release-history.json", import.meta.url)));
  const root = new URL("../dist/", import.meta.url);
  const html = await readFile(new URL("changelog/index.html", root), "utf8");
  const { window } = new JSDOM(html, { url: "https://example.test/DropSpace/changelog/", runScripts: "outside-only" });
  try {
    const document = window.document;
    const entries = [...document.querySelectorAll("[data-release-tag]")];
    const published = history.filter(entry => entry.date);
    assert.deepEqual(entries.map(node => node.dataset.releaseTag), published.map(entry => entry.tag));
    assert.equal(new Set(history.map(entry => entry.tag)).size, history.length);
    assert.equal(document.querySelectorAll("[data-release-tag] a, [data-release-tag] button").length, 0);
    const runtime = document.querySelector('script[src*="localization-runtime."]').src;
    window.eval(await readFile(new URL("assets/" + new URL(runtime).pathname.split("/").pop(), root), "utf8"));
    window.DropSpaceI18n.apply();
    const selector = document.querySelector("[data-language-selector]");
    for (const language of ["zh-CN", "zh-TW", "de-DE"]) {
      selector.value = language;
      selector.dispatchEvent(new window.Event("change"));
      const copy = language.startsWith("zh-") ? language : "en";
      for (const [index, entry] of entries.entries()) {
        assert.deepEqual([...entry.querySelectorAll("li")].map(item => item.textContent), published[index][copy]);
        assert.equal(entry.querySelector("ul").lang, copy);
      }
    }
  } finally {
    window.close();
  }
});
