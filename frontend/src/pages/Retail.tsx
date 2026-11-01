import { useCallback, useEffect, useRef, useState } from 'react';
import { Link } from 'react-router-dom';
import { useQuery, useQueryClient } from '@tanstack/react-query';
import { all, api, ApiError, params, write } from '../api';
import { Empty, ErrorNotice, Field, Form, Loading, Modal, numeric, PageHeading, ResultNotice, Search, Table, text, useResource, useUser } from '../components';
import { date, money, number } from '../format';
import { cashChange, commandTotal, discardRejected, enqueueSale, listQueue, loadSnapshot, saveSnapshot, scopeFor, sufficientCash, synchronizeSale, type QueuedSale } from '../offline';
import type { Product, TeamUser, Warehouse } from '../types';
import type { Assignment, Branch, CatalogProduct, Journal, Price, RetailCatalog, RetailReport, Sale, SaleCommand, SaleReturn, SaleSummary, Shift } from '../retail-types';
import { discardOperation, enqueueOperation, listOperations, synchronizeOperation, type RetailOperation } from '../retail-operations';

const openShift = (shift: Shift) => shift.status.toLowerCase() === 'open';
const stateText = { pending: 'Menunggu server', syncing: 'Sedang dikirim', confirmed: 'Terkonfirmasi', conflict: 'Perlu ditinjau' };
function today() { return new Intl.DateTimeFormat('en-CA', { timeZone: 'Asia/Jakarta', year: 'numeric', month: '2-digit', day: '2-digit' }).format(new Date()); }
function queueError(error: unknown) { return error instanceof Error ? error.message : 'Penyimpanan antrean belum tersedia.'; }

export function PointOfSale() {
  const user = useUser();
  const client = useQueryClient();
  const [branches, setBranches] = useState<Branch[]>([]);
  const [branchId, setBranchId] = useState('');
  const [catalog, setCatalog] = useState<RetailCatalog | null>(null);
  const [shifts, setShifts] = useState<Shift[]>([]);
  const [cached, setCached] = useState(false);
  const [cacheSaved, setCacheSaved] = useState<string | null>(null);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState<unknown>(null);
  const [search, setSearch] = useState('');
  const [tier, setTier] = useState<'Retail' | 'Wholesale'>('Retail');
  const [cart, setCart] = useState<{ product: CatalogProduct; quantity: number }[]>([]);
  const [cash, setCash] = useState('');
  const [saving, setSaving] = useState(false);
  const [lastSale, setLastSale] = useState<QueuedSale | null>(null);
  const [showCheckout, setShowCheckout] = useState(false);
  const [queue, setQueue] = useState<QueuedSale[]>([]);
  const [shellReady, setShellReady] = useState(Boolean(navigator.serviceWorker?.controller));
  const generation = useRef(0);
  const scope = scopeFor(user);
  const refreshQueue = useCallback(async () => {
    try { setQueue(await listQueue(user)); } catch (failure) { setError(failure); }
  }, [scope]);
  const refresh = useCallback(async (requested?: string) => {
    const token = ++generation.current;
    setLoading(true); setError(null);
    let stored = null;
    try { stored = await loadSnapshot(user); } catch (failure) { setError(failure); }
    try {
      const available = await api<Branch[]>('/retail/branches');
      if (requested && !available.some(row => row.id === requested)) throw new ApiError('Akses cabang ini berubah. Keranjang tetap pada cabang asal; kosongkan sebelum memilih cabang lain.', 403);
      const nextId = available.some(row => row.id === requested) ? requested! : available[0]?.id || '';
      const [nextCatalog, nextShifts] = nextId ? await Promise.all([
        api<RetailCatalog>(`/retail/catalog${params({ branchId: nextId })}`),
        api<Shift[]>(`/retail/shifts${params({ branchId: nextId })}`),
      ]) : [null, [] as Shift[]];
      if (token !== generation.current) return;
      setBranches(available); setBranchId(nextId); setCatalog(nextCatalog); setShifts(nextShifts); setCached(false);
      const catalogs = stored?.catalogs.filter(row => available.some(branch => branch.id === row.branch.id) && row.branch.id !== nextId) || [];
      if (nextCatalog) catalogs.push(nextCatalog);
      await saveSnapshot(user, { branches: available, catalogs, shifts: [...(stored?.shifts.filter(row => row.branchId !== nextId) || []), ...nextShifts].filter(row => row.openedById === user.id) });
      if (token === generation.current) setCacheSaved(new Date().toISOString());
    } catch (failure) {
      if (token !== generation.current) return;
      const disconnected = failure instanceof ApiError && failure.status === 0;
      if (failure instanceof ApiError && failure.status === 403) setShifts([]);
      if (disconnected && stored) {
        const nextId = stored.branches.some(row => row.id === requested) ? requested! : stored.branches[0]?.id || '';
        setBranches(stored.branches); setBranchId(nextId); setCatalog(stored.catalogs.find(row => row.branch.id === nextId) || null);
        setShifts(stored.shifts.filter(row => row.branchId === nextId)); setCached(true); setCacheSaved(stored.savedAt);
      }
      setError(failure);
    } finally { if (token === generation.current) setLoading(false); }
  }, [scope]);
  useEffect(() => { void refresh(); void refreshQueue(); return () => { generation.current += 1; }; }, [refresh, refreshQueue]);
  useEffect(() => {
    if (!navigator.serviceWorker) return;
    const changed = () => setShellReady(Boolean(navigator.serviceWorker.controller));
    navigator.serviceWorker.addEventListener('controllerchange', changed);
    return () => navigator.serviceWorker.removeEventListener('controllerchange', changed);
  }, []);
  useEffect(() => {
    const changed = () => { void refreshQueue(); };
    window.addEventListener('retail-queue-changed', changed);
    window.addEventListener('storage', changed);
    return () => { window.removeEventListener('retail-queue-changed', changed); window.removeEventListener('storage', changed); };
  }, [refreshQueue]);
  const shift = shifts.find(row => openShift(row) && row.openedById === user.id);
  const branch = branches.find(row => row.id === branchId);
  const pending = queue.filter(row => !row.discarded && row.state !== 'confirmed');
  const products = (catalog?.products || []).filter(row => `${row.sku} ${row.name}`.toLowerCase().includes(search.toLowerCase()));
  const lines = cart.map(({ product, quantity }) => ({ productId: product.productId, quantity, unitPrice: tier === 'Wholesale' ? product.wholesalePrice : product.retailPrice, priceVersion: product.version }));
  const total = commandTotal({ lines });
  const wholesaleError = tier === 'Wholesale' && cart.some(row => row.quantity < row.product.wholesaleMinimum);
  const cashValue = Number(cash);
  const validCash = cash !== '' && sufficientCash(cashValue, total);
  const change = validCash ? cashChange(cashValue, total) : 0;
  function changeBranch(value: string) {
    if (cart.length) { setError(new Error('Kosongkan keranjang sebelum mengganti cabang agar transaksi tidak berpindah lokasi.')); return; }
    void refresh(value);
  }
  async function confirmSale() {
    if (!shift || !branch || !lines.length || !validCash || wholesaleError || saving) return;
    setSaving(true); setError(null);
    try {
      const record = await enqueueSale(user, branch.name, { branchId, shiftId: shift.id, priceTier: tier, clientCreatedAt: new Date().toISOString(), lines: [...lines].sort((a, b) => a.productId.localeCompare(b.productId)), cashTendered: cashValue });
      setLastSale(record); setCart([]); setCash(''); setShowCheckout(false);
      if (!cached && navigator.onLine) {
        try { await synchronizeSale(user, record.id); } catch (failure) { setError(failure); }
      }
      const next = await listQueue(user); setQueue(next); setLastSale(next.find(row => row.id === record.id) || record);
      if (!cached) { await refresh(branchId); await client.invalidateQueries(); }
    } catch (failure) { setError(failure); }
    finally { setSaving(false); }
  }
  return <>
    <PageHeading title="Kasir cabang" description="Catat penjualan tunai dengan harga yang tersimpan, shift sendiri, dan bukti penerimaan server." action={<Link className="button secondary" to="/shifts">Kelola shift</Link>} />
    <div className={`retail-connection ${cached ? 'is-offline' : ''}`} role="status">
      <strong>{cached ? 'Offline: memakai katalog tersimpan' : 'Katalog dari server'}</strong>
      <span>{cacheSaved ? `Cache terakhir ${date(cacheSaved, true)}. Stok terakhir dapat berubah sebelum sinkronisasi.` : 'Belum ada cache tersimpan untuk akun ini.'}</span>
      <small>{shellReady ? 'Muat ulang offline siap pada browser ini.' : 'Muat ulang offline belum siap. Tetap tersambung sampai persiapan selesai.'}</small>
      <button type="button" className="button secondary" disabled={loading || saving} onClick={() => void refresh(branchId)}>Perbarui katalog</button>
    </div>
    {!cached && <ErrorNotice error={error} retry={() => void refresh(branchId)} />}
    {cached && error && <p className="field-note">{queueError(error)} Transaksi lokal tetap menunggu pemeriksaan server.</p>}
    <div className="retail-toolbar panel">
      <Field label="Cabang"><select value={branchId} onChange={event => changeBranch(event.target.value)} disabled={loading || saving}><option value="">Pilih cabang</option>{branches.map(row => <option value={row.id} key={row.id}>{row.code} · {row.name}</option>)}</select></Field>
      <Field label="Harga jual"><select value={tier} disabled={saving || showCheckout} onChange={event => setTier(event.target.value as 'Retail' | 'Wholesale')}><option value="Retail">Eceran</option><option value="Wholesale">Grosir</option></select></Field>
      <div className="shift-context"><span>Shift saya</span><strong>{shift ? `Aktif sejak ${date(shift.openedAt, true)}` : 'Belum ada shift aktif'}</strong><small>{shift ? `${shift.id.slice(0, 8)} · ${shift.branchName}${cached ? ' · status terakhir tersimpan' : ''}` : 'Buka shift saat terhubung sebelum mencatat penjualan.'}</small></div>
      <Link className="queue-link" to="/sync">Antrean sinkronisasi <strong>{pending.length}</strong></Link>
    </div>
    {lastSale && <div className={`notice ${lastSale.state === 'confirmed' ? 'success' : ''}`} role="status"><strong>{lastSale.state === 'confirmed' ? `Struk ${lastSale.result?.number}` : `Perintah lokal ${lastSale.id}`}</strong><p>{lastSale.message}</p>{lastSale.result && <Link to={`/sales/${lastSale.result.id}`}>Buka struk server</Link>}{lastSale.state !== 'confirmed' && <Link to="/sync">Periksa hasil di antrean sinkronisasi</Link>}</div>}
    {loading && !catalog && <Loading text="Memuat katalog dan shift cabang…" />}
    {!loading && !branches.length && <Empty title="Belum ada cabang yang dapat dibuka">Admin perlu membuat cabang dan memberikan akses ke akun kasir.</Empty>}
    {branch && <div className="pos-layout">
      <section className="panel product-workspace" aria-labelledby="pos-products-heading">
        <div className="panel-title"><div><h2 id="pos-products-heading">Produk · {branch.name}</h2><p>{catalog?.capturedAt ? `Snapshot stok ${date(catalog.capturedAt, true)}` : 'Katalog cabang belum tersimpan.'}</p></div></div>
        <div className="filter-bar"><Search label="Cari produk kasir" value={search} onChange={setSearch} placeholder="Nama produk atau SKU" /></div>
        {products.length ? <div className="pos-products">{products.map(product => <article key={product.productId} className="pos-product"><span className="document-code">{product.sku}</span><h3>{product.name}</h3><p className="pos-price">{money(tier === 'Wholesale' ? product.wholesalePrice : product.retailPrice)}<small> / {product.unit}</small></p><p className="product-stock">Stok terakhir {number(product.onHand)} {product.unit}{tier === 'Wholesale' ? ` · minimum ${product.wholesaleMinimum}` : ''}</p><button className="button secondary" type="button" disabled={!shift || saving || cart.length >= 50 && !cart.some(row => row.product.productId === product.productId)} aria-label={`Tambah ${product.name}`} onClick={() => setCart(previous => previous.some(row => row.product.productId === product.productId) ? previous.map(row => row.product.productId === product.productId ? { ...row, quantity: row.quantity + 1 } : row) : [...previous, { product: structuredClone(product), quantity: tier === 'Wholesale' ? product.wholesaleMinimum : 1 }])}>Tambah ke keranjang</button></article>)}</div> : <Empty title={search ? 'Produk tidak ditemukan' : 'Belum ada produk berharga di cabang ini'}>{search ? 'Coba nama atau SKU lain.' : 'Admin menetapkan harga jual. Persediaan dikelola melalui penerimaan atau transfer gudang.'}</Empty>}
      </section>
      <section className="panel pos-cart" aria-labelledby="cart-heading">
        <header className="panel-title"><div><h2 id="cart-heading">Keranjang</h2><p>{tier === 'Retail' ? 'Harga eceran' : 'Harga grosir'} · {cart.length} jenis barang</p></div></header>
        {!cart.length ? <Empty title="Keranjang masih kosong">Pilih produk di katalog untuk menyiapkan penjualan.</Empty> : <>
          <ul className="cart-lines">
            {cart.map(({ product, quantity }) => <li key={product.productId}>
              <div>
                <strong>{product.name}</strong><span className="document-code">{product.sku}</span>
                <small>{money(tier === 'Wholesale' ? product.wholesalePrice : product.retailPrice)} / {product.unit} · versi {product.version}</small>
              </div>
              <div className="cart-controls">
                <label>Jumlah {product.name}
                  <input aria-label={`Jumlah ${product.name}`} type="number" min="1" max="1000000" step="1" value={quantity} disabled={saving}
                    onChange={event => setCart(previous => previous.map(row => row.product.productId === product.productId ? { ...row, quantity: Math.max(1, Math.floor(Number(event.target.value) || 1)) } : row))} />
                </label>
                <button className="button text" type="button" disabled={saving} aria-label={`Hapus ${product.name}`}
                  onClick={() => setCart(previous => previous.filter(row => row.product.productId !== product.productId))}>Hapus</button>
              </div>
            </li>)}
          </ul>
          <div className="cart-total"><span>Total yang diajukan</span><strong>{money(total)}</strong></div>
          <div className="cart-payment">
            <Field label="Uang tunai diterima (IDR)"><input value={cash} onChange={event => setCash(event.target.value)} type="number" min={total} step="0.01" inputMode="decimal" disabled={saving} /></Field>
            <p>Kembalian {money(change)}</p>
            {wholesaleError && <p className="field-error">Setiap produk grosir harus memenuhi jumlah minimumnya.</p>}
            <button className="button primary" type="button" disabled={!shift || !validCash || wholesaleError || saving} onClick={() => setShowCheckout(true)}>Tinjau penjualan tunai</button>
            <button className="button text" type="button" disabled={saving} onClick={() => setCart([])}>Kosongkan keranjang</button>
            <small>Harga pada keranjang tetap dari saat produk ditambahkan. Server menolak versi harga yang sudah berubah; antrean tidak dihargai ulang otomatis.</small>
          </div>
        </>}
      </section>
    </div>}
    {showCheckout && <Modal title="Konfirmasi penjualan tunai" onClose={() => setShowCheckout(false)}>
      <div className="form">
        <ErrorNotice error={error} />
        <p>{branch?.name} · {tier === 'Retail' ? 'Eceran' : 'Grosir'} · shift {shift?.id.slice(0, 8)}</p>
        <dl className="document-facts">
          <div><dt>Total diajukan</dt><dd>{money(total)}</dd></div>
          <div><dt>Tunai diterima</dt><dd>{money(cashValue)}</dd></div>
          <div><dt>Kembalian</dt><dd>{money(change)}</dd></div>
        </dl>
        <p>{cached ? 'Perintah akan disimpan lokal. Struk server baru tersedia setelah sinkronisasi diterima. Harga, akses dan stok diperiksa saat itu.' : 'Perintah disimpan lokal sebelum dikirim. Hasil yang belum pasti tetap memakai identitas transaksi yang sama.'}</p>
        <div className="form-actions">
          <button className="button secondary" disabled={saving} onClick={() => setShowCheckout(false)}>Kembali ke keranjang</button>
          <button className="button primary" disabled={saving} onClick={() => void confirmSale()}>{saving ? 'Menyimpan…' : cached ? 'Simpan perintah offline' : 'Catat dan kirim penjualan'}</button>
        </div>
      </div>
    </Modal>}
  </>;
}

export function SyncQueue() {
  const user = useUser();
  const [rows, setRows] = useState<QueuedSale[]>([]);
  const [error, setError] = useState<unknown>(null);
  const [busy, setBusy] = useState('');
  const [loading, setLoading] = useState(true);
  const scope = scopeFor(user);
  const refresh = useCallback(async () => { try { setRows(await listQueue(user)); setError(null); } catch (failure) { setError(failure); } finally { setLoading(false); } }, [scope]);
  useEffect(() => { void refresh(); const tick = setInterval(() => void refresh(), 4000); window.addEventListener('retail-queue-changed', refresh); return () => { clearInterval(tick); window.removeEventListener('retail-queue-changed', refresh); }; }, [refresh]);
  async function send(row: QueuedSale) { setBusy(row.id); setError(null); try { await synchronizeSale(user, row.id); } catch (failure) { setError(failure); } finally { setBusy(''); await refresh(); } }
  return <><PageHeading title="Antrean sinkronisasi" description="Perintah milik akun ini tetap memakai key, harga dan isi aslinya sampai server memberi hasil." action={<button className="button secondary" onClick={() => void refresh()}>Perbarui antrean</button>} /><ErrorNotice error={error} />
    <div className="notice"><strong>Perintah lokal bukan struk penjualan</strong><p>Hasil yang belum pasti harus dikirim ulang dengan key asli. Penolakan pasti dapat diarsipkan setelah ditinjau, lalu buat penjualan baru dari katalog terbaru. Antrean tersimpan pada browser ini dan hanya ditampilkan untuk akun serta organisasi yang sama.</p></div>
    <OperationQueue />
    {loading && <Loading text="Membuka antrean IndexedDB…" />}
    {!loading && !rows.length && <section className="panel"><Empty title="Belum ada perintah penjualan">Penjualan kasir akan disimpan di sini sebelum pengiriman pertama.</Empty></section>}
    <div className="queue-list">{rows.map(row => <article key={row.id} className="panel queue-record"><header><div><span className="document-code">{row.id}</span><h2>{row.branchName}</h2><p>{date(row.createdAt, true)} · {row.command.priceTier === 'Retail' ? 'Eceran' : 'Grosir'} · {money(commandTotal(row.command))}</p></div><span className={`status status-${row.state}`}>{row.discarded ? 'Penolakan diarsipkan' : stateText[row.state]}</span></header><p>{row.message}</p>{row.uncertain && <p className="field-note">Hasil belum pasti. Jangan buat ulang transaksi ini dengan key baru.</p>}<details><summary>Isi perintah dan riwayat percobaan</summary><dl className="document-facts"><div><dt>Shift</dt><dd className="document-code">{row.command.shiftId}</dd></div><div><dt>Percobaan kirim</dt><dd>{row.attempts}</dd></div><div><dt>Waktu perangkat</dt><dd>{date(row.command.clientCreatedAt, true)}</dd></div></dl><pre>{JSON.stringify(row.command, null, 2)}</pre></details><div className="form-actions">{row.result && <Link className="button secondary" to={`/sales/${row.result.id}`}>Buka struk {row.result.number}</Link>}{!row.discarded && (row.state === 'pending' || row.state === 'syncing') && <button type="button" className="button primary" disabled={Boolean(busy) || row.state === 'syncing' && (row.leaseUntil || 0) > Date.now()} onClick={() => void send(row)}>{busy === row.id ? 'Memeriksa…' : 'Kirim ulang key asli'}</button>}{!row.discarded && row.state === 'conflict' && !row.uncertain && <button type="button" className="button secondary" disabled={Boolean(busy)} onClick={async () => { try { await discardRejected(user, row.id); await refresh(); } catch (failure) { setError(failure); } }}>Saya sudah meninjau, arsipkan penolakan</button>}</div></article>)}</div>
  </>;
}

function OperationQueue() {
  const user = useUser();
  const [rows, setRows] = useState<RetailOperation[]>([]);
  const [error, setError] = useState<unknown>(null);
  const [busy, setBusy] = useState('');
  const scope = scopeFor(user);
  const refresh = useCallback(async () => {
    try { setRows(await listOperations(user)); } catch (failure) { setError(failure); }
  }, [scope]);
  useEffect(() => {
    void refresh();
    const timer = setInterval(() => void refresh(), 4000);
    window.addEventListener('retail-queue-changed', refresh);
    return () => { clearInterval(timer); window.removeEventListener('retail-queue-changed', refresh); };
  }, [refresh]);
  if (!rows.length && !error) return null;
  return <section className="panel operation-queue">
    <div className="panel-title"><div><h2>Operasi shift & retur</h2><p>Key tetap tersimpan setelah dialog ditutup atau halaman dimuat ulang.</p></div></div>
    <ErrorNotice error={error} />
    {rows.map(row => <article className="queue-record" key={row.id}>
      <header><div><span className="document-code">{row.id}</span><h3>{row.label}</h3><p>{date(row.createdAt, true)} · percobaan {row.attempts}</p></div><span className={`status status-${row.state}`}>{row.discarded ? 'Penolakan diarsipkan' : stateText[row.state]}</span></header>
      <p>{row.message}</p>
      {row.uncertain && <p className="field-note">Hasil belum pasti. Jangan buat operasi pengganti dengan key baru.</p>}
      <details><summary>Lihat isi operasi yang tersimpan</summary><pre>{JSON.stringify(row.payload, null, 2)}</pre></details>
      <div className="form-actions">
        {!row.discarded && ['pending', 'syncing'].includes(row.state) && <button className="button primary" disabled={Boolean(busy) || row.state === 'syncing' && (row.leaseUntil || 0) > Date.now()} onClick={async () => {
          setBusy(row.id); setError(null);
          try { await synchronizeOperation(user, row.id); } catch (failure) { setError(failure); }
          finally { setBusy(''); await refresh(); }
        }}>Periksa operasi dengan key asli</button>}
        {!row.discarded && row.state === 'conflict' && !row.uncertain && <button className="button secondary" onClick={async () => {
          try { await discardOperation(user, row.id); await refresh(); } catch (failure) { setError(failure); }
        }}>Arsipkan operasi yang ditolak</button>}
      </div>
    </article>)}
  </section>;
}

export function Shifts() {
  const user = useUser(); const client = useQueryClient();
  const branches = useResource<Branch[]>(['retail-branches', user.id], '/retail/branches');
  const [branch, setBranch] = useState('');
  const query = useResource<Shift[]>(['retail-shifts', user.id, branch], `/retail/shifts${params({ branchId: branch })}`);
  const [editing, setEditing] = useState<Shift | 'new' | null>(null);
  const [operationKey, setOperationKey] = useState('');
  const [notice, setNotice] = useState('');
  const [blocked, setBlocked] = useState(false);
  const intent = useRef<Record<string, unknown> | null>(null);
  const operation = useRef<RetailOperation | null>(null);
  useEffect(() => { intent.current = null; operation.current = null; }, [editing]);
  async function verifyQueue() { const queue = await listQueue(user); return queue.some(row => !row.discarded && row.state !== 'confirmed' && (editing === 'new' || row.command.shiftId === editing?.id)); }
  return <><PageHeading title="Shift kasir" description="Buka laci sendiri dan rekonsiliasi penjualan serta retur yang diterima server." action={<button className="button primary" onClick={() => { setEditing('new'); setOperationKey(crypto.randomUUID()); setBlocked(false); intent.current = null; }}>Buka shift saya</button>} />
    <div className="notice"><strong>Hitungan laci adalah catatan operasional</strong><p>Kas pembuka dan selisih tutup tidak otomatis menjadi modal atau posting buku besar. Kas yang diharapkan = pembuka + penjualan diterima - retur pada shift ini. Selesaikan antrean penjualan sebelum menutup shift.</p></div>
    <ErrorNotice error={branches.error || query.error} retry={() => { void branches.refetch(); void query.refetch(); }} />{notice && <ResultNotice>{notice}</ResultNotice>}
    <section className="panel"><div className="filter-bar"><Field label="Filter cabang"><select value={branch} onChange={event => setBranch(event.target.value)}><option value="">Semua cabang yang diizinkan</option>{branches.data?.map(row => <option key={row.id} value={row.id}>{row.name}</option>)}</select></Field><button className="button secondary" onClick={() => void query.refetch()}>Perbarui shift</button></div>{query.isLoading && <Loading />}{query.data?.length ? <Table label="Shift kasir"><thead><tr><th>Cabang / kasir</th><th>Dibuka</th><th>Status</th><th className="numeric">Pembuka</th><th className="numeric">Kas diharapkan</th><th className="numeric">Kas dihitung / selisih</th><th>Tindakan</th></tr></thead><tbody>{query.data.map(row => <tr key={row.id}><td><strong>{row.branchName}</strong><small>{row.openedByName}</small></td><td>{date(row.openedAt, true)}</td><td>{openShift(row) ? 'Aktif' : 'Ditutup'}</td><td className="numeric">{money(row.openingCash)}</td><td className="numeric">{money(row.expectedCash)}</td><td className="numeric">{row.countedCash === null ? 'Belum dihitung' : money(row.countedCash)}<small>{row.variance === null ? '' : `Selisih ${money(row.variance)}`}</small></td><td>{openShift(row) && row.openedById === user.id && <button className="button secondary" onClick={() => { setEditing(row); setOperationKey(crypto.randomUUID()); setBlocked(false); }}>Tutup shift</button>}</td></tr>)}</tbody></Table> : query.data && <Empty title="Belum ada shift">Buka shift milik sendiri pada cabang yang diizinkan sebelum melakukan penjualan.</Empty>}</section>
    {editing && <Modal title={editing === 'new' ? 'Buka shift saya' : `Tutup shift · ${editing.branchName}`} onClose={() => setEditing(null)}><Form submitLabel={blocked ? 'Periksa ulang dengan key yang sama' : editing === 'new' ? 'Buka laci kasir' : 'Simpan hitungan dan tutup'} onCancel={() => setEditing(null)} submit={async data => {
      if (await verifyQueue()) throw new Error('Masih ada perintah penjualan yang belum selesai. Buka antrean sinkronisasi sebelum mengubah shift.');
      try {
        const payload = intent.current || (editing === 'new' ? { branchId: text(data, 'branchId'), openingCash: numeric(data, 'openingCash') } : { countedCash: numeric(data, 'countedCash') });
        intent.current = payload;
        const path = editing === 'new' ? '/retail/shifts' : `/retail/shifts/${editing.id}/close`;
        if (!operation.current) {
          const unresolved = (await listOperations(user)).some(row => !row.discarded && row.state !== 'confirmed');
          if (unresolved) throw new Error('Ada operasi kasir yang belum selesai. Periksa antrean sinkronisasi sebelum membuat operasi baru.');
          operation.current = await enqueueOperation(user, path, payload, editing === 'new' ? 'Buka shift saya' : `Tutup shift ${editing.branchName}`, editing === 'new' ? String(payload.branchId) : editing.branchId);
        }
        const completed = await synchronizeOperation(user, operation.current.id);
        if (!completed?.result || completed.state !== 'confirmed') { setBlocked(true); throw new Error(completed?.message || 'Hasil belum diketahui. Periksa antrean sinkronisasi dengan key asli.'); }
        const result = completed.result as unknown as Shift;
        setNotice(openShift(result) ? `Shift ${result.branchName} berhasil dibuka.` : `Shift ditutup. Kas diharapkan ${money(result.expectedCash)}, selisih ${money(result.variance || 0)}.`); setEditing(null); setBlocked(false); await client.invalidateQueries();
      } catch (failure) { if (!(failure instanceof ApiError) || failure.status === 0 || failure.status >= 500) setBlocked(true); else intent.current = null; throw failure; }
    }}>{blocked && <p className="notice">Hasil belum diketahui. Pertahankan isi dan key ini sampai server memberi jawaban.</p>}{editing === 'new' ? <><Field label="Cabang"><select name="branchId" required defaultValue={branch} disabled={blocked}><option value="">Pilih cabang</option>{branches.data?.map(row => <option key={row.id} value={row.id}>{row.name}</option>)}</select></Field><Field label="Kas pembuka (IDR)"><input type="number" name="openingCash" min="0" max="1000000000000" step="0.01" defaultValue="0" required readOnly={blocked} /></Field></> : <><dl className="document-facts"><div><dt>Kas diharapkan terakhir</dt><dd>{money(editing.expectedCash)}</dd></div></dl><Field label="Kas fisik dihitung (IDR)"><input name="countedCash" type="number" min="0" max="1000000000000" step="0.01" required readOnly={blocked} autoFocus /></Field></>}</Form></Modal>}
  </>;
}

export function Sales() {
  const user = useUser(); const [branch, setBranch] = useState('');
  const branches = useResource<Branch[]>(['retail-branches', user.id], '/retail/branches');
  const query = useResource<SaleSummary[]>(['retail-sales', user.id, branch], `/retail/sales${params({ branchId: branch })}`);
  return <><PageHeading title="Struk penjualan" description="Hanya penjualan yang sudah diterima server. Kasir melihat struk miliknya pada cabang yang diizinkan." /><ErrorNotice error={query.error || branches.error} retry={() => void query.refetch()} /><section className="panel"><div className="filter-bar"><Field label="Cabang struk"><select value={branch} onChange={event => setBranch(event.target.value)}><option value="">Semua cabang yang diizinkan</option>{branches.data?.map(row => <option value={row.id} key={row.id}>{row.name}</option>)}</select></Field><button className="button secondary" onClick={() => void query.refetch()}>Perbarui struk</button></div>{query.isLoading && <Loading />}{query.data?.length ? <Table label="Struk penjualan diterima"><thead><tr><th>Nomor / waktu</th><th>Cabang</th><th>Kasir</th><th>Harga</th><th className="numeric">Total</th></tr></thead><tbody>{query.data.map(row => <tr key={row.id}><td><Link className="document-code" to={`/sales/${row.id}`}>{row.number}</Link><small>{date(row.createdAt, true)}</small></td><td>{row.branchName}</td><td>{row.actorName}</td><td>{row.priceTier === 'Retail' ? 'Eceran' : 'Grosir'}</td><td className="numeric">{money(row.total)}</td></tr>)}</tbody></Table> : query.data && <Empty title="Belum ada struk server">Perintah yang menunggu kirim hanya tampil pada antrean sinkronisasi.</Empty>}</section></>;
}

export function SaleDetail({ id }: { id: string }) {
  const user = useUser(); const client = useQueryClient();
  const query = useResource<Sale>(['retail-sale', user.id, id], `/retail/sales/${id}`);
  const shifts = useResource<Shift[]>(['retail-return-shifts', user.id], '/retail/shifts');
  const [returning, setReturning] = useState(false); const [returnKey, setReturnKey] = useState('');
  const [result, setResult] = useState<SaleReturn | null>(null); const [uncertain, setUncertain] = useState(false);
  const returnIntent = useRef<Record<string, unknown> | null>(null);
  const returnOperation = useRef<RetailOperation | null>(null);
  useEffect(() => { returnIntent.current = null; returnOperation.current = null; }, [returning]);
  const sale = query.data;
  return <><PageHeading title={sale ? `Struk ${sale.number}` : 'Struk penjualan'} description="Nilai berasal dari transaksi yang diterima server; retur menggunakan harga dan biaya transaksi aslinya." action={<Link className="button secondary" to="/sales">Kembali ke daftar</Link>} /><ErrorNotice error={query.error} retry={() => void query.refetch()} />{query.isLoading && <Loading />}{sale && <section className="panel sale-document"><header className="document-header"><div><span className="document-label">STRUK SERVER</span><h2>{sale.number}</h2><p>{sale.branchName} · {date(sale.createdAt, true)}</p></div><div><strong>{money(sale.total)}</strong><span>Tunai diterima server</span></div></header><dl className="document-facts"><div><dt>Kasir</dt><dd>{sale.actorName}</dd></div><div><dt>Harga</dt><dd>{sale.priceTier === 'Retail' ? 'Eceran' : 'Grosir'}</dd></div><div><dt>Uang diterima / kembali</dt><dd>{money(sale.cashTendered)} / {money(sale.change)}</dd></div><div><dt>Shift</dt><dd className="document-code">{sale.shiftId}</dd></div></dl><Table label="Baris struk"><thead><tr><th>Produk</th><th className="numeric">Jumlah</th><th className="numeric">Harga satuan</th><th className="numeric">Total baris</th><th className="numeric">Sudah diretur</th></tr></thead><tbody>{sale.lines.map(line => <tr key={line.id}><td><strong>{line.name}</strong><small>{line.sku}</small></td><td className="numeric">{line.quantity}</td><td className="numeric">{money(line.unitPrice)}</td><td className="numeric">{money(line.lineTotal)}</td><td className="numeric">{line.returnedQuantity}</td></tr>)}</tbody></Table><div className="receipt-actions"><button className="button secondary" type="button" onClick={() => window.print()}>Cetak struk</button>{['Admin', 'Manager'].includes(user.role) && sale.lines.some(line => line.returnedQuantity < line.quantity) && <button className="button primary" onClick={() => { setReturning(true); setReturnKey(crypto.randomUUID()); setUncertain(false); }}>Catat retur sebagian</button>}</div><p className="field-note">Retur mencatat pengembalian tunai oleh operator. Aplikasi tidak mengirim uang melalui bank atau penyedia pembayaran.</p>{result && <ResultNotice>Retur {result.number} diterima: {money(result.total)}.</ResultNotice>}</section>}
    {returning && sale && <Modal title={`Retur ${sale.number}`} wide onClose={() => setReturning(false)}><Form submitLabel={uncertain ? 'Periksa ulang retur yang sama' : 'Catat retur dan kas keluar'} onCancel={() => setReturning(false)} submit={async data => {
      const lines = sale.lines.map(line => ({ saleLineId: line.id, quantity: numeric(data, `return-${line.id}`) })).filter(line => line.quantity > 0);
      if (!lines.length && !returnIntent.current) throw new Error('Pilih setidaknya satu barang yang dikembalikan.');
      const payload = returnIntent.current || { shiftId: text(data, 'shiftId'), reason: text(data, 'reason'), lines };
      returnIntent.current = payload;
      try {
        if (!returnOperation.current) {
          if ((await listOperations(user)).some(row => !row.discarded && row.state !== 'confirmed')) throw new Error('Periksa operasi yang belum selesai pada antrean sinkronisasi sebelum membuat retur baru.');
          returnOperation.current = await enqueueOperation(user, `/retail/sales/${sale.id}/returns`, payload, `Retur ${sale.number}`, sale.branchId);
        }
        const completed = await synchronizeOperation(user, returnOperation.current.id);
        if (!completed?.result || completed.state !== 'confirmed') { setUncertain(true); throw new Error(completed?.message || 'Hasil retur belum diketahui. Key asli tetap tersimpan di antrean.'); }
        setResult(completed.result as unknown as SaleReturn); setReturning(false); setUncertain(false); await client.invalidateQueries();
      }
      catch (failure) { if (!(failure instanceof ApiError) || failure.status === 0 || failure.status >= 500) setUncertain(true); else returnIntent.current = null; throw failure; }
    }}>
      {uncertain && <p className="notice">Hasil belum diketahui. Isi tetap dikunci untuk memeriksa key yang sama.</p>}<Field label="Shift saya pada cabang struk"><select name="shiftId" required disabled={uncertain}><option value="">Pilih shift aktif</option>{shifts.data?.filter(row => row.branchId === sale.branchId && row.openedById === user.id && openShift(row)).map(row => <option key={row.id} value={row.id}>{row.branchName} · {date(row.openedAt, true)}</option>)}</select></Field><p className="field-note">Retur memerlukan shift milik sendiri yang aktif pada cabang penjualan.</p>{sale.lines.map(line => <Field key={line.id} label={`${line.name}: jumlah dikembalikan`} hint={`Maksimal tersisa ${line.quantity - line.returnedQuantity}. Harga asal ${money(line.unitPrice)}.`}><input name={`return-${line.id}`} type="number" min="0" max={line.quantity - line.returnedQuantity} step="1" defaultValue="0" required readOnly={uncertain} /></Field>)}<Field label="Alasan retur"><textarea name="reason" required minLength={10} maxLength={1000} rows={3} readOnly={uncertain} /></Field>
    </Form></Modal>}
  </>;
}

export function RetailAdministration() {
  const user = useUser(); const client = useQueryClient();
  const branches = useResource<Branch[]>(['retail-branches', user.id], '/retail/branches');
  const assignments = useResource<Assignment[]>(['retail-assignments'], '/retail/assignments');
  const warehouses = useResource<Warehouse[]>(['retail-warehouses'], '/warehouses');
  const members = useQuery({ queryKey: ['retail-users'], queryFn: () => all<TeamUser>('/users') });
  const [editing, setEditing] = useState(false); const [notice, setNotice] = useState(''); const [error, setError] = useState<unknown>(null);
  return <><PageHeading title="Cabang & akses kasir" description="Setiap cabang terhubung permanen ke satu gudang. Pemetaan awal hanya untuk gudang kosong." action={<button className="button primary" onClick={() => setEditing(true)}>Tambah cabang</button>} /><ErrorNotice error={error || branches.error || assignments.error || warehouses.error || members.error} />{notice && <ResultNotice>{notice}</ResultNotice>}<section className="panel"><div className="panel-title"><h2>Cabang terdaftar</h2></div>{branches.isLoading && <Loading />}{branches.data?.length ? <Table label="Pemetaan cabang dan gudang"><thead><tr><th>Kode</th><th>Cabang</th><th>Gudang tetap</th></tr></thead><tbody>{branches.data.map(row => <tr key={row.id}><td className="document-code">{row.code}</td><td>{row.name}</td><td>{row.warehouseName}</td></tr>)}</tbody></Table> : branches.data && <Empty title="Belum ada cabang">Buat gudang kosong, lalu petakan satu cabang ke gudang tersebut sebelum mengisi stok.</Empty>}</section><section className="panel assignment-panel"><div className="panel-title"><h2>Berikan akses cabang</h2></div><Form submitLabel="Berikan akses kasir" submit={async data => { await write('/retail/assignments', { userId: text(data, 'userId'), branchId: text(data, 'branchId') }); await client.invalidateQueries(); setNotice('Akses cabang tersimpan.'); }}><div className="form-grid"><Field label="Akun kasir"><select name="userId" required><option value="">Pilih kasir aktif</option>{members.data?.filter(row => row.isActive && row.role === 'Cashier').map(row => <option key={row.id} value={row.id}>{row.fullName}</option>)}</select></Field><Field label="Cabang akses"><select name="branchId" required><option value="">Pilih cabang</option>{branches.data?.map(row => <option key={row.id} value={row.id}>{row.name}</option>)}</select></Field></div></Form><Table label="Akses kasir saat ini"><thead><tr><th>Akun</th><th>Cabang</th><th>Tindakan</th></tr></thead><tbody>{assignments.data?.map(row => <tr key={`${row.userId}:${row.branchId}`}><td>{members.data?.find(member => member.id === row.userId)?.fullName || row.userId}</td><td>{branches.data?.find(branch => branch.id === row.branchId)?.name || row.branchId}</td><td><button className="button text" onClick={async () => { try { await write(`/retail/assignments/${row.userId}/${row.branchId}`, undefined, 'DELETE'); await client.invalidateQueries(); setNotice('Akses dicabut. Replay berikutnya memerlukan izin yang masih berlaku.'); } catch (failure) { setError(failure); } }}>Cabut akses</button></td></tr>)}</tbody></Table></section>{editing && <Modal title="Tambah cabang" onClose={() => setEditing(false)}><Form submitLabel="Simpan pemetaan cabang" onCancel={() => setEditing(false)} submit={async data => { await write('/retail/branches', { code: text(data, 'code'), name: text(data, 'name'), warehouseId: text(data, 'warehouseId') }); await client.invalidateQueries(); setEditing(false); }}><Field label="Kode cabang"><input name="code" required maxLength={24} autoFocus /></Field><Field label="Nama cabang"><input name="name" required maxLength={100} /></Field><Field label="Gudang kosong"><select name="warehouseId" required><option value="">Pilih gudang</option>{warehouses.data?.filter(row => !branches.data?.some(branch => branch.warehouseId === row.id)).map(row => <option value={row.id} key={row.id}>{row.code} · {row.name}</option>)}</select></Field><p className="field-note">Server menolak gudang yang sudah berisi stok atau sudah dipetakan. Cabang tidak dapat berpindah gudang setelah dibuat.</p></Form></Modal>}</>;
}

export function RetailPrices() {
  const user = useUser(); const client = useQueryClient();
  const prices = useResource<Price[]>(['retail-prices'], '/retail/prices');
  const products = useQuery({ queryKey: ['retail-price-products'], queryFn: () => all<Product>('/products') });
  const [editing, setEditing] = useState<Price | null>(null); const [selectedProduct, setSelectedProduct] = useState('');
  const productItems = products.data || [];
  return <><PageHeading title="Harga eceran & grosir" description="Harga berversi diperiksa server saat penjualan diterima. Antrean lama tidak berubah mengikuti harga baru." /><ErrorNotice error={prices.error || products.error} /><section className="panel">{user.role === 'Admin' && <div className="filter-bar"><Field label="Produk untuk ditetapkan harga"><select value={selectedProduct} onChange={event => setSelectedProduct(event.target.value)}><option value="">Pilih produk</option>{productItems.filter(row => row.isActive).map(row => <option key={row.id} value={row.id}>{row.sku} · {row.name}</option>)}</select></Field><button className="button primary" disabled={!selectedProduct} onClick={() => { const product = productItems.find(row => row.id === selectedProduct)!; setEditing(prices.data?.find(row => row.productId === selectedProduct) || { productId: product.id, sku: product.sku, name: product.name, retailPrice: 0, wholesalePrice: 0, wholesaleMinimum: 2, version: 0 }); }}>Atur harga produk</button></div>}{prices.isLoading && <Loading />}{prices.data?.length ? <Table label="Harga jual berversi"><thead><tr><th>Produk</th><th className="numeric">Eceran</th><th className="numeric">Grosir / minimum</th><th>Versi</th>{user.role === 'Admin' && <th>Tindakan</th>}</tr></thead><tbody>{prices.data.map(row => <tr key={row.productId}><td><strong>{row.name}</strong><small>{row.sku}</small></td><td className="numeric">{money(row.retailPrice)}</td><td className="numeric">{money(row.wholesalePrice)} / {row.wholesaleMinimum}</td><td>{row.version}</td>{user.role === 'Admin' && <td><button className="button text" onClick={() => setEditing(row)}>Ubah harga {row.sku}</button></td>}</tr>)}</tbody></Table> : prices.data && <Empty title="Belum ada harga jual">Produk baru muncul di katalog kasir setelah harga aktif ditetapkan.</Empty>}</section>{editing && <Modal title={`Harga · ${editing.sku}`} onClose={() => setEditing(null)}><Form submitLabel="Simpan versi harga" onCancel={() => setEditing(null)} submit={async data => { await write(`/retail/prices/${editing.productId}`, { retailPrice: numeric(data, 'retailPrice'), wholesalePrice: numeric(data, 'wholesalePrice'), wholesaleMinimum: numeric(data, 'wholesaleMinimum'), expectedVersion: editing.version }, 'PUT'); await client.invalidateQueries(); setEditing(null); }}><p>{editing.name} · versi yang dibaca {editing.version}</p><Field label="Harga eceran (IDR)"><input name="retailPrice" type="number" required min="0.01" step="0.01" defaultValue={editing.retailPrice || ''} autoFocus /></Field><Field label="Harga grosir (IDR)"><input name="wholesalePrice" type="number" required min="0.01" step="0.01" defaultValue={editing.wholesalePrice || ''} /></Field><Field label="Jumlah minimum grosir"><input name="wholesaleMinimum" type="number" required min="2" step="1" defaultValue={editing.wholesaleMinimum} /></Field><p className="field-note">Harga grosir tidak boleh lebih tinggi dari eceran. Jika versi berubah, muat ulang lalu tinjau kembali sebelum menyimpan.</p></Form></Modal>}</>;
}

export function Accounting() {
  const user = useUser(); const [from, setFrom] = useState(today()); const [to, setTo] = useState(today()); const [branch, setBranch] = useState(''); const [filter, setFilter] = useState({ from: today(), to: today(), branchId: '' });
  const branches = useResource<Branch[]>(['retail-branches', user.id], '/retail/branches');
  const query = useResource<RetailReport>(['retail-reports', filter], `/retail/reports${params(filter)}`);
  const journals = useResource<Journal[]>(['retail-journals'], '/retail/journals');
  const report = query.data;
  return <><PageHeading title="Laporan & jurnal" description="Penjualan dan persediaan menghasilkan jurnal berpasangan dalam transaksi database yang sama." /><div className="notice"><strong>Cakupan laporan berbeda</strong><p>Laba rugi mengikuti tanggal WIB dan cabang yang dipilih. Neraca saldo dan neraca selalu seluruh organisasi sejak awal posting sampai tanggal akhir. Laporan ini tidak menghitung pajak atau penyelesaian pembayaran pemasok.</p></div><section className="panel report-filters"><form className="filter-bar" onSubmit={event => { event.preventDefault(); setFilter({ from, to, branchId: branch }); }}><Field label="Dari tanggal WIB"><input type="date" value={from} onChange={event => setFrom(event.target.value)} required /></Field><Field label="Sampai tanggal WIB"><input type="date" value={to} onChange={event => setTo(event.target.value)} required /></Field><Field label="Cabang laba rugi"><select value={branch} onChange={event => setBranch(event.target.value)}><option value="">Seluruh organisasi</option>{branches.data?.map(row => <option key={row.id} value={row.id}>{row.name}</option>)}</select></Field><button className="button primary" type="submit">Tampilkan laporan</button></form></section><ErrorNotice error={query.error || journals.error || branches.error} retry={() => { void query.refetch(); void journals.refetch(); }} />{query.isLoading && <Loading text="Menghitung laporan dari jurnal committed…" />}{report && <><div className="financial-layout"><section className="panel financial-statement"><header className="panel-title"><div><h2>Laba rugi</h2><p>{report.from} sampai {report.to} · {branches.data?.find(row => row.id === report.branchId)?.name || 'Seluruh organisasi'}</p></div></header><dl>{[['Penjualan kotor', report.grossSales], ['Retur penjualan', -report.refunds], ['Penjualan bersih', report.netSales], ['Harga pokok penjualan', -report.costOfGoodsSold], ['Varians persediaan & pembelian', -report.varianceExpense], ['Laba bersih tercatat', report.netProfit]].map(([name, value], index) => <div key={String(name)} className={index === 2 || index === 5 ? 'statement-total' : ''}><dt>{name}</dt><dd>{money(Number(value))}</dd></div>)}</dl><p className="field-note">{report.salesCount} penjualan diterima pada cakupan ini. Valuasi memakai standard cost produk.</p></section><section className="panel financial-statement"><header className="panel-title"><div><h2>Neraca organisasi</h2><p>Sejak awal posting sampai {report.balanceSheet.asOf}</p></div></header><dl>{[['Aset', report.balanceSheet.assets], ['Liabilitas', report.balanceSheet.liabilities], ['Ekuitas tercatat', report.balanceSheet.equity], ['Laba ditahan dari seluruh posting', report.balanceSheet.retainedEarnings]].map(([name, value]) => <div key={String(name)}><dt>{name}</dt><dd>{money(Number(value))}</dd></div>)}</dl><div className={`notice ${report.balanceSheet.balanced && report.journalBalanced ? 'success' : 'error'}`}><strong>{report.balanceSheet.balanced ? 'Persamaan neraca seimbang' : 'Neraca perlu diperiksa'}</strong><p>{report.journalBalanced ? 'Debit dan kredit jurnal seimbang.' : 'Debit dan kredit belum cocok.'}</p></div></section></div><section className="panel"><div className="panel-title"><div><h2>Neraca saldo seluruh organisasi</h2><p>Sejak awal posting sampai {report.to}; tidak mengikuti filter cabang.</p></div></div><Table label="Neraca saldo organisasi"><thead><tr><th>Akun</th><th>Tipe</th><th className="numeric">Debit</th><th className="numeric">Kredit</th><th className="numeric">Saldo</th></tr></thead><tbody>{report.trialBalance.map(row => <tr key={row.code}><td><span className="document-code">{row.code}</span><strong>{row.name}</strong></td><td>{row.type}</td><td className="numeric">{money(row.debit)}</td><td className="numeric">{money(row.credit)}</td><td className="numeric">{money(row.balance)}</td></tr>)}</tbody></Table></section></>}
    <section className="panel journal-panel"><div className="panel-title"><div><h2>Jurnal transaksi</h2><p>Posting append-only dari sumber transaksi. Buka untuk melihat pasangan akun.</p></div></div>{journals.isLoading && <Loading />}{journals.data?.length ? journals.data.map(journal => <details className="journal-entry" key={journal.id}><summary><span className="document-code">{journal.number}</span><span>{journal.description}</span><strong>{money(journal.totalDebit)}</strong></summary><p>{date(journal.createdAt, true)} · {journal.sourceType} · <span className="document-code">{journal.sourceId}</span></p><Table label={`Baris jurnal ${journal.number}`}><thead><tr><th>Akun</th><th className="numeric">Debit</th><th className="numeric">Kredit</th></tr></thead><tbody>{journal.lines.map((line, index) => <tr key={`${line.accountCode}:${index}`}><td>{line.accountCode} · {line.accountName}</td><td className="numeric">{money(line.debit)}</td><td className="numeric">{money(line.credit)}</td></tr>)}</tbody></Table><p>Total debit {money(journal.totalDebit)} · kredit {money(journal.totalCredit)}</p></details>) : journals.data && <Empty title="Belum ada jurnal">Transaksi persediaan dan penjualan akan membuat posting terkait saat tersimpan.</Empty>}</section>
  </>;
}
