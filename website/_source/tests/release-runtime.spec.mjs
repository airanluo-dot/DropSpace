import { expect, test } from "@playwright/test";
import { readFile } from "node:fs/promises";

const fixture = JSON.parse(await readFile(new URL("../data/releases.json", import.meta.url), "utf8"));
const stable = fixture.api.releases.find((release) => release.tagName === fixture.stable.tag);

function stableRelease(tagName, publishedAt) {
  return {
    ...stable,
    tagName,
    name: `DropSpace ${tagName}`,
    publishedAt,
    htmlUrl: `https://github.com/airanluo-dot/DropSpace/releases/tag/${tagName}`,
    assets: stable.assets.map((asset) => ({
      ...asset,
      downloadUrl: `https://github.com/airanluo-dot/DropSpace/releases/download/${tagName}/${asset.name}`
    }))
  };
}

async function serveReleaseApi(page, releases) {
  await page.route("**/api/v1/releases.json", (route) => route.fulfill({
    contentType: "application/json",
    body: JSON.stringify({
      schemaVersion: 1,
      generatedAt: "2026-09-30T00:00:00Z",
      source: "github-releases",
      releases
    })
  }));
}

for (const [route, expected, fallback] of [
  ["en", "Stable v0.3.0: 64-bit Windows build 28000 or later", "Stable v0.4.0: 64-bit Windows · See release notes for system requirements"],
  ["zh-cn", "稳定版 v0.3.0: 需要 64 位 Windows Build 28000 或更高版本", "稳定版 v0.4.0: 64 位 Windows · 系统要求请查看发布说明"]
]) {
  test(`runtime updates Stable requirements with its downloads (${route})`, async ({ page }) => {
    const known = stableRelease("v0.3.0", "2026-09-28T00:00:00Z");
    known.body = "Requires 64-bit Windows build 28000 or later.";
    await serveReleaseApi(page, [known]);
    await page.goto(`/DropSpace/${route}/`);
    await expect(page.locator("html")).toHaveAttribute("data-release-api", "current");
    await expect(page.locator("[data-stable-requirements]")).toHaveText([expected, expected, expected]);
    await expect(page.locator('[data-download="installer"]').first()).toHaveAttribute("href", known.assets.find((asset) => asset.kind === "installer").downloadUrl);

    const unknown = stableRelease("v0.4.0", "2026-09-29T00:00:00Z");
    unknown.body = "See this release's compatibility notes.";
    await serveReleaseApi(page, [unknown]);
    await page.reload();
    await expect(page.locator("html")).toHaveAttribute("data-release-api", "current");
    await expect(page.locator("[data-stable-requirements]")).toHaveText([fallback, fallback, fallback]);
    await expect(page.locator('[data-download="installer"]').first()).toHaveAttribute("href", unknown.assets.find((asset) => asset.kind === "installer").downloadUrl);
  });
}

test("runtime Stable downloads choose the highest version after an older release is republished", async ({ page }) => {
  const newest = stableRelease("v0.3.0", "2026-09-28T00:00:00Z");
  const republished = stableRelease("v0.2.1", "2026-09-29T00:00:00Z");
  await serveReleaseApi(page, [republished, newest]);
  await page.goto("/DropSpace/en/");
  await expect(page.locator("html")).toHaveAttribute("data-release-api", "current");
  await expect(page.locator("[data-stable-version]").first()).toContainText(newest.tagName);
  await expect(page.locator('[data-download="installer"]').first()).toHaveAttribute("href", newest.assets.find((asset) => asset.kind === "installer").downloadUrl);
});

test("runtime rejects an asset kind that disagrees with the official artifact name", async ({ page }) => {
  const invalid = stableRelease("v0.3.0", "2026-09-29T00:00:00Z");
  invalid.assets = invalid.assets.filter((asset) => asset.kind !== "installer");
  invalid.assets.find((asset) => asset.name === "SHA256SUMS.txt").kind = "installer";
  await serveReleaseApi(page, [invalid]);
  await page.goto("/DropSpace/en/");
  await expect(page.locator("html")).toHaveAttribute("data-release-api", "build-snapshot");
  await expect(page.locator('[data-download="installer"]').first()).toHaveAttribute("href", fixture.stable.assets.installer);
});

test("runtime retains the changelog snapshot when release metadata is malformed", async ({ page }) => {
  const invalid = stableRelease("v0.3.0", "2026-09-29T00:00:00Z");
  invalid.body = null;
  await serveReleaseApi(page, [invalid]);
  await page.goto("/DropSpace/en/changelog/");
  await expect(page.locator("html")).toHaveAttribute("data-release-api", "build-snapshot");
  await expect(page.locator('[data-download="installer"]').first()).toHaveAttribute("href", fixture.stable.assets.installer);
});

test("runtime uses official asset names when optional artifact kinds are absent", async ({ page }) => {
  const release = stableRelease("v0.3.0", "2026-09-29T00:00:00Z");
  release.assets.forEach((asset) => { delete asset.kind; });
  await serveReleaseApi(page, [release]);
  await page.goto("/DropSpace/en/");
  await expect(page.locator("html")).toHaveAttribute("data-release-api", "current");
  await expect(page.locator("[data-stable-version]").first()).toContainText(release.tagName);
  await expect(page.locator('[data-download="installer"]').first()).toHaveAttribute("href", release.assets.find((asset) => asset.name === "DropSpaceSetup.exe").downloadUrl);
});
