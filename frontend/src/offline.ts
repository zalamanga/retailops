import { api, ApiError, refreshCsrf, write } from './api';
import type { User } from './types';
import type { Branch, RetailCatalog, Sale, SaleCommand, Shift } from './retail-types';

export const OFFLINE_DB = 'retailops-workstation-v1';
const POINTER = 'retailops.offline-owner';
const LEASE_MS = 120_000;
let identityEpoch = 0;
function notifyQueue() {
  window.dispatchEvent(new Event('retail-queue-changed'));
  try { localStorage.setItem('retailops.queue-pulse', crypto.randomUUID()); } catch { /* Other tabs also refresh their queue on a bounded timer. */ }
}
export type QueueState = 'pending' | 'syncing' | 'confirmed' | 'conflict';
export interface QueuedSale {
  id: string; scope: string; branchId: string; branchName: string; command: SaleCommand;
  state: QueueState; createdAt: string; attempts: number; message: string; uncertain: boolean;
  leaseOwner?: string; leaseUntil?: number; result?: Sale; rejectedStatus?: number; discarded?: boolean;
}
export interface WorkstationSnapshot { key: string; scope: string; branches: Branch[]; catalogs: RetailCatalog[]; shifts: Shift[]; savedAt: string }

function database(): Promise<IDBDatabase> {
  return new Promise((resolve, reject) => {
    if (!globalThis.indexedDB) { reject(new Error('Penyimpanan offline tidak tersedia. Penjualan belum dikirim.')); return; }
    const request = indexedDB.open(OFFLINE_DB, 2);
    request.onupgradeneeded = () => {
      const db = request.result;
      if (!db.objectStoreNames.contains('identities')) db.createObjectStore('identities', { keyPath: 'key' });
      if (!db.objectStoreNames.contains('snapshots')) db.createObjectStore('snapshots', { keyPath: 'key' });
      if (!db.objectStoreNames.contains('sales')) {
        const sales = db.createObjectStore('sales', { keyPath: 'id' });
        sales.createIndex('scope', 'scope');
      }
      if (!db.objectStoreNames.contains('operations')) {
        const operations = db.createObjectStore('operations', { keyPath: 'id' });
        operations.createIndex('scope', 'scope');
      }
    };
    request.onerror = () => reject(new Error('Penyimpanan offline gagal dibuka. Izinkan penyimpanan browser sebelum bertransaksi.'));
    request.onblocked = () => reject(new Error('Versi penyimpanan tertahan tab lain. Tutup tab RetailOps lama lalu coba lagi.'));
    request.onsuccess = () => { request.result.onversionchange = () => request.result.close(); resolve(request.result); };
  });
}

export async function transact<T>(store: string, mode: IDBTransactionMode, action: (object: IDBObjectStore, done: (value: T) => void, fail: (error: Error) => void) => void): Promise<T> {
  const db = await database();
  return new Promise<T>((resolve, reject) => {
    const tx = db.transaction(store, mode);
    let result: T;
    let failure: Error | undefined;
    tx.oncomplete = () => { db.close(); resolve(result); };
    tx.onabort = tx.onerror = () => { db.close(); reject(failure || new Error('Perubahan penyimpanan offline belum tersimpan. Penjualan tidak dikirim.')); };
    try { action(tx.objectStore(store), value => { result = value; }, error => { failure = error; tx.abort(); }); }
    catch (error) { failure = error instanceof Error ? error : new Error('Penyimpanan lokal gagal.'); tx.abort(); }
  });
}

export function scopeFor(user: Pick<User, 'id' | 'organizationId'>): string {
  if (!user.id || !user.organizationId) throw new Error('Identitas organisasi belum tersedia. Masuk kembali saat terhubung sebelum menggunakan kasir.');
  return `${user.organizationId}:${user.id}`;
}
export async function rememberIdentity(user: User) {
  const epoch = ++identityEpoch;
  const key = scopeFor(user);
  // Email and authentication credentials are unnecessary for a disconnected workstation.
  const identity = { id: user.id, organizationId: user.organizationId, organizationName: user.organizationName, fullName: user.fullName, role: user.role, email: '' };
  await transact<void>('identities', 'readwrite', (store, done) => { store.put({ key, identity }); done(); });
  if (epoch === identityEpoch) localStorage.setItem(POINTER, key);
}
export async function lastIdentity(): Promise<User | null> {
  let key: string | null;
  try { key = localStorage.getItem(POINTER); } catch { return null; }
  if (!key) return null;
  return transact<User | null>('identities', 'readonly', (store, done) => {
    const request = store.get(key);
    request.onsuccess = () => done(request.result?.identity || null);
  });
}
export function invalidateIdentityWrites() { identityEpoch += 1; }
export function forgetIdentity() { invalidateIdentityWrites(); try { localStorage.removeItem(POINTER); } catch { /* Logout must not erase the owner's queued evidence. */ } }
export function queuedOwnerHeaders(scope: string, key: string): Record<string, string> {
  const [organizationId, actorId, extra] = scope.split(':');
  const uuid = /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i;
  if (extra || !uuid.test(organizationId || '') || !uuid.test(actorId || '')) throw new Error('Identitas pemilik perintah tersimpan tidak valid.');
  return { 'Idempotency-Key': key, 'X-Retail-Actor': actorId, 'X-Retail-Organization': organizationId };
}
export async function saveSnapshot(user: User, value: Omit<WorkstationSnapshot, 'key' | 'scope' | 'savedAt'>) {
  const scope = scopeFor(user);
  return transact<void>('snapshots', 'readwrite', (store, done) => { store.put({ ...value, key: scope, scope, savedAt: new Date().toISOString() }); done(); });
}
export async function loadSnapshot(user: User): Promise<WorkstationSnapshot | null> {
  const scope = scopeFor(user);
  return transact<WorkstationSnapshot | null>('snapshots', 'readonly', (store, done) => { const request = store.get(scope); request.onsuccess = () => done(request.result || null); });
}
export async function listQueue(user: Pick<User, 'id' | 'organizationId'>): Promise<QueuedSale[]> {
  const scope = scopeFor(user);
  return transact<QueuedSale[]>('sales', 'readonly', (store, done) => {
    const request = store.index('scope').getAll(scope);
    request.onsuccess = () => done((request.result as QueuedSale[]).sort((a, b) => a.createdAt.localeCompare(b.createdAt)));
  });
}
export function commandTotal(command: Pick<SaleCommand, 'lines'>): number {
  return command.lines.reduce((sum, line) => sum + Math.round(line.unitPrice * 100) * line.quantity, 0) / 100;
}
export function isMoney(value: number): boolean {
  return Number.isFinite(value) && Number.isSafeInteger(Math.round(value * 100)) && Number(value.toFixed(2)) === value;
}
export function cashChange(tendered: number, total: number): number {
  return (Math.round(tendered * 100) - Math.round(total * 100)) / 100;
}
export function sufficientCash(tendered: number, total: number): boolean {
  return isMoney(tendered) && isMoney(total) && Math.round(tendered * 100) >= Math.round(total * 100);
}
export function validateSaleCommand(command: SaleCommand): void {
  const uuid = /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i;
  if (!uuid.test(command.branchId) || !uuid.test(command.shiftId) || !['Retail', 'Wholesale'].includes(command.priceTier) || !Number.isFinite(Date.parse(command.clientCreatedAt))) throw new Error('Identitas cabang, shift atau waktu perintah belum valid.');
  if (!command.lines.length || command.lines.length > 50 || new Set(command.lines.map(line => line.productId)).size !== command.lines.length) throw new Error('Penjualan memerlukan 1 sampai 50 produk berbeda.');
  if (command.lines.some(line => !uuid.test(line.productId) || !Number.isSafeInteger(line.quantity) || line.quantity <= 0 || !isMoney(line.unitPrice) || line.unitPrice <= 0 || !Number.isSafeInteger(line.priceVersion) || line.priceVersion < 1)) throw new Error('Jumlah, harga atau versi harga produk belum valid.');
  const total = commandTotal(command);
  if (!sufficientCash(command.cashTendered, total)) throw new Error('Nilai tunai harus menutupi total dan memakai maksimal dua angka desimal.');
}
export async function enqueueSale(user: User, branchName: string, command: SaleCommand): Promise<QueuedSale> {
  validateSaleCommand(command);
  const record: QueuedSale = {
    id: crypto.randomUUID(), scope: scopeFor(user), branchId: command.branchId, branchName,
    command: structuredClone(command), state: 'pending', createdAt: new Date().toISOString(),
    attempts: 0, message: 'Tersimpan lokal. Belum menjadi penjualan server.', uncertain: false,
  };
  await transact<void>('sales', 'readwrite', (store, done) => { store.add(record); done(); });
  const saved = (await listQueue(user)).find(row => row.id === record.id);
  if (!saved || JSON.stringify(saved.command) !== JSON.stringify(command)) throw new Error('Pembacaan ulang antrean gagal. Jangan serahkan struk sebagai penjualan terkonfirmasi.');
  notifyQueue();
  return saved;
}
async function claim(user: User, id: string, owner: string): Promise<{ record: QueuedSale; priorUncertain: boolean } | null> {
  return transact<{ record: QueuedSale; priorUncertain: boolean } | null>('sales', 'readwrite', (store, done) => {
    const request = store.get(id);
    request.onsuccess = () => {
      const record = request.result as QueuedSale | undefined;
      if (!record || record.scope !== scopeFor(user) || record.discarded || record.state === 'confirmed' || record.state === 'conflict' || (record.state === 'syncing' && (record.leaseUntil || 0) > Date.now())) { done(null); return; }
      const priorUncertain = record.uncertain || record.state === 'syncing';
      record.state = 'syncing'; record.leaseOwner = owner; record.leaseUntil = Date.now() + LEASE_MS;
      // Persist ambiguity before POST so a crash cannot turn an accepted sale into a fresh intent.
      record.uncertain = true;
      record.attempts += 1; record.message = 'Mengirim perintah asli ke server.';
      store.put(record); done({ record, priorUncertain });
    };
  });
}
async function updateClaim(user: User, id: string, owner: string, patch: Partial<Pick<QueuedSale, 'state' | 'message' | 'uncertain' | 'result' | 'rejectedStatus'>>) {
  await transact<void>('sales', 'readwrite', (store, done) => {
    const request = store.get(id);
    request.onsuccess = () => {
      const record = request.result as QueuedSale | undefined;
      if (!record || record.scope !== scopeFor(user) || record.leaseOwner !== owner) { done(); return; }
      Object.assign(record, patch); delete record.leaseOwner; delete record.leaseUntil;
      store.put(record); done();
    };
  });
  notifyQueue();
}
export async function discardRejected(user: User, id: string): Promise<void> {
  await transact<void>('sales', 'readwrite', (store, done, fail) => {
    const request = store.get(id);
    request.onsuccess = () => {
      const record = request.result as QueuedSale | undefined;
      if (!record || record.scope !== scopeFor(user) || record.state !== 'conflict' || record.uncertain) { fail(new Error('Hanya penolakan pasti yang boleh diarsipkan. Hasil yang belum pasti harus diperiksa memakai key asli.')); return; }
      record.discarded = true; record.message = 'Penolakan ditinjau dan diarsipkan. Buat transaksi baru secara eksplisit jika diperlukan.';
      store.put(record); done();
    };
  });
  notifyQueue();
}
export async function synchronizeSale(user: User, id: string): Promise<void> {
  // Confirm the active cookie still belongs to the command owner before any sale POST.
  let current: User;
  try { current = await api<User>('/auth/me'); }
  catch (error) {
    if (error instanceof ApiError && error.status === 401) window.dispatchEvent(new Event('session-expired'));
    throw error;
  }
  if (scopeFor(current) !== scopeFor(user)) throw new Error('Akun aktif berbeda. Masuk sebagai pemilik antrean sebelum mengirimnya.');
  const owner = crypto.randomUUID();
  const claimed = await claim(user, id, owner);
  if (!claimed) return;
  const { record, priorUncertain } = claimed;
  const ownerHeaders = queuedOwnerHeaders(record.scope, record.id);
  try {
    let result: Sale;
    try { result = await write<Sale>('/retail/sales', record.command, 'POST', ownerHeaders); }
    catch (error) {
      if (!(error instanceof ApiError) || error.status !== 400 || error.title !== 'Request verification failed') throw error;
      await refreshCsrf();
      result = await write<Sale>('/retail/sales', record.command, 'POST', ownerHeaders);
    }
    if (!result?.id || !result.number) throw new Error('Respons penjualan belum dapat dibuktikan. Periksa dengan key asli.');
    await updateClaim(user, id, owner, { state: 'confirmed', message: `Diterima server: ${result.number}.`, uncertain: false, result });
  } catch (error) {
    const csrfFailure = error instanceof ApiError && error.status === 400 && error.title === 'Request verification failed';
    const definite = error instanceof ApiError && [400, 403, 404, 409, 422].includes(error.status) && !csrfFailure;
    await updateClaim(user, id, owner, {
      state: definite && !priorUncertain ? 'conflict' : 'pending', uncertain: priorUncertain || !definite,
      rejectedStatus: definite && error instanceof ApiError ? error.status : undefined,
      message: error instanceof Error ? error.message : 'Hasil belum diketahui. Periksa kembali dengan key asli.',
    });
    if (error instanceof ApiError && error.status === 401) throw error;
  }
}
