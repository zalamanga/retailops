import { useState } from 'react';
import { useSearchParams } from 'react-router-dom';
import { useQueryClient } from '@tanstack/react-query';
import { exportInventory, params, write } from '../api';
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
import { canOperate, date, label, money, number } from '../format';
import { useReferences } from '../references';
import type { Movement, PageResult, Position } from '../types';

function StockOperation({
  kind,
  position,
  onClose,
}: {
  kind: 'adjust' | 'transfer';
  position?: Position;
  onClose: () => void;
}) {
  const refs = useReferences(true);
  const client = useQueryClient();
  const [from, setFrom] = useState(position?.warehouseId ?? '');
  return (
    <Modal
      title={kind === 'adjust' ? 'Adjust stock balance' : 'Transfer between warehouses'}
      onClose={onClose}
    >
      {refs.loading ? (
        <Loading text="Loading products and warehouse locations…" />
      ) : refs.error ? (
        <ErrorNotice error={refs.error} retry={refs.retry} />
      ) : !refs.products.length || !refs.warehouses.length ? (
        <Empty title="Stock reference data is missing">
          Add active products and warehouses before recording stock movements.
        </Empty>
      ) : (
        <Form
          onCancel={onClose}
          submitLabel={kind === 'adjust' ? 'Record adjustment' : 'Record transfer'}
          submit={async (data) => {
            const quantity = numeric(data, 'quantity');
            if (
              !Number.isInteger(quantity) ||
              quantity === 0 ||
              (kind === 'transfer' && quantity < 0)
            )
              throw new Error(
                kind === 'adjust'
                  ? 'Enter a nonzero whole quantity. Use a minus sign to remove units.'
                  : 'Enter a positive whole transfer quantity.',
              );
            const common = {
              productId: text(data, 'productId'),
              quantity,
              reason: text(data, 'reason'),
            };
            if (kind === 'adjust')
              await write('/inventory/adjustments', {
                ...common,
                warehouseId: text(data, 'warehouseId'),
              });
            else {
              if (from === text(data, 'toWarehouseId'))
                throw new Error('Choose different source and destination warehouses.');
              await write('/inventory/transfers', {
                ...common,
                fromWarehouseId: from,
                toWarehouseId: text(data, 'toWarehouseId'),
              });
            }
            await client.invalidateQueries();
            onClose();
          }}
        >
          <p className="form-introduction">
            {kind === 'adjust'
              ? 'Reconcile a physical stock count or record stock leaving the warehouse. Every adjustment needs a reason.'
              : 'Move stock between locations. Both warehouse balances are updated together.'}
          </p>
          <Field label="Product">
            <select name="productId" required defaultValue={position?.productId ?? ''} autoFocus>
              <option value="" disabled>
                Select a product
              </option>
              {refs.products.map((product) => (
                <option key={product.id} value={product.id}>
                  {product.sku} · {product.name}
                  {!product.isActive && ' (archived)'}
                </option>
              ))}
            </select>
          </Field>
          {kind === 'adjust' ? (
            <Field label="Warehouse">
              <select name="warehouseId" required defaultValue={position?.warehouseId ?? ''}>
                <option value="" disabled>
                  Select a warehouse
                </option>
                {refs.warehouses.map((warehouse) => (
                  <option key={warehouse.id} value={warehouse.id}>
                    {warehouse.code} · {warehouse.name}
                  </option>
                ))}
              </select>
            </Field>
          ) : (
            <div className="form-grid">
              <Field label="From warehouse">
                <select
                  name="fromWarehouseId"
                  required
                  value={from}
                  onChange={(event) => setFrom(event.target.value)}
                >
                  <option value="" disabled>
                    Select source
                  </option>
                  {refs.warehouses.map((warehouse) => (
                    <option key={warehouse.id} value={warehouse.id}>
                      {warehouse.name}
                    </option>
                  ))}
                </select>
              </Field>
              <Field label="To warehouse">
                <select name="toWarehouseId" required defaultValue="">
                  <option value="" disabled>
                    Select destination
                  </option>
                  {refs.warehouses
                    .filter((warehouse) => warehouse.id !== from)
                    .map((warehouse) => (
                      <option key={warehouse.id} value={warehouse.id}>
                        {warehouse.name}
                      </option>
                    ))}
                </select>
              </Field>
            </div>
          )}
          <Field
            label={kind === 'adjust' ? 'Quantity change' : 'Units to transfer'}
            hint={
              kind === 'adjust'
                ? 'Positive adds stock; negative removes stock. For example, -2 removes two units.'
                : 'The source warehouse must have enough units for this transfer.'
            }
          >
            <input
              type="number"
              name="quantity"
              required
              step={1}
              min={kind === 'transfer' ? 1 : undefined}
            />
          </Field>
          <Field label="Reason">
            <textarea name="reason" required minLength={3} maxLength={1000} rows={3} />
          </Field>
        </Form>
      )}
    </Modal>
  );
}
export function Inventory() {
  const user = useUser();
  const [searchParams] = useSearchParams();
  const refs = useReferences();
  const [search, setSearch] = useState('');
  const [warehouseId, setWarehouseId] = useState('');
  const [lowStockOnly, setLowStockOnly] = useState(searchParams.get('low') === '1');
  const [page, setPage] = useState(1);
  const [operation, setOperation] = useState<{
    kind: 'adjust' | 'transfer';
    position?: Position;
  } | null>(null);
  const [exportError, setExportError] = useState<unknown>(null);
  const [exporting, setExporting] = useState(false);
  const filters = { search, warehouseId, lowStockOnly };
  const query = useResource<PageResult<Position>>(
    ['inventory', search, warehouseId, lowStockOnly, page],
    `/inventory${params({ ...filters, page })}`,
  );
  return (
    <>
      <PageHeading
        title="Inventory"
        description="Reconcile on-hand units by warehouse and act on low-stock positions."
        action={
          <>
            <button
              className="button secondary"
              disabled={exporting}
              onClick={async () => {
                setExportError(null);
                setExporting(true);
                try {
                  await exportInventory(params(filters));
                } catch (error) {
                  setExportError(error);
                } finally {
                  setExporting(false);
                }
              }}
            >
              <Icon name="download" />
              {exporting ? 'Exporting…' : 'Export CSV'}
            </button>
            {canOperate(user.role) && (
              <button className="button primary" onClick={() => setOperation({ kind: 'adjust' })}>
                <Icon name="plus" />
                Adjust stock
              </button>
            )}
          </>
        }
      />
      <ErrorNotice error={exportError} />
      <section className="panel">
        <div className="filter-bar">
          <Search
            value={search}
            onChange={(value) => {
              setSearch(value);
              setPage(1);
            }}
            placeholder="Search SKU or product"
          />
          <label className="filter-select">
            <span>Warehouse</span>
            <select
              value={warehouseId}
              onChange={(event) => {
                setWarehouseId(event.target.value);
                setPage(1);
              }}
            >
              <option value="">All warehouses</option>
              {refs.warehouses.map((warehouse) => (
                <option key={warehouse.id} value={warehouse.id}>
                  {warehouse.name}
                </option>
              ))}
            </select>
          </label>
          <label className="checkbox">
            <input
              type="checkbox"
              checked={lowStockOnly}
              onChange={(event) => {
                setLowStockOnly(event.target.checked);
                setPage(1);
              }}
            />
            Low stock only
          </label>
        </div>
        <ErrorNotice error={refs.error} retry={refs.retry} />
        <ErrorNotice error={query.error} retry={() => void query.refetch()} />
        {query.isLoading && <Loading text="Loading warehouse stock positions…" />}
        {query.data && (
          <>
            {query.data.items.length ? (
              <Table label="Warehouse inventory">
                <thead>
                  <tr>
                    <th>SKU / product</th>
                    <th>Warehouse</th>
                    <th className="numeric">On hand</th>
                    <th className="numeric">Reorder at</th>
                    <th className="numeric">Stock value</th>
                    <th>Status</th>
                    {canOperate(user.role) && <th>Stock actions</th>}
                  </tr>
                </thead>
                <tbody>
                  {query.data.items.map((position) => (
                    <tr key={`${position.productId}-${position.warehouseId}`}>
                      <td>
                        <span className="document-code">{position.sku}</span>
                        <strong>{position.productName}</strong>
                        <small>
                          {money(position.standardCost)} / {position.unit}
                        </small>
                      </td>
                      <td>{position.warehouseName}</td>
                      <td className={`numeric ${position.isLowStock ? 'attention-text' : ''}`}>
                        <strong>{number(position.onHand)}</strong>
                        <small>{position.unit}</small>
                      </td>
                      <td className="numeric">{number(position.reorderPoint)}</td>
                      <td className="numeric">{money(position.stockValue)}</td>
                      <td>
                        <Status value={position.isLowStock ? 'LowStock' : 'InStock'} />
                      </td>
                      {canOperate(user.role) && (
                        <td>
                          <div className="table-actions">
                            <button
                              className="button text"
                              onClick={() => setOperation({ kind: 'adjust', position })}
                              aria-label={`Adjust ${position.productName} at ${position.warehouseName}`}
                            >
                              Adjust
                            </button>
                            <button
                              className="button text"
                              disabled={position.onHand <= 0 || refs.warehouses.length < 2}
                              onClick={() => setOperation({ kind: 'transfer', position })}
                              aria-label={`Transfer ${position.productName} from ${position.warehouseName}`}
                            >
                              Transfer
                            </button>
                          </div>
                        </td>
                      )}
                    </tr>
                  ))}
                </tbody>
              </Table>
            ) : (
              <Empty
                title={
                  search || warehouseId || lowStockOnly
                    ? 'No stock positions match these filters'
                    : 'No inventory positions yet'
                }
              >
                {search || warehouseId || lowStockOnly
                  ? 'Adjust the filters to view other products or warehouse positions.'
                  : 'Add a product and a warehouse. Their stock starts at zero until a receipt or adjustment is recorded.'}
              </Empty>
            )}
            <Pagination result={query.data} page={page} onPage={setPage} />
          </>
        )}
      </section>
      <p className="page-note">
        Stock value uses current standard cost. Reorder alerts include positions with zero stock.
        Transfer is available when another warehouse exists and stock is on hand.
      </p>
      {operation && <StockOperation {...operation} onClose={() => setOperation(null)} />}
    </>
  );
}
export function Movements() {
  const refs = useReferences(true);
  const [warehouseId, setWarehouseId] = useState('');
  const [productId, setProductId] = useState('');
  const [page, setPage] = useState(1);
  const query = useResource<PageResult<Movement>>(
    ['movements', warehouseId, productId, page],
    `/movements${params({ warehouseId, productId, page })}`,
  );
  return (
    <>
      <PageHeading
        title="Stock movements"
        description="Trace the quantity, actor, and source behind every stock change."
      />
      <section className="panel">
        <div className="filter-bar">
          <label className="filter-select">
            <span>Product</span>
            <select
              value={productId}
              onChange={(event) => {
                setProductId(event.target.value);
                setPage(1);
              }}
            >
              <option value="">All products</option>
              {refs.products.map((product) => (
                <option key={product.id} value={product.id}>
                  {product.sku} · {product.name}
                </option>
              ))}
            </select>
          </label>
          <label className="filter-select">
            <span>Warehouse</span>
            <select
              value={warehouseId}
              onChange={(event) => {
                setWarehouseId(event.target.value);
                setPage(1);
              }}
            >
              <option value="">All warehouses</option>
              {refs.warehouses.map((warehouse) => (
                <option key={warehouse.id} value={warehouse.id}>
                  {warehouse.name}
                </option>
              ))}
            </select>
          </label>
        </div>
        <ErrorNotice error={refs.error} retry={refs.retry} />
        <ErrorNotice error={query.error} retry={() => void query.refetch()} />
        {query.isLoading && <Loading text="Loading the stock movement ledger…" />}
        {query.data && (
          <>
            {query.data.items.length ? (
              <Table label="Stock movement ledger">
                <thead>
                  <tr>
                    <th>When / actor</th>
                    <th>SKU / product</th>
                    <th>Warehouse</th>
                    <th>Movement</th>
                    <th className="numeric">Units</th>
                    <th>Reference / reason</th>
                  </tr>
                </thead>
                <tbody>
                  {query.data.items.map((movement) => (
                    <tr key={movement.id}>
                      <td>
                        {date(movement.createdAt, true)}
                        <small>{movement.actorName}</small>
                      </td>
                      <td>
                        <span className="document-code">{movement.sku}</span>
                        <strong>{movement.productName}</strong>
                      </td>
                      <td>{movement.warehouseName}</td>
                      <td>{label(movement.type)}</td>
                      <td className={`numeric ${movement.quantity < 0 ? 'attention-text' : ''}`}>
                        <strong>
                          {movement.quantity > 0 ? '+' : ''}
                          {number(movement.quantity)}
                        </strong>
                      </td>
                      <td>
                        <span className="document-code">{movement.reference}</span>
                        <small className="reason-text">{movement.reason}</small>
                      </td>
                    </tr>
                  ))}
                </tbody>
              </Table>
            ) : (
              <Empty title="No movements for this selection">
                Record a receipt, stock adjustment, or transfer to create a traceable movement.
                Clear filters to see the full history.
              </Empty>
            )}
            <Pagination result={query.data} page={page} onPage={setPage} />
          </>
        )}
      </section>
    </>
  );
}
