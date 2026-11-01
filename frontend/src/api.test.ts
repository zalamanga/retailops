import { afterEach, describe, expect, it, vi } from 'vitest';

const response = (value: unknown, status = 200) => ({
  ok: status >= 200 && status < 300,
  status,
  json: async () => value,
});
afterEach(() => {
  vi.unstubAllGlobals();
  vi.resetModules();
});
describe('session API client', () => {
  it('explains a disconnected antiforgery request and never attempts the mutation', async () => {
    const fetchMock = vi.fn().mockRejectedValue(new TypeError('Failed to fetch'));
    vi.stubGlobal('fetch', fetchMock);
    const { write } = await import('./api');
    await expect(write('/products', { sku: 'SKU-1' })).rejects.toMatchObject({
      name: 'ApiError',
      status: 0,
      message: 'Could not prepare a protected request. Check your connection and retry.',
    });
    expect(fetchMock).toHaveBeenCalledTimes(1);
    expect(fetchMock.mock.calls[0][0]).toBe('/api/auth/csrf');
  });

  it('notifies the workspace shell when a CSV export finds an expired session', async () => {
    vi.stubGlobal('fetch', vi.fn().mockResolvedValue(response({}, 401)));
    const listener = vi.fn();
    window.addEventListener('session-expired', listener);
    const { exportInventory } = await import('./api');
    await expect(exportInventory('?warehouseId=one')).rejects.toMatchObject({ status: 401 });
    expect(listener).toHaveBeenCalledTimes(1);
    window.removeEventListener('session-expired', listener);
  });
  it('obtains an antiforgery token before a write and sends it with same-origin credentials', async () => {
    const fetchMock = vi
      .fn()
      .mockResolvedValueOnce(response({ token: 'request-token' }))
      .mockResolvedValueOnce(response({ id: 'new-product' }));
    vi.stubGlobal('fetch', fetchMock);
    const { write } = await import('./api');
    await write('/products', { sku: 'SKU-1' });
    expect(fetchMock.mock.calls[0][0]).toBe('/api/auth/csrf');
    expect(fetchMock.mock.calls[1][0]).toBe('/api/products');
    const request = fetchMock.mock.calls[1][1] as RequestInit;
    expect(request.credentials).toBe('same-origin');
    expect(new Headers(request.headers).get('X-CSRF-TOKEN')).toBe('request-token');
    expect(JSON.parse(String(request.body))).toEqual({ sku: 'SKU-1' });
  });
  it('keeps server field errors and request references available to the form', async () => {
    vi.stubGlobal(
      'fetch',
      vi.fn().mockResolvedValue(
        response(
          {
            detail: 'SKU already exists.',
            errors: { sku: ['Choose another SKU.'] },
            traceId: 'request-7',
          },
          409,
        ),
      ),
    );
    const { api } = await import('./api');
    await expect(api('/products')).rejects.toMatchObject({
      message: 'SKU already exists.',
      status: 409,
      fields: { sku: ['Choose another SKU.'] },
      traceId: 'request-7',
    });
  });
  it('clears a previous identity token before the next protected request', async () => {
    const fetchMock = vi
      .fn()
      .mockResolvedValueOnce(response({ token: 'identity-one' }))
      .mockResolvedValueOnce(response({}))
      .mockResolvedValueOnce(response({ token: 'anonymous' }))
      .mockResolvedValueOnce(response({}));
    vi.stubGlobal('fetch', fetchMock);
    const { write, clearCsrf } = await import('./api');
    await write('/inventory/adjustments', {});
    clearCsrf();
    await write('/auth/login', {});
    expect(fetchMock.mock.calls[2][0]).toBe('/api/auth/csrf');
    expect(
      new Headers((fetchMock.mock.calls[3][1] as RequestInit).headers).get('X-CSRF-TOKEN'),
    ).toBe('anonymous');
  });
  it('notifies the shell when an operational session expires', async () => {
    vi.stubGlobal('fetch', vi.fn().mockResolvedValue(response({ title: 'Unauthorized' }, 401)));
    const listener = vi.fn();
    window.addEventListener('session-expired', listener);
    const { api } = await import('./api');
    await expect(api('/orders')).rejects.toMatchObject({ status: 401 });
    expect(listener).toHaveBeenCalledTimes(1);
    window.removeEventListener('session-expired', listener);
  });
  it('loads all reference pages rather than silently truncating product choices', async () => {
    const firstPage = Array.from({ length: 100 }, (_, index) => `product-${index + 1}`);
    const fetchMock = vi
      .fn()
      .mockResolvedValueOnce(response({ items: firstPage, total: 101 }))
      .mockResolvedValueOnce(response({ items: ['product-101'], total: 101 }));
    vi.stubGlobal('fetch', fetchMock);
    const { all } = await import('./api');
    expect(await all('/products?includeArchived=false')).toEqual([...firstPage, 'product-101']);
    expect(fetchMock.mock.calls[1][0]).toContain('page=2&pageSize=100');
  });
  it('does not expire a new identity when an old request returns unauthorized late', async () => {
    let release: ((value: ReturnType<typeof response>) => void) | undefined;
    vi.stubGlobal('fetch', vi.fn(() => new Promise(resolve => { release = resolve; })));
    const listener = vi.fn();
    window.addEventListener('session-expired', listener);
    const { api, clearCsrf } = await import('./api');
    const pending = api('/orders');
    clearCsrf();
    release!(response({ title: 'Unauthorized' }, 401));
    await expect(pending).rejects.toMatchObject({ status: 401 });
    expect(listener).not.toHaveBeenCalled();
    window.removeEventListener('session-expired', listener);
  });
});
