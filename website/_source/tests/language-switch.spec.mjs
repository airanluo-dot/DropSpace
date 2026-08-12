import { expect, test } from "@playwright/test";

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
