import { useState } from 'react';
import { useQueryClient } from '@tanstack/react-query';
import { params, write } from '../api';
import {
  Empty,
  ErrorNotice,
  Field,
  Form,
  Icon,
  Loading,
  Modal,
  numeric,
  PageHeading,
  Pagination,
  Search,
  Status,
  Table,
  text,
  useResource,
  useUser,
} from '../components';
import { canMaintain, money, number } from '../format';
import type { PageResult, Product, Supplier, Warehouse } from '../types';

export function Products() {
  const user = useUser();
  const client = useQueryClient();
  const [search, setSearch] = useState('');
  const [page, setPage] = useState(1);
  const [archived, setArchived] = useState(false);
  const [editing, setEditing] = useState<Product | 'new' | null>(null);
  const query = useResource<PageResult<Product>>(
    ['products', search, page, archived],
    `/products${params({ search, page, includeArchived: archived })}`,
  );
  const product = editing && editing !== 'new' ? editing : null;
  return (
    <>
      <PageHeading
        title="Product catalog"
        description="Define SKUs, purchase costs, and the stock level that triggers a reorder."
        action={
          canMaintain(user.role) && (
            <button className="button primary" onClick={() => setEditing('new')}>
              <Icon name="plus" />
              Add product
            </button>
          )
        }
      />
      <section className="panel">
        <div className="filter-bar">
          <Search
            value={search}
            onChange={(value) => {
              setSearch(value);
              setPage(1);
            }}
            placeholder="Search product name or SKU"
          />
          <label className="checkbox">
            <input
              type="checkbox"
              checked={archived}
              onChange={(event) => {
                setArchived(event.target.checked);
                setPage(1);
              }}
            />
            Include archived products
          </label>
        </div>
        <ErrorNotice error={query.error} retry={() => void query.refetch()} />
        {query.isLoading && <Loading text="Loading the product catalog…" />}
        {query.data && (
          <>
            {query.data.items.length ? (
              <Table label="Product catalog">
                <thead>
                  <tr>
                    <th>SKU / product</th>
                    <th>Unit</th>
                    <th className="numeric">Reorder point</th>
                    <th className="numeric">Standard cost</th>
                    <th>Status</th>
                    {canMaintain(user.role) && (
                      <th>
                        <span className="sr-only">Actions</span>
                      </th>
                    )}
                  </tr>
                </thead>
                <tbody>
                  {query.data.items.map((item) => (
                    <tr key={item.id}>
                      <td>
                        <span className="document-code">{item.sku}</span>
                        <strong>{item.name}</strong>
                      </td>
                      <td>{item.unit}</td>
                      <td className="numeric">{number(item.reorderPoint)}</td>
                      <td className="numeric">{money(item.standardCost)}</td>
                      <td>
                        <Status value={item.isActive ? 'Active' : 'Archived'} />
                      </td>
                      {canMaintain(user.role) && (
                        <td className="actions">
                          <button
                            className="button text"
                            onClick={() => setEditing(item)}
                            aria-label={`Edit ${item.name}`}
                          >
                            Edit product
                          </button>
                        </td>
                      )}
                    </tr>
                  ))}
                </tbody>
              </Table>
            ) : (
              <Empty title={search ? 'No matching products' : 'No products in this catalog'}>
                {search
                  ? 'Try a different SKU or product name, or include archived products.'
                  : 'Add your first product to make it available for purchasing and inventory.'}
              </Empty>
            )}
            <Pagination result={query.data} page={page} onPage={setPage} />
          </>
        )}
      </section>
      {editing && (
        <Modal
          title={product ? `Edit product · ${product.sku}` : 'Add a product'}
          onClose={() => setEditing(null)}
        >
          <Form
            onCancel={() => setEditing(null)}
            submitLabel={product ? 'Save product' : 'Add product'}
            submit={async (data) => {
              await write(
                `/products${product ? `/${product.id}` : ''}`,
                {
                  sku: text(data, 'sku'),
                  name: text(data, 'name'),
                  unit: text(data, 'unit'),
                  reorderPoint: numeric(data, 'reorderPoint'),
                  standardCost: numeric(data, 'standardCost'),
                  isActive: data.get('isActive') === 'on',
                },
                product ? 'PUT' : 'POST',
              );
              await client.invalidateQueries();
              setEditing(null);
            }}
          >
            <div className="form-grid">
              <Field label="SKU" hint="A unique stock-keeping code.">
                <input name="sku" required maxLength={64} defaultValue={product?.sku} autoFocus />
              </Field>
              <Field label="Product name">
                <input name="name" required maxLength={180} defaultValue={product?.name} />
              </Field>
              <Field label="Unit" hint="For example: pcs, box, or roll.">
                <input name="unit" required maxLength={24} defaultValue={product?.unit} />
              </Field>
              <Field label="Reorder point">
                <input
                  name="reorderPoint"
                  type="number"
                  required
                  min={0}
                  step={1}
                  defaultValue={product?.reorderPoint ?? 0}
                />
              </Field>
              <Field label="Standard cost (IDR)">
                <input
                  name="standardCost"
                  type="number"
                  required
                  min={0}
                  step="0.01"
                  defaultValue={product?.standardCost ?? 0}
                />
              </Field>
            </div>
            <label className="checkbox">
              <input type="checkbox" name="isActive" defaultChecked={product?.isActive ?? true} />
              Active and available for new purchase orders
            </label>
            {product && (
              <p className="field-note">
                Uncheck to archive. Existing orders and stock history stay available.
              </p>
            )}
          </Form>
        </Modal>
      )}
    </>
  );
}
export function Suppliers() {
  const user = useUser();
  const client = useQueryClient();
  const [search, setSearch] = useState('');
  const [page, setPage] = useState(1);
  const [editing, setEditing] = useState<Supplier | 'new' | null>(null);
  const query = useResource<PageResult<Supplier>>(
    ['suppliers', search, page],
    `/suppliers${params({ search, page })}`,
  );
  const supplier = editing && editing !== 'new' ? editing : null;
  return (
    <>
      <PageHeading
        title="Suppliers"
        description="Keep purchasing contacts and expected lead times in one shared record."
        action={
          canMaintain(user.role) && (
            <button className="button primary" onClick={() => setEditing('new')}>
              <Icon name="plus" />
              Add supplier
            </button>
          )
        }
      />
      <section className="panel">
        <div className="filter-bar">
          <Search
            value={search}
            onChange={(value) => {
              setSearch(value);
              setPage(1);
            }}
            placeholder="Search suppliers"
          />
        </div>
        <ErrorNotice error={query.error} retry={() => void query.refetch()} />
        {query.isLoading && <Loading text="Loading supplier records…" />}
        {query.data && (
          <>
            {query.data.items.length ? (
              <Table label="Suppliers">
                <thead>
                  <tr>
                    <th>Supplier</th>
                    <th>Email</th>
                    <th>Phone</th>
                    <th>Lead time</th>
                    {canMaintain(user.role) && (
                      <th>
                        <span className="sr-only">Actions</span>
                      </th>
                    )}
                  </tr>
                </thead>
                <tbody>
                  {query.data.items.map((item) => (
                    <tr key={item.id}>
                      <td>
                        <strong>{item.name}</strong>
                      </td>
                      <td>{item.email || 'Not provided'}</td>
                      <td>{item.phone || 'Not provided'}</td>
                      <td>{item.leadTimeDays} days</td>
                      {canMaintain(user.role) && (
                        <td className="actions">
                          <button
                            className="button text"
                            onClick={() => setEditing(item)}
                            aria-label={`Edit ${item.name}`}
                          >
                            Edit supplier
                          </button>
                        </td>
                      )}
                    </tr>
                  ))}
                </tbody>
              </Table>
            ) : (
              <Empty title={search ? 'No matching suppliers' : 'No suppliers added'}>
                {search
                  ? 'Try another supplier name or contact detail.'
                  : 'Add a supplier before preparing your first purchase order.'}
              </Empty>
            )}
            <Pagination result={query.data} page={page} onPage={setPage} />
          </>
        )}
      </section>
      {editing && (
        <Modal
          title={supplier ? 'Edit supplier' : 'Add a supplier'}
          onClose={() => setEditing(null)}
        >
          <Form
            onCancel={() => setEditing(null)}
            submitLabel={supplier ? 'Save supplier' : 'Add supplier'}
            submit={async (data) => {
              await write(
                `/suppliers${supplier ? `/${supplier.id}` : ''}`,
                {
                  name: text(data, 'name'),
                  email: text(data, 'email'),
                  phone: text(data, 'phone'),
                  leadTimeDays: numeric(data, 'leadTimeDays'),
                },
                supplier ? 'PUT' : 'POST',
              );
              await client.invalidateQueries();
              setEditing(null);
            }}
          >
            <Field label="Supplier name">
              <input name="name" required maxLength={180} defaultValue={supplier?.name} autoFocus />
            </Field>
            <div className="form-grid">
              <Field label="Contact email (optional)">
                <input name="email" type="email" maxLength={254} defaultValue={supplier?.email} />
              </Field>
              <Field label="Phone (optional)">
                <input name="phone" type="tel" maxLength={40} defaultValue={supplier?.phone} />
              </Field>
              <Field label="Lead time in days">
                <input
                  name="leadTimeDays"
                  type="number"
                  min={0}
                  max={3650}
                  step={1}
                  required
                  defaultValue={supplier?.leadTimeDays ?? 0}
                />
              </Field>
            </div>
          </Form>
        </Modal>
      )}
    </>
  );
}
export function Warehouses() {
  const user = useUser();
  const client = useQueryClient();
  const [open, setOpen] = useState(false);
  const query = useResource<Warehouse[]>(['warehouses'], '/warehouses');
  return (
    <>
      <PageHeading
        title="Warehouses"
        description="Every receipt and stock movement belongs to a named location."
        action={
          canMaintain(user.role) && (
            <button className="button primary" onClick={() => setOpen(true)}>
              <Icon name="plus" />
              Add warehouse
            </button>
          )
        }
      />
      <section className="panel">
        <ErrorNotice error={query.error} retry={() => void query.refetch()} />
        {query.isLoading && <Loading text="Loading warehouse locations…" />}
        {query.data &&
          (query.data.length ? (
            <Table label="Warehouse locations">
              <thead>
                <tr>
                  <th>Warehouse code</th>
                  <th>Location name</th>
                </tr>
              </thead>
              <tbody>
                {query.data.map((item) => (
                  <tr key={item.id}>
                    <td>
                      <span className="document-code">{item.code}</span>
                    </td>
                    <td>
                      <strong>{item.name}</strong>
                    </td>
                  </tr>
                ))}
              </tbody>
            </Table>
          ) : (
            <Empty title="No warehouses yet">
              Add the first receiving location so purchase orders have a stock destination.
            </Empty>
          ))}
      </section>
      {open && (
        <Modal title="Add a warehouse" onClose={() => setOpen(false)}>
          <Form
            onCancel={() => setOpen(false)}
            submitLabel="Add warehouse"
            submit={async (data) => {
              await write('/warehouses', { code: text(data, 'code'), name: text(data, 'name') });
              await client.invalidateQueries();
              setOpen(false);
            }}
          >
            <Field label="Warehouse code" hint="A short, unique location code.">
              <input name="code" required maxLength={32} autoFocus />
            </Field>
            <Field label="Warehouse name">
              <input name="name" required maxLength={120} />
            </Field>
          </Form>
        </Modal>
      )}
    </>
  );
}
