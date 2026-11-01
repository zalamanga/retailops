import { randomUUID } from 'node:crypto';
import { expect, type APIRequestContext, type BrowserContext, type Page } from '@playwright/test';
import type { Branch, Journal, RetailCatalog, RetailReport, Sale, Shift } from '../src/retail-types';
import type { QueuedSale } from '../src/offline';
import type { User } from '../src/types';

export const trainingPassword = process.env.RETAILOPS_E2E_PASSWORD || 'ReviewWorkspaceAccess947!';
export const administratorEmail = process.env.RETAILOPS_E2E_ADMIN || 'operations@retailops.test';
export const workstationDatabase = 'retailops-workstation-v1';

export interface RetailTraining {
  branch: Branch;
  warehouse: { id: string; name: string };
  product: { id: string; sku: string; name: string; standardCost: number };
  cashier: User;
  cashierEmail: string;
  price: { retailPrice: number; wholesalePrice: number; wholesaleMinimum: number; version: number };
  initialStock: number;
}

export async function json<T>(request: APIRequestContext, path: string): Promise<T> {
  const response = await request.get(`/api${path}`);
  expect(response.ok(), `GET ${path}: ${response.status()} ${await response.text()}`).toBe(true);
  return response.json() as Promise<T>;
}

export async function mutate<T>(request: APIRequestContext, path: string, data?: unknown, method = 'POST', key?: string): Promise<T> {
  const csrf = await json<{ token: string }>(request, '/auth/csrf');
  const response = await request.fetch(`/api${path}`, {
    method, data, headers: { 'X-CSRF-TOKEN': csrf.token, ...(key ? { 'Idempotency-Key': key } : {}) },
  });
  expect(response.ok(), `${method} ${path}: ${response.status()} ${await response.text()}`).toBe(true);
  return response.status() === 204 ? undefined as T : response.json() as Promise<T>;
}

export async function authenticateAdministrator(request: APIRequestContext): Promise<User> {
  const status = await json<{ setupRequired: boolean }>(request, '/auth/status');
  return mutate<User>(request, status.setupRequired ? '/auth/setup' : '/auth/login', {
    email: administratorEmail, password: trainingPassword,
    ...(status.setupRequired ? { organizationName: 'RetailOps Verification', fullName: 'Operations administrator' } : {}),
  });
}

export async function createCashier(request: APIRequestContext, branchId: string, label = 'Training cashier') {
  const email = `cashier-${randomUUID()}@retailops.test`;
  const user = await mutate<User>(request, '/users', {
    fullName: label, email, password: trainingPassword, role: 'Cashier',
  });
  await mutate(request, '/retail/assignments', { userId: user.id, branchId });
  return { user, email };
}

export async function createTraining(request: APIRequestContext, label: string): Promise<RetailTraining> {
  const suffix = randomUUID().slice(0, 8);
  const warehouse = await mutate<{ id: string; name: string }>(request, '/warehouses', {
    code: `T-${suffix}`, name: `Training ${label} ${suffix}`,
  });
  const branch = await mutate<Branch>(request, '/retail/branches', {
    code: `B-${suffix}`, name: `Training ${label} ${suffix}`, warehouseId: warehouse.id,
  });
  const product = await mutate<RetailTraining['product']>(request, '/products', {
    sku: `POS-${suffix}`, name: `Training tea ${suffix}`, unit: 'box', reorderPoint: 2, standardCost: 6000,
  });
  const price = await mutate<RetailTraining['price']>(request, `/retail/prices/${product.id}`, {
    retailPrice: 10000, wholesalePrice: 9000, wholesaleMinimum: 3, expectedVersion: 0,
  }, 'PUT');
  const initialStock = 30;
  await mutate(request, '/inventory/adjustments', {
    productId: product.id, warehouseId: warehouse.id, quantity: initialStock, reason: 'Synthetic browser training opening stock',
  });
  const cashier = await createCashier(request, branch.id);
  return { branch, warehouse, product, price, initialStock, cashier: cashier.user, cashierEmail: cashier.email };
}

export async function signIn(page: Page, email: string): Promise<User> {
  await page.goto('/');
  return signInCurrentPage(page, email);
}

export async function signInCurrentPage(page: Page, email: string): Promise<User> {
  await expect(page.getByRole('heading', { name: 'Welcome back', exact: true })).toBeVisible();
  await page.getByLabel('Email address', { exact: true }).fill(email);
  await page.getByLabel('Password', { exact: true }).fill(trainingPassword);
  const authenticated = page.waitForResponse(response => new URL(response.url()).pathname === '/api/auth/login' && response.request().method() === 'POST');
  await page.getByRole('button', { name: 'Sign in', exact: true }).click();
  const response = await authenticated;
  expect(response.status()).toBe(200);
  const user = await response.json() as User;
  expect(user.organizationId).toMatch(/^[0-9a-f-]{36}$/i);
  await expect(page.getByRole('button', { name: 'Sign out', exact: true })).toBeAttached();
  return user;
}

type WorkstationStore = 'identities' | 'snapshots' | 'sales' | 'operations';

export async function records<T>(page: Page, store: WorkstationStore): Promise<T[]> {
  return page.evaluate(async ({ databaseName, storeName }) => {
    const databases = await indexedDB.databases();
    if (!databases.some(database => database.name === databaseName)) return [];
    return new Promise<T[]>((resolve, reject) => {
      const opening = indexedDB.open(databaseName);
      opening.onupgradeneeded = () => { opening.transaction?.abort(); reject(new Error('Workstation database must be created by the app.')); };
      opening.onerror = () => reject(opening.error);
      opening.onsuccess = () => {
        const db = opening.result;
        const transaction = db.transaction(storeName, 'readonly');
        const reading = transaction.objectStore(storeName).getAll();
        reading.onerror = () => { db.close(); reject(reading.error); };
        transaction.oncomplete = () => { db.close(); resolve(reading.result as T[]); };
        transaction.onabort = () => { db.close(); reject(transaction.error); };
      };
    });
  }, { databaseName: workstationDatabase, storeName: store });
}

export async function queueFor(page: Page, user: Pick<User, 'organizationId' | 'id'>): Promise<QueuedSale[]> {
  return (await records<QueuedSale>(page, 'sales')).filter(record => record.scope === `${user.organizationId}:${user.id}`);
}

export async function queued(page: Page, key: string): Promise<QueuedSale | undefined> {
  return (await records<QueuedSale>(page, 'sales')).find(record => record.id === key);
}

export async function expectQueue(page: Page, key: string, state: QueuedSale['state']): Promise<QueuedSale> {
  await expect.poll(async () => (await queued(page, key))?.state).toBe(state);
  return (await queued(page, key))!;
}

export async function expectPublicShell(page: Page) {
  await expect.poll(() => page.evaluate(() => !!navigator.serviceWorker?.controller)).toBe(true);
  const cached = await page.evaluate(async () => {
    const registrations = await navigator.serviceWorker.getRegistrations();
    const cacheNames = await caches.keys();
    const urls: string[] = [];
    for (const name of cacheNames) for (const request of await (await caches.open(name)).keys()) urls.push(request.url);
    return { workers: registrations.map(registration => registration.active?.scriptURL), urls };
  });
  expect(cached.workers.some(url => url && new URL(url).origin === new URL(page.url()).origin)).toBe(true);
  expect(cached.urls.some(url => /\/assets\/.*\.(?:js|css)$/.test(new URL(url).pathname))).toBe(true);
  expect(cached.urls.every(url => !new URL(url).pathname.startsWith('/api/'))).toBe(true);
}

export async function expectNoStoredCredentials(page: Page) {
  const contents = await Promise.all(['identities', 'snapshots', 'sales', 'operations'].map(store => records(page, store as WorkstationStore)));
  expect(JSON.stringify(contents)).not.toContain(trainingPassword);
  expect(JSON.stringify(contents)).not.toMatch(/"(?:password|csrfToken|sessionToken|token)"\s*:/i);
}

export async function stock(request: APIRequestContext, training: RetailTraining): Promise<number> {
  const catalog = await json<RetailCatalog>(request, `/retail/catalog?branchId=${training.branch.id}`);
  return catalog.products.find(product => product.productId === training.product.id)!.onHand;
}

export async function branchSales(request: APIRequestContext, branchId: string): Promise<Sale[]> {
  return json<Sale[]>(request, `/retail/sales?branchId=${branchId}`);
}

export async function assertOnePosting(request: APIRequestContext, sale: Sale, quantity: number, training: RetailTraining) {
  const saleJournals = (await json<Journal[]>(request, '/retail/journals')).filter(journal => journal.sourceId === sale.id);
  expect(saleJournals).toHaveLength(1);
  const journal = saleJournals[0];
  expect(journal.totalDebit).toBe(sale.total + quantity * training.product.standardCost);
  expect(journal.totalCredit).toBe(journal.totalDebit);
  for (const code of ['1000', '1200', '4000', '5000']) expect(journal.lines.some(line => line.accountCode === code)).toBe(true);
  return journal;
}

export function wibDate(value = new Date()): string {
  return new Intl.DateTimeFormat('en-CA', { timeZone: 'Asia/Jakarta', year: 'numeric', month: '2-digit', day: '2-digit' }).format(value);
}

export async function reportFor(request: APIRequestContext, branchId: string): Promise<RetailReport> {
  const date = wibDate();
  return json<RetailReport>(request, `/retail/reports?from=${date}&to=${date}&branchId=${branchId}`);
}

export async function assertFits(page: Page) {
  expect(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth)).toBe(true);
}

export async function offline(context: BrowserContext, value: boolean) {
  await context.setOffline(value);
  for (const page of context.pages()) await expect.poll(() => page.evaluate(() => navigator.onLine)).toBe(!value);
}

export async function ownShifts(page: Page, branchId: string): Promise<Shift[]> {
  return json<Shift[]>(page.request, `/retail/shifts?branchId=${branchId}`);
}

export async function openOwnShift(page: Page, branchId: string, openingCash = 50000): Promise<Shift> {
  await page.goto('/#/shifts');
  await expect(page.getByRole('heading', { name: 'Shift kasir', exact: true })).toBeVisible();
  await page.getByRole('button', { name: 'Buka shift saya', exact: true }).click();
  const dialog = page.getByRole('dialog');
  await dialog.getByLabel('Cabang', { exact: true }).selectOption(branchId);
  await dialog.getByLabel('Kas pembuka (IDR)', { exact: true }).fill(String(openingCash));
  const opened = page.waitForResponse(response => new URL(response.url()).pathname === '/api/retail/shifts' && response.request().method() === 'POST');
  await dialog.getByRole('button', { name: 'Buka laci kasir', exact: true }).click();
  const response = await opened;
  expect(response.status()).toBe(200);
  await expect(dialog).not.toBeVisible();
  return response.json() as Promise<Shift>;
}

export async function openPointOfSale(page: Page, training: RetailTraining) {
  await page.goto('/#/pos');
  await expect(page.getByRole('heading', { name: 'Kasir cabang', exact: true })).toBeVisible();
  await page.getByLabel('Cabang', { exact: true }).selectOption(training.branch.id);
  await expect(page.getByRole('button', { name: `Tambah ${training.product.name}`, exact: true })).toBeEnabled();
  await expect.poll(async () => {
    const snapshots = await records<{ catalogs: RetailCatalog[]; shifts: Shift[] }>(page, 'snapshots');
    return snapshots.some(snapshot => snapshot.catalogs.some(catalog => catalog.branch.id === training.branch.id) && snapshot.shifts.some(shift => shift.branchId === training.branch.id && shift.status.toLowerCase() === 'open'));
  }).toBe(true);
}

export async function addToCart(page: Page, training: RetailTraining, quantity = 1, tier: 'Retail' | 'Wholesale' = 'Retail', cash = 100000) {
  await page.getByLabel('Harga jual', { exact: true }).selectOption(tier);
  await page.getByRole('button', { name: `Tambah ${training.product.name}`, exact: true }).click();
  await page.getByLabel(`Jumlah ${training.product.name}`, { exact: true }).fill(String(quantity));
  await page.getByLabel('Uang tunai diterima (IDR)', { exact: true }).fill(String(cash));
}

export async function checkout(page: Page): Promise<string> {
  const before = new Set((await records<QueuedSale>(page, 'sales')).map(row => row.id));
  await page.getByRole('button', { name: 'Tinjau penjualan tunai', exact: true }).click();
  const dialog = page.getByRole('dialog', { name: 'Konfirmasi penjualan tunai', exact: true });
  await dialog.getByRole('button', { name: /^(?:Simpan perintah offline|Catat dan kirim penjualan)$/ }).click();
  await expect(dialog).not.toBeVisible();
  await expect.poll(async () => (await records<QueuedSale>(page, 'sales')).filter(row => !before.has(row.id)).length).toBe(1);
  return (await records<QueuedSale>(page, 'sales')).find(row => !before.has(row.id))!.id;
}

export function queueArticle(page: Page, key: string) {
  return page.getByRole('article').filter({ hasText: key });
}

export async function synchronize(page: Page, key: string) {
  await page.goto('/#/sync');
  await expect(page.getByRole('heading', { name: 'Antrean sinkronisasi', exact: true })).toBeVisible();
  await queueArticle(page, key).getByRole('button', { name: 'Kirim ulang key asli', exact: true }).click();
}
