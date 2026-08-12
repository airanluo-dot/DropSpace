import { expect, test } from "@playwright/test";

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
  await expect(page.locator("main")).toContainText("v0.1.0");
  const installer = page.locator('a[href*="DropSpaceSetup.exe"]').first();
  await expect(installer).toBeVisible();
  await expect(installer).toHaveAttribute("href", "https://github.com/airanluo-dot/DropSpace/releases/download/v0.1.0/DropSpaceSetup.exe");
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
