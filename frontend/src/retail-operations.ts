import { api, ApiError, refreshCsrf, write } from './api';
import { queuedOwnerHeaders, scopeFor, transact, type QueueState } from './offline';
import type { User } from './types';

export interface RetailOperation {
  id: string; scope: string; branchId: string; path: string; label: string; payload: Record<string, unknown>;
  createdAt: string; state: QueueState; uncertain: boolean; attempts: number; message: string;
  leaseOwner?: string; leaseUntil?: number; result?: Record<string, unknown>; discarded?: boolean;
}
const allowedPath = /^\/retail\/(?:shifts|shifts\/[0-9a-f-]{36}\/close|sales\/[0-9a-f-]{36}\/returns)$/i;
function changed() { window.dispatchEvent(new Event('retail-queue-changed')); try { localStorage.setItem('retailops.queue-pulse', crypto.randomUUID()); } catch { /* The queue also polls while open. */ } }
export async function listOperations(user: User): Promise<RetailOperation[]> {
  return transact<RetailOperation[]>('operations', 'readonly', (store, done) => {
    const request = store.index('scope').getAll(scopeFor(user));
    request.onsuccess = () => done((request.result as RetailOperation[]).sort((a, b) => a.createdAt.localeCompare(b.createdAt)));
  });
}
export async function enqueueOperation(user: User, path: string, payload: Record<string, unknown>, label: string, branchId: string): Promise<RetailOperation> {
  if (!allowedPath.test(path)) throw new Error('Aksi ini tidak termasuk operasi kasir yang dapat dipulihkan.');
  const row: RetailOperation = { id: crypto.randomUUID(), scope: scopeFor(user), branchId, path, payload: structuredClone(payload), label, state: 'pending', uncertain: false, attempts: 0, createdAt: new Date().toISOString(), message: 'Perintah tersimpan. Belum ada konfirmasi server.' };
  await transact<void>('operations', 'readwrite', (store, done) => { store.add(row); done(); });
  const persisted = (await listOperations(user)).find(item => item.id === row.id);
  if (!persisted || JSON.stringify(persisted.payload) !== JSON.stringify(payload)) throw new Error('Perintah belum berhasil dibaca ulang. Server belum dihubungi.');
  changed(); return persisted;
}
export async function synchronizeOperation(user: User, id: string): Promise<RetailOperation | null> {
  let current: User;
  try { current = await api<User>('/auth/me'); } catch (error) { if (error instanceof ApiError && error.status === 401) window.dispatchEvent(new Event('session-expired')); throw error; }
  if (scopeFor(current) !== scopeFor(user)) throw new Error('Akun aktif berbeda dari pemilik perintah.');
  const owner = crypto.randomUUID();
  const claim = await transact<{ row: RetailOperation; priorUncertain: boolean } | null>('operations', 'readwrite', (store, done) => {
    const request = store.get(id);
    request.onsuccess = () => {
      const row = request.result as RetailOperation | undefined;
      if (!row || row.scope !== scopeFor(user) || !allowedPath.test(row.path) || row.discarded || row.state === 'confirmed' || row.state === 'conflict' || row.state === 'syncing' && (row.leaseUntil || 0) > Date.now()) { done(null); return; }
      const priorUncertain = row.uncertain || row.state === 'syncing';
      row.state = 'syncing'; row.uncertain = true; row.leaseOwner = owner; row.leaseUntil = Date.now() + 120_000; row.attempts += 1;
      store.put(row); done({ row, priorUncertain });
    };
  });
  if (!claim) return (await listOperations(user)).find(row => row.id === id) || null;
  const ownerHeaders = queuedOwnerHeaders(claim.row.scope, claim.row.id);
  let patch: Partial<RetailOperation>;
  try {
    let result: Record<string, unknown>;
    try { result = await write(claim.row.path, claim.row.payload, 'POST', ownerHeaders); }
    catch (error) {
      if (!(error instanceof ApiError) || error.status !== 400 || error.title !== 'Request verification failed') throw error;
      await refreshCsrf(); result = await write(claim.row.path, claim.row.payload, 'POST', ownerHeaders);
    }
    if (!result?.id) throw new Error('Respons belum dapat dibuktikan. Periksa key asli melalui antrean.');
    patch = { state: 'confirmed', uncertain: false, result, message: `Diterima server${typeof result.number === 'string' ? `: ${result.number}` : ''}.` };
  } catch (error) {
    const definite = error instanceof ApiError && [400, 403, 404, 409, 422].includes(error.status) && !(error.status === 400 && error.title === 'Request verification failed');
    patch = { state: definite && !claim.priorUncertain ? 'conflict' : 'pending', uncertain: claim.priorUncertain || !definite, message: error instanceof Error ? error.message : 'Hasil belum diketahui.' };
  }
  await transact<void>('operations', 'readwrite', (store, done) => {
    const request = store.get(id);
    request.onsuccess = () => {
      const row = request.result as RetailOperation | undefined;
      if (row?.scope === scopeFor(user) && row.leaseOwner === owner) { Object.assign(row, patch); delete row.leaseOwner; delete row.leaseUntil; store.put(row); }
      done();
    };
  });
  changed(); return (await listOperations(user)).find(row => row.id === id) || null;
}
export async function discardOperation(user: User, id: string) {
  await transact<void>('operations', 'readwrite', (store, done, fail) => {
    const request = store.get(id);
    request.onsuccess = () => {
      const row = request.result as RetailOperation | undefined;
      if (!row || row.scope !== scopeFor(user) || row.state !== 'conflict' || row.uncertain) { fail(new Error('Hasil yang belum pasti tidak boleh dihapus atau diganti.')); return; }
      row.discarded = true; store.put(row); done();
    };
  });
  changed();
}
