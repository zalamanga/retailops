import { fireEvent, render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { MemoryRouter } from 'react-router-dom';
import { afterEach, describe, expect, it, vi } from 'vitest';
import App from './App';
import { clearCsrf } from './api';
import * as offline from './offline';

const response = (body: unknown, status = 200) => ({
  ok: status >= 200 && status < 300,
  status,
  json: async () => body,
});
const user = {
  id: 'b3019ba0-7c0c-493f-ad5b-72dc8db38f83',
  fullName: 'Operations user',
  email: 'operations@example.invalid',
  role: 'Operator',
  organizationName: 'Inventory test workspace',
};

function openApp() {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false, gcTime: 0 } } });
  render(
    <QueryClientProvider client={client}>
      <MemoryRouter>
        <App />
      </MemoryRouter>
    </QueryClientProvider>,
  );
  return client;
}
afterEach(() => {
  clearCsrf();
  vi.unstubAllGlobals();
});

describe('successful authentication transitions', () => {
  for (const mode of ['setup', 'login'] as const) {
    it(`opens Overview after successful ${mode} without reloading the page`, async () => {
      let setupRequired = mode === 'setup';
      let signedIn = false;
      const identity = { ...user, ...(mode === 'setup' ? { role: 'Admin' } : {}) };
      const fetchMock = vi.fn(async (path: string, options?: RequestInit) => {
        if (path.endsWith('/auth/status')) return response({ setupRequired });
        if (path.endsWith('/auth/me'))
          return signedIn ? response(identity) : response({ title: 'Unauthorized' }, 401);
        if (path.endsWith('/auth/csrf'))
          return response({ token: signedIn ? 'signed-in-token' : 'anonymous-token' });
        if (path.endsWith(`/auth/${mode}`) && options?.method === 'POST') {
          signedIn = true;
          setupRequired = false;
          return response(identity);
        }
        if (path.endsWith('/dashboard'))
          return response({
            activeProducts: 0,
            stockValue: 0,
            lowStockCount: 0,
            openOrders: 0,
            movementSeries: [],
            lowStock: [],
            recentOrders: [],
          });
        throw new Error(`Unexpected request: ${path}`);
      });
      vi.stubGlobal('fetch', fetchMock);
      const client = openApp();
      const person = userEvent.setup();
      expect(
        await screen.findByRole('heading', {
          name: mode === 'setup' ? 'Set up your workspace' : 'Welcome back',
          level: 1,
        }),
      ).toBeVisible();
      if (mode === 'setup') {
        fireEvent.change(screen.getByLabelText('Organization name', { exact: true }), {
          target: { value: identity.organizationName },
        });
        fireEvent.change(screen.getByLabelText('Your full name', { exact: true }), {
          target: { value: identity.fullName },
        });
      }
      fireEvent.change(screen.getByLabelText('Email address', { exact: true }), {
        target: { value: identity.email },
      });
      fireEvent.change(screen.getByLabelText('Password', { exact: true }), {
        target: { value: 'WorkspaceAccess932!' },
      });
      await person.click(
        screen.getByRole('button', {
          name: mode === 'setup' ? 'Create workspace' : 'Sign in',
        }),
      );
      expect(await screen.findByRole('heading', { name: 'Operations overview' })).toBeVisible();
      expect(
        screen.queryByRole('heading', { name: /Set up your workspace|Welcome back/ }),
      ).not.toBeInTheDocument();
      expect(screen.getByRole('button', { name: 'Sign out' })).toBeVisible();
      expect(fetchMock.mock.calls.filter(([path]) => path.endsWith('/auth/csrf'))).toHaveLength(2);
      const authentication = fetchMock.mock.calls.find(
        ([path, options]) => path.endsWith(`/auth/${mode}`) && options?.method === 'POST',
      );
      expect(JSON.parse(String(authentication?.[1]?.body))).toMatchObject({
        email: identity.email,
        password: 'WorkspaceAccess932!',
      });
      expect(new Headers(authentication?.[1]?.headers).get('X-CSRF-TOKEN')).toBe('anonymous-token');
      client.clear();
    });
  }
});

describe('expired sessions', () => {
  it('revalidates both auth queries on native reconnect and replaces the cached owner with the server identity', async () => {
    const cached = { ...user, role: 'Operator' as const, organizationId: '11111111-1111-4111-8111-111111111111', fullName: 'Cached offline operator' };
    const server = { ...cached, id: '22222222-2222-4222-8222-222222222222', fullName: 'Current server operator' };
    const lastIdentity = vi.spyOn(offline, 'lastIdentity').mockResolvedValue(cached);
    let connected = false;
    let statusReads = 0;
    let meReads = 0;
    vi.stubGlobal('fetch', vi.fn(async (path: string) => {
      if (path.endsWith('/auth/status')) { statusReads += 1; if (!connected) throw new TypeError('Failed to fetch'); return response({ setupRequired: false }); }
      if (path.endsWith('/auth/me')) { meReads += 1; return response(server); }
      if (path.endsWith('/dashboard')) return response({ activeProducts: 0, stockValue: 0, lowStockCount: 0, openOrders: 0, movementSeries: [], lowStock: [], recentOrders: [] });
      throw new Error(`Unexpected request: ${path}`);
    }));
    const client = openApp();
    try {
      expect(await screen.findByText('Mode offline · Cached offline operator')).toBeVisible();
      connected = true;
      fireEvent(window, new Event('online'));
      expect(await screen.findByText('Current server operator')).toBeVisible();
      await waitFor(() => expect(screen.queryByText('Mode offline · Cached offline operator')).not.toBeInTheDocument());
      expect(screen.queryByText('Cached offline operator')).not.toBeInTheDocument();
      expect(statusReads).toBeGreaterThanOrEqual(2);
      expect(meReads).toBeGreaterThanOrEqual(1);
    } finally { client.clear(); lastIdentity.mockRestore(); }
  });
  it('revalidates a logged-out tab when another tab signs in without trusting the pointer as authentication', async () => {
    const identity = { ...user, organizationId: '11111111-1111-4111-8111-111111111111', fullName: 'Reauthenticated cookie owner' };
    let signedIn = false;
    let meRequests = 0;
    vi.stubGlobal('fetch', vi.fn(async (path: string) => {
      if (path.endsWith('/auth/status')) return response({ setupRequired: false });
      if (path.endsWith('/auth/me')) { meRequests += 1; return signedIn ? response(identity) : response({ title: 'Unauthorized' }, 401); }
      if (path.endsWith('/dashboard')) return response({ activeProducts: 0, stockValue: 0, lowStockCount: 0, openOrders: 0, movementSeries: [], lowStock: [], recentOrders: [] });
      throw new Error(`Unexpected request: ${path}`);
    }));
    const client = openApp();
    expect(await screen.findByRole('heading', { name: 'Welcome back' })).toBeVisible();
    expect(meRequests).toBe(1);
    fireEvent(window, new StorageEvent('storage', { key: 'retailops.offline-owner', newValue: null }));
    expect(meRequests).toBe(1);
    signedIn = true;
    fireEvent(window, new StorageEvent('storage', { key: 'retailops.offline-owner', newValue: `${identity.organizationId}:${identity.id}` }));
    expect(await screen.findByText('Reauthenticated cookie owner')).toBeVisible();
    expect(meRequests).toBe(2);
    expect(screen.queryByRole('heading', { name: 'Welcome back' })).not.toBeInTheDocument();
    client.clear();
  });
  it('replaces the old workspace identity after another tab switches owners', async () => {
    const first = { ...user, organizationId: '11111111-1111-4111-8111-111111111111', fullName: 'Current operator A' };
    const second = { ...first, id: '22222222-2222-4222-8222-222222222222', fullName: 'Current operator B' };
    let current = first;
    vi.stubGlobal('fetch', vi.fn(async (path: string) => {
      if (path.endsWith('/auth/status')) return response({ setupRequired: false });
      if (path.endsWith('/auth/me')) return response(current);
      if (path.endsWith('/dashboard')) return response({ activeProducts: 0, stockValue: 0, lowStockCount: 0, openOrders: 0, movementSeries: [], lowStock: [], recentOrders: [] });
      throw new Error(`Unexpected request: ${path}`);
    }));
    const client = openApp();
    expect(await screen.findByText('Current operator A')).toBeVisible();
    current = second;
    window.dispatchEvent(new StorageEvent('storage', { key: 'retailops.offline-owner', newValue: `${second.organizationId}:${second.id}` }));
    expect(await screen.findByText('Current operator B')).toBeVisible();
    expect(screen.queryByText('Current operator A')).not.toBeInTheDocument();
    client.clear();
  });
  it('opens sign-in directly when the current-user request is unauthorized', async () => {
    const fetchMock = vi.fn(async (path: string) => {
      if (path.endsWith('/auth/status')) return response({ setupRequired: false });
      if (path.endsWith('/auth/me')) return response({ title: 'Unauthorized' }, 401);
      throw new Error(`Unexpected request: ${path}`);
    });
    vi.stubGlobal('fetch', fetchMock);
    const client = openApp();
    expect(await screen.findByRole('heading', { name: 'Welcome back' })).toBeVisible();
    expect(fetchMock.mock.calls.filter(([path]) => path.endsWith('/auth/me'))).toHaveLength(1);
    expect(screen.queryByRole('alert')).not.toBeInTheDocument();
    client.clear();
  });

  it('clears an operational session once and returns to sign-in without an authentication loop', async () => {
    let meRequests = 0;
    const fetchMock = vi.fn(async (path: string) => {
      if (path.endsWith('/auth/status')) return response({ setupRequired: false });
      if (path.endsWith('/auth/me'))
        return ++meRequests === 1 ? response(user) : response({ title: 'Unauthorized' }, 401);
      if (path.endsWith('/dashboard')) return response({ title: 'Unauthorized' }, 401);
      throw new Error(`Unexpected request: ${path}`);
    });
    vi.stubGlobal('fetch', fetchMock);
    const client = openApp();
    expect(await screen.findByRole('heading', { name: 'Welcome back' })).toBeVisible();
    await waitFor(() => expect(meRequests).toBe(2));
    expect(fetchMock.mock.calls.filter(([path]) => path.endsWith('/dashboard'))).toHaveLength(1);
    expect(fetchMock.mock.calls.filter(([path]) => path.endsWith('/auth/status'))).toHaveLength(2);
    expect(screen.queryByRole('alert')).not.toBeInTheDocument();
    client.clear();
  });
});
