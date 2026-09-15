import { test, expect } from '@playwright/test';

for (const route of ['en', 'zh-cn']) {
  test(`Beta 24 showcase supports page selection and keyboard navigation (${route})`, async ({ page }) => {
    await page.goto(`/DropSpace/${route}/`);
    const story = page.locator('#native-island');
    await story.scrollIntoViewIfNeeded();
    const tabs = story.getByRole('tab');
    await expect(tabs).toHaveCount(4);
    await tabs.nth(1).click();
    await expect(page.locator('#native-panel-music')).toBeVisible();
    await expect(page.locator('#native-panel-widgets')).toBeHidden();
    await tabs.nth(1).press('End');
    await expect(tabs.nth(3)).toBeFocused();
    await expect(page.locator('#native-panel-clipboard')).toBeVisible();
    await tabs.nth(3).press('ArrowRight');
    await expect(tabs.nth(3)).toBeFocused();
    await tabs.nth(3).press('Home');
    await expect(page.locator('#native-panel-widgets')).toBeVisible();
    await expect(story.locator('a')).toHaveAttribute('href', /v0\.3\.0-beta\.24\/DropSpaceSetup\.exe$/);
    await expect(page.locator('.widget-tile')).toHaveCount(8);
    if (route === 'zh-cn') {
      await expect(tabs.nth(0)).toHaveText('小组件');
      await expect(page.locator('#music-title')).toContainText('一直在身边');
    }
    await story.screenshot({ path: `test-results/native-${route}.png` });
    await page.locator('#widgets').screenshot({ path: `test-results/widgets-${route}.png` });
    await page.locator('#music').screenshot({ path: `test-results/music-${route}.png` });
    await page.setViewportSize({width: 390, height: 844});
    await page.emulateMedia({reducedMotion: 'reduce'});
    await story.scrollIntoViewIfNeeded();
    await tabs.nth(1).click();
    await expect(page.locator('#native-panel-music')).toBeVisible();
    expect(await page.evaluate(() => document.documentElement.scrollWidth <= window.innerWidth)).toBe(true);
    await story.screenshot({ path: `test-results/native-mobile-${route}.png` });
  });
}
