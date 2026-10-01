import { defineConfig } from "@playwright/test";

export default defineConfig({
  testDir: "./tests",
  fullyParallel: false,
  use: {
    baseURL: "http://127.0.0.1:4173/DropSpace",
    // GitHub-hosted Ubuntu runners include Chrome. Reusing it keeps the
    // deployment gate independent of Playwright's browser-download service;
    // local development continues to use Playwright's bundled Chromium.
    channel: process.env.CI ? "chrome" : undefined,
    viewport: { width: 1440, height: 900 },
    launchOptions: process.env.PLAYWRIGHT_CHROMIUM_EXECUTABLE_PATH ? { executablePath: process.env.PLAYWRIGHT_CHROMIUM_EXECUTABLE_PATH } : {},
    trace: "retain-on-failure"
  },
  webServer: [{
    command: "node scripts/serve.mjs",
    url: "http://127.0.0.1:4173/DropSpace/en/",
    reuseExistingServer: true
  }, {
    command: "node scripts/build-static.mjs && node scripts/serve.mjs --static",
    url: "http://127.0.0.1:4174/en/",
    reuseExistingServer: true
  }]
});
