import { mkdirSync } from 'node:fs';
import { resolve } from 'node:path';
import { expect, test, type APIRequestContext, type BrowserContext, type Page, type Request, type Route } from '@playwright/test';
import type { Journal, Sale, SaleReturn } from '../src/retail-types';
import type { RetailOperation } from '../src/retail-operations';
import type { User } from '../src/types';
import {
  addToCart, administratorEmail, assertFits, assertOnePosting, authenticateAdministrator,
  branchSales, checkout, createCashier, createTraining, expectNoStoredCredentials, expectPublicShell,
  expectQueue, json, mutate, offline, openOwnShift, openPointOfSale, queued, queueArticle,
  queueFor, records, reportFor, signIn, signInCurrentPage, stock, synchronize,
} from './retail-support';

test.describe('RetailOps production workstation with native storage and server transactions', () => {
  test.skip(process.env.RETAILOPS_E2E !== '1', 'Requires the dedicated synthetic RetailOps verification database.');
  test.setTimeout(180_000);
  let admin: APIRequestContext;

  test.beforeAll(async ({ playwright, baseURL }) => {
    expect(baseURL).toBeTruthy();
    const target = new URL(baseURL!);
    expect(['127.0.0.1', 'localhost', '[::1]']).toContain(target.hostname);
    expect(['8150', '5188']).toContain(target.port);
    admin = await playwright.request.newContext({ baseURL });
    await authenticateAdministrator(admin);
  });

  test.afterAll(async () => { await admin?.dispose(); });

  test('wholesale receipt, partial cash return, drawer count, and accounting reconcile at three widths', async ({ page }) => {
    const training = await createTraining(admin, 'receipt');
    const user = await signIn(page, training.cashierEmail);
    const beforeShift = await json<Journal[]>(admin, '/retail/journals');
    const shift = await openOwnShift(page, training.branch.id);
    expect(await json<Journal[]>(admin, '/retail/journals')).toHaveLength(beforeShift.length);
    await openPointOfSale(page, training);
    await addToCart(page, training, 2, 'Wholesale', 30000);
    await expect(page.getByText('Setiap produk grosir harus memenuhi jumlah minimumnya.', { exact: true })).toBeVisible();
    await expect(page.getByRole('button', { name: 'Tinjau penjualan tunai', exact: true })).toBeDisabled();
    await page.getByLabel(`Jumlah ${training.product.name}`, { exact: true }).fill('3');
    for (const width of [320, 390, 1440]) {
      await page.setViewportSize({ width, height: width === 1440 ? 1000 : 844 });
      await assertFits(page);
      await expect(page.getByRole('button', { name: 'Tinjau penjualan tunai', exact: true })).toBeEnabled();
      await capture(page, `retail-pos-${width}`);
    }
    const key = await checkout(page);
    const row = await expectQueue(page, key, 'confirmed');
    expect(row.scope).toBe(`${user.organizationId}:${user.id}`);
    expect(row.command.shiftId).toBe(shift.id);
    const sale = row.result!;
    expect(sale.total).toBe(27000);
    expect(sale.cashTendered).toBe(30000);
    expect(sale.change).toBe(3000);
    expect(await stock(admin, training)).toBe(27);
    const saleJournal = await assertOnePosting(admin, sale, 3, training);
    await page.goto(`/#/sales/${sale.id}`);
    await expect(page.getByRole('heading', { name: `Struk ${sale.number}`, exact: true })).toBeVisible();
    await expect(page.getByRole('region', { name: 'Baris struk', exact: true })).toContainText(training.product.sku);
    await expect(page.getByRole('button', { name: 'Catat retur sebagian', exact: true })).toHaveCount(0);
    for (const path of ['/products', '/suppliers', '/users', '/retail/journals', '/retail/reports'])
      expect((await page.request.get(`/api${path}`)).status()).toBe(403);
    await capture(page, 'retail-receipt-desktop');
    await expectNoStoredCredentials(page);
    await signOut(page);
    await signIn(page, administratorEmail);
    const returnShift = await openOwnShift(page, training.branch.id, 40000);
    await page.goto(`/#/sales/${sale.id}`);
    await page.getByRole('button', { name: 'Catat retur sebagian', exact: true }).click();
    const dialog = page.getByRole('dialog', { name: `Retur ${sale.number}`, exact: true });
    await dialog.getByLabel('Shift saya pada cabang struk', { exact: true }).selectOption(returnShift.id);
    await dialog.getByLabel(`${training.product.name}: jumlah dikembalikan`, { exact: true }).fill('1');
    await dialog.getByLabel('Alasan retur', { exact: true }).fill('Synthetic unopened item returned by training customer');
    const returned = page.waitForResponse(response => new URL(response.url()).pathname === `/api/retail/sales/${sale.id}/returns` && response.request().method() === 'POST');
    await dialog.getByRole('button', { name: 'Catat retur dan kas keluar', exact: true }).click();
    const returnResponse = await returned;
    expect(returnResponse.status()).toBe(200);
    const refund = await returnResponse.json() as SaleReturn;
    expect(refund.total).toBe(9000);
    await expect(dialog).not.toBeVisible();
    expect((await json<Sale>(admin, `/retail/sales/${sale.id}`)).lines[0].returnedQuantity).toBe(1);
    expect(await stock(admin, training)).toBe(28);
    const journals = await json<Journal[]>(admin, '/retail/journals');
    const refundJournals = journals.filter(journal => journal.sourceId === refund.id);
    expect(refundJournals).toHaveLength(1);
    expect(refundJournals[0].totalDebit).toBe(15000);
    expect(refundJournals[0].totalCredit).toBe(15000);
    const report = await reportFor(admin, training.branch.id);
    expect(report).toMatchObject({ salesCount: 1, grossSales: 27000, refunds: 9000, netSales: 18000, costOfGoodsSold: 12000, varianceExpense: 0, netProfit: 6000, journalBalanced: true });
    expect(report.balanceSheet.balanced).toBe(true);
    expect(report.trialBalance.reduce((sum, line) => sum + line.debit - line.credit, 0)).toBeCloseTo(0, 2);
    await page.goto('/#/accounting');
    await page.getByLabel('Cabang laba rugi', { exact: true }).selectOption(training.branch.id);
    const reported = page.waitForResponse(response => new URL(response.url()).pathname === '/api/retail/reports' && new URL(response.url()).searchParams.get('branchId') === training.branch.id);
    await page.getByRole('button', { name: 'Tampilkan laporan', exact: true }).click();
    expect((await reported).status()).toBe(200);
    await expect(page.getByText('Persamaan neraca seimbang', { exact: true })).toBeVisible();
    await expect(page.getByRole('heading', { name: 'Neraca saldo seluruh organisasi', exact: true })).toBeVisible();
    const saleEntry = page.locator('details').filter({ hasText: saleJournal.number });
    await saleEntry.locator('summary').click();
    await expect(saleEntry.getByRole('region', { name: `Baris jurnal ${saleJournal.number}`, exact: true })).toBeVisible();
    for (const width of [320, 390, 1440]) {
      await page.setViewportSize({ width, height: width === 1440 ? 1000 : 844 });
      await assertFits(page);
      await expect(page.getByRole('region', { name: 'Neraca saldo organisasi', exact: true })).toBeVisible();
      await capture(page, `retail-accounting-${width}`);
    }
    await page.goto('/#/shifts');
    const shiftRow = page.getByRole('row').filter({ hasText: training.branch.name }).filter({ hasText: returnShift.openedByName });
    await shiftRow.getByRole('button', { name: 'Tutup shift', exact: true }).click();
    const closing = page.getByRole('dialog');
    await closing.getByLabel('Kas fisik dihitung (IDR)', { exact: true }).fill('31000');
    await closing.getByRole('button', { name: 'Simpan hitungan dan tutup', exact: true }).click();
    await expect(closing).not.toBeVisible();
    const closed = (await json<{ id: string; expectedCash: number; variance: number; status: string }[]>(admin, `/retail/shifts?branchId=${training.branch.id}`)).find(item => item.id === returnShift.id)!;
    expect(closed).toMatchObject({ expectedCash: 31000, variance: 0 });
    expect(closed.status.toLowerCase()).toBe('closed');
    expect(await json<Journal[]>(admin, '/retail/journals')).toHaveLength(journals.length);
  });

  test('real service worker reloads offline and IndexedDB retains an unconfirmed command until sync', async ({ page, context }) => {
    const training = await createTraining(admin, 'offline');
    const user = await signIn(page, training.cashierEmail);
    await openOwnShift(page, training.branch.id);
    await openPointOfSale(page, training);
    await expectPublicShell(page);
    const failed: string[] = [];
    page.on('requestfailed', request => { if (new URL(request.url()).pathname.startsWith('/api/')) failed.push(request.url()); });
    await offline(context, true);
    await page.reload({ waitUntil: 'domcontentloaded' });
    await expect(page.getByRole('heading', { name: 'Kasir cabang', exact: true })).toBeVisible();
    await expect(page.getByText('Offline: memakai katalog tersimpan', { exact: true })).toBeVisible();
    await expect(page.getByText(/Cache terakhir/)).toBeVisible();
    await expect(page.locator('.retail-offline-notice')).toBeVisible();
    await addToCart(page, training, 2, 'Retail', 25000);
    const key = await checkout(page);
    const pending = await expectQueue(page, key, 'pending');
    expect(pending.attempts).toBe(0);
    expect(pending.result).toBeUndefined();
    expect(pending.uncertain).toBe(false);
    expect(pending.command.branchId).toBe(training.branch.id);
    const original = structuredClone(pending.command);
    await page.goto('/#/sync');
    await expect(queueArticle(page, key).getByText('Menunggu server', { exact: true })).toBeVisible();
    await capture(page, 'retail-offline-queue');
    await page.reload({ waitUntil: 'domcontentloaded' });
    expect((await queued(page, key))!.command).toEqual(original);
    await expect(page.getByRole('heading', { name: 'Antrean sinkronisasi', exact: true })).toBeVisible();
    await expect(queueArticle(page, key).getByRole('link', { name: /^Buka struk/ })).toHaveCount(0);
    expect(failed.length).toBeGreaterThan(0);
    expect(await branchSales(admin, training.branch.id)).toHaveLength(0);
    await expectNoStoredCredentials(page);
    await offline(context, false);
    await expect(page.locator('.retail-offline-notice')).toHaveCount(0, { timeout: 30_000 });
    await synchronize(page, key);
    const confirmed = await expectQueue(page, key, 'confirmed');
    expect(confirmed.command).toEqual(original);
    expect(confirmed.attempts).toBe(1);
    expect(await queueFor(page, user)).toHaveLength(1);
    expect(await stock(admin, training)).toBe(28);
    expect(await branchSales(admin, training.branch.id)).toHaveLength(1);
    await assertOnePosting(admin, confirmed.result!, 2, training);
    await expect(queueArticle(page, key).getByRole('link', { name: `Buka struk ${confirmed.result!.number}`, exact: true })).toBeVisible();
  });

  test('a committed sale with a lost response retries its original key and payload exactly once', async ({ page }) => {
    const training = await createTraining(admin, 'unknown');
    await signIn(page, training.cashierEmail);
    await openOwnShift(page, training.branch.id);
    await openPointOfSale(page, training);
    const attempts: { key: string; body: unknown }[] = [];
    let serverSale: Sale | undefined;
    const intercepted = deferred<void>();
    void intercepted.promise.catch(() => {});
    const pattern = '**/api/retail/sales';
    const handler = async (route: Route) => {
      if (route.request().method() !== 'POST') { await route.continue(); return; }
      try {
        const request = route.request();
        const key = request.headers()['idempotency-key'];
        attempts.push({ key, body: request.postDataJSON() });
        const durable = await queued(page, key);
        expect(durable).toMatchObject({ state: 'syncing', attempts: 1 });
        expect(durable!.command).toEqual(request.postDataJSON());
        // The response comes from the real server; only delivery to the page is interrupted.
        const accepted = await route.fetch({ timeout: 30_000 });
        expect(accepted.ok()).toBe(true);
        serverSale = await accepted.json() as Sale;
        await route.abort('connectionreset');
        await page.unroute(pattern, handler);
        intercepted.resolve();
      } catch (error) {
        intercepted.reject(error);
        await route.abort().catch(() => {});
      }
    };
    await page.route(pattern, handler);
    let completionTimeout: ReturnType<typeof setTimeout> | undefined;
    try {
      await addToCart(page, training, 1);
      const key = await checkout(page);
      await Promise.race([
        intercepted.promise,
        new Promise<never>((_, reject) => {
          completionTimeout = setTimeout(() => reject(new Error('The real sale must commit and its page response must be interrupted within 30 seconds.')), 30_000);
        }),
      ]);
      clearTimeout(completionTimeout);
      const unknown = await expectQueue(page, key, 'pending');
      expect(unknown.uncertain).toBe(true);
      expect(unknown.result).toBeUndefined();
      expect(serverSale?.id).toBeTruthy();
      expect((await branchSales(admin, training.branch.id)).map(sale => sale.id)).toEqual([serverSale!.id]);
      await page.goto('/#/sync');
      await expect(queueArticle(page, key).getByText('Hasil belum pasti. Jangan buat ulang transaksi ini dengan key baru.', { exact: true })).toBeVisible();
      await expect(queueArticle(page, key).getByRole('button', { name: 'Saya sudah meninjau, arsipkan penolakan', exact: true })).toHaveCount(0);
      page.on('request', request => { if (isSalePost(request)) attempts.push({ key: request.headers()['idempotency-key'], body: request.postDataJSON() }); });
      await page.reload();
      await synchronize(page, key);
      const confirmed = await expectQueue(page, key, 'confirmed');
      expect(attempts).toHaveLength(2);
      expect(attempts[0].key).toBe(key);
      expect(attempts[1]).toEqual(attempts[0]);
      expect(confirmed.command).toEqual(unknown.command);
      expect(confirmed.result!.id).toBe(serverSale!.id);
      expect(confirmed.result!.replayed).toBe(true);
      expect(confirmed.attempts).toBe(2);
      expect(await stock(admin, training)).toBe(29);
      expect(await branchSales(admin, training.branch.id)).toHaveLength(1);
      await assertOnePosting(admin, confirmed.result!, 1, training);
    } finally {
      if (completionTimeout) clearTimeout(completionTimeout);
      if (!page.isClosed()) await page.unroute(pattern, handler);
    }
  });

  test('pending commands survive warned logout and remain invisible and unreplayable for a second user', async ({ page, context }) => {
    const training = await createTraining(admin, 'identity');
    const other = await createCashier(admin, training.branch.id, 'Second training cashier');
    const owner = await signIn(page, training.cashierEmail);
    await openOwnShift(page, training.branch.id);
    await openPointOfSale(page, training);
    await offline(context, true);
    await addToCart(page, training);
    const key = await checkout(page);
    const original = (await queued(page, key))!;
    await offline(context, false);
    await visibleSignOut(page);
    const warning = page.getByRole('dialog', { name: 'Keluar dengan antrean belum selesai?', exact: true });
    await expect(warning).toContainText('1 perintah lokal');
    await expect(warning).toContainText('akun dan organisasi yang sama');
    await warning.getByRole('button', { name: 'Tetap di workspace', exact: true }).click();
    await expect(warning).not.toBeVisible();
    await expect(page.getByRole('heading', { name: 'Kasir cabang', exact: true })).toBeVisible();
    expect((await queued(page, key))!.command).toEqual(original.command);
    await visibleSignOut(page);
    await warning.getByRole('button', { name: 'Keluar dan simpan antrean pemilik', exact: true }).click();
    await expect(page.getByRole('heading', { name: 'Welcome back', exact: true })).toBeVisible();
    const secondUser = await signIn(page, other.email);
    const posts: string[] = [];
    page.on('request', request => { if (isSalePost(request)) posts.push(request.headers()['idempotency-key']); });
    await page.goto('/#/sync');
    await expect(page.getByText('Belum ada perintah penjualan', { exact: true })).toBeVisible();
    await expect(page.getByText(key, { exact: true })).toHaveCount(0);
    expect(await queueFor(page, secondUser)).toHaveLength(0);
    expect((await queued(page, key))!.scope).toBe(`${owner.organizationId}:${owner.id}`);
    expect(posts).toEqual([]);
    const csrf = await json<{ token: string }>(page.request, '/auth/csrf');
    const forbidden = await page.request.post('/api/retail/sales', { data: original.command, headers: { 'X-CSRF-TOKEN': csrf.token, 'Idempotency-Key': key } });
    expect([403, 409]).toContain(forbidden.status());
    expect(await branchSales(admin, training.branch.id)).toHaveLength(0);
    await signOut(page);
    const recoveredOwner = await signIn(page, training.cashierEmail);
    expect(recoveredOwner.id).toBe(owner.id);
    await page.goto('/#/sync');
    await expect(queueArticle(page, key)).toBeVisible();
    expect((await queued(page, key))!.command).toEqual(original.command);
    await synchronize(page, key);
    const confirmed = await expectQueue(page, key, 'confirmed');
    expect(confirmed.result!.actorId).toBe(owner.id);
    expect(confirmed.result!.branchId).toBe(training.branch.id);
    expect(await branchSales(admin, training.branch.id)).toHaveLength(1);
    await expectNoStoredCredentials(page);
  });

  test('a definite stale-price rejection requires review and an explicit new sale with a new key', async ({ page, context }) => {
    const training = await createTraining(admin, 'conflict');
    await signIn(page, training.cashierEmail);
    await openOwnShift(page, training.branch.id);
    await openPointOfSale(page, training);
    await offline(context, true);
    await addToCart(page, training, 2, 'Retail', 30000);
    const rejectedKey = await checkout(page);
    const original = (await queued(page, rejectedKey))!.command;
    const updated = await mutate<{ version: number }>(admin, `/retail/prices/${training.product.id}`, {
      retailPrice: 12000, wholesalePrice: 10000, wholesaleMinimum: 3, expectedVersion: training.price.version,
    }, 'PUT');
    await offline(context, false);
    await synchronize(page, rejectedKey);
    const conflict = await expectQueue(page, rejectedKey, 'conflict');
    expect(conflict).toMatchObject({ uncertain: false, rejectedStatus: 409, attempts: 1 });
    expect(conflict.command).toEqual(original);
    expect(conflict.result).toBeUndefined();
    expect(await stock(admin, training)).toBe(30);
    expect(await branchSales(admin, training.branch.id)).toHaveLength(0);
    const article = queueArticle(page, rejectedKey);
    await expect(article.getByText('Perlu ditinjau', { exact: true })).toBeVisible();
    await expect(article.getByRole('button', { name: 'Kirim ulang key asli', exact: true })).toHaveCount(0);
    await article.getByText('Isi perintah dan riwayat percobaan', { exact: true }).click();
    await expect(article.locator('pre')).toContainText('10000');
    await capture(page, 'retail-price-conflict');
    await article.getByRole('button', { name: 'Saya sudah meninjau, arsipkan penolakan', exact: true }).click();
    await expect.poll(async () => (await queued(page, rejectedKey))?.discarded).toBe(true);
    await expect(article.getByText('Penolakan diarsipkan', { exact: true })).toBeVisible();
    await openPointOfSale(page, training);
    await addToCart(page, training, 2, 'Retail', 30000);
    await expect(page.getByLabel('Jumlah ' + training.product.name, { exact: true })).toHaveValue('2');
    const replacementKey = await checkout(page);
    expect(replacementKey).not.toBe(rejectedKey);
    const replacement = await expectQueue(page, replacementKey, 'confirmed');
    expect(replacement.command.lines[0]).toMatchObject({ productId: training.product.id, quantity: 2, unitPrice: 12000, priceVersion: updated.version });
    expect(replacement.result!.total).toBe(24000);
    expect((await queued(page, rejectedKey))!.command).toEqual(original);
    expect(await branchSales(admin, training.branch.id)).toHaveLength(1);
    expect(await stock(admin, training)).toBe(28);
    await assertOnePosting(admin, replacement.result!, 2, training);
  });

  test('an account switch between owner verification and POST preserves owner headers through CSRF retry in two tabs', async ({ page, context }) => {
    test.setTimeout(240_000);
    const training = await createTraining(admin, 'owner-race');
    const other = await createCashier(admin, training.branch.id, 'Race training cashier B');
    const owner = await signIn(page, training.cashierEmail);
    const second = await context.newPage();
    const gates: Awaited<ReturnType<typeof ownerRaceGate>>[] = [];
    try {
      await second.goto('/#/pos');
      await expect(second.getByRole('heading', { name: 'Kasir cabang', exact: true })).toBeVisible();
      await page.goto('/#/shifts');
      await page.getByRole('button', { name: 'Buka shift saya', exact: true }).click();
      const opening = page.getByRole('dialog', { name: 'Buka shift saya', exact: true });
      await opening.getByLabel('Cabang', { exact: true }).selectOption(training.branch.id);
      await opening.getByLabel('Kas pembuka (IDR)', { exact: true }).fill('45000');
      const shiftGate = await ownerRaceGate(page, context, '/api/retail/shifts', owner);
      gates.push(shiftGate);
      const shiftOwnerCheck = page.waitForResponse(response => new URL(response.url()).pathname === '/api/auth/me' && response.request().method() === 'GET');
      await opening.getByRole('button', { name: 'Buka laci kasir', exact: true }).click();
      expect((await (await shiftOwnerCheck).json()).id).toBe(owner.id);
      await shiftGate.arrived;
      const operationKey = shiftGate.attempts[0].key;
      const persisted = (await records<RetailOperation>(page, 'operations')).find(operation => operation.id === operationKey)!;
      expect(persisted).toMatchObject({ scope: `${owner.organizationId}:${owner.id}`, path: '/retail/shifts', state: 'syncing', uncertain: true, attempts: 1 });
      expect(persisted.payload).toEqual({ branchId: training.branch.id, openingCash: 45000 });
      expect(persisted.payload).toEqual(shiftGate.attempts[0].body);

      await signOutRetainingPending(second);
      const switched = await signInCurrentPage(second, other.email);
      expect(switched.id).toBe(other.user.id);
      await expect.poll(() => page.evaluate(() => localStorage.getItem('retailops.offline-owner'))).toBe(`${switched.organizationId}:${switched.id}`);
      await expect(opening).not.toBeVisible();
      expect((await json<User>(context.request, '/auth/me')).id).toBe(switched.id);
      shiftGate.release();
      await shiftGate.finished;
      await expect.poll(async () => (await records<RetailOperation>(page, 'operations')).find(operation => operation.id === operationKey)?.state).toBe('conflict');
      const rejectedOperation = (await records<RetailOperation>(page, 'operations')).find(operation => operation.id === operationKey)!;
      expect(rejectedOperation.payload).toEqual(persisted.payload);
      expect(rejectedOperation.uncertain).toBe(false);
      expect(rejectedOperation.result).toBeUndefined();
      expect(await json<{ id: string }[]>(admin, `/retail/shifts?branchId=${training.branch.id}`)).toHaveLength(0);
      expect(await branchSales(admin, training.branch.id)).toHaveLength(0);
      await second.goto('/#/sync');
      await expect(second.getByText(operationKey, { exact: true })).toHaveCount(0);

      await signOut(second);
      await signInCurrentPage(second, training.cashierEmail);
      await page.goto('/#/sync');
      const rejectedArticle = queueArticle(page, operationKey);
      await expect(rejectedArticle).toBeVisible();
      await rejectedArticle.getByRole('button', { name: 'Arsipkan operasi yang ditolak', exact: true }).click();
      await expect.poll(async () => (await records<RetailOperation>(page, 'operations')).find(operation => operation.id === operationKey)?.discarded).toBe(true);
      await openOwnShift(page, training.branch.id);
      await openPointOfSale(second, training);
      await offline(context, true);
      await addToCart(second, training, 2, 'Retail', 30000);
      const saleKey = await checkout(second);
      const originalSale = (await queued(second, saleKey))!;
      await offline(context, false);
      await second.goto('/#/sync');
      await openPointOfSale(page, training);
      await addToCart(page, training, 4);
      await expect(page.getByLabel(`Jumlah ${training.product.name}`, { exact: true })).toHaveValue('4');
      const saleGate = await ownerRaceGate(second, context, '/api/retail/sales', owner);
      gates.push(saleGate);
      const saleOwnerCheck = second.waitForResponse(response => new URL(response.url()).pathname === '/api/auth/me' && response.request().method() === 'GET');
      await queueArticle(second, saleKey).getByRole('button', { name: 'Kirim ulang key asli', exact: true }).click();
      expect((await (await saleOwnerCheck).json()).id).toBe(owner.id);
      await saleGate.arrived;
      expect(saleGate.attempts[0]).toMatchObject({ key: saleKey, body: originalSale.command });
      await signOutRetainingPending(second);
      const switchedAgain = await signInCurrentPage(second, other.email);
      await expect.poll(() => page.evaluate(() => localStorage.getItem('retailops.offline-owner'))).toBe(`${switchedAgain.organizationId}:${switchedAgain.id}`);
      await expect(page.getByRole('heading', { name: 'Kasir cabang', exact: true })).toBeVisible();
      await expect(page.getByText('Keranjang masih kosong', { exact: true })).toBeVisible();
      await expect(page.getByLabel(`Jumlah ${training.product.name}`, { exact: true })).toHaveCount(0);
      await expect(page.getByText('Belum ada shift aktif', { exact: true })).toBeVisible();
      expect((await json<User>(context.request, '/auth/me')).id).toBe(switchedAgain.id);
      saleGate.release();
      await saleGate.finished;
      const rejectedSale = await expectQueue(page, saleKey, 'conflict');
      expect(rejectedSale).toMatchObject({ uncertain: false, rejectedStatus: 403, attempts: 1 });
      expect(rejectedSale.command).toEqual(originalSale.command);
      expect(rejectedSale.result).toBeUndefined();
      expect(await queueFor(page, switchedAgain)).toHaveLength(0);
      await page.goto('/#/sync');
      await expect(page.getByText(saleKey, { exact: true })).toHaveCount(0);
      await expect(page.getByText(operationKey, { exact: true })).toHaveCount(0);
      expect(await branchSales(admin, training.branch.id)).toHaveLength(0);
      expect(await stock(admin, training)).toBe(30);
      const shifts = await json<{ openedById: string }[]>(admin, `/retail/shifts?branchId=${training.branch.id}`);
      expect(shifts).toHaveLength(1);
      expect(shifts[0].openedById).toBe(owner.id);
      expect((await json<Journal[]>(admin, '/retail/journals')).filter(journal => journal.branchId === training.branch.id && /sale/i.test(journal.sourceType))).toHaveLength(0);
      await expectNoStoredCredentials(page);
    } finally {
      try { for (const gate of gates) await gate.dispose(); }
      finally { await second.close(); }
    }
  });

  test('two real tabs claim one persisted command and create only one stock and journal effect', async ({ page, context }) => {
    const training = await createTraining(admin, 'tabs');
    await signIn(page, training.cashierEmail);
    await openOwnShift(page, training.branch.id);
    await openPointOfSale(page, training);
    await expectPublicShell(page);
    await offline(context, true);
    await addToCart(page, training);
    const key = await checkout(page);
    await offline(context, false);
    await page.goto('/#/sync');
    const other = await context.newPage();
    try {
      await other.goto('/#/sync');
      await expect(queueArticle(other, key)).toBeVisible();
      const writes: string[] = [];
      for (const tab of [page, other]) tab.on('request', request => { if (isSalePost(request)) writes.push(request.headers()['idempotency-key']); });
      await Promise.all([
        queueArticle(page, key).getByRole('button', { name: 'Kirim ulang key asli', exact: true }).click(),
        queueArticle(other, key).getByRole('button', { name: 'Kirim ulang key asli', exact: true }).click(),
      ]);
      const confirmed = await expectQueue(page, key, 'confirmed');
      expect(writes).toEqual([key]);
      expect(confirmed.attempts).toBe(1);
      expect(await stock(admin, training)).toBe(29);
      expect(await branchSales(admin, training.branch.id)).toHaveLength(1);
      await assertOnePosting(admin, confirmed.result!, 1, training);
      await expect(queueArticle(other, key).getByText('Terkonfirmasi', { exact: true })).toBeVisible();
    } finally { await other.close(); }
  });

  test('native CSRF rejection refreshes once without changing the persisted sale command', async ({ page, context }) => {
    const training = await createTraining(admin, 'csrf');
    await signIn(page, training.cashierEmail);
    await openOwnShift(page, training.branch.id);
    await openPointOfSale(page, training);
    const attempts: { key: string; body: unknown }[] = [];
    const statuses: number[] = [];
    page.on('request', request => { if (isSalePost(request)) attempts.push({ key: request.headers()['idempotency-key'], body: request.postDataJSON() }); });
    page.on('response', response => { if (isSalePost(response.request())) statuses.push(response.status()); });
    await context.clearCookies({ name: 'retailops.csrf' });
    await addToCart(page, training);
    const key = await checkout(page);
    const confirmed = await expectQueue(page, key, 'confirmed');
    expect(statuses).toEqual([400, 200]);
    expect(attempts).toHaveLength(2);
    expect(attempts[1]).toEqual(attempts[0]);
    expect(attempts[0]).toEqual({ key, body: confirmed.command });
    expect(confirmed.attempts).toBe(1);
    expect(await branchSales(admin, training.branch.id)).toHaveLength(1);
    expect(await stock(admin, training)).toBe(29);
    await assertOnePosting(admin, confirmed.result!, 1, training);
  });
});

function isSalePost(request: Request) {
  return new URL(request.url()).pathname === '/api/retail/sales' && request.method() === 'POST';
}

async function visibleSignOut(page: Page) {
  const button = page.getByRole('button', { name: 'Sign out', exact: true });
  if (!await button.isVisible()) await page.getByRole('button', { name: 'Menu', exact: true }).click();
  await button.click();
}

async function signOut(page: Page) {
  await visibleSignOut(page);
  await expect(page.getByRole('heading', { name: 'Welcome back', exact: true })).toBeVisible();
}

async function signOutRetainingPending(page: Page) {
  await visibleSignOut(page);
  const warning = page.getByRole('dialog', { name: 'Keluar dengan antrean belum selesai?', exact: true });
  await expect(warning).toBeVisible();
  await warning.getByRole('button', { name: 'Keluar dan simpan antrean pemilik', exact: true }).click();
  await expect(page.getByRole('heading', { name: 'Welcome back', exact: true })).toBeVisible();
}

async function ownerRaceGate(page: Page, context: BrowserContext, pathname: string, owner: User) {
  const entered = deferred<void>();
  const proceed = deferred<void>();
  const done = deferred<void>();
  const attempts: { key: string; actor: string; organization: string; body: unknown }[] = [];
  const statuses: number[] = [];
  const pattern = `**${pathname}`;
  let timeout: ReturnType<typeof setTimeout> | undefined;
  let disposed = false;
  const finished = done.promise.finally(() => { if (timeout) clearTimeout(timeout); });
  void finished.catch(() => {});
  const unregister = async () => { if (!page.isClosed()) await page.unroute(pattern, handler); };
  const dispose = async () => {
    disposed = true;
    if (timeout) clearTimeout(timeout);
    proceed.resolve();
    await unregister();
  };
  const handler = async (route: Route) => {
    if (route.request().method() !== 'POST') { await route.continue(); return; }
    try {
      const request = route.request();
      const headers = await request.allHeaders();
      attempts.push({ key: headers['idempotency-key'], actor: headers['x-retail-actor'], organization: headers['x-retail-organization'], body: request.postDataJSON() });
      expect(attempts.at(-1)).toMatchObject({ actor: owner.id, organization: owner.organizationId });
      if (attempts.length === 1) { entered.resolve(); await proceed.promise; }
      if (disposed) { await route.abort(); return; }
      // Use the live browser cookie jar after the gate, with the app's original command and owner headers.
      delete headers.cookie;
      const response = await context.request.fetch(request.url(), { method: 'POST', headers, data: request.postDataBuffer()!, maxRedirects: 0, timeout: 30_000 });
      statuses.push(response.status());
      if (statuses.length === 1) expect((await response.json()).title).toBe('Request verification failed');
      await route.fulfill({ response });
      if (statuses.length === 2) {
        expect(statuses).toEqual([400, 403]);
        expect(attempts[1]).toEqual(attempts[0]);
        await unregister();
        done.resolve();
      }
    } catch (error) {
      entered.reject(error); done.reject(error);
      await route.abort().catch(() => {});
      await unregister();
    }
  };
  await page.route(pattern, handler);
  const release = () => {
    timeout ??= setTimeout(() => {
      done.reject(new Error(`Expected two completed owner-bound POST attempts for ${pathname} within 30 seconds; observed statuses: ${statuses.join(', ')}`));
      void dispose().catch(() => {});
    }, 30_000);
    proceed.resolve();
  };
  return { arrived: entered.promise, release, finished, attempts, dispose };
}

function deferred<T>() {
  let resolve!: (value: T | PromiseLike<T>) => void;
  let reject!: (reason?: unknown) => void;
  const promise = new Promise<T>((accept, decline) => { resolve = accept; reject = decline; });
  return { promise, resolve, reject };
}

async function capture(page: Page, name: string) {
  if (process.env.CAPTURE_SCREENSHOTS !== '1') return;
  await expect(page.getByText(/^(?:Loading workspace records|Memuat katalog|Menghitung laporan)/)).toHaveCount(0);
  await page.evaluate(async () => { await document.fonts.ready; window.scrollTo(0, 0); if (document.activeElement instanceof HTMLElement) document.activeElement.blur(); });
  const destination = resolve('../docs/screenshots');
  mkdirSync(destination, { recursive: true });
  await page.screenshot({ path: resolve(destination, `${name}.png`), fullPage: true, animations: 'disabled' });
}
