import assert from "node:assert/strict";
import fs from "node:fs/promises";
import { Script } from "node:vm";
import test from "node:test";
import { JSDOM } from "jsdom";
import { stableRequirements } from "../src/release-requirements.js";

test("rejected release cannot change retained snapshot requirements after a language change", async () => {
  const baseline = { tagName: "v1.0.0", body: "Requires 64-bit Windows build 20348 or later." };
  const newer = {
    tagName: "v1.1.0", name: "Incomplete release", body: "Requires 64-bit Windows build 26100 or later.",
    htmlUrl: "https://github.com/airanluo-dot/DropSpace/releases/tag/v1.1.0",
    isDraft: false, isPrerelease: false, publishedAt: "2026-10-31T00:00:00Z",
    // Its schema is valid, but missing required artifacts prevents publication to the UI.
    assets: [{ name: "DropSpace.exe", size: 100, kind: "portable",
      downloadUrl: "https://github.com/airanluo-dot/DropSpace/releases/download/v1.1.0/DropSpace.exe" }],
  };
  const originalDownload = "https://github.com/airanluo-dot/DropSpace/releases/download/v1.0.0/DropSpace.exe";
  const dom = new JSDOM(`<p data-stable-requirements></p><a data-download="portable"></a><span data-stable-tag>v1.0.0</span>`,
    { url: "https://airanluo-dot.github.io/DropSpace/", runScripts: "outside-only" });
  try {
    const { window } = dom;
    const requirements = window.document.querySelector("[data-stable-requirements]");
    requirements.dataset.requirementRelease = JSON.stringify(baseline);
    window.document.querySelector("[data-download]").href = originalDownload;
    window.DropSpaceI18n = {
      language: "en-US", apply() {},
      t(key, args = {}) {
        if (key === "release.stable") return "Stable";
        if (key === "requirements.minimum") return `${this.language}: ${args.channel} ${args.tag} ${args.windows} ${args.build}`;
        return key;
      },
    };
    const translate = (key, args) => window.DropSpaceI18n.t(key, args);
    window.stableRequirements = (release, locale) => stableRequirements(release, locale, translate);
    window.matchMedia = () => ({ matches: false, addEventListener() {} });
    window.IntersectionObserver = class { observe() {} };
    window.AbortSignal = AbortSignal;
    window.fetch = async endpoint => String(endpoint).endsWith("latest-change.json")
      ? new Response(null, { status: 404 })
      : Response.json({ schemaVersion: 1, source: "github-releases", generatedAt: "2026-10-31T00:00:00Z", releases: [newer] });

    // Execute the production runtime with its real requirement formatter. Replace only
    // module syntax and expose its existing refresh promise so no timing polling is needed.
    const filename = new URL("../src/script.js", import.meta.url);
    const source = (await fs.readFile(filename, "utf8"))
      .replace('import { stableRequirements } from "./release-requirements.js";', "")
      .replaceAll("import.meta.url", JSON.stringify(filename.href))
      .replace("void Promise.all([refreshReleaseData(), refreshLatestChangeData()]);",
        "globalThis.releaseRefresh = Promise.all([refreshReleaseData(), refreshLatestChangeData()]);");
    new Script(source, { filename: "script.js" }).runInContext(dom.getInternalVMContext());
    await window.releaseRefresh;
    assert.equal(window.document.documentElement.dataset.releaseApi, "build-snapshot");
    assert.match(requirements.textContent, /v1\.0\.0.*20348/);

    window.DropSpaceI18n.language = "fr-FR";
    window.dispatchEvent(new window.Event("dropspace-language-change"));
    assert.match(requirements.textContent, /^fr-FR: Stable v1\.0\.0 .*20348$/);
    assert.equal(window.document.querySelector("[data-download]").href, originalDownload);
    assert.equal(window.document.querySelector("[data-stable-tag]").textContent, "v1.0.0");
  } finally { dom.window.close(); }
});
