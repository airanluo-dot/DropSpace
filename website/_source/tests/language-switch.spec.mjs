import { expect, test } from "@playwright/test";
import { readFile } from "node:fs/promises";

const releases = JSON.parse(await readFile(new URL("../data/releases.json", import.meta.url), "utf8"));

test("root redirects before rendering a separate unstyled page", async ({ browser }) => {
  const english = await browser.newContext({ locale: "en-US" });
  const englishPage = await english.newPage();
  await englishPage.goto("/DropSpace/");
  await expect(englishPage).toHaveURL(/\/DropSpace\/en\/$/);
  await expect(englishPage).toHaveTitle("DropSpace — A Temporary Space for Windows");
  await english.close();

  const chinese = await browser.newContext({ locale: "zh-CN" });
  const chinesePage = await chinese.newPage();
  await chinesePage.goto("/DropSpace/");
  await expect(chinesePage).toHaveURL(/\/DropSpace\/zh-cn\/$/);
  await expect(chinesePage).toHaveTitle("DropSpace — Windows 临时空间");
  await chinese.close();

  const noScript = await browser.newContext({ javaScriptEnabled: false });
  const fallbackPage = await noScript.newPage();
  await fallbackPage.goto("/DropSpace/");
  await expect(fallbackPage.locator("body")).toHaveCSS("background-color", "rgb(5, 5, 6)");
  await expect(fallbackPage.getByRole("link", { name: "简体中文" })).toBeVisible();
  await noScript.close();
});

test("language switch navigates between complete static documents", async ({ page }) => {
  await page.goto("/DropSpace/en/");
  await expect(page).toHaveTitle("DropSpace — A Temporary Space for Windows");
  await expect(page.locator("html")).toHaveAttribute("lang", "en");
  await expect(page.locator("h1")).toContainText("Drag it. Keep it.");
  await page.locator("[data-language-switch]").click();
  await expect(page).toHaveURL(/\/DropSpace\/zh-cn\/$/);
  await expect(page).toHaveTitle("DropSpace — Windows 临时空间");
  await expect(page.locator("html")).toHaveAttribute("lang", "zh-CN");
  await expect(page.locator("h1")).toContainText("拖进来。暂存好。");
  await page.locator("[data-language-switch]").click();
  await expect(page).toHaveURL(/\/DropSpace\/en\/$/);
});

test("localized changelog and Stable downloads are real", async ({ page }) => {
  await page.goto("/DropSpace/zh-cn/changelog/");
  await expect(page).toHaveTitle("更新日志 — DropSpace");
  await expect(page.locator("html")).toHaveAttribute("lang", "zh-CN");
  await expect(page.locator("main")).toContainText(releases.stable.tag);
  const installer = page.locator('a[href*="DropSpaceSetup.exe"]').first();
  await expect(installer).toBeVisible();
  await expect(installer).toHaveAttribute("href", releases.stable.assets.installer);
});

test("live Stable status and Dynamic Island showcase keep their intended layout", async ({ page }) => {
  await page.goto("/DropSpace/zh-cn/");
  await expect(page.locator(".release-live-dot")).toHaveCount(1);
  await expect(page.locator(".release-live-copy")).toHaveText(/最新稳定版 · v\d+\.\d+\.\d+/);

  const stableLayout = await page.locator(".stable-line").evaluate((line) => {
    const dot = line.querySelector(".release-live-dot").getBoundingClientRect();
    const copy = line.querySelector(".release-live-copy").getBoundingClientRect();
    return {
      direction: getComputedStyle(line).flexDirection,
      writingMode: getComputedStyle(line).writingMode,
      dotWidth: dot.width,
      copyWidth: copy.width,
      verticalOverlap: Math.min(dot.bottom, copy.bottom) - Math.max(dot.top, copy.top)
    };
  });
  expect(stableLayout.direction).toBe("row");
  expect(stableLayout.writingMode).toBe("horizontal-tb");
  expect(stableLayout.dotWidth).toBe(7);
  expect(stableLayout.copyWidth).toBeGreaterThan(80);
  expect(stableLayout.verticalOverlap).toBeGreaterThan(0);

  const showcase = page.locator("section.modes");
  await expect(showcase).toContainText("一个灵动岛");
  await expect(showcase).toContainText("接住每一次拖放");
  await expect(showcase).not.toContainText("刘海");
  await expect(showcase.locator(".mode-overlay")).toHaveCount(3);
  await expect(showcase.locator("button")).toHaveCount(0);
});

test("reduced motion, system detection and narrow layout remain usable", async ({ page }) => {
  await page.emulateMedia({ reducedMotion: "reduce" });
  await page.setViewportSize({ width: 390, height: 844 });
  await page.goto("/DropSpace/zh-cn/");
  await expect(page.locator("[data-demo]")).toHaveAttribute("data-state", "expanded");
  await expect(page.locator("[data-system-check]")).not.toHaveText("正在检测此设备……");
  const overflow = await page.evaluate(() => document.documentElement.scrollWidth > document.documentElement.clientWidth);
  expect(overflow).toBe(false);
  await expect(page.locator("[data-language-switch]")).toBeVisible();
});

test("latest-change API updates the large release story with any supported summary count", async ({ page }) => {
  await page.route("**/api/v1/latest-change.json", async (route) => {
    await route.fulfill({
      contentType: "application/json",
      body: JSON.stringify({
        schemaVersion: 1,
        generatedAt: "2026-08-20T00:01:00Z",
        source: "github-releases",
        release: {
          tagName: "v0.2.1-preview.1",
          channel: "preview",
          headline: { en: "Latest Preview.", "zh-CN": "最新预览版。" },
          title: "Release-driven latest changes",
          publishedAt: "2026-08-20T00:00:00Z",
          htmlUrl: "https://github.com/airanluo-dot/DropSpace/releases/tag/v0.2.1-preview.1",
          highlights: { en: ["One", "Two", "Three"], "zh-CN": ["第一项", "第二项", "第三项"] }
        }
      })
    });
  });
  await page.setViewportSize({ width: 390, height: 844 });
  await page.goto("/DropSpace/zh-cn/");
  await expect(page.locator("html")).toHaveAttribute("data-latest-change-api", "current");
  await expect(page.locator("[data-latest-change-headline]")).toHaveText("最新预览版。");
  await expect(page.locator("[data-latest-change-tag]")).toHaveText("v0.2.1-preview.1");
  await expect(page.locator("[data-latest-change-highlights] > span")).toHaveCount(3);
  await expect(page.locator("[data-latest-change-url]")).toHaveAttribute("href", /v0\.2\.1-preview\.1$/);
  expect(await page.evaluate(() => document.documentElement.scrollWidth > document.documentElement.clientWidth)).toBe(false);
});
