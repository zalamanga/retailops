import { expect, test, type Page, type Response } from '@playwright/test';
import { mkdirSync, readFileSync } from 'node:fs';
import { resolve } from 'node:path';

const password = 'ReviewWorkspaceAccess947!';
const adminEmail = 'operations@retailops.test';
const managerEmail = 'manager@retailops.test';
const viewerEmail = 'records@retailops.test';

test('purchasing, receiving, stock control, staff access, and mobile workspace', async ({ page }) => {
  test.skip(process.env.RETAILOPS_E2E !== '1', 'Use a dedicated verification database and set RETAILOPS_E2E=1.');
  test.setTimeout(300_000);
  const faults: string[] = [];
  page.on('pageerror', (error) => faults.push(error.message));
  page.on('response', (response) => {
    if (response.url().includes('/api/') && response.status() >= 500)
      faults.push(`${response.status()} ${response.url()}`);
  });
  const suffix = Date.now().toString().slice(-6);

  await page.setViewportSize({ width: 1440, height: 1000 });
  await page.goto('/');
  const setup = await page.request.get('/api/auth/status');
  if ((await setup.json()).setupRequired) {
    await page.getByLabel('Organization name', { exact: true }).fill('RetailOps Verification');
    await page.getByLabel('Your full name', { exact: true }).fill('Operations administrator');
    await page.getByLabel('Email address', { exact: true }).fill(adminEmail);
    await page.getByLabel('Password', { exact: true }).fill(password);
    const created = page.waitForResponse((response) => new URL(response.url()).pathname === '/api/auth/setup' && response.request().method() === 'POST', { timeout: 45_000 });
    await page.getByRole('button', { name: 'Create workspace', exact: true }).click();
    expect((await created).status()).toBe(200);
  } else {
    await signIn(page, adminEmail);
  }
  await expect(page.getByRole('heading', { name: 'Operations overview', exact: true })).toBeVisible();

  await page.goto('/#/team');
  const team = (await (await page.request.get('/api/users?pageSize=100')).json()).items;
  for (const [email, name, role] of [
    [managerEmail, 'Purchasing manager', 'Manager'],
    [viewerEmail, 'Records reviewer', 'Viewer'],
  ]) {
    const existing = team.find((member: { email: string }) => member.email === email);
    if (existing) {
      if (!existing.isActive) {
        await page.getByRole('button', { name: `Edit access for ${name}`, exact: true }).click();
        const dialog = page.getByRole('dialog');
        await dialog.getByLabel('Allow this person to sign in', { exact: true }).check();
        await dialog.getByRole('button', { name: 'Save access', exact: true }).click();
        await expect(dialog).not.toBeVisible();
      }
      continue;
    }
    await page.getByRole('button', { name: 'Add team member', exact: true }).click();
    const dialog = page.getByRole('dialog');
    await dialog.getByLabel('Full name', { exact: true }).fill(name);
    await dialog.getByLabel('Email address', { exact: true }).fill(email);
    await dialog.getByLabel('Initial password', { exact: true }).fill(password);
    await dialog.getByRole('combobox', { name: 'Role', exact: true }).selectOption(role);
    await dialog.getByRole('button', { name: 'Create team member', exact: true }).click();
    await expect(dialog).not.toBeVisible();
  }

  await page.goto('/#/products');
  const products: { id: string; sku: string; name: string; cost: number; reorder: number }[] = [];
  for (const entry of [
    { sku: `BOX-${suffix}`, name: 'Corrugated shipping carton', cost: 12500, reorder: 30 },
    { sku: `LBL-${suffix}`, name: 'Thermal shipping label roll', cost: 18500, reorder: 20 },
    { sku: `MLR-${suffix}`, name: 'Protective document mailer', cost: 4500, reorder: 40 },
  ]) {
    await page.getByRole('button', { name: 'Add product', exact: true }).click();
    const dialog = page.getByRole('dialog');
    await dialog.getByLabel('SKU', { exact: true }).fill(entry.sku);
    await dialog.getByLabel('Product name', { exact: true }).fill(entry.name);
    await dialog.getByLabel('Unit', { exact: true }).fill('unit');
    await dialog.getByLabel('Reorder point', { exact: true }).fill(String(entry.reorder));
    await dialog.getByLabel('Standard cost (IDR)', { exact: true }).fill(String(entry.cost));
    const response = waitForWrite(page, '/api/products');
    await dialog.getByRole('button', { name: 'Add product', exact: true }).click();
    products.push({ ...entry, id: (await (await response).json()).id });
    await expect(dialog).not.toBeVisible();
  }
  await page.getByRole('searchbox', { name: 'Search', exact: true }).fill(products[0].sku);
  await page.getByRole('row').filter({ hasText: products[0].sku }).getByRole('button', { name: `Edit ${products[0].name}`, exact: true }).click();
  let productDialog = page.getByRole('dialog');
  await productDialog.getByLabel('Active and available for new purchase orders', { exact: true }).uncheck();
  await productDialog.getByRole('button', { name: 'Save product', exact: true }).click();
  await expect(productDialog).not.toBeVisible();
  await expect(page.getByText('No matching products', { exact: true })).toBeVisible();
  await page.getByLabel('Include archived products', { exact: true }).check();
  await expect(page.getByText('Archived', { exact: true })).toBeVisible();
  await page.getByRole('row').filter({ hasText: products[0].sku }).getByRole('button', { name: `Edit ${products[0].name}`, exact: true }).click();
  productDialog = page.getByRole('dialog');
  await productDialog.getByLabel('Active and available for new purchase orders', { exact: true }).check();
  await productDialog.getByRole('button', { name: 'Save product', exact: true }).click();
  await expect(productDialog).not.toBeVisible();
  await page.getByLabel('Include archived products', { exact: true }).uncheck();
  await page.getByRole('searchbox', { name: 'Search', exact: true }).fill('');
  await capture(page, 'products-desktop');

  await page.goto('/#/suppliers');
  await page.getByRole('button', { name: 'Add supplier', exact: true }).click();
  let dialog = page.getByRole('dialog');
  await dialog.getByLabel('Supplier name', { exact: true }).fill(`Packaging supply ${suffix}`);
  await dialog.getByLabel('Contact email (optional)', { exact: true }).fill('purchasing@example.test');
  await dialog.getByLabel('Phone (optional)', { exact: true }).fill('0215550100');
  await dialog.getByLabel('Lead time in days', { exact: true }).fill('4');
  let response = waitForWrite(page, '/api/suppliers');
  await dialog.getByRole('button', { name: 'Add supplier', exact: true }).click();
  const supplier = await (await response).json();
  await expect(dialog).not.toBeVisible();
  await page.getByRole('button', { name: `Edit ${supplier.name}`, exact: true }).click();
  dialog = page.getByRole('dialog');
  await dialog.getByLabel('Lead time in days', { exact: true }).fill('5');
  await dialog.getByRole('button', { name: 'Save supplier', exact: true }).click();
  await expect(dialog).not.toBeVisible();

  await page.goto('/#/warehouses');
  const warehouses: { id: string; name: string }[] = [];
  for (const [code, name] of [[`M-${suffix}`, 'Main warehouse'], [`E-${suffix}`, 'East distribution']]) {
    await page.getByRole('button', { name: 'Add warehouse', exact: true }).click();
    dialog = page.getByRole('dialog');
    await dialog.getByLabel('Warehouse code', { exact: true }).fill(code);
    await dialog.getByLabel('Warehouse name', { exact: true }).fill(name);
    response = waitForWrite(page, '/api/warehouses');
    await dialog.getByRole('button', { name: 'Add warehouse', exact: true }).click();
    warehouses.push(await (await response).json());
    await expect(dialog).not.toBeVisible();
  }

  await page.goto('/#/orders');
  await createOrder(page, supplier.id, warehouses[0].id, products[0].id, 80, products[0].cost);
  const orderUrl = page.url();
  await page.getByRole('button', { name: 'Submit for approval', exact: true }).click();
  await expect(page.getByText(/^Pending approval$/i).first()).toBeVisible();
  await expect(page.getByRole('button', { name: 'Approve order', exact: true })).toBeDisabled();
  await page.getByRole('button', { name: 'Sign out', exact: true }).click();
  await signIn(page, managerEmail);
  await expect(page.getByRole('link', { name: 'Team & access', exact: true })).toHaveCount(0);
  await page.goto(orderUrl);
  await page.getByRole('button', { name: 'Approve order', exact: true }).click();
  await expect(page.getByText('Approved', { exact: true }).first()).toBeVisible();
  await receive(page, products[0].name, 35);
  await expect(page.getByText(/^Partially received$/i).first()).toBeVisible();
  await capture(page, 'purchase-order-desktop');
  await receive(page, products[0].name, 45);
  await expect(page.getByText('Received', { exact: true }).first()).toBeVisible();

  await page.goto('/#/inventory');
  await page.getByRole('button', { name: 'Adjust stock', exact: true }).click();
  dialog = page.getByRole('dialog');
  await dialog.getByRole('combobox', { name: 'Product', exact: true }).selectOption(products[0].id);
  await dialog.getByRole('combobox', { name: 'Warehouse', exact: true }).selectOption(warehouses[0].id);
  await dialog.getByLabel('Quantity change', { exact: true }).fill('-9999');
  await dialog.getByLabel('Reason', { exact: true }).fill('Physical stock reconciliation');
  await dialog.getByRole('button', { name: 'Record adjustment', exact: true }).click();
  await expect(dialog.getByRole('alert')).toBeVisible();
  await dialog.getByLabel('Quantity change', { exact: true }).fill('-3');
  await dialog.getByRole('button', { name: 'Record adjustment', exact: true }).click();
  await expect(dialog).not.toBeVisible();

  await page.getByRole('searchbox', { name: 'Search', exact: true }).fill(products[0].sku);
  await page.getByRole('combobox', { name: 'Warehouse', exact: true }).selectOption(warehouses[0].id);
  await page.getByRole('button', { name: `Transfer ${products[0].name} from ${warehouses[0].name}`, exact: true }).click();
  dialog = page.getByRole('dialog');
  await dialog.getByRole('combobox', { name: 'To warehouse', exact: true }).selectOption(warehouses[1].id);
  await dialog.getByLabel('Units to transfer', { exact: true }).fill('20');
  await dialog.getByLabel('Reason', { exact: true }).fill('Replenish distribution packing station');
  await dialog.getByRole('button', { name: 'Record transfer', exact: true }).click();
  await expect(dialog).not.toBeVisible();
  await page.getByRole('button', { name: 'Adjust stock', exact: true }).click();
  dialog = page.getByRole('dialog');
  await dialog.getByRole('combobox', { name: 'Product', exact: true }).selectOption(products[1].id);
  await dialog.getByRole('combobox', { name: 'Warehouse', exact: true }).selectOption(warehouses[0].id);
  await dialog.getByLabel('Quantity change', { exact: true }).fill('24');
  await dialog.getByLabel('Reason', { exact: true }).fill('Opening physical count');
  await dialog.getByRole('button', { name: 'Record adjustment', exact: true }).click();
  await expect(dialog).not.toBeVisible();
  const inventory = await (await page.request.get(`/api/inventory?search=${products[0].sku}`)).json();
  expect(inventory.items.find((item: { warehouseId: string }) => item.warehouseId === warehouses[0].id).onHand).toBe(57);
  expect(inventory.items.find((item: { warehouseId: string }) => item.warehouseId === warehouses[1].id).onHand).toBe(20);
  const download = page.waitForEvent('download');
  await page.getByRole('button', { name: 'Export CSV', exact: true }).click();
  const exported = await download;
  expect(exported.suggestedFilename()).toMatch(/\.csv$/);
  const csv = readFileSync((await exported.path())!, 'utf8');
  expect(csv).toContain(products[0].sku);
  expect(csv).not.toContain(products[1].sku);
  expect(csv).not.toContain(warehouses[1].name);
  await page.getByRole('searchbox', { name: 'Search', exact: true }).fill('');
  await page.getByRole('combobox', { name: 'Warehouse', exact: true }).selectOption('');
  await page.getByLabel('Low stock only', { exact: true }).check();
  await expect(page.getByRole('row').filter({ hasText: products[0].sku }).filter({ hasText: warehouses[0].name })).toHaveCount(0);
  await page.getByLabel('Low stock only', { exact: true }).uncheck();
  await capture(page, 'inventory-desktop');

  await page.goto('/#/orders');
  await createOrder(page, supplier.id, warehouses[0].id, products[2].id, 5, products[2].cost);
  await page.getByRole('button', { name: 'Cancel order', exact: true }).click();
  dialog = page.getByRole('dialog');
  await dialog.getByLabel('Reason', { exact: true }).fill('Duplicate purchase request identified before approval');
  await dialog.getByRole('button', { name: 'Cancel order', exact: true }).click();
  await expect(dialog).not.toBeVisible();
  await expect(page.getByText('Cancelled', { exact: true }).first()).toBeVisible();
  await page.getByRole('link', { name: 'Back to purchase orders', exact: true }).click();
  await createOrder(page, supplier.id, warehouses[0].id, products[2].id, 10, products[2].cost);
  await page.getByRole('button', { name: 'Submit for approval', exact: true }).click();
  await page.getByRole('button', { name: 'Reject order', exact: true }).click();
  dialog = page.getByRole('dialog');
  await dialog.getByLabel('Reason', { exact: true }).fill('Purchase quantity requires a revised request');
  await dialog.getByRole('button', { name: 'Reject order', exact: true }).click();
  await expect(dialog).not.toBeVisible();
  await expect(page.getByText('Rejected', { exact: true }).first()).toBeVisible();
  await page.getByRole('link', { name: 'Back to purchase orders', exact: true }).click();
  await createOrder(page, supplier.id, warehouses[0].id, products[2].id, 120, products[2].cost);
  await page.getByRole('button', { name: 'Submit for approval', exact: true }).click();
  await page.goto('/#/movements');
  await expect(page.getByRole('heading', { name: 'Stock movements', exact: true })).toBeVisible();
  await expect(page.getByText('Replenish distribution packing station').first()).toBeVisible();
  await page.goto('/#/activity');
  await expect(page.getByRole('heading', { name: 'Activity log', exact: true })).toBeVisible();
  if (await page.getByRole('button', { name: 'Next', exact: true }).isEnabled()) {
    await page.getByRole('button', { name: 'Next', exact: true }).click();
    await expect(page.getByText(/^Page 2 of/)).toBeVisible();
    await page.getByRole('button', { name: 'Previous', exact: true }).click();
    await expect(page.getByText(/^Page 1 of/)).toBeVisible();
  }
  await capture(page, 'activity-desktop');
  await page.goto('/#/');
  await expect(page.getByRole('heading', { name: 'Operations overview', exact: true })).toBeVisible();
  await expect(page.getByText('Loading', { exact: true })).toHaveCount(0);
  await capture(page, 'overview-desktop');

  await page.setViewportSize({ width: 390, height: 844 });
  await assertFits(page);
  await capture(page, 'overview-mobile');
  const menu = page.getByRole('button', { name: /Open navigation|Open menu|Menu/ });
  await menu.click();
  await page.keyboard.press('Escape');
  await expect(menu).toBeFocused();
  await expect(page.getByRole('link', { name: 'Inventory', exact: true })).not.toBeVisible();
  await page.getByRole('button', { name: /Open navigation|Open menu|Menu/ }).click();
  await page.getByRole('link', { name: 'Inventory', exact: true }).click();
  await expect(page.getByRole('heading', { name: 'Inventory', exact: true })).toBeVisible();
  await assertFits(page);
  const mobileTable = page.getByRole('region', { name: 'Warehouse inventory', exact: true });
  await mobileTable.focus();
  await page.keyboard.press('ArrowRight');
  await expect.poll(() => mobileTable.evaluate((element) => element.scrollLeft)).toBeGreaterThan(0);
  await mobileTable.evaluate((element) => { element.scrollLeft = 0; });
  await capture(page, 'inventory-mobile');
  await page.setViewportSize({ width: 768, height: 1024 });
  await assertFits(page);
  await page.evaluate(() => { document.documentElement.style.fontSize = '200%'; });
  await assertFits(page);
  await expect(page.getByRole('heading', { name: 'Inventory', exact: true })).toBeVisible();
  await page.evaluate(() => { document.documentElement.style.fontSize = ''; });
  await page.setViewportSize({ width: 1440, height: 1000 });
  await page.getByRole('button', { name: 'Sign out', exact: true }).click();
  await signIn(page, viewerEmail);
  await page.goto('/#/inventory');
  await expect(page.getByRole('button', { name: 'Export CSV', exact: true })).toBeVisible();
  await expect(page.getByRole('button', { name: 'Adjust stock', exact: true })).toHaveCount(0);
  await expect(page.getByRole('link', { name: 'Team & access', exact: true })).toHaveCount(0);
  await page.goto('/#/products');
  await expect(page.getByRole('button', { name: 'Add product', exact: true })).toHaveCount(0);
  await page.getByRole('button', { name: 'Sign out', exact: true }).click();
  await signIn(page, adminEmail);
  await page.goto('/#/team');
  await page.getByRole('button', { name: 'Edit access for Records reviewer', exact: true }).click();
  dialog = page.getByRole('dialog');
  await dialog.getByLabel('Allow this person to sign in', { exact: true }).uncheck();
  await dialog.getByRole('button', { name: 'Save access', exact: true }).click();
  await expect(dialog).not.toBeVisible();
  await expect(page.getByText('Deactivated', { exact: true })).toBeVisible();
  await page.getByRole('button', { name: 'Add team member', exact: true }).click();
  await expect(page.getByRole('dialog')).toBeVisible();
  await page.keyboard.press('Escape');
  await expect(page.getByRole('dialog')).not.toBeVisible();
  await expect(page.getByRole('button', { name: 'Add team member', exact: true })).toBeFocused();
  await page.getByRole('button', { name: 'Sign out', exact: true }).click();
  await page.getByLabel('Email address', { exact: true }).fill(viewerEmail);
  await page.getByLabel('Password', { exact: true }).fill(password);
  await page.getByRole('button', { name: 'Sign in', exact: true }).click();
  await expect(page.getByRole('alert')).toBeVisible();
  await expect(page.getByRole('heading', { name: 'Welcome back', exact: true })).toBeVisible();
  expect(faults).toEqual([]);
});

function waitForWrite(page: Page, pathname: string): Promise<Response> {
  return page.waitForResponse((response) => new URL(response.url()).pathname === pathname && response.request().method() === 'POST' && response.ok());
}

async function signIn(page: Page, email: string) {
  await expect(page.getByRole('heading', { name: 'Welcome back', exact: true })).toBeVisible();
  await page.getByLabel('Email address', { exact: true }).fill(email);
  await page.getByLabel('Password', { exact: true }).fill(password);
  const authenticated = page.waitForResponse((response) => new URL(response.url()).pathname === '/api/auth/login' && response.request().method() === 'POST', { timeout: 45_000 });
  await page.getByRole('button', { name: 'Sign in', exact: true }).click();
  expect((await authenticated).status()).toBe(200);
  await expect(page.getByRole('button', { name: 'Sign out', exact: true })).toBeVisible();
}

async function createOrder(page: Page, supplierId: string, warehouseId: string, productId: string, quantity: number, cost: number) {
  await page.getByRole('button', { name: 'Create purchase order', exact: true }).click();
  const dialog = page.getByRole('dialog');
  await dialog.getByRole('combobox', { name: 'Supplier', exact: true }).selectOption(supplierId);
  await dialog.getByRole('combobox', { name: 'Receiving warehouse', exact: true }).selectOption(warehouseId);
  await dialog.getByLabel('Expected delivery date', { exact: true }).fill(new Date(Date.now() + 7 * 86_400_000).toISOString().slice(0, 10));
  await dialog.getByRole('combobox', { name: 'Product 1', exact: true }).selectOption(productId);
  await dialog.getByLabel('Quantity for line 1', { exact: true }).fill(String(quantity));
  await dialog.getByLabel('Unit cost for line 1', { exact: true }).fill(String(cost));
  await dialog.getByRole('button', { name: 'Add order line', exact: true }).click();
  await expect(dialog.getByRole('combobox', { name: 'Product 2', exact: true })).toBeVisible();
  await dialog.getByRole('button', { name: 'Remove line 2', exact: true }).click();
  await expect(dialog.getByRole('combobox', { name: 'Product 2', exact: true })).toHaveCount(0);
  await dialog.getByLabel('Order notes (optional)', { exact: true }).fill('Warehouse packing materials, reviewed against stock position');
  await dialog.getByRole('button', { name: 'Save draft order', exact: true }).click();
  await expect(dialog).not.toBeVisible();
  await expect(page.getByRole('button', { name: 'Submit for approval', exact: true })).toBeVisible();
}

async function receive(page: Page, productName: string, quantity: number) {
  await page.getByRole('button', { name: 'Receive stock', exact: true }).click();
  const dialog = page.getByRole('dialog');
  await dialog.getByLabel(`Received now for ${productName}`, { exact: true }).fill(String(quantity));
  await dialog.getByLabel('Delivery notes (optional)', { exact: true }).fill('Verified against packing slip');
  await dialog.getByRole('button', { name: 'Record receipt', exact: true }).click();
  await expect(dialog).not.toBeVisible();
}

async function assertFits(page: Page) {
  expect(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth)).toBe(true);
}

async function capture(page: Page, name: string) {
  if (process.env.CAPTURE_SCREENSHOTS !== '1') return;
  await page.waitForLoadState('networkidle');
  await page.evaluate(() => {
    if (document.activeElement instanceof HTMLElement) document.activeElement.blur();
    window.scrollTo(0, 0);
  });
  await expect.poll(() => page.evaluate(() => window.scrollY)).toBe(0);
  const destination = resolve('../docs/screenshots');
  mkdirSync(destination, { recursive: true });
  await page.screenshot({ path: resolve(destination, `${name}.png`), fullPage: true, animations: 'disabled' });
}
