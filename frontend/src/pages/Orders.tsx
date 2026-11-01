import { useEffect, useRef, useState } from 'react';
import { Link, useNavigate, useParams, useSearchParams } from 'react-router-dom';
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
  PageHeading,
  Pagination,
  Search,
  Status,
  Table,
  text,
  useResource,
  useUser,
} from '../components';
import { canMaintain, canOperate, date, label, money, number, validLines } from '../format';
import { useReferences } from '../references';
import { statuses, type Order, type PageResult, type OrderSummary } from '../types';

function CreateOrder({ onClose }: { onClose: () => void }) {
  const refs = useReferences();
  const client = useQueryClient();
  const navigate = useNavigate();
  const [lines, setLines] = useState([
    { key: crypto.randomUUID(), productId: '', quantity: 1, unitCost: 0 },
  ]);
  const ready =
    !refs.loading &&
    !refs.error &&
    refs.products.length > 0 &&
    refs.suppliers.length > 0 &&
    refs.warehouses.length > 0;
  function updateLine(
    key: string,
    property: 'productId' | 'quantity' | 'unitCost',
    value: string | number,
  ) {
    setLines((current) =>
      current.map((line) =>
        line.key !== key
          ? line
          : {
              ...line,
              [property]: value,
              ...(property === 'productId'
                ? {
                    unitCost:
                      refs.products.find((product) => product.id === value)?.standardCost ?? 0,
                  }
                : {}),
            },
      ),
    );
  }
  return (
    <Modal title="Create purchase order" onClose={onClose} wide>
      {refs.loading ? (
        <Loading text="Loading available products, suppliers, and destinations…" />
      ) : refs.error ? (
        <ErrorNotice error={refs.error} retry={refs.retry} />
      ) : !ready ? (
        <Empty title="Finish your purchasing reference data">
          <span>
            Add at least one{' '}
            <Link to="/products" onClick={onClose}>
              product
            </Link>
            ,{' '}
            <Link to="/suppliers" onClick={onClose}>
              supplier
            </Link>
            , and{' '}
            <Link to="/warehouses" onClick={onClose}>
              warehouse
            </Link>{' '}
            before creating an order.
          </span>
        </Empty>
      ) : (
        <Form
          onCancel={onClose}
          submitLabel="Save draft order"
          submit={async (data) => {
            const payloadLines = lines.map(({ productId, quantity, unitCost }) => ({
              productId,
              quantity,
              unitCost,
            }));
            const message = validLines(payloadLines);
            if (message) throw new Error(message);
            const order = await write<Order>('/orders', {
              supplierId: text(data, 'supplierId'),
              warehouseId: text(data, 'warehouseId'),
              expectedDate: text(data, 'expectedDate'),
              notes: text(data, 'notes'),
              lines: payloadLines,
            });
            await client.invalidateQueries();
            onClose();
            navigate(`/orders/${order.id}`);
          }}
        >
          <div className="form-grid">
            <Field label="Supplier">
              <select name="supplierId" required defaultValue="" autoFocus>
                <option value="" disabled>
                  Select a supplier
                </option>
                {refs.suppliers.map((supplier) => (
                  <option key={supplier.id} value={supplier.id}>
                    {supplier.name}
                  </option>
                ))}
              </select>
            </Field>
            <Field label="Receiving warehouse">
              <select name="warehouseId" required defaultValue="">
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
            <Field label="Expected delivery date">
              <input type="date" name="expectedDate" required />
            </Field>
          </div>
          <div className="form-section-heading">
            <h3>Order lines</h3>
            <span>{lines.length} / 50 lines</span>
          </div>
          <div className="order-form-lines">
            {lines.map((line, index) => (
              <div className="order-form-line" key={line.key}>
                <Field label={`Product ${index + 1}`}>
                  <select
                    aria-label={`Product ${index + 1}`}
                    required
                    value={line.productId}
                    onChange={(event) => updateLine(line.key, 'productId', event.target.value)}
                  >
                    <option value="" disabled>
                      Select product
                    </option>
                    {refs.products.map((product) => (
                      <option key={product.id} value={product.id}>
                        {product.sku} · {product.name}
                      </option>
                    ))}
                  </select>
                </Field>
                <Field label="Quantity">
                  <input
                    aria-label={`Quantity for line ${index + 1}`}
                    type="number"
                    min={1}
                    step={1}
                    required
                    value={line.quantity || ''}
                    onChange={(event) =>
                      updateLine(line.key, 'quantity', Number(event.target.value))
                    }
                  />
                </Field>
                <Field label="Unit cost (IDR)">
                  <input
                    aria-label={`Unit cost for line ${index + 1}`}
                    type="number"
                    min={0}
                    step="0.01"
                    required
                    value={line.unitCost}
                    onChange={(event) =>
                      updateLine(line.key, 'unitCost', Number(event.target.value))
                    }
                  />
                </Field>
                <button
                  type="button"
                  className="icon-button remove-line"
                  aria-label={`Remove line ${index + 1}`}
                  disabled={lines.length === 1}
                  onClick={() =>
                    setLines((current) => current.filter((item) => item.key !== line.key))
                  }
                >
                  <Icon name="close" />
                </button>
              </div>
            ))}
          </div>
          <div className="line-summary">
            <button
              type="button"
              className="button secondary"
              disabled={lines.length >= 50}
              onClick={() =>
                setLines((current) => [
                  ...current,
                  { key: crypto.randomUUID(), productId: '', quantity: 1, unitCost: 0 },
                ])
              }
            >
              <Icon name="plus" />
              Add order line
            </button>
            <span>
              Calculated estimate{' '}
              <strong>
                {money(lines.reduce((sum, line) => sum + line.quantity * line.unitCost, 0))}
              </strong>
            </span>
          </div>
          <Field label="Order notes (optional)">
            <textarea name="notes" maxLength={2000} rows={3} />
          </Field>
          <p className="field-note">
            The order is saved as a draft. Submit it for a different Manager or Admin to approve
            before receiving stock.
          </p>
        </Form>
      )}
    </Modal>
  );
}
export function Orders() {
  const user = useUser();
  const [searchParams, setSearchParams] = useSearchParams();
  const [open, setOpen] = useState(false);
  const [search, setSearch] = useState('');
  const [status, setStatus] = useState('');
  const [page, setPage] = useState(1);
  useEffect(() => {
    if (searchParams.has('new') && canOperate(user.role)) {
      setOpen(true);
      setSearchParams({}, { replace: true });
    }
  }, [searchParams, setSearchParams, user.role]);
  const query = useResource<PageResult<OrderSummary>>(
    ['orders', search, status, page],
    `/orders${params({ search, status, page })}`,
  );
  return (
    <>
      <PageHeading
        title="Purchase orders"
        description="Prepare purchases, collect approvals, and reconcile every delivery."
        action={
          canOperate(user.role) && (
            <button className="button primary" onClick={() => setOpen(true)}>
              <Icon name="plus" />
              Create purchase order
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
            placeholder="Search order number or supplier"
          />
          <label className="filter-select">
            <span>Status</span>
            <select
              value={status}
              onChange={(event) => {
                setStatus(event.target.value);
                setPage(1);
              }}
            >
              <option value="">All statuses</option>
              {statuses.map((value) => (
                <option key={value} value={value}>
                  {label(value)}
                </option>
              ))}
            </select>
          </label>
        </div>
        <ErrorNotice error={query.error} retry={() => void query.refetch()} />
        {query.isLoading && <Loading text="Loading purchase orders…" />}
        {query.data && (
          <>
            {query.data.items.length ? (
              <Table label="Purchase orders">
                <thead>
                  <tr>
                    <th>Order / supplier</th>
                    <th>Receiving warehouse</th>
                    <th>Expected delivery</th>
                    <th>Status</th>
                    <th className="numeric">Value</th>
                  </tr>
                </thead>
                <tbody>
                  {query.data.items.map((order) => (
                    <tr key={order.id}>
                      <td>
                        <Link to={`/orders/${order.id}`} className="table-link">
                          {order.number}
                        </Link>
                        <small>{order.supplierName}</small>
                      </td>
                      <td>{order.warehouseName}</td>
                      <td>{date(order.expectedDate)}</td>
                      <td>
                        <Status value={order.status} />
                      </td>
                      <td className="numeric">{money(order.total)}</td>
                    </tr>
                  ))}
                </tbody>
              </Table>
            ) : (
              <Empty
                title={
                  search || status ? 'No orders match these filters' : 'No purchase orders yet'
                }
              >
                {search || status
                  ? 'Change the search or status filter to find another order.'
                  : 'Create an order with its supplier, destination, and product quantities. Stock changes only when an approved order is received.'}
              </Empty>
            )}
            <Pagination result={query.data} page={page} onPage={setPage} />
          </>
        )}
      </section>
      {open && <CreateOrder onClose={() => setOpen(false)} />}
    </>
  );
}
function ReceiveOrder({ order, onClose }: { order: Order; onClose: () => void }) {
  const client = useQueryClient();
  const submission = useRef<{ signature: string; key: string } | null>(null);
  const remaining = order.lines.filter((line) => line.receivedQuantity < line.quantity);
  return (
    <Modal title={`Receive stock · ${order.number}`} onClose={onClose} wide>
      <Form
        onCancel={onClose}
        submitLabel="Record receipt"
        submit={async (data) => {
          const lines = remaining
            .map((line) => ({ orderLineId: line.id, quantity: Number(data.get(line.id)) }))
            .filter((line) => line.quantity > 0);
          if (!lines.length) throw new Error('Enter a received quantity for at least one product.');
          if (lines.some((line) => !Number.isInteger(line.quantity)))
            throw new Error('Received quantities must be whole units.');
          const body = { notes: text(data, 'notes'), lines };
          const signature = JSON.stringify(body);
          if (submission.current?.signature !== signature)
            submission.current = { signature, key: crypto.randomUUID() };
          await write(`/orders/${order.id}/receipts`, body, 'POST', {
            'Idempotency-Key': submission.current.key,
          });
          await client.invalidateQueries();
          onClose();
        }}
      >
        <p className="form-introduction">
          Record the quantities physically delivered to <strong>{order.warehouseName}</strong>.
          Leave undelivered lines at zero for a partial receipt.
        </p>
        <div className="receipt-lines">
          {remaining.map((line) => (
            <div className="receipt-line" key={line.id}>
              <div>
                <span className="document-code">{line.sku}</span>
                <strong>{line.productName}</strong>
                <small>
                  {line.receivedQuantity} received / {line.quantity} ordered
                </small>
              </div>
              <Field
                label="Received now"
                hint={`Up to ${line.quantity - line.receivedQuantity} units`}
              >
                <input
                  type="number"
                  name={line.id}
                  min={0}
                  max={line.quantity - line.receivedQuantity}
                  step={1}
                  required
                  defaultValue={0}
                  aria-label={`Received now for ${line.productName}`}
                />
              </Field>
            </div>
          ))}
        </div>
        <Field label="Delivery notes (optional)">
          <textarea name="notes" rows={3} maxLength={2000} />
        </Field>
        <p className="field-note">
          The receipt updates stock and the order together. Retrying an unchanged submission uses
          the same receipt key.
        </p>
      </Form>
    </Modal>
  );
}
export function OrderDetail() {
  const { id } = useParams();
  const user = useUser();
  const client = useQueryClient();
  const query = useResource<Order>(['order', id], `/orders/${id}`);
  const [actionError, setActionError] = useState<unknown>(null);
  const [busy, setBusy] = useState(false);
  const [dialog, setDialog] = useState<'reject' | 'cancel' | 'receive' | null>(null);
  const order = query.data;
  async function transition(action: string, reason?: string) {
    setBusy(true);
    setActionError(null);
    try {
      await write(`/orders/${id}/${action}`, reason !== undefined ? { reason } : undefined);
      await client.invalidateQueries();
    } catch (error) {
      setActionError(error);
      throw error;
    } finally {
      setBusy(false);
    }
  }
  const canReceive =
    order && ['Approved', 'PartiallyReceived'].includes(order.status) && canOperate(user.role);
  const canCancel =
    order &&
    ['Draft', 'PendingApproval', 'Approved'].includes(order.status) &&
    canOperate(user.role);
  return (
    <>
      <Link className="back-link" to="/orders">
        Back to purchase orders
      </Link>
      <ErrorNotice error={query.error} retry={() => void query.refetch()} />
      {query.isLoading && <Loading text="Opening purchase order and its receipts…" />}
      {order && (
        <>
          <PageHeading
            title={order.number}
            description={`Prepared by ${order.createdByName} on ${date(order.createdAt)}`}
            action={<Status value={order.status} />}
          />
          <ErrorNotice error={actionError} />
          <section className="order-command-bar" aria-label="Order actions">
            <div>
              {order.status === 'Draft' && (
                <p>Check the lines, then submit this order for approval.</p>
              )}
              {order.status === 'PendingApproval' && (
                <p>
                  {order.createdById === user.id
                    ? 'A different Manager or Admin must approve your order. Your own approval is not allowed.'
                    : 'Review the purchase quantities and value before making a decision.'}
                </p>
              )}
              {canReceive && (
                <p>
                  Receive all or part of the delivery. Stock updates after the receipt is saved.
                </p>
              )}
              {order.status === 'Received' && <p>All ordered units have been received.</p>}
              {['Rejected', 'Cancelled'].includes(order.status) && (
                <p>This order is closed. {order.decisionReason}</p>
              )}
            </div>
            <div className="button-row">
              {order.status === 'Draft' && canOperate(user.role) && (
                <button
                  className="button primary"
                  disabled={busy}
                  onClick={() => void transition('submit').catch(() => undefined)}
                >
                  Submit for approval
                </button>
              )}
              {order.status === 'PendingApproval' && canMaintain(user.role) && (
                <>
                  <button
                    className="button primary"
                    disabled={busy || order.createdById === user.id}
                    onClick={() => void transition('approve').catch(() => undefined)}
                  >
                    Approve order
                  </button>
                  <button
                    className="button secondary"
                    disabled={busy}
                    onClick={() => setDialog('reject')}
                  >
                    Reject order
                  </button>
                </>
              )}
              {canReceive && (
                <button className="button primary" onClick={() => setDialog('receive')}>
                  Receive stock
                </button>
              )}
              {canCancel && (
                <button
                  className="button secondary"
                  disabled={busy}
                  onClick={() => setDialog('cancel')}
                >
                  Cancel order
                </button>
              )}
            </div>
          </section>
          <section className="panel order-document">
            <div className="order-facts">
              <div>
                <span>Supplier</span>
                <strong>{order.supplierName}</strong>
              </div>
              <div>
                <span>Deliver to</span>
                <strong>{order.warehouseName}</strong>
              </div>
              <div>
                <span>Expected delivery</span>
                <strong>{date(order.expectedDate)}</strong>
              </div>
              <div>
                <span>Purchase value</span>
                <strong>{money(order.total)}</strong>
              </div>
            </div>
            <Table label="Purchase order lines">
              <thead>
                <tr>
                  <th>SKU / product</th>
                  <th className="numeric">Ordered</th>
                  <th className="numeric">Received</th>
                  <th className="numeric">Remaining</th>
                  <th className="numeric">Unit cost</th>
                  <th className="numeric">Line value</th>
                </tr>
              </thead>
              <tbody>
                {order.lines.map((line) => (
                  <tr key={line.id}>
                    <td>
                      <span className="document-code">{line.sku}</span>
                      <strong>{line.productName}</strong>
                    </td>
                    <td className="numeric">{number(line.quantity)}</td>
                    <td className="numeric">{number(line.receivedQuantity)}</td>
                    <td className="numeric">{number(line.quantity - line.receivedQuantity)}</td>
                    <td className="numeric">{money(line.unitCost)}</td>
                    <td className="numeric">{money(line.lineTotal)}</td>
                  </tr>
                ))}
              </tbody>
              <tfoot>
                <tr>
                  <th colSpan={5} scope="row">
                    Total purchase value
                  </th>
                  <td className="numeric">{money(order.total)}</td>
                </tr>
              </tfoot>
            </Table>
            {order.notes && (
              <div className="document-notes">
                <h3>Order notes</h3>
                <p>{order.notes}</p>
              </div>
            )}
          </section>
          <section className="panel">
            <header className="panel-heading">
              <div>
                <h2>Delivery receipts</h2>
                <p>Each recorded delivery contributes to the received quantities above.</p>
              </div>
              <span>{order.receipts.length} receipts</span>
            </header>
            {order.receipts.length ? (
              <div className="receipt-history">
                {order.receipts.map((receipt) => (
                  <article key={receipt.id}>
                    <div className="receipt-history-heading">
                      <strong className="document-code">{receipt.number}</strong>
                      <span>
                        {date(receipt.createdAt, true)} · {receipt.createdByName}
                      </span>
                    </div>
                    <ul>
                      {receipt.lines.map((line) => (
                        <li key={line.orderLineId}>
                          <span>
                            {order.lines.find((item) => item.id === line.orderLineId)
                              ?.productName ?? line.orderLineId}
                          </span>
                          <strong>{number(line.quantity)} units</strong>
                        </li>
                      ))}
                    </ul>
                    {receipt.notes && <p>{receipt.notes}</p>}
                  </article>
                ))}
              </div>
            ) : (
              <Empty title="No delivery receipts">
                Receipts become available after the order is approved. Only delivered quantities
                increase stock.
              </Empty>
            )}
          </section>
          {dialog === 'receive' && <ReceiveOrder order={order} onClose={() => setDialog(null)} />}
          {(dialog === 'cancel' || dialog === 'reject') && (
            <Modal
              title={dialog === 'reject' ? `Reject ${order.number}` : `Cancel ${order.number}`}
              onClose={() => setDialog(null)}
            >
              <Form
                onCancel={() => setDialog(null)}
                submitLabel={dialog === 'reject' ? 'Reject order' : 'Cancel order'}
                submit={async (data) => {
                  await transition(dialog, text(data, 'reason'));
                  setDialog(null);
                }}
              >
                <p className="form-introduction">
                  {dialog === 'reject'
                    ? 'Explain why this purchase cannot proceed. A rejected order cannot be reopened.'
                    : 'Explain why this order is being cancelled. Cancellation is only available before the first receipt.'}
                </p>
                <Field label="Reason">
                  <textarea
                    name="reason"
                    required
                    minLength={3}
                    maxLength={1000}
                    rows={4}
                    autoFocus
                  />
                </Field>
              </Form>
            </Modal>
          )}
        </>
      )}
    </>
  );
}
