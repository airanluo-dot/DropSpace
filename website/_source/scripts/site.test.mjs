import assert from "node:assert/strict";
import { readdir, readFile, stat } from "node:fs/promises";
import test from "node:test";
import { JSDOM } from "jsdom";
import { validateLatestChangeApi, validateReleaseApi } from "./release-contract.mjs";

const read = (relative) => readFile(new URL(`../dist/${relative}`, import.meta.url), "utf8");
const releases = JSON.parse(await readFile(new URL("../data/releases.json", import.meta.url), "utf8"));
const en = await read("en/index.html");
const zh = await read("zh-cn/index.html");
const enChangelog = await read("en/changelog/index.html");
const zhChangelog = await read("zh-cn/changelog/index.html");
const cssHref = (en.match(/href="([^"]*styles\.[^"]+\.css)"/) ?? [])[1];
const css = await read(new URL(cssHref, "https://airanluo-dot.github.io/DropSpace/en/").pathname.replace(/^\/DropSpace\//, ""));
const releaseApi = JSON.parse(await read("api/v1/releases.json"));
const latestChangeApi = JSON.parse(await read("api/v1/latest-change.json"));

test("release data has a SemVer Stable with all downloads", () => {
  assert.match(releases.stable.tag, /^v\d+\.\d+\.\d+$/);
  for (const key of ["installer", "portable", "msix", "checksums"]) {
    assert.match(releases.stable.assets[key], /^https:\/\/github\.com\/airanluo-dot\/DropSpace\/releases\//);
  }
});

test("versioned release API is emitted for the app and runtime website", () => {
  assert.equal(validateReleaseApi(releaseApi), releaseApi);
  assert.equal(releaseApi.schemaVersion, 1);
  assert.ok(releaseApi.generatedAt);
  assert.ok(releaseApi.releases.some((release) => release.tagName === releases.stable.tag && !release.isPrerelease));
  assert.ok(releases.previews.length > 0);
  assert.ok(releaseApi.releases.some((release) => release.tagName === releases.previews[0].tag && release.isPrerelease));
  for (const release of releaseApi.releases) {
    assert.doesNotMatch(release.body, /dropspace\.pages\.dev/);
    assert.match(release.htmlUrl, /^https:\/\/github\.com\/airanluo-dot\/DropSpace\/releases\/tag\//);
    for (const asset of release.assets) {
      assert.ok(asset.size > 0);
      assert.equal(asset.downloadUrl, `https://github.com/airanluo-dot/DropSpace/releases/download/${release.tagName}/${asset.name}`);
    }
  }
});

test("latest-change API drives the large headline and variable summary list", () => {
  assert.equal(validateLatestChangeApi(latestChangeApi), latestChangeApi);
  for (const [html, locale] of [[en, "en"], [zh, "zh-CN"]]) {
    const document = new JSDOM(html).window.document;
    const release = latestChangeApi.release;
    assert.equal(document.querySelector("[data-latest-change-headline]")?.textContent, release.headline[locale]);
    assert.equal(document.querySelector("[data-latest-change-tag]")?.textContent, release.tagName);
    assert.equal(document.querySelector("[data-latest-change-title]")?.textContent, release.title);
    assert.equal(document.querySelector("[data-latest-change-url]")?.href, release.htmlUrl);
    assert.equal(document.querySelectorAll("[data-latest-change-highlights] > span").length, release.highlights[locale].length);
  }
});

test("build emits independent English and Simplified Chinese pages", () => {
  assert.match(en, /<html lang="en">/);
  assert.match(zh, /<html lang="zh-CN">/);
  assert.match(en, /Drag it\. Keep it\./);
  assert.match(zh, /拖进来。暂存好。/);
  assert.doesNotMatch(en, /localStorage\.setItem\("dropspace-language"/);
  assert.doesNotMatch(zh, /translatePage|createTreeWalker/);
});

test("localized metadata, canonical, hreflang, OG and structured data are complete", () => {
  for (const [html, route, title] of [[en, "en", "DropSpace — A Temporary Space for Windows"], [zh, "zh-cn", "DropSpace — Windows 临时空间"]]) {
    const document = new JSDOM(html).window.document;
    assert.equal(document.title, title);
    assert.equal(document.querySelector('link[rel="canonical"]')?.href, `https://airanluo-dot.github.io/DropSpace/${route}/`);
    assert.equal(document.querySelector('meta[property="og:url"]')?.content, `https://airanluo-dot.github.io/DropSpace/${route}/`);
    assert.ok(document.querySelector('meta[http-equiv="Content-Security-Policy"]')?.content.includes("sha256-"));
    assert.ok(document.querySelector('meta[http-equiv="Content-Security-Policy"]')?.content.includes("connect-src 'self'"));
    const structured = JSON.parse(document.querySelector('script[type="application/ld+json"]').textContent);
    assert.equal(structured.inLanguage, route === "en" ? "en" : "zh-CN");
    assert.equal(structured.softwareVersion, releases.stable.tag);
    assert.equal(document.querySelectorAll('link[rel="alternate"]').length, 3);
  }
});

test("homepage includes product, media, requirements, limitations and verification", () => {
  for (const value of ["Temporary Space", "Dynamic Island", "One Island.", "Ready for every drop.", "Clipboard History", "Download Installer", "PRODUCT PREVIEW", "SYSTEM REQUIREMENTS", "KNOWN LIMITATIONS", "VERIFY YOUR DOWNLOAD"]) {
    assert.ok(en.includes(value), `missing ${value}`);
  }
  assert.doesNotMatch(en, /data-mode="notch"|>Notch<|Two shapes\./);
  assert.doesNotMatch(zh, />刘海<|两种外形。/);
  assert.ok(en.includes("product-overview."));
  assert.ok(en.includes("drag-demo."));
  assert.ok(!en.includes("localhost"));
  assert.ok(!en.includes("Lorem ipsum"));
});

test("live Stable status keeps one green dot and a horizontal API-updated label", () => {
  for (const html of [en, zh]) {
    const document = new JSDOM(html).window.document;
    const status = document.querySelector(".stable-line");
    assert.ok(status);
    assert.equal(status.querySelectorAll(".release-live-dot").length, 1);
    assert.equal(status.querySelectorAll("[data-stable-version].release-live-copy").length, 1);
  }
  assert.match(css, /\.stable-line\{[^}]*display:flex!important[^}]*white-space:nowrap/);
  assert.match(css, /\.release-live-dot\{[^}]*width:7px[^}]*height:7px/);
  assert.match(css, /\.release-live-copy\{[^}]*width:auto[^}]*height:auto[^}]*background:none/);
  assert.doesNotMatch(css, /\.stable-line span\{/);
});

test("release data drives every download and changelog entry", () => {
  for (const url of Object.values(releases.stable.assets)) assert.ok(en.includes(url));
  for (const release of [releases.stable, ...releases.previews]) {
    assert.ok(enChangelog.includes(release.tag));
    assert.ok(zhChangelog.includes(release.tag));
    assert.ok(enChangelog.includes(release.url));
  }
});

test("all external blank-target links are secured", () => {
  for (const html of [en, zh, enChangelog, zhChangelog]) {
    const document = new JSDOM(html).window.document;
    for (const link of document.querySelectorAll('a[target="_blank"]')) {
      assert.equal(link.rel, "noopener noreferrer");
    }
  }
});

test("generated assets are content-versioned", async () => {
  const files = await readdir(new URL("../dist/assets/", import.meta.url));
  for (const prefix of ["styles", "script", "dropspace-logo", "favicon", "og-image"]) {
    assert.ok(files.some((file) => new RegExp(`^${prefix}\\.[a-f0-9]{12}\\.`).test(file)), `missing versioned ${prefix}`);
  }
});

test("active website logo and favicon retain true PNG alpha", async () => {
  const files = await readdir(new URL("../dist/assets/", import.meta.url));
  for (const prefix of ["dropspace-logo", "favicon"]) {
    const name = files.find((file) => new RegExp(`^${prefix}\\.[a-f0-9]{12}\\.png$`).test(file));
    assert.ok(name, `missing versioned ${prefix}`);
    const png = await readFile(new URL(`../dist/assets/${name}`, import.meta.url));
    assert.equal(png.subarray(1, 4).toString("ascii"), "PNG");
    assert.equal(png[25], 6, `${prefix} must use PNG color type 6 (RGBA)`);
  }
});

test("root, error page, robots and sitemap are production-safe", async () => {
  const root = await read("index.html");
  const notFound = await read("404.html");
  const sitemap = await read("sitemap.xml");
  assert.match(root, /location\.replace/);
  assert.match(root, /background:#050506/);
  assert.match(root, /Content-Security-Policy/);
  assert.doesNotMatch(root, /<script[^>]+src=/);
  assert.match(notFound, /noindex/);
  assert.match(sitemap, /\/en\//);
  assert.match(sitemap, /\/zh-cn\//);
});

test("all generated internal links and assets resolve", async () => {
  for (const [relative, html] of [["en/index.html", en], ["zh-cn/index.html", zh], ["en/changelog/index.html", enChangelog], ["zh-cn/changelog/index.html", zhChangelog]]) {
    const document = new JSDOM(html).window.document;
    for (const element of document.querySelectorAll("[href], [src], [poster]")) {
      const value = element.getAttribute("href") ?? element.getAttribute("src") ?? element.getAttribute("poster");
      if (!value || value.startsWith("#") || /^https?:/.test(value)) continue;
      const url = new URL(value, `https://airanluo-dot.github.io/DropSpace/${relative}`);
      if (!url.pathname.startsWith("/DropSpace/")) continue;
      let target = url.pathname.slice("/DropSpace/".length);
      if (!target || target.endsWith("/")) target += "index.html";
      await assert.doesNotReject(stat(new URL(`../dist/${target}`, import.meta.url)), `${relative} has broken ${value}`);
    }
  }
});
