import { defineConfig, devices } from '@playwright/test';

/**
 * The journey runs against the full stack from docker-compose.yml (the production image, PostgreSQL, Mailpit):
 * docker compose up --build --wait, then npm test here. E2E_BASE_URL and E2E_MAILPIT_URL point elsewhere if needed.
 */
export default defineConfig({
  testDir: '.',
  timeout: 120_000,
  expect: { timeout: 15_000 },
  fullyParallel: false,
  forbidOnly: !!process.env['CI'],
  retries: 0,
  reporter: process.env['CI'] ? [['list'], ['html', { open: 'never' }]] : 'list',
  use: {
    baseURL: process.env['E2E_BASE_URL'] ?? 'http://localhost:8080',
    trace: 'retain-on-failure',
    screenshot: 'only-on-failure',
  },
  projects: [{ name: 'chromium', use: { ...devices['Desktop Chrome'] } }],
});
