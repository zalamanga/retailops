import { expect, test } from '@playwright/test';

test('workspace access is keyboard reachable and fits a narrow viewport', async ({ page }) => {
  await page.setViewportSize({ width: 375, height: 812 });
  await page.goto('/');
  await expect(
    page.getByRole('heading', { name: /Set up your workspace|Welcome back/ }),
  ).toBeVisible({ timeout: 30_000 });
  await expect(page.getByRole('textbox', { name: 'Email address' })).toBeVisible();
  await expect(page.getByRole('button', { name: /Create workspace|Sign in/ })).toBeVisible();
  expect(await page.evaluate(() => document.documentElement.scrollWidth <= window.innerWidth)).toBe(
    true,
  );
  await page.getByRole('textbox', { name: 'Email address' }).focus();
  await page.keyboard.press('Tab');
  await expect(page.getByLabel('Password', { exact: true })).toBeFocused();
});
