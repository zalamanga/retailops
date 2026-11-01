# RetailOps implementation contract

One .NET 10 / React / PostgreSQL 18 app extending StockPilot. Preserve purchasing, approvals, stock locking, opaque sessions and CSRF. No payment gateway or tax computation. Synthetic training data only.

## Authorization

Admin/Manager are central back-office roles. Operator/Viewer retain inherited permissions. Add Cashier: no inherited business routes or organization-wide inventory, costs, reports, supplier/user/procurement data. A cashier uses assigned branches only, own shift and own receipts. Admin assigns branches and prices; Admin/Manager may operate their own shifts anywhere and authorize returns. Check current permission on every request/replay. Branch maps one warehouse permanently; require empty warehouse on initial mapping. Legacy business routes need a Backoffice policy excluding Cashier.

## Common API rules

New routes `/api/retail/...`; below paths omit `/api`. CamelCase JSON. IDR decimal max2places, positive bounded prices/totals, integer quantities and max50distinct lines. Mutation key is `Idempotency-Key` UUID header. Identical key/owner/canonical payload replays original effect/result; different payload/owner conflicts. Replay works after a shift closes if currently authorized. Server time determines accounting posting; client time is provenance only. Use inherited ProblemDetails errors. Collections bounded max200; no fake rows.

Auth setup/login/me UserView adds stable `organizationId` GUID alongside organizationName; use this identity for queue partitions. Inherited CSRF400 has title `Request verification failed`: refresh token and retry once with unchanged command key/payload. Actual401 requires same-owner login; generic403 is a definite permission conflict to review, not permission to replay as another user.

Queued mutations always send `X-Retail-Actor` and `X-Retail-Organization` with the immutable command owner's GUIDs, including after CSRF refresh. Server retail mutation filter validates both if either is present, and rejects mismatch with current cookie identity before any effect/replay. Direct API calls without this optional pair retain ordinary current-session semantics. This closes the cookie change race after a client `/auth/me` check, especially for opening a shift. Account change/logout in another tab must invalidate the old visible workspace/queue and remount state under the new identity.

## Exact minimum shapes

- `GET /retail/branches` -> `[{id,code,name,warehouseId,warehouseName}]`, scoped.
- `POST /retail/branches` Admin `{code,name,warehouseId}` -> branch.
- `GET /retail/assignments` Admin -> `[{userId,branchId}]`; `POST` `{userId,branchId}` idempotent assignment; `DELETE /retail/assignments/{userId}/{branchId}` revokes.
- `GET /retail/prices` Admin/Manager -> `[{productId,sku,name,retailPrice,wholesalePrice,wholesaleMinimum,version}]`.
- `PUT /retail/prices/{productId}` Admin `{retailPrice,wholesalePrice,wholesaleMinimum,expectedVersion}` -> price. Initial expectedVersion0. Positive prices, wholesale<=retail, minimum>=2; version conflict409.
- `GET /retail/catalog?branchId=UUID` -> `{branch:{...},products:[{productId,sku,name,unit,onHand,retailPrice,wholesalePrice,wholesaleMinimum,version}],capturedAt}`. Only active priced products, no Cashier cost disclosure.
- `GET /retail/shifts?branchId=UUID` -> shift array, Cashier own only. Shift `{id,branchId,branchName,openedById,openedByName,openingCash,status,openedAt,closedAt,countedCash,expectedCash,variance}`.
- `POST /retail/shifts` + key `{branchId,openingCash}` -> shift. One open shift per user, database-enforced. `POST /retail/shifts/{id}/close` + key `{countedCash}` -> shift, owner only, serialized with sales/returns, stable close replay. Expected cash = opening + accepted sales - returns in this shift. Opening cash/closing variance are operational drawer counts, not equity or automatic GL postings. Label this in UI/docs.
- `POST /retail/sales` + key `{branchId,shiftId,priceTier:"Retail"|"Wholesale",clientCreatedAt:ISO,lines:[{productId,quantity,unitPrice,priceVersion}],cashTendered}` -> sale `{id,number,branchId,branchName,shiftId,actorId,actorName,priceTier,total,cashTendered,change,createdAt,clientCreatedAt,lines:[{id,productId,sku,name,quantity,unitPrice,lineTotal,returnedQuantity}],replayed}`. No browser total/cost trusted. Canonical product order; duplicate products rejected. Wholesale quantity meets each minimum. Cached price/version must match current server or409 with no effects. Lock shift then products/prices then sorted inventory. Sale, inventory, GL and audit commit atomically, stock never negative.
- `GET /retail/sales?branchId=UUID` -> summary array `{id,number,branchId,branchName,shiftId,actorId,actorName,priceTier,total,createdAt}`; Cashier own only. `GET /retail/sales/{id}` -> detailed sale with cumulative returned quantities, authorized.
- `POST /retail/sales/{id}/returns` Admin/Manager + key `{shiftId,reason,lines:[{saleLineId,quantity}]}` -> `{id,number,saleId,shiftId,total,createdAt,replayed}`. Own open shift at sale branch. Serialize cumulative returns <= sold; original unit price/cost; return stock, reverse revenue/COGS/financial cash atomically. Cash refund is recorded by operator, no provider transfer.
- `GET /retail/journals` Admin/Manager -> `[{id,number,sourceType,sourceId,branchId,description,createdAt,totalDebit,totalCredit,lines:[{accountCode,accountName,debit,credit}]}]`.
- `GET /retail/reports?from=YYYY-MM-DD&to=YYYY-MM-DD&branchId=UUID` Admin/Manager -> `{from,to,branchId,salesCount,grossSales,refunds,netSales,costOfGoodsSold,varianceExpense,netProfit,trialBalance:[{code,name,type,debit,credit,balance}],balanceSheet:{asOf,assets,liabilities,equity,retainedEarnings,balanced},journalBalanced}`. Sales/P&L use inclusive WIB dates and optional branch. Trial balance and balance sheet are organization-wide inception-through-to, explicitly labelled. Validate date range/order/max span. Retained earnings derive from all posted revenue/expense.

## Accounting

Fixed chart:1000 Cash/Asset;1200 Inventory/Asset;2000 Trade payables/Liability;3000 Opening equity/Equity;4000 Sales revenue/Revenue;5000 COGS/Expense;5100 Inventory and purchase variance/Expense.

Standard-cost valuation. Product StandardCost changes rejected while stock remains, serialized with inventory writes. Initial positive adjustment Dr Inventory/Cr Equity; negative adjustment Dr Variance/Cr Inventory. Purchase receipt Dr Inventory at standard cost/Cr Payables at actual cost; price difference posts to Variance with proper sign. Transfer Dr/Cr Inventory with no company-wide effect. Sale Dr Cash/Cr Revenue and Dr COGS/Cr Inventory; return reverses original price/cost. Zero-value components skip journal lines; do not create empty journals for zero-cost movements. Existing procurement and adjustment transactions must post GL inside that same transaction.

Return valuation edge: once all stock is sold, StandardCost may change. A later return still reverses ORIGINAL revenue/COGS, but restored inventory is valued at CURRENT standard cost. Post the difference to5100 (credit when current>original, debit when current<original). Original sale/journal remain immutable. Test this edge and reconcile inventory GL to current on-hand valuation; disclose valuation variance separately from cash refund.

Unique sourceType/sourceId per journal. Headers and lines append-only in EF AND PostgreSQL. Deferred database constraint rejects empty, nonpositive or unbalanced journals at commit. Reports use committed lines and reconcile to inventory valuation, sales/refunds and variance. Financial reports cover posted transactions, not tax compliance, invoicing or supplier payment settlement.

## Offline contract

IndexedDB holds minimal catalog/identity metadata and immutable queued sale commands, scoped by user+organization+branch. Never store passwords, session/CSRF tokens. Persist before first POST; retain key/payload through network errors, crash or500. Queue states pending/syncing/confirmed/conflict; attempt/error/result metadata visible. Multi-tab claiming serialized, server idempotency final protection. Definite400/403/404/409/422 are review conflicts,401/CSRF requires login; unknown outcomes retry original key. Never silently reprice or change identity after an uncertain result. Explicit discard/replacement allowed only on definite rejection; replacement newkey/reviewed payload.

Uncertainty is sticky: after any previous attempt might have committed, a later403 (for example assignment revoked) does not prove the earlier attempt failed. Keep original key/payload and block discard/replacement until the accepted result can be reconciled. Mark uncertainty before POST so a crash during the request cannot falsely mark it unsubmitted. Initial definitive validation rejection may be reviewed/discarded; this rule does not authorize deleting an ambiguous command.

Production service worker precaches public versioned app shell assets, not arbitrary API data. After first online load/catalog fetch, offline reload must work. No invented server receipt before acceptance. Keep current offline identity without treating a failed network GET as logout. Another user cannot see/replay old user's queue. Warn before logout with pending work, retain it for owner recovery. Display cache age, branch, current shift and unsynced state.

## Test/runtime isolation

Compose projectretailops, loopback app8150/DB5450, new volumes/credentials. Source StockPilot and other running projects untouched. SDK/cache onD. Real PG tests: branch access, last-unit race, idempotency mismatches/replay, parallel partial returns, shift closure races, stale prices, financial balancing/immutability/report reconciliation/standard-cost changes. Browser: actual IndexedDB, offline reload, unknown outcome retry, second-user isolation, conflict resolution, mobile/desktop. Inherited procurement tests stay green. Real screenshots from synthetic records only, ignored local credentials. Update this contract only after coordination with root.
