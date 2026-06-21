const { defineConfig, devices } = require('@playwright/test');

const port = Number(process.env.EPATA_PLAYWRIGHT_PORT || 5120);
const baseURL = process.env.EPATA_PLAYWRIGHT_BASE_URL || `http://127.0.0.1:${port}`;
const skipWebServer = process.env.EPATA_PLAYWRIGHT_SKIP_WEBSERVER === '1';

const config = {
  testDir: './tests/playwright',
  timeout: 90_000,
  expect: { timeout: 10_000 },
  fullyParallel: false,
  workers: 1,
  reporter: [['list']],
  use: {
    baseURL,
    trace: 'retain-on-failure',
    screenshot: 'only-on-failure',
    video: 'retain-on-failure',
  },
  projects: [
    {
      name: 'chromium-desktop',
      use: { ...devices['Desktop Chrome'], viewport: { width: 1440, height: 900 } },
    },
    {
      name: 'chromium-mobile',
      use: { ...devices['Pixel 5'] },
    },
  ],
};

if (!skipWebServer) {
  config.webServer = {
    command: `pwsh -NoProfile -ExecutionPolicy Bypass -File ./tools/playwright-server.ps1 -Port ${port}`,
    url: `${baseURL}/api/health`,
    reuseExistingServer: false,
    gracefulShutdown: { signal: 'SIGTERM', timeout: 500 },
    timeout: 120_000,
  };
}

module.exports = defineConfig(config);
