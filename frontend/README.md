# RetailOps web client

React and TypeScript render purchasing, branch POS, reconciliation, and accounting views. The ASP.NET Core API owns accepted business records. The POS additionally keeps an identity-scoped catalog snapshot and immutable command queue in IndexedDB for offline preparation. It stores no session credentials there; a pending command becomes a confirmed sale only after server acceptance.

## Local development

The repository's `scripts/dev.ps1` starts the API at `http://127.0.0.1:5190` and Vite at `http://127.0.0.1:5191`. For a separately running API on that development port:

```sh
npm ci
npm run dev
```

Vite proxies `/api` to the API. Production serves the built `dist` files and API from the same origin. Navigation uses hash routes so a bookmarked order does not require a separate web-server rewrite.

## Checks

```sh
npm run typecheck
npm test
npm run build
```

Browser checks use the production frontend served by a running API with a dedicated synthetic PostgreSQL database. Set `RETAILOPS_E2E=1` and `PLAYWRIGHT_BASE_URL` to the isolated loopback test host on port5188, then run `npm run test:browser`. The test suite creates staff, stock and transactions; never target a working database. See [verification](../docs/VERIFICATION.md) for the complete database-backed checks. Vite does not provide the production offline shell.

## UI behavior

- Query keys include the relevant search, warehouse, status, and page filters. Mutations invalidate the persisted records they affect.
- The antiforgery token is requested before writes and refreshed after sign-in. The session cookie remains inaccessible to application JavaScript.
- Receipt retries retain their idempotency key while the payload is unchanged. A changed quantity or note receives a new key.
- POS, shift and return commands retain their original key and payload through reloads and uncertain responses. An uncertain result must be reconciled before creating a replacement command.
- The production service worker caches only public application assets. Catalog snapshots and queues are partitioned by stable organization/user IDs; session revalidation and expected-owner headers protect synchronization after account switches. Browser storage remains local, unencrypted device data.
- Role-based controls guide operators; the API independently authorizes every request.
- Stock adjustments and transfers include archived products so remaining units can be reconciled. New purchase orders only offer active products.
- Native modal dialogs provide focus containment and Escape support. Tables scroll inside labeled regions on small screens.
