import { test, expect } from '@playwright/test';

for (const route of ['en', 'zh-cn']) {
  test(`Native Island showcase supports page selection and keyboard navigation (${route})`, async ({ page }) => {
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
    await expect(story.locator('a')).toHaveCount(0);
    await expect(page.locator('.widget-tile')).toHaveCount(8);
    const design = await page.evaluate(() => {
      const style = (selector) => getComputedStyle(document.querySelector(selector));
      return {
        stage: style('.native-stage').backgroundImage,
        originalStage: style('.mode-screen').backgroundImage,
        surface: style('.native-surface').backgroundColor,
        originalSurface: style('.mode-overlay.expanded').backgroundColor,
        radius: style('.native-surface').borderRadius,
        originalRadius: style('.mode-overlay.expanded').borderRadius,
        duration: style('.native-panel:not([hidden])').animationDuration,
        originalDuration: style('.space-popover').transitionDuration,
        easing: style('.native-panel:not([hidden])').animationTimingFunction,
        originalEasing: style('.space-popover').transitionTimingFunction
      };
    });
    expect(design.stage).toBe(design.originalStage);
    expect(design.surface).toBe(design.originalSurface);
    expect(design.radius).toBe(design.originalRadius);
    expect(design.duration).toBe(design.originalDuration);
    expect(design.easing).toBe(design.originalEasing);
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
    expect(await page.locator('#native-panel-music').evaluate(el => parseFloat(getComputedStyle(el).animationDuration))).toBeLessThan(0.001);
    expect(await page.evaluate(() => document.documentElement.scrollWidth <= window.innerWidth)).toBe(true);
    await story.screenshot({ path: `test-results/native-mobile-${route}.png` });
  });
}
