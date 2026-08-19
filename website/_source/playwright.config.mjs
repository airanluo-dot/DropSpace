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
    trace: "retain-on-failure"
  },
  webServer: {
    command: "node scripts/serve.mjs",
    url: "http://127.0.0.1:4173/DropSpace/en/",
    reuseExistingServer: true
  }
});
