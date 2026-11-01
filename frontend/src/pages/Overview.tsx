import { Link } from 'react-router-dom';
import {
  Empty,
  ErrorNotice,
  Icon,
  Loading,
  PageHeading,
  Status,
  Table,
  useResource,
  useUser,
} from '../components';
import { canOperate, date, money, number } from '../format';
import type { Dashboard } from '../types';

export function Overview() {
  const user = useUser();
  const query = useResource<Dashboard>(['dashboard'], '/dashboard');
  const data = query.data;
  return (
    <>
      <PageHeading
        title="Operations overview"
        description="See what needs replenishing and keep incoming purchases moving."
        action={
          canOperate(user.role) && (
            <Link className="button primary" to="/orders?new=1">
              <Icon name="plus" />
              Create purchase order
            </Link>
          )
        }
      />
      <ErrorNotice error={query.error} retry={() => void query.refetch()} />
      {query.isLoading && <Loading text="Loading stock balances and open purchases…" />}
      {data && (
        <>
          <section className="overview-summary" aria-label="Current workspace totals">
            <div className="summary-primary">
              <span>Inventory at standard cost</span>
              <strong>{money(data.stockValue)}</strong>
              <small>Across all warehouse positions</small>
            </div>
            <dl>
              <div>
                <dt>Active products</dt>
                <dd>{number(data.activeProducts)}</dd>
              </div>
              <div>
                <dt>Open purchase orders</dt>
                <dd>{number(data.openOrders)}</dd>
              </div>
            </dl>
            <Link className="summary-attention" to="/inventory?low=1">
              <strong>{number(data.lowStockCount)}</strong>
              <span>positions need attention</span>
              <small>At or below reorder point</small>
            </Link>
          </section>
          <div className="overview-columns">
            <section className="panel attention-panel">
              <header className="panel-heading">
                <div>
                  <h2>Replenishment watch</h2>
                  <p>Prioritize positions at or below their reorder point.</p>
                </div>
                <Link to="/inventory?low=1">View inventory</Link>
              </header>
              {data.lowStock.length ? (
                <ul className="reorder-list">
                  {data.lowStock.map((position) => (
                    <li key={`${position.productId}-${position.warehouseId}`}>
                      <div>
                        <span className="document-code">{position.sku}</span>
                        <strong>{position.productName}</strong>
                        <small>{position.warehouseName}</small>
                      </div>
                      <div className="quantity-alert">
                        <strong>
                          {number(position.onHand)} <small>{position.unit}</small>
                        </strong>
                        <span>Reorder at {number(position.reorderPoint)}</span>
                      </div>
                    </li>
                  ))}
                </ul>
              ) : (
                <Empty
                  title={
                    data.activeProducts
                      ? 'No positions need replenishment'
                      : 'Your catalog starts here'
                  }
                  action={
                    !data.activeProducts && (
                      <Link to="/products" className="button secondary">
                        Open product catalog
                      </Link>
                    )
                  }
                >
                  {data.activeProducts
                    ? 'Every stocked position is above its reorder point.'
                    : 'Add products and a warehouse to begin tracking stock and purchase orders.'}
                </Empty>
              )}
            </section>
            <section className="panel chart-panel">
              <header className="panel-heading">
                <div>
                  <h2>Units received and issued</h2>
                  <p>Actual stock movements over the last seven days.</p>
                </div>
              </header>
              <div className="chart-legend">
                <span>
                  <i className="legend-in" />
                  Incoming
                </span>
                <span>
                  <i className="legend-out" />
                  Outgoing
                </span>
              </div>
              {data.movementSeries.length ? (
                <div
                  className="movement-chart"
                  role="img"
                  aria-label={data.movementSeries
                    .map(
                      (day) =>
                        `${date(day.date)}: ${day.incoming} incoming, ${day.outgoing} outgoing`,
                    )
                    .join('; ')}
                >
                  {data.movementSeries.map((day) => {
                    const max = Math.max(
                      1,
                      ...data.movementSeries.flatMap((point) => [point.incoming, point.outgoing]),
                    );
                    return (
                      <div className="chart-day" key={day.date}>
                        <div className="chart-bars">
                          <div
                            className="bar incoming"
                            style={{ height: `${(day.incoming / max) * 100}%` }}
                            title={`${day.incoming} incoming`}
                          />
                          <div
                            className="bar outgoing"
                            style={{ height: `${(day.outgoing / max) * 100}%` }}
                            title={`${day.outgoing} outgoing`}
                          />
                        </div>
                        <span>
                          {new Intl.DateTimeFormat(undefined, { weekday: 'short' }).format(
                            new Date(`${day.date.slice(0, 10)}T12:00:00`),
                          )}
                        </span>
                        <small>
                          {day.incoming} / {day.outgoing}
                        </small>
                      </div>
                    );
                  })}
                </div>
              ) : (
                <Empty title="No stock movements recorded">
                  Receiving an approved purchase order will add its units to this chart.
                </Empty>
              )}
              <p className="chart-note">
                Includes receipts, adjustments, and warehouse transfers. Quantities are whole units.
              </p>
            </section>
          </div>
          <section className="panel">
            <header className="panel-heading">
              <div>
                <h2>Recent purchase orders</h2>
                <p>Follow each order from preparation to receipt.</p>
              </div>
              <Link to="/orders">All purchase orders</Link>
            </header>
            {data.recentOrders.length ? (
              <Table label="Recent purchase orders">
                <thead>
                  <tr>
                    <th>Order / supplier</th>
                    <th>Destination</th>
                    <th>Expected</th>
                    <th>Status</th>
                    <th className="numeric">Order value</th>
                  </tr>
                </thead>
                <tbody>
                  {data.recentOrders.map((order) => (
                    <tr key={order.id}>
                      <td>
                        <Link className="table-link" to={`/orders/${order.id}`}>
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
              <Empty title="No purchase orders yet">
                Create an order after adding a product, a supplier, and a receiving warehouse.
              </Empty>
            )}
          </section>
        </>
      )}
    </>
  );
}
