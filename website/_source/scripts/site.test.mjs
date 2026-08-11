import assert from "node:assert/strict";
import { readFile } from "node:fs/promises";
import test from "node:test";

const html = await readFile(new URL("../src/index.html", import.meta.url), "utf8");
const changelog = await readFile(new URL("../src/changelog/index.html", import.meta.url), "utf8");
const script = await readFile(new URL("../src/script.js", import.meta.url), "utf8");
const releases = JSON.parse(await readFile(new URL("../data/releases.json", import.meta.url), "utf8"));

test("fallback has all Stable downloads", () => {
  assert.equal(releases.stable.tag, "v0.1.0");
  for (const key of ["installer", "portable", "msix", "checksums"]) {
    assert.match(releases.stable.assets[key], /^https:\/\/github\.com\/airanluo-dot\/DropSpace\/releases\//);
  }
});

test("homepage has required sections and metadata", () => {
  for (const value of ["Temporary Space", "Dynamic Island", "Notch", "Clipboard History", "Download Installer", "OPEN SOURCE", "application/ld+json"]) {
    assert.ok(html.includes(value), `missing ${value}`);
  }
  assert.ok(!html.includes("localhost"));
  assert.ok(!html.includes("Lorem ipsum"));
});

test("external links are secured", () => {
  const targets = [...html.matchAll(/<a[^>]+target="_blank"[^>]*>/g)].map((match) => match[0]);
  assert.ok(targets.length > 0);
  for (const tag of targets) assert.match(tag, /rel="noopener noreferrer"/);
});

test("Chinese localization and language switch are present", () => {
  assert.match(html, /data-language-switch/);
  assert.match(changelog, /data-language-switch/);
  for (const value of ["Windows 11 的临时空间", "下载安装程序", "更新日志 — DropSpace"]) {
    assert.ok(script.includes(value), `missing Chinese translation: ${value}`);
  }
  assert.match(script, /localStorage\.setItem\("dropspace-language"/);
});

test("production metadata uses the CDN deployment", () => {
  const origin = "https://cdn.jsdelivr.net/gh/airanluo-dot/DropSpace@main/website/";
  assert.ok(html.includes(`${origin}index.html`));
  assert.ok(changelog.includes(`${origin}changelog/index.html`));
});
