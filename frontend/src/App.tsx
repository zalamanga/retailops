import { useEffect, useRef, useState } from 'react';
import { NavLink, Navigate, Route, Routes, useParams } from 'react-router-dom';
import { useQuery, useQueryClient } from '@tanstack/react-query';
import { api, ApiError, clearCsrf, refreshCsrf, write } from './api';
import { ErrorNotice, Field, Form, Icon, Loading, Modal, SessionContext, text } from './components';
import { passwordMessage } from './format';
import type { User } from './types';
import { Overview } from './pages/Overview';
import { Products, Suppliers, Warehouses } from './pages/Catalog';
import { OrderDetail, Orders } from './pages/Orders';
import { Inventory, Movements } from './pages/Inventory';
import { ActivityPage, Team } from './pages/Administration';
import { Accounting, PointOfSale, RetailAdministration, RetailPrices, SaleDetail, Sales, Shifts, SyncQueue } from './pages/Retail';
import { forgetIdentity, invalidateIdentityWrites, lastIdentity, listQueue, rememberIdentity, scopeFor } from './offline';
import { listOperations } from './retail-operations';

function SaleRoute() { const { id = '' } = useParams(); return <SaleDetail id={id} />; }

function Authentication({
  setup,
  onAuthenticated,
}: {
  setup: boolean;
  onAuthenticated: (user: User) => void;
}) {
  return (
    <div className="auth-layout">
      <aside className="auth-story">
        <div className="wordmark">
          RetailOps<span>Retail & purchasing</span>
        </div>
        <div>
          <p className="auth-statement">
            Keep the branch moving.
            <br />
            Reconcile every receipt.
          </p>
          <p>Branch sales, purchasing, stock movements, and the financial record in one workspace.</p>
          <ol className="auth-process">
            <li>Receive and transfer stock</li>
            <li>Record sales in your own branch shift</li>
            <li>Review synchronization and balanced journals</li>
          </ol>
        </div>
        <p className="auth-footnote">Built for the people behind your daily operations.</p>
      </aside>
      <main className="auth-panel">
        <div className="auth-form">
          <span className="document-label">{setup ? 'WORKSPACE / 01' : 'WORKSPACE ACCESS'}</span>
          <h1>{setup ? 'Set up your workspace' : 'Welcome back'}</h1>
          <p>
            {setup
              ? 'Create your organization and its first administrator. You can invite your team after setup.'
              : 'Sign in to your branch and operations workspace.'}
          </p>
          <Form
            submitLabel={setup ? 'Create workspace' : 'Sign in'}
            submit={async (data) => {
              const password = String(data.get('password'));
              if (setup) {
                const message = passwordMessage(password);
                if (message) throw new Error(message);
              }
              const user = await write<User>(setup ? '/auth/setup' : '/auth/login', {
                email: text(data, 'email'),
                password,
                ...(setup
                  ? {
                      organizationName: text(data, 'organizationName'),
                      fullName: text(data, 'fullName'),
                    }
                  : {}),
              });
              await refreshCsrf();
              onAuthenticated(user);
            }}
          >
            {setup && (
              <>
                <Field label="Organization name">
                  <input
                    name="organizationName"
                    required
                    minLength={2}
                    maxLength={100}
                    autoComplete="organization"
                    autoFocus
                  />
                </Field>
                <Field label="Your full name">
                  <input
                    name="fullName"
                    required
                    minLength={2}
                    maxLength={100}
                    autoComplete="name"
                  />
                </Field>
              </>
            )}
            <Field label="Email address">
              <input
                name="email"
                type="email"
                required
                maxLength={254}
                autoComplete="username"
                autoFocus={!setup}
              />
            </Field>
            <Field
              label="Password"
              hint={
                setup
                  ? '12-128 characters, including uppercase, lowercase, and a number.'
                  : undefined
              }
            >
              <input
                name="password"
                type="password"
                required
                minLength={setup ? 12 : 1}
                maxLength={128}
                autoComplete={setup ? 'new-password' : 'current-password'}
              />
            </Field>
          </Form>
          <p className="auth-help">
            {setup
              ? 'Purchase approvals require a different authorized person. Add a Manager or another Admin from Team after setup.'
              : 'Need access or a password reset? Contact your workspace administrator.'}
          </p>
        </div>
      </main>
    </div>
  );
}
const sections = [
  {
    title: 'Workspace',
    items: [
      ['/', 'overview', 'Overview'],
      ['/orders', 'orders', 'Purchase orders'],
      ['/inventory', 'inventory', 'Inventory'],
    ],
  },
  {
    title: 'Reference data',
    items: [
      ['/products', 'products', 'Products'],
      ['/suppliers', 'suppliers', 'Suppliers'],
      ['/warehouses', 'warehouses', 'Warehouses'],
    ],
  },
  {
    title: 'Records',
    items: [
      ['/movements', 'movements', 'Stock movements'],
      ['/activity', 'activity', 'Activity log'],
    ],
  },
];
export default function App() {
  const client = useQueryClient();
  const [sessionVersion, setSessionVersion] = useState(0);
  const [mobileOpen, setMobileOpen] = useState(false);
  const [logoutError, setLogoutError] = useState<unknown>(null);
  const [signingOut, setSigningOut] = useState(false);
  const [cachedIdentity, setCachedIdentity] = useState<User | null>(null);
  const [offlineReady, setOfflineReady] = useState(false);
  const [logoutPendingCount, setLogoutPendingCount] = useState<number | null>(null);
  const activeIdentity = useRef<User | null>(null);
  const offlineRestoreVersion = useRef(0);
  const menuButton = useRef<HTMLButtonElement>(null);
  const navigation = useRef<HTMLElement>(null);
  const status = useQuery({
    queryKey: ['auth-status', sessionVersion],
    queryFn: () => api<{ setupRequired: boolean }>('/auth/status'),
    retry: false,
    networkMode: 'always',
  });
  const me = useQuery({
    queryKey: ['auth-me', sessionVersion],
    queryFn: () => api<User>('/auth/me'),
    enabled: status.data?.setupRequired === false,
    retry: false,
    staleTime: Infinity,
    networkMode: 'always',
  });
  useEffect(() => {
    let alive = true;
    const version = offlineRestoreVersion.current;
    lastIdentity().then(user => { if (alive && version === offlineRestoreVersion.current) setCachedIdentity(user); }).catch(() => {}).finally(() => { if (alive) setOfflineReady(true); });
    return () => { alive = false; };
  }, []);
  useEffect(() => {
    if (!me.data) return;
    setCachedIdentity(me.data);
    void rememberIdentity(me.data).catch(() => {});
  }, [me.data]);
  useEffect(() => {
    if (me.error instanceof ApiError && me.error.status === 401) {
      forgetIdentity(); setCachedIdentity(null);
    }
  }, [me.error]);
  useEffect(() => {
    const expired = () => {
      setMobileOpen(false);
      offlineRestoreVersion.current += 1;
      setCachedIdentity(null);
      forgetIdentity();
      clearCsrf();
      client.clear();
      setSessionVersion((value) => value + 1);
    };
    window.addEventListener('session-expired', expired);
    return () => window.removeEventListener('session-expired', expired);
  }, [client]);
  useEffect(() => {
    const changed = (event: StorageEvent) => {
      const current = activeIdentity.current;
      if (event.key !== 'retailops.offline-owner') return;
      if (current?.organizationId && event.newValue === scopeFor(current)) return;
      const validOwnerPointer = /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}:[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i.test(event.newValue || '');
      if (!current && !validOwnerPointer) return;
      // Invalidate this tab without deleting the new owner's shared pointer.
      offlineRestoreVersion.current += 1;
      invalidateIdentityWrites(); activeIdentity.current = null;
      setCachedIdentity(null); setMobileOpen(false); setLogoutPendingCount(null); setLogoutError(null); setSigningOut(false); clearCsrf(); client.clear();
      setSessionVersion(value => value + 1);
    };
    window.addEventListener('storage', changed);
    return () => window.removeEventListener('storage', changed);
  }, [client]);
  useEffect(() => {
    const focus = () => { if (!document.hidden && status.data?.setupRequired === false) void me.refetch(); };
    window.addEventListener('focus', focus);
    document.addEventListener('visibilitychange', focus);
    return () => { window.removeEventListener('focus', focus); document.removeEventListener('visibilitychange', focus); };
  }, [me.refetch, status.data?.setupRequired]);
  useEffect(() => {
    const online = () => { void status.refetch(); void me.refetch(); };
    window.addEventListener('online', online);
    return () => window.removeEventListener('online', online);
  }, [status.refetch, me.refetch]);
  useEffect(() => {
    const close = (event: KeyboardEvent) => {
      if (event.key === 'Escape') setMobileOpen(false);
    };
    document.addEventListener('keydown', close);
    return () => document.removeEventListener('keydown', close);
  }, []);
  useEffect(() => {
    if (!mobileOpen) return;
    const panel = navigation.current;
    const trigger = menuButton.current;
    const focusable = () =>
      Array.from(
        panel?.querySelectorAll<HTMLElement>('a[href], button:not(:disabled)') ?? [],
      ).filter((element) => element.getClientRects().length > 0);
    focusable()[0]?.focus();
    function containFocus(event: KeyboardEvent) {
      if (event.key !== 'Tab') return;
      const controls = focusable();
      const first = controls[0];
      const last = controls.at(-1);
      if (event.shiftKey && document.activeElement === first) {
        event.preventDefault();
        last?.focus();
      } else if (!event.shiftKey && document.activeElement === last) {
        event.preventDefault();
        first?.focus();
      }
    }
    const onResize = () => {
      if (window.innerWidth > 800) setMobileOpen(false);
    };
    document.addEventListener('keydown', containFocus);
    window.addEventListener('resize', onResize);
    return () => {
      document.removeEventListener('keydown', containFocus);
      window.removeEventListener('resize', onResize);
      trigger?.focus();
    };
  }, [mobileOpen]);
  function authenticated(user: User) {
    setMobileOpen(false);
    setLogoutPendingCount(null); setLogoutError(null);
    const nextVersion = sessionVersion + 1;
    client.clear();
    client.setQueryData(['auth-status', nextVersion], { setupRequired: false });
    client.setQueryData(['auth-me', nextVersion], user);
    setSessionVersion(nextVersion);
    setCachedIdentity(user);
    void rememberIdentity(user).catch(() => {});
  }
  async function signOut() {
    setSigningOut(true); setLogoutError(null);
    try {
      await write('/auth/logout');
      offlineRestoreVersion.current += 1;
      clearCsrf(); forgetIdentity(); setCachedIdentity(null); setMobileOpen(false);
      client.clear(); setSessionVersion(value => value + 1); setLogoutPendingCount(null);
    } catch (error) { setLogoutError(error); }
    finally { setSigningOut(false); }
  }
  const disconnected = (status.error instanceof ApiError && status.error.status === 0) || (me.error instanceof ApiError && me.error.status === 0);
  const offlineUser = disconnected ? cachedIdentity : null;
  if (status.isLoading || (!status.data?.setupRequired && me.isLoading) || disconnected && !offlineReady)
    return (
      <div className="startup">
        <div className="wordmark">RetailOps</div>
        <Loading text="Opening your workspace…" />
      </div>
    );
  if (
    !offlineUser && (status.error ||
    (me.error && !(me.error instanceof Error && 'status' in me.error && me.error.status === 401))
    )
  )
    return (
      <div className="startup">
        <div className="wordmark">RetailOps</div>
        <ErrorNotice
          error={status.error || me.error}
          retry={() => {
            void status.refetch();
            void me.refetch();
          }}
        />
      </div>
    );
  const user = me.data || offlineUser;
  activeIdentity.current = user || null;
  if (status.data?.setupRequired || !user)
    return <Authentication setup={!!status.data?.setupRequired} onAuthenticated={authenticated} />;
  const cashier = user.role === 'Cashier';
  const retail = ['Cashier', 'Admin', 'Manager'].includes(user.role);
  const finance = ['Admin', 'Manager'].includes(user.role);
  return (
    <SessionContext.Provider value={user}>
      <div className="app-shell" key={`${user.organizationId || ''}:${user.id}:${user.role}`}>
        <a
          href="#main-content"
          className="skip-link"
          onClick={(event) => {
            event.preventDefault();
            document.getElementById('main-content')?.focus();
          }}
        >
          Skip to workspace content
        </a>
        <header className="mobile-header">
          <button
            className="button secondary"
            ref={menuButton}
            aria-expanded={mobileOpen}
            aria-controls="workspace-navigation"
            onClick={() => setMobileOpen(!mobileOpen)}
          >
            <Icon name="menu" />
            Menu
          </button>
          <span>RetailOps</span>
        </header>
        {mobileOpen && (
          <button
            className="nav-scrim"
            tabIndex={-1}
            aria-label="Close navigation"
            onClick={() => setMobileOpen(false)}
          />
        )}
        <aside
          ref={navigation}
          className={`sidebar ${mobileOpen ? 'open' : ''}`}
          id="workspace-navigation"
          role={mobileOpen ? 'dialog' : undefined}
          aria-modal={mobileOpen || undefined}
          aria-label="Workspace navigation"
        >
          <div className="sidebar-brand">
            <div className="wordmark">
              RetailOps<span>Retail & purchasing</span>
            </div>
            <button
              className="icon-button mobile-close"
              aria-label="Close navigation"
              onClick={() => setMobileOpen(false)}
            >
              <Icon name="close" />
            </button>
          </div>
          <div className="organization">
            <span>Workspace</span>
            <strong>{user.organizationName}</strong>
          </div>
          <nav aria-label="Workspace">
            {retail && <div className="nav-group"><h2>Penjualan cabang</h2>{[['/pos', 'products', 'Kasir cabang'], ['/shifts', 'activity', 'Shift saya'], ['/sales', 'orders', 'Struk penjualan'], ['/sync', 'movements', 'Sinkronisasi']].map(([to, icon, title]) => <NavLink key={to} to={to} onClick={() => setMobileOpen(false)}><Icon name={icon} />{title}</NavLink>)}</div>}
            {!cashier && sections.map((section) => (
              <div className="nav-group" key={section.title}>
                <h2>{section.title}</h2>
                {section.items.map(([to, icon, title]) => (
                  <NavLink key={to} to={to} end={to === '/'} onClick={() => setMobileOpen(false)}>
                    <Icon name={icon} />
                    {title}
                  </NavLink>
                ))}
              </div>
            ))}
            {finance && <div className="nav-group"><h2>Kontrol penjualan</h2><NavLink to="/prices" onClick={() => setMobileOpen(false)}><Icon name="products" />Harga jual</NavLink><NavLink to="/accounting" onClick={() => setMobileOpen(false)}><Icon name="activity" />Laporan & jurnal</NavLink></div>}
            {user.role === 'Admin' && (
              <div className="nav-group">
                <h2>Administration</h2>
                <NavLink to="/team" onClick={() => setMobileOpen(false)}>
                  <Icon name="team" />
                  Team & access
                </NavLink>
                <NavLink to="/branches" onClick={() => setMobileOpen(false)}><Icon name="warehouses" />Cabang & akses kasir</NavLink>
              </div>
            )}
          </nav>
          <div className="user-block">
            <div className="initials" aria-hidden="true">
              {user.fullName
                .split(' ')
                .slice(0, 2)
                .map((name) => name[0])
                .join('')}
            </div>
            <div>
              <strong>{user.fullName}</strong>
              <span>{user.role}</span>
            </div>
            <button
              className="icon-button"
              title="Sign out"
              aria-label="Sign out"
              disabled={signingOut}
              onClick={async () => { try {
                const [sales, operations] = user.organizationId ? await Promise.all([listQueue(user), listOperations(user)]) : [[], []];
                const queued = [...sales, ...operations].filter(row => !row.discarded && row.state !== 'confirmed').length;
                if (queued) setLogoutPendingCount(queued); else await signOut();
              } catch (failure) { setLogoutError(failure); } }}
            >
              <Icon name="logout" />
            </button>
          </div>
        </aside>
        <main id="main-content" className="main-content" tabIndex={-1} inert={mobileOpen}>
          <div className="workspace-topline">
            <span>{user.organizationName}</span>
            <span>
              {new Intl.DateTimeFormat(undefined, {
                weekday: 'long',
                day: 'numeric',
                month: 'long',
                year: 'numeric',
              }).format(new Date())}
            </span>
          </div>
          <ErrorNotice error={logoutError} />
          {offlineUser && <div className="notice retail-offline-notice" role="status"><strong>Mode offline · {user.fullName}</strong><p>Identitas dan data tersimpan hanya untuk menyiapkan perintah lokal. Akses, shift, harga dan stok diperiksa kembali sebelum server menerima penjualan.</p><button className="button secondary" onClick={() => { void status.refetch(); void me.refetch(); }}>Periksa koneksi dan sesi</button></div>}
          <Routes>
            <Route path="/" element={cashier ? <Navigate to="/pos" replace /> : <Overview />} />
            <Route path="/products" element={cashier ? <Navigate to="/pos" replace /> : <Products />} />
            <Route path="/suppliers" element={cashier ? <Navigate to="/pos" replace /> : <Suppliers />} />
            <Route path="/warehouses" element={cashier ? <Navigate to="/pos" replace /> : <Warehouses />} />
            <Route path="/orders" element={cashier ? <Navigate to="/pos" replace /> : <Orders />} />
            <Route path="/orders/:id" element={cashier ? <Navigate to="/pos" replace /> : <OrderDetail />} />
            <Route path="/inventory" element={cashier ? <Navigate to="/pos" replace /> : <Inventory />} />
            <Route path="/movements" element={cashier ? <Navigate to="/pos" replace /> : <Movements />} />
            <Route path="/activity" element={cashier ? <Navigate to="/pos" replace /> : <ActivityPage />} />
            <Route path="/pos" element={retail ? <PointOfSale /> : <Navigate to="/" replace />} />
            <Route path="/sync" element={retail ? <SyncQueue /> : <Navigate to="/" replace />} />
            <Route path="/shifts" element={retail ? <Shifts /> : <Navigate to="/" replace />} />
            <Route path="/sales" element={retail ? <Sales /> : <Navigate to="/" replace />} />
            <Route path="/sales/:id" element={retail ? <SaleRoute /> : <Navigate to="/" replace />} />
            <Route path="/branches" element={user.role === 'Admin' ? <RetailAdministration /> : <Navigate to="/" replace />} />
            <Route path="/prices" element={finance ? <RetailPrices /> : <Navigate to="/" replace />} />
            <Route path="/accounting" element={finance ? <Accounting /> : <Navigate to="/" replace />} />
            <Route
              path="/team"
              element={user.role === 'Admin' ? <Team /> : <Navigate to="/" replace />}
            />
            <Route path="*" element={<Navigate to="/" replace />} />
          </Routes>
          <footer className="workspace-footer">
            RetailOps<span>Branch sales. Traceable stock. Posted journals.</span>
          </footer>
        </main>
        {logoutPendingCount !== null && <Modal title="Keluar dengan antrean belum selesai?" onClose={() => setLogoutPendingCount(null)}><div className="form"><p>{logoutPendingCount} perintah lokal milik {user.fullName} belum terkonfirmasi. Keluar tetap menyimpannya pada browser ini. Masuk kembali dengan akun dan organisasi yang sama untuk memeriksa hasil; akun lain tidak menampilkan antrean ini.</p><div className="form-actions"><button className="button secondary" onClick={() => setLogoutPendingCount(null)}>Tetap di workspace</button><button className="button primary" disabled={signingOut} onClick={() => void signOut()}>Keluar dan simpan antrean pemilik</button></div></div></Modal>}
      </div>
    </SessionContext.Provider>
  );
}
