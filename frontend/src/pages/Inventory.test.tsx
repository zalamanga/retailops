import { render, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { MemoryRouter } from 'react-router-dom';
import { afterEach, beforeAll, describe, expect, it, vi } from 'vitest';
import { SessionContext } from '../components';
import { clearCsrf } from '../api';
import { Inventory } from './Inventory';
import { Orders } from './Orders';
import type { Product, User, Warehouse } from '../types';
import type { ReactNode } from 'react';

const archived: Product = {
  id: 'dc240df9-0bc8-40c2-929c-52321fd98cb6',
  sku: 'RETIRED-1',
  name: 'Retired filter',
  unit: 'pcs',
  reorderPoint: 0,
  standardCost: 100,
  isActive: false,
};
const active: Product = {
  ...archived,
  id: '2dbbb6c2-4515-4dda-af1c-51d19458e592',
  sku: 'CURRENT-1',
  name: 'Current filter',
  isActive: true,
};
const warehouse: Warehouse = {
  id: '07458939-3246-4765-8acc-dd829e740bfe',
  code: 'CENTRAL',
  name: 'Central store',
};
const user: User = {
  id: 'a3b35150-6b02-4d3e-a99a-c52d997e8997',
  fullName: 'Operations user',
  email: 'operations@example.invalid',
  role: 'Operator',
  organizationName: 'Inventory test workspace',
};
const paged = (items: unknown[]) => ({ items, total: items.length, page: 1, pageSize: 20 });
const response = (body: unknown) => ({ ok: true, status: 200, json: async () => body });

function fixtureFetch() {
  return vi.fn(async (path: string, options?: RequestInit) => {
    const url = new URL(path, 'http://localhost');
    if (url.pathname === '/api/auth/csrf') return response({ token: 'protected-request' });
    if (url.pathname === '/api/products')
      return response(
        paged(url.searchParams.get('includeArchived') === 'true' ? [active, archived] : [active]),
      );
    if (url.pathname === '/api/suppliers')
      return response(
        paged([
          {
            id: 'cf6a51ac-b26c-4268-9dad-4aba50556130',
            name: 'Supply contact',
            email: '',
            phone: '',
            leadTimeDays: 1,
          },
        ]),
      );
    if (url.pathname === '/api/warehouses')
      return response([
        warehouse,
        {
          ...warehouse,
          id: 'ed2f6e4a-9993-4cc2-81e4-23b886161fe5',
          code: 'EAST',
          name: 'East store',
        },
      ]);
    if (url.pathname === '/api/inventory')
      return response(
        paged([
          {
            productId: archived.id,
            warehouseId: warehouse.id,
            sku: archived.sku,
            productName: archived.name,
            unit: archived.unit,
            warehouseName: warehouse.name,
            onHand: 5,
            reorderPoint: 0,
            standardCost: 100,
            stockValue: 500,
            isLowStock: false,
          },
        ]),
      );
    if (url.pathname === '/api/inventory/adjustments' && options?.method === 'POST')
      return response({ movementId: 'd7c9ad5b-3cc0-497a-80d3-095ba423a4bc' });
    if (url.pathname === '/api/orders') return response(paged([]));
    throw new Error(`Unexpected request: ${path}`);
  });
}
function openPage(page: ReactNode) {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false, gcTime: 0 } } });
  render(
    <QueryClientProvider client={client}>
      <MemoryRouter>
        <SessionContext.Provider value={user}>{page}</SessionContext.Provider>
      </MemoryRouter>
    </QueryClientProvider>,
  );
  return client;
}
beforeAll(() => {
  HTMLDialogElement.prototype.showModal = function () {
    this.setAttribute('open', '');
  };
  HTMLDialogElement.prototype.close = function () {
    this.removeAttribute('open');
  };
});
afterEach(() => {
  clearCsrf();
  vi.unstubAllGlobals();
});

describe('archived product stock operations', () => {
  it('keeps an archived stock row selected and allows its remaining units to be issued', async () => {
    const fetchMock = fixtureFetch();
    vi.stubGlobal('fetch', fetchMock);
    const client = openPage(<Inventory />);
    const person = userEvent.setup();
    await person.click(
      await screen.findByRole('button', { name: 'Adjust Retired filter at Central store' }),
    );
    const dialog = screen.getByRole('dialog');
    const product = await within(dialog).findByRole('combobox', { name: 'Product' });
    expect(product).toHaveValue(archived.id);
    expect(
      within(product).getByRole('option', { name: 'RETIRED-1 · Retired filter (archived)' }),
    ).toBeInTheDocument();
    await person.type(within(dialog).getByRole('spinbutton', { name: 'Quantity change' }), '-2');
    await person.type(
      within(dialog).getByRole('textbox', { name: 'Reason' }),
      'Issued remaining retired units',
    );
    await person.click(within(dialog).getByRole('button', { name: 'Record adjustment' }));
    await waitFor(() => expect(screen.queryByRole('dialog')).not.toBeInTheDocument());
    const request = fetchMock.mock.calls.find(
      ([path, options]) => path === '/api/inventory/adjustments' && options?.method === 'POST',
    );
    expect(JSON.parse(String(request?.[1]?.body))).toMatchObject({
      productId: archived.id,
      warehouseId: warehouse.id,
      quantity: -2,
    });
    client.clear();
  });

  it('continues to exclude archived products from new purchase orders', async () => {
    vi.stubGlobal('fetch', fixtureFetch());
    const client = openPage(<Orders />);
    const person = userEvent.setup();
    await person.click(screen.getByRole('button', { name: 'Create purchase order' }));
    const dialog = screen.getByRole('dialog');
    const product = await within(dialog).findByRole('combobox', { name: 'Product 1' });
    expect(
      within(product).getByRole('option', { name: 'CURRENT-1 · Current filter' }),
    ).toBeInTheDocument();
    expect(within(product).queryByRole('option', { name: /RETIRED-1/ })).not.toBeInTheDocument();
    client.clear();
  });
});
