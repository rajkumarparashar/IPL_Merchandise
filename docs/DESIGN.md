# Design decisions

Each decision below follows the same shape: **Context → Problem → Options → Decision → Reasoning → Trade-offs → Consequences**.
The last sections cover assumptions, the extra scenarios added beyond the brief, known limitations, scaling to 10×, and what I would do next.

Contents

1. [Front end: Blazor WebAssembly vs Angular and React](#adr-1-front-end-blazor-webassembly-vs-angular-and-react)
2. [Clean Architecture, and what was deliberately left out](#adr-2-clean-architecture-and-what-was-deliberately-left-out)
3. [Cosmos DB data model and partition keys](#adr-3-cosmos-db-data-model-and-partition-keys)
4. [Optimistic concurrency and consistency level](#adr-4-optimistic-concurrency-and-consistency-level)
5. [Idempotency](#adr-5-idempotency)
6. [Checkout atomicity](#adr-6-checkout-atomicity)
7. [Authentication and authorisation](#adr-7-authentication-and-authorisation)
8. [API versioning and the backwards-compatible change](#adr-8-api-versioning-and-the-backwards-compatible-change)
9. [Pagination](#adr-9-pagination)
10. [Future multi-tenancy (design only)](#adr-10-future-multi-tenancy-design-only)
11. [Product photos in Azure Blob Storage](#adr-11-product-photos-in-azure-blob-storage)
12. [Price and stock changes reach open carts by push, not polling](#adr-12-price-and-stock-changes-reach-open-carts-by-push-not-polling)
13. [Assumptions](#assumptions) · [Extra scenarios](#extra-scenarios-beyond-the-brief) · [Limitations](#known-limitations) · [10× scale](#scaling-to-10) · [Next steps](#what-i-would-do-next)

---

## ADR-1: Front end: Blazor WebAssembly vs Angular and React

**Context.** The brief allows Angular with TypeScript, React with TypeScript, or Blazor. The backend is C#.

**Problem.** Pick a front end that a .NET team can own, with a typed API client and testable components.

**Options.**

| Option | Component tests | Notes |
|---|---|---|
| (a) Angular + TypeScript | Jasmine/Karma or Jest, a second test toolchain | Complete framework (routing, forms, DI, `HttpClient` interceptors for retry and correlation ids); strong for large teams; most opinionated and heaviest to set up. API models generated from the OpenAPI document, with a drift check in CI |
| (b) React + TypeScript | Jest + Testing Library | Smallest runtime and largest hiring pool; routing, forms and data fetching are separate library choices. API models generated from OpenAPI, same drift risk |
| (c) **Blazor WebAssembly** | bUnit, in the same `dotnet test` run as the backend | Shares the API's C# `Contracts` project, so no generation step; larger first download; smaller hiring pool |
| (d) Blazor Server | bUnit | Client state lives in a server circuit (see below) |

**Decision.** Blazor Web App with global **Interactive WebAssembly** render mode, prerendering off, MudBlazor components.

**Reasoning.**
- One language and one set of DTOs. `IPLMerchandise.Contracts` is referenced by both the API and the client, so a renamed field breaks the build, not production. With React I would need a code generator and a drift check in CI.
- The client keeps state that matters for correctness (idempotency keys, the pending order id) in the browser. WebAssembly keeps that state on the device; Blazor Server would hold it in a server circuit that dies on reconnect.
- Prerendering is off because every meaningful page needs the user's token, which only the browser has.
- bUnit gives fast component tests in the same test runner as the backend.
- *Why not Angular.* It fits the problem well: its DI, interceptors and reactive forms map closely to what the client needs (retry handler, correlation ids, validated checkout form). But it adds a second language, a generated client that must be kept in step with the API, and a second build and test toolchain (npm, Karma/Jest) in CI and Docker. For a team that owns a C# backend, those costs buy nothing the brief asks for.
- *Why not React.* Same contract-generation cost as Angular, plus choosing and wiring a router, form library and data-fetching layer. Its advantages (bundle size, hiring pool) matter less for a store whose risks are in checkout correctness, not first paint.

**Visual design.** The store follows the supplied mockup's layout, colours and components, but uses original vector illustrations in team colours instead of IPL/team logos and branded product photos, which are trademarks we hold no licence for. Admins can upload real product photos (ADR-11); a product without one, or whose photo fails to load, shows the illustration.

**Trade-offs.** Larger first download (~2–3 MB compressed runtime) than an Angular or React bundle; smaller hiring pool than either; MudBlazor is a third-party dependency (chosen over hand-written CSS for accessible inputs, labels and focus handling out of the box).

**Consequences.** The web host (`IPLMerchandise.Web`) is a thin static host that also serves `/client-config` so the API URL is runtime configuration, not baked into the build. The host must not try to authorise pages itself (`.AllowAnonymous()` on the page endpoints); the API is the security boundary.

---

## ADR-2: Clean Architecture, and what was deliberately left out

**Context.** The brief asks for domain/application/infrastructure layers, DI and a testable design, and to justify patterns used and avoided.

**Decision.** Five production projects with dependencies pointing inwards:

| Project | Contains | Depends on |
|---|---|---|
| `Domain` | Entities (`Product`, `Cart`, `Order`), invariants, domain errors | nothing |
| `Contracts` | v1 wire DTOs and problem codes | nothing |
| `Application` | Use cases (`CheckoutService`, `CartService`…), repository ports, paging tokens, telemetry | Domain, Contracts |
| `Infrastructure` | Cosmos repositories, DB initialiser, seeding, password hashing, blob photo store | Application |
| `API` | Controllers, SignalR hub, middleware, auth, rate limiting, problem details | Application, Infrastructure (composition root only) |

**Patterns used, and why.**
- **Rich domain model.** Rules live where the data lives: `Cart.AddItem` enforces 1–10 per line, the limited-edition cap and stock; `Product.DeductStock` makes negative stock impossible; every price/stock mutation returns its audit entry, so an audit can't be forgotten.
- **Repository ports owned by Application.** Not generic `IRepository<T>`: each interface exposes the exact atomic operation the use case needs (`ICheckoutRepository.CommitAsync`). That makes the transaction boundary explicit. Use cases are tested against the real database (integration tests), not against a fake that would have to imitate Cosmos's concurrency behaviour.
- **Result of a use case is a Contracts DTO.** Controllers are 1–3 lines.
- **One exception-to-HTTP mapping** (`ApplicationExceptionHandler`), not try/catch in controllers.

**Patterns avoided, and why.**
- **MediatR / CQRS pipeline.** Seven use cases don't need a dispatcher; direct service calls are easier to step through in an interview and in production debugging.
- **Generic repository / Unit of Work.** Cosmos has no cross-partition transaction, so a UoW abstraction would *promise* something the database can't do.
- **AutoMapper.** ~60 lines of explicit mapping in `Mapping.cs`; enum mapping is by name and covered by a test.
- **FluentValidation.** DataAnnotations on the contracts give the same rules to the API (automatic 400) and to Blazor forms (`DataAnnotationsValidator`); business rules stay in the domain.
- **Microservices.** One deployable API. The domain is small; the hard parts are consistency and idempotency, which distribution would make harder.

**Consequence.** The Application project references `Microsoft.AspNetCore.App` only for `ILogger`/`TimeProvider` abstractions; it has no HTTP or Cosmos types. `Contracts` duplicates the error-code list; `ContractConsistencyTests` keeps the two in sync.

---

## ADR-3: Cosmos DB data model and partition keys

**Context.** The most important requirement is all-or-nothing stock deduction across every line of an order, under concurrency. Cosmos transactions (`TransactionalBatch`) only work **within one logical partition** and are limited to **100 operations / 2 MB**.

**Problem.** Choose partition keys that make the critical transaction possible, keep common queries single-partition, and avoid hot partitions where it matters.

**Options considered for products.**

| Option | Atomic multi-product checkout? | Notes |
|---|---|---|
| `/id` (one partition per product) | ❌ needs a saga with compensation | Perfect distribution; correctness becomes much harder |
| `/franchise` | ❌ for mixed-franchise carts | Natural for multi-tenancy (ADR-10) |
| **`/storeId` (one value for the single store)** | ✅ one batch | All catalogue writes share one logical partition |

**Decision.**

| Container | Partition key | Documents | Why |
|---|---|---|---|
| `catalog` | `/storeId` (`"main"`) | products, price/stock audit entries, checkout commit records | Everything checkout must change atomically lives together → one `TransactionalBatch`. Product listing/search is a single-partition query. |
| `carts` | `/customerId` | the cart (`id = "cart"`) + idempotency records (TTL 24 h) | Cart and its idempotency record saved in one batch. Point reads only. |
| `orders` | `/customerId` | orders | Order history ("my orders, newest first, date range") is a single-partition query; customers spread load evenly. |
| `users` | `/id` (SHA-256 of normalised email) | accounts | Login is a 1 RU point read; email uniqueness enforced by id; no emails in ids or URLs. |

**Indexing (cost control).** Indexing is **opt-in**: each container excludes `/*` and includes only the paths we filter or sort on, plus the composite indexes the `ORDER BY` clauses need (`createdAtUtc, id`, `price, id` and `searchName, id` ascending for products; `changedAtUtc, id` descending for audit; `createdAtUtc, id` descending for orders). Descriptions, cart lines and order snapshots are never indexed, so writes are cheaper. `carts` and `users` index nothing (point reads only).

**RU estimates** (rule-of-thumb figures for ~1 KB documents with opt-in indexing; to be confirmed with real diagnostics on an Azure account, the emulator does not report realistic charges):

| Operation | Approx. RU |
|---|---|
| Product / cart / order / user point read | 1 |
| Product list page (20 items, single partition, composite index) | 3–6 + 1 for `COUNT` on the first page only |
| Name search (`CONTAINS` on `searchName`) | scans the store partition: fine for hundreds/thousands of products, see Limitations |
| Cart change (batch: replace cart + create idempotency record) | ~12–20 |
| Checkout with *n* lines (batch: 1 commit + *n* product replaces + *n* audit creates) | ~10 + 15·*n* |
| Order history page | 3–6 (projection of 5 fields) |

**Hot partition, stated plainly.** Every stock change in the store goes to one logical partition, which lives on one physical partition: ceiling **10,000 RU/s** and **20 GB**. At ~55 RU for a 3-line checkout that is roughly **150–180 checkouts/second** before throttling, plus contention retries on popular SKUs. For one store of official merchandise that is ample; a limited-edition drop is the stress case (see [10× scale](#scaling-to-10)). Growth of the partition is bounded: audit entries are small, and checkout commit records expire 30 days after completion (TTL).

**Cross-partition queries.** None on any customer path. The only fan-out would be an admin "all orders" report, which is deliberately not built against the operational store (see Next steps: change feed → analytics).

**Consequences.** Checkout is a single atomic write: no saga, no compensation, no "deducted but order missing" state, except the narrow window ADR-6 handles explicitly. Moving to per-franchise partitions is the natural evolution and is what multi-tenancy wants anyway (ADR-10).

---

## ADR-4: Optimistic concurrency and consistency level

**Context.** Admins edit prices and stock; checkouts deduct stock; two admins or an admin and a buyer can race.

**Decision.**
- Every product carries its Cosmos `_etag`, exposed as the HTTP `ETag` header and `eTag` field.
- Admin updates **require** `If-Match` (missing → **428**). The service checks it against the current version (fast fail) and the repository writes with `IfMatchEtag` (the real, atomic check). A mismatch → **412** with the current ETag in the body, and nothing is written. The UI shows "someone else changed this product, reload".
- Checkout reads products, validates, then writes each product with `IfMatchEtag` = the version it validated. Any concurrent change fails the whole batch; the service re-reads, re-validates and retries (up to 5 attempts, exponential backoff with full jitter). If stock ran out meanwhile, the retry produces the correct "out of stock" answer.
- Cart saves use the cart's ETag too (two browser tabs).

**Why ETag-guarded replace rather than a `Patch` with `Increment(-n)` + filter predicate.** A conditional patch (`FROM c WHERE c.stockQuantity >= 2`) would avoid conflicts between buyers of *different* products, but it can't also guarantee the **price** the customer accepted is still the price at commit time, and it would make the audit entry's "old value" a guess. The ETag approach gives one rule for every write: "only if nothing changed since I looked". The cost is extra retries under contention; the integration test log shows losers retrying once and then being correctly rejected.

**Consistency level: Session (account default).** Correctness never depends on reading the latest value. It depends on **conditional writes**, which Cosmos evaluates against the primary copy regardless of read consistency. A stale read just causes a 412/conflict and a retry. Strong consistency would double read RU cost, add latency, and is unavailable with multi-region writes, for no correctness gain here. Where a stale read could matter (reading the winner's record right after a duplicate-key conflict), the code returns a retryable 409 instead of a 500.

**Consequence.** No pessimistic locks, no lease documents, nothing to clean up after a crash.

---

## ADR-5: Idempotency

**Context.** Double-clicks, browser retries, mobile networks: the same intent can arrive more than once. "A repeated ID is applied once and returns the original result."

**Decision.**

*Cart changes.* Every mutation requires an `Idempotency-Key` header (8–64 URL-safe chars; the client sends a GUID).
1. If a record for `(customer, key)` exists: same request (SHA-256 of operation + product + quantity) → return the stored response with `Idempotent-Replay: true`; different request → **409 `idempotency-key-reused`**.
2. Otherwise apply the change and save **the cart and the idempotency record in one transactional batch** (same `/customerId` partition). The record is created *first* in the batch, so if two identical requests race, the loser's batch fails on a 409 for the record, and it replays the winner's result.
3. Records expire after 24 h (TTL).
4. Rejected requests (out of stock, quantity 11) change nothing and are **not** recorded: a retry re-evaluates against current stock, which is what a user expects after a restock.

*Checkout.* The client-generated **order id is the idempotency key**. Three layers make a second order impossible:
1. Order already exists in the customer's partition → return it (**200**, `Idempotent-Replay: true`; the first call returns **201**).
2. A checkout commit record with that id exists (stock deducted, order write interrupted) → finish the job and return it.
3. The commit record is created in the same batch as the stock deduction with `id = checkout:{orderId}`, so a racing duplicate fails atomically with a 409 and nothing is deducted twice. The integration test fires 5 simultaneous checkouts with the same id: one 201, stock deducted once, one order.

*Client side.* `CartState` creates one key per user action and reuses it for automatic retries (`RetryHandler`) and the Retry button; it refuses a second action while one is in flight. `CheckoutSession` stores the pending order id in `sessionStorage`, so even a page reload after a lost response retries with the same id. The id is discarded only after success or a definitive rejection.

**Alternatives rejected.**
- *Server-side de-duplication by content* (e.g. "same product added within 5 s"): guesses intent, and wrongly merges two deliberate clicks.
- *Server-issued request ids:* need an extra round trip, and a lost response still leaves the client unsure whether the change was applied.
- *Idempotency records in a separate container:* can't be saved in the same transaction as the cart, so a crash between the two writes would apply a change twice or record one that never happened.

*Transport.* `RetryHandler` retries only GETs and requests explicitly marked safe (those with an idempotency key or order id), only on network errors / 502 / 503 / 504, with exponential backoff and jitter. 400/409/412 are never retried: repeating a business "no" cannot change it.

---

## ADR-6: Checkout atomicity

**Problem.** "If any line cannot be fulfilled, reject the whole order, deduct no stock, identify the failing lines, and keep the cart." Plus: never oversell, never duplicate, and the order must record the price at the time of purchase.

**Decision: the flow.**

```text
PlaceOrder(customer, orderId, expectedTotal)
 1. order exists?               → return it (replay)
 2. commit record exists?       → complete it (crash recovery), return it
 3. loop (max 5):
    a. read cart + products (one single-partition query)
    b. any line unavailable / short of stock → 409 checkout-rejected + failingLines   (nothing written)
    c. any price changed since added        → 409 cart-requires-review               (nothing written)
    d. build Order with name/price snapshots; total ≠ expectedTotal → 409 total-mismatch
    e. TransactionalBatch in catalog/"main":
         create  checkout:{orderId}  (status Pending, full order snapshot)
         replace product[i]          IfMatch = etag read in (a)   — stock − qty
         create  audit[i]            reason = Order, orderId
       → Committed: go to 4.   AlreadyCommitted: replay.   Conflict: backoff + jitter, retry.
 4. CompleteAsync: create order in orders/{customerId} (idempotent) → remove bought units from cart (idempotent per order)
                   → mark commit Completed (TTL 30 days)
```

**Why it's correct.**
- *No oversell:* stock only changes in step 3e, and only if every product is still exactly the version validated in 3b.
- *All or nothing:* 3e is one Cosmos transaction; failure of any op rolls back all.
- *Failing lines:* computed in 3b before any write, so a rejected order deducts nothing and the cart is untouched.
- *Price snapshot:* the order is built from the same product versions the batch is conditioned on; a price change between read and write fails the batch.

**The one non-atomic step, handled explicitly.** The order document lives in another container (partition `/customerId`), so writing it can't be part of the stock transaction. The window between 3e and 4 is covered three ways: the commit record holds the full order; any retry with the same order id finishes the job (step 2); and `PendingCheckoutRecoveryWorker` completes commits left `Pending` for more than a minute (customers who never retry). Once the order exists, failures in the tidy-up steps are logged and left to recovery, so the customer is never told "failed" for an order that was placed. Tested against the real database: `Interrupted_checkout_is_completed_by_recovery_without_deducting_twice`.

**Alternatives rejected.**
- *Saga with per-product reservations* (needed with `/id` partitioning): more moving parts, compensations, reservation expiry; justified only once one partition can't hold the throughput.
- *Stored procedure:* same single-partition limit as batches, JavaScript, harder to test.
- *Putting orders in the catalog partition:* makes the order write atomic but puts all customers' history into the hot, size-capped partition.

**Limits.** 2 operations per line + 1 ≤ 100 → the cart is capped at **20 distinct products** (`CartRules.MaxLines`), enforced when adding.

---

## ADR-7: Authentication and authorisation

**Decision.**
- Email + password accounts; passwords hashed with ASP.NET Core Identity's `PasswordHasher` (PBKDF2-HMAC-SHA512, 100k iterations, per-user salt, versioned format). No hand-rolled crypto.
- Short-lived JWT (60 min, HMAC-SHA256) issued by the API; validation checks issuer, audience, lifetime (30 s skew) and signature. Claims: `sub` (user id), `email`, `name`, `role`.
- Roles: `Customer` (cart, checkout, own orders) and `Admin` (create/edit products, price, stock, audit). Policies, not ad-hoc role strings, in controllers.
- **Ownership comes only from the token.** Customer id = `sub`; no endpoint accepts a customer id. Cart, orders and order lookups are partitioned by that id, so another customer's order is not just forbidden but unreachable (**404**, not 403, to avoid confirming it exists).
- 401/403 are problem details too (`token-expired` vs `unauthorized`), so the client can show "session expired" and sign the user out.
- Login and register are rate-limited per IP (10/min) against credential stuffing; the same generic error is returned for unknown email and wrong password.
- Secrets: JWT key and Cosmos connection string come from configuration (user-secrets / environment / Key Vault). The API refuses to start without a ≥32-byte key (`ValidateOnStart`). Only the **public, well-known emulator key** and a dev-only JWT key appear in `appsettings.Development.json` / compose. The admin account is seeded only when `AdminSeed__Email` and `AdminSeed__Password` are set.
- Delivery addresses and mobile numbers are personal data: stored only on the order document (customer's own partition, not indexed), returned only to the owner, never logged. A real deployment would add a retention period and data-subject export/delete.
- Logs never contain passwords, tokens or request bodies: HTTP logging records method, path, status and duration only; customer identifiers in logs are opaque GUIDs.

**Trade-off: token in `localStorage`.** Simple and stateless, but readable by any script on the page, so an XSS bug would leak it. Mitigations in place: no third-party scripts, Blazor encodes output by default, short token lifetime, `nosniff`/`X-Frame-Options`. The production upgrade is a backend-for-frontend with an HttpOnly, SameSite cookie, plus refresh tokens and revocation. Not built: social login, MFA, password reset, account lockout (rate limiting covers the basic risk).

---

## ADR-8: API versioning and the backwards-compatible change

**Decision.** URL segment versioning (`/api/v1/...`) via `Asp.Versioning` (maintained by Microsoft), `api-supported-versions` header on every response. URL versioning is the easiest to see in logs, caches and browser dev tools.

**The backwards-compatible change.** `ProductDto.maxQuantityPerLine` (`int?`) was added to the v1 product contract after the first version, so the client can cap its quantity picker (2 for limited editions, 10 otherwise) instead of discovering the limit through a 400.

**How existing clients are protected.**
1. *Additive only:* a new, optional, nullable field; no field removed, renamed, retyped or made required, and no new enum values in existing responses.
2. *Tolerant readers:* System.Text.Json (and the documented client guidance) ignores unknown members, so old clients deserialise the response unchanged.
3. *Semantics unchanged:* the server still enforces the limit for every client; the field is a hint, not a new rule.
4. *Graceful absence:* the Blazor client falls back to the documented default when the field is null (10, or 2 for limited editions; `State/ProductLimits.cs`), so a new client also works against an older server.
5. *A second additive change, same rules:* the product list later gained an optional `sort` parameter and accepts `type` / `franchise` more than once. Omitting `sort` keeps the original order, and a single `type=Cap` still binds exactly as before, so v1 clients see no difference. Cart lines likewise gained optional `franchiseName` and `type` fields, and checkout gained an optional `shippingAddress` and `paymentMethod` (echoed on `OrderDto`); an older client that sends neither still places an order exactly as before. Products gained an optional `isFeatured` flag (and a `featured=true` list filter); on update, an omitted flag leaves it unchanged, so an older admin client cannot clear it. The order list gained optional `orderId` / `status` filters and an `items` preview per row, and order lines an optional product `type`. Products, cart lines and order lines gained an optional `imageUrl` (null = no photo, clients show their own placeholder), set through two new admin endpoints (`PUT` / `DELETE /products/{id}/image`); new endpoints do not change existing ones.
6. *Tests that hold these promises:* `Original_v1_product_fields_keep_their_names_and_types` fails if an original v1 field is renamed, retyped or removed; `Against_an_api_that_does_not_send_the_optional_limit_the_documented_defaults_apply` covers a new client against an older server; `Featured_filter_returns_only_featured_products_and_edits_keep_the_flag` shows an omitted optional request field changes nothing.
7. *Guardrail:* a breaking change would ship as `/api/v2` side by side, with `v1` marked deprecated through `api-supported-versions` / `api-deprecated-versions` headers and a removal date.

---

## ADR-9: Pagination

**Decision.** Keyset (seek) pagination with an **opaque continuation token**; page size defaults to 20 and is **capped at 50** (larger values are clamped; `< 1` is a 400).

- Products: default ("Featured") order puts featured products first, then the rest, each group by `createdAtUtc ASC, id ASC` (`id` breaks ties). It is read as two keyset segments one after the other (`isFeatured = true`, then not featured, which also matches products stored before the flag existed); the cursor records which segment the last item was in, and a page that crosses the boundary is filled from both. That keeps every query a simple range on the existing `(createdAtUtc, id)` composite index rather than needing a mixed-direction index on a boolean. In a fresh database the walkthrough products are seeded as featured, so page 1 is still MI Jersey and CSK Cap. Optional sorts by price (both directions) and name use the same keyset technique: the cursor carries every sortable key plus the id, and each order has a composite index (a descending sort reads the ascending index in reverse).
- Orders: `createdAtUtc DESC, id DESC` (newest first), optional `from` (inclusive) / `to` (exclusive) as ISO-8601 instants.
- Token = base64url(JSON `{ f: fingerprint of the filters, c: last key seen }`). Reusing a token with different filters, or for another customer's orders, is rejected (400 `invalid-continuation-token`).
- `totalCount` is computed **only on the first page** (a `COUNT` costs RUs); the client remembers it while paging.

**Why not Cosmos's own continuation token.** It is opaque to us, can be large, is tied to the query plan and SDK version, and can't be validated against the filters. Why not `OFFSET/LIMIT`: Cosmos still reads and discards skipped rows, so page *N* costs O(N), and inserts shift pages (duplicates or gaps).

**Stability.** A product added mid-browse appears at the end, not in pages already seen (tested: `Paging_a_large_list_returns_every_item_once_in_stable_order_with_a_capped_page_size`). Timestamps are stored in a fixed-width UTC format (`yyyy-MM-ddTHH:mm:ss.fffffffZ`) because Cosmos compares strings lexicographically; System.Text.Json's default variable-length fractions would sort `…00.5Z` before `…00Z` and break paging.

**Trade-off.** No "jump to page 7". The UI offers Previous/Next by keeping a stack of tokens.

---

## ADR-10: Future multi-tenancy (design only)

**Goal.** Each franchise runs its own store: own catalogue, stock and sales reports, data isolated from other franchises. Not built.

### Data model and partition key
- Introduce `tenantId` (= franchise code) on **every** document.
- `catalog`: partition key becomes `/tenantId` (replacing `/storeId`). Each franchise's products, audit and commit records form one logical partition. Checkout atomicity stays intact *within a franchise*, and the hot partition is split ten ways for free.
- `orders` and `carts`: **hierarchical partition key** `/tenantId, /customerId`. Per-customer queries stay single-partition; per-tenant reports become a prefix query on `tenantId` without fan-out across all tenants; large tenants still spread over many physical partitions.
- `users`: global identity (one fan account across franchises) with per-tenant role assignments `{ tenantId, role }`.
- Very large or regulated tenants can move to a dedicated container/account without code changes, if the repositories resolve `Container` through an `ITenantContainerResolver`.

### Cross-franchise carts (the key trade-off)
Franchise partitioning means one transaction can no longer cover a cart with MI *and* CSK items. Options:
1. **One cart and order per franchise store** (recommended). It matches "each franchise runs its own store", keeps checkout atomic, and gives each franchise clean sales and refund ownership. UI: the fan sees one basket per store.
2. A marketplace checkout across stores: a saga that commits per franchise partition with compensation (refund/restock) if a later partition fails. More complex, and needs payment splitting. Only worth it if the business insists on one payment.

### API
- Tenant in the route: `/api/v2/stores/{tenantId}/products`, `/stores/{tenantId}/orders`. Explicit, cacheable and visible in logs. (Sub-domains `mi.store.example` can map onto the same route at the gateway.)
- A `TenantContext` resolved once per request by middleware; repositories *require* it, so a query without a tenant cannot compile. A new major version (`v2`), because URLs change; `v1` maps to a default tenant during migration.

### Authorisation
- Roles become tenant-scoped claims: `role=FranchiseAdmin, tenant=MI`; a `PlatformAdmin` for cross-tenant operations.
- Policy `TenantAdmin`: the route `tenantId` must equal a tenant in the user's claims; checked in one authorisation handler, not in controllers.
- Defence in depth: the partition key itself scopes every query, and tests assert that an MI admin gets 404/403 on CSK resources.

### Sales reports
- Per-tenant, built from the **change feed** of `orders` into a reporting store (Synapse Link / analytical store, or a per-tenant daily aggregate document). Not cross-partition queries on the operational containers.

### Migration of existing data
Partition keys can't be changed in place, so:
1. Deploy code that writes `tenantId` on all new documents (derived from `franchiseCode`) while still reading the old layout.
2. Create new containers with the new keys; backfill with a change-feed processor (idempotent upserts) from the old containers, mapping `storeId: "main"` → `tenantId: product.franchiseCode`, and orders → per-franchise orders (split multi-franchise historical orders into per-tenant views, keeping the original order id as a reference).
3. Dual-read verification (counts, checksums per tenant), then switch reads, then writes, behind a feature flag; keep the old containers read-only for rollback.
4. Remove the old containers after the retention window.

### What I would do first
Add `tenantId` and the `TenantContext` plumbing (default tenant `main`) with tests that every repository call is tenant-scoped. That is a small, low-risk change that makes every later step mechanical, and it can ship before any data moves.

---

## ADR-11: Product photos in Azure Blob Storage

**Context.** Real product photos sell better than illustrations, and staff need to manage them without a deployment. The brief doesn't ask for photos; this is an addition. Uploads are a classic attack surface (script disguised as an image, huge files), so the checks matter as much as the storage.

**Decision.** Admins upload one photo per product (`PUT /api/v1/products/{id}/image`, multipart, `If-Match` required; `DELETE` removes it). The file goes to Azure Blob Storage (Azurite locally) and the product stores only its blob name and public URL.

**Why not elsewhere.**
- *Cosmos DB:* an item is limited to 2 MB, every product read would pay RUs for the image bytes, and list pages would carry megabytes of JSON.
- *The API server's disk:* breaks with two or more replicas, is lost when a container is replaced, and needs its own backup.
- *Blob Storage:* built for files, cheap, and browsers or a CDN load images directly without going through the API.

**Upload checks** (`ProductImageRules`, `ProductsController.SetImage`):
- Admin only, and conditional on the version on screen (`If-Match`), like every other product change.
- At most 2 MB, enforced by the request size limit and again on the bytes.
- JPEG, PNG or WebP only, recognised from the file's own first bytes. The file name and declared type are ignored, so a script renamed `photo.png` is rejected. SVG is excluded on purpose because it can carry script; GIF is not needed.
- The stored content type comes from the detected format. Each file is stored under a name we generate (`{productId}/{random}.{ext}`), never the uploader's file name: no path tricks, no clashes, no personal data in URLs.

**Files are never overwritten.** Each upload gets a new name, so:
- browsers and CDNs can cache a file for a year (`Cache-Control: immutable`), and a new photo shows up straight away because its URL is new;
- an order line copies the photo URL at checkout (like the name and price), so order history keeps showing what the customer bought. Replaced or removed photos therefore stay in storage. Production would add a lifecycle rule that deletes files no product or order references.

**Failure handling.**
- The file is checked before anything is stored.
- If storage is down, the API answers 503 + `Retry-After` (`dependency-unavailable`) and the product is unchanged.
- If the product changed while the file was uploading (412), the new file is deleted again. After an unclear failure (e.g. a timeout), the file is kept, because the product may have been saved pointing at it.
- Storage is deliberately not part of the readiness probe: if it is down, the shop keeps selling and shows illustrations.
- In the browser, a photo that fails to load falls back to the illustration.

**New products.** The form creates the product first, then uploads the photo with the ETag the create returned (the upload needs the product id). If the upload fails, the admin lands on the product's edit page with a message, rather than being offered "Create" again and making a duplicate.

**Local development.** Azurite runs in Docker Compose. The API reaches it as `azurite:10000`, but browsers reach it as `localhost:10000`, so `ImageStorage__PublicBaseUrl` sets the URL written into products. In development the API creates the container on first upload, with read access to individual files but no listing. Azurite runs with `--skipApiVersionCheck` so a newer storage SDK works with it. Integration tests run against Azurite in a throwaway container, as they do against the Cosmos emulator.

**Production.** A Storage account (ZRS) with the container provisioned up front (`ImageStorage__CreateContainerIfMissing=false`), access from the API through a managed identity, and Azure Front Door/CDN in front with `ImageStorage__PublicBaseUrl` pointing at it. The container can then stay private behind Front Door Private Link.

**Consequences.** A second dependency to run (Azurite locally, a Storage account in Azure) and one more secret or managed identity. The shop never depends on it to sell. Storage grows with every replaced photo until a lifecycle rule is added.

**Not done yet.**
- Re-encoding uploads, which would strip camera metadata such as GPS and neutralise malformed files. It needs an image library; for now, photos are only taken from staff.
- Resized thumbnails.
- Several photos per product.
- Malware scanning (Microsoft Defender for Storage).

---

## ADR-12: Price and stock changes reach open carts by push, not polling

**Context.** "When a price or stock level changes after a product was added, the cart flags it and the user must accept the new total before checkout." The server already reviews the cart on every read and refuses a checkout at an outdated price. But a shopper who sits on the cart or checkout page would only see the change after pressing Refresh, or when "Place order" was refused.

**Decision.** Event-driven updates over SignalR (`/hubs/product-updates`):
1. After a successful save, `ProductAdminService` (price, stock, details incl. availability, photo) and `CheckoutService` (stock taken by an order) call `IProductChangeNotifier` with the changed product ids. The port is in Application; the API implements it with SignalR.
2. The cart and checkout pages (`CartLiveUpdates`) join one SignalR group per product in the cart, and follow the cart as lines are added or removed.
3. On a push for a product they hold, they re-read the cart through the normal authenticated API. The server flags the change, and the existing banner and "Accept new total" appear, with checkout blocked. The checkout page can now accept the new total in place.
4. Catch-up when pushes may have been missed: after an automatic reconnect, and when the customer returns to the tab, the cart is re-read once.

**Alternatives.**
- *Polling every N seconds:* simple, but adds delay (up to N seconds) and costs RUs even when nothing changes. It was the first version and was replaced.
- *Pushing the new price itself:* the client would have to recompute the cart review rules and could disagree with the server. The push carries ids only; the server stays the single source of truth.
- *Cosmos change feed → SignalR:* catches every write path, including future ones, without touching the services. But it needs a lease container and a processor host, and the Linux emulator's change feed support is limited. It is the next step if more writers appear (see Consequences).

**Security.** The hub is anonymous because it carries only product ids, which are public. It never sends prices, carts or customer data; the cart is re-read with the customer's token. `Watch` accepts at most 20 ids (a cart's worth) that match the product id format. The hub has its own CORS policy, because SignalR's negotiate request uses credentials; the rest of the API keeps the stricter default.

**Failure handling.** Publishing is best effort: a failed push is logged and never fails the price change or the checkout. If the browser cannot connect, the page still works and catches up when the customer returns to the tab. In every case, the checkout itself re-validates prices and stock (ADR-6), so nobody can buy at an outdated price, push or no push.

**Consequences.**
- *Scale-out:* with several API instances, a change made on one instance only reaches shoppers connected to that instance. Production adds Azure SignalR Service (`AddSignalR().AddAzureSignalR()`), which fans out across instances and holds the WebSocket connections.
- *Coverage:* only changes made through the API are pushed. A manual edit in the database is caught by the tab-return check and at checkout.
- *Load:* one WebSocket per open cart or checkout tab; cost scales with open carts, not with time.

---

## Assumptions

| Area | Assumption |
|---|---|
| Walkthrough stock | The walkthrough starts KKR Signed Photo at 2 but says Ravi holds "the last" one and the final stock is 0 after Asha buys 1. I assume one unit sold elsewhere first; the walkthrough test sets stock to 1 before the race. Because the race is real, the test accepts either winner and asserts the exact walkthrough numbers when Asha wins. |
| Seed data | The four walkthrough products come first (stable order is creation order, so page 1 at size 2 is MI Jersey and CSK Cap, with total > 2), followed by four more across other franchises and every product type. The walkthrough mockup says "2 of 4"; with eight products it shows "2 of 8", which still meets "total count > X". |
| Prices | Stored and computed as `decimal`; JSON may show `499` instead of `499.00` (a number has no formatting); all display formatting is ₹ with two decimals and Indian digit grouping (`₹1,00,000.00`). |
| Shipping | "Good to have": flat ₹99.00, free at ₹999.00 or more. All walkthrough orders qualify for free shipping, so totals match the brief. |
| Payment | Online payment is out of scope. Checkout offers cash on delivery only; UPI, cards and net banking are shown as "coming soon" (disabled) so nothing pretends to take money. Orders are `Confirmed` when stock is committed. |
| Stock-change flag | The cart flags a stock change only when it matters: stock below the line quantity (or zero). A drop from 100 to 90 with 2 in the cart is not shown. |
| Limited editions | Signed photographs are limited editions, capped at 2 per order line (anti-scalping). |
| Product deletion | Products are disabled, not deleted, so audit trail and orders keep their references. Disabled products are hidden from shoppers (404) and flagged in carts. |
| Time zones | UTC everywhere in storage and the API. The UI defaults to IST and lets the user choose UTC or their device zone; the date filter converts the chosen calendar days to UTC instants (`to` inclusive for the user, exclusive on the wire). |
| Identity | A customer is a registered user; the seeded admin also has the Customer role so it can shop. |

## Extra scenarios beyond the brief

Tests cover the scenarios where a bug would cost money or data; the rest are handled in code and documented here.

| Scenario | Risk if ignored | Behaviour | Automated test |
|---|---|---|---|
| Crash after the stock commit, before the order is written | Stock lost, no order | Retry or background recovery completes from the commit record | `Interrupted_checkout_is_completed_by_recovery_without_deducting_twice` |
| Post-order tidy-up fails | Customer told "failed" for a placed order | Order returned; tidy-up left to recovery | – |
| Same order id reused by another customer | Data confusion | 409 `order-id-conflict` | – |
| Idempotency key reused for a different change | Wrong result replayed | 409 `idempotency-key-reused` | part of `Repeated_request_id_is_applied_once…` |
| Price changes between viewing the cart and pressing "Place order" | Charged a total never seen | `expectedTotal` must match → 409 `total-mismatch` | – |
| Product disabled after being added to a cart | Selling withdrawn stock | Line flagged `Unavailable`, checkout rejects it, accept removes it | – |
| Two admins edit the same product | Lost update | 412 for the second; UI "reload latest" | `Concurrent_updates_with_the_same_etag_only_one_succeeds` |
| Missing `If-Match` | Accidental blind overwrite | 428 | part of `Update_with_a_stale_etag_is_412…` |
| Empty cart checkout | Empty orders | 409 `cart-empty` | – |
| Invalid / tampered continuation token | Errors or data from another query | 400 `invalid-continuation-token` (token bound to filters and customer) | `Invalid_input_returns_400…` |
| Invalid date range | Confusing empty results | 400 (API) and inline error (UI) | – |
| Expired / forged token | Unauthorised access | 401 `token-expired` / `unauthorized`, UI signs out | `Expired_and_forged_tokens_are_rejected_with_401` |
| Credential stuffing | Account takeover | Per-IP auth rate limit (10/min), same error for unknown email and wrong password | – |
| Limited edition scalping | One buyer takes the run | Max 2 per line | `Limited_editions_are_capped_at_two_per_line` |
| Cart larger than a transaction | Checkout can never succeed | Max 20 distinct products | – |
| Open redirect via `returnUrl` | Phishing | Only local paths accepted | – |
| Cosmos throttled / unavailable | Generic 500s, retry storms | SDK retries 429s; then 503 + `Retry-After`; readiness probe fails | Not automated; checked manually by stopping the emulator |
| Upload of a non-image renamed `.png`, an SVG with script, or a huge file | Stored XSS, storage abuse | Format read from the file's bytes (JPEG/PNG/WebP only), 2 MB limit, our own file names and content type | `Files_that_are_not_jpeg_png_or_webp_are_rejected…`, `Files_over_2_MB_are_rejected` |
| Photo replaced after an order | Order history shows a different product | Files are never overwritten; order lines keep the URL they were bought with | `Admin_uploads_a_photo_shoppers_see_it_and_orders_keep_it_after_it_is_removed` |
| Image storage down | Whole shop down | Upload → 503 + `Retry-After`; shop keeps working, illustrations shown | – |
| Price or stock changes while the shopper has the cart or checkout open | Shopper only finds out when "Place order" is refused | Pushed over SignalR to carts holding that product; banner and "Accept new total" appear immediately (ADR-12) | `Price_changes_and_checkouts_are_pushed_to_watchers_of_that_product_only`, `A_pushed_price_change_flags_the_cart…` |
| Caching of personal data | Data leak via shared caches | `Cache-Control: no-store` on authenticated responses | – |

## Known limitations

- **Order statuses**: only `Confirmed` exists (no fulfilment workflow), so the My Orders status filter offers All / Confirmed; Shipped / Delivered would come with a fulfilment feature (admin or carrier updates).
- **Order stats** (`/orders/summary`) project created-at and total for the customer's orders and sum in the API: one single-partition query, fine for a customer's history.
- **Facet counts** (`/products/facets`) project type and franchise for every matching product and count in the API: one small single-partition query, fine for a store catalogue; at thousands of SKUs use `GROUP BY` or a facet document maintained from the change feed. Counts reflect the search text only, not the other selected filters.
- **Name search** is `CONTAINS` on a lower-cased field: a partition scan. Fine for a store catalogue; for thousands of SKUs or fuzzy matching use Cosmos full-text search or Azure AI Search fed by the change feed.
- **Single catalogue partition**: ~150–180 checkouts/s ceiling (ADR-3). Multi-tenancy removes most of it.
- **No payment, shipping provider, returns, cancellations or stock restoration.** Cancellation would be a new batch (restock + audit reason `Cancellation`).
- **Token in localStorage** (ADR-7). No refresh tokens, revocation or password reset.
- **In-memory rate limiting** is per API instance; behind several instances use a distributed limiter (Redis) or the gateway (API Management / Front Door). Anonymous limits key on the client IP, so behind a reverse proxy `ForwardedHeaders` must be enabled with the proxy's address as trusted.
- **Emulator** (Linux vNext preview) supports the features used here, but not every indexing policy shape (opt-in indexing was chosen partly for that reason); its RU charges are not realistic. Data is not persisted across container restarts.
- **Live cart updates** (ADR-12) reach only shoppers connected to the API instance that made the change until Azure SignalR Service is added; changes made outside the API are seen on tab return or at checkout.
- **Product photos** (ADR-11) are stored as uploaded: not re-encoded, resized or scanned for malware, and replaced files are kept until a lifecycle rule is added. One photo per product.
- **No content security policy** yet: Blazor's inline import map needs a hash/nonce, which I'd add with the BFF work.
- **Observability** exposes `ActivitySource`/`Meter` hooks and JSON logs; no exporter (OpenTelemetry → Azure Monitor) is wired up.

## Scaling to 10×

| Pressure | First move | Next move |
|---|---|---|
| Catalogue write throughput (hot partition) | Multi-tenancy (`/tenantId`) splits it per franchise | For a single hot SKU drop: a reservation queue (accept intents, process sequentially per SKU) or stock "buckets" across sub-partitions with a periodic rebalance |
| Read traffic on listing/search | Output cache / CDN for anonymous catalogue pages (ETags already present) | Search index fed by the change feed |
| Checkout contention retries | Backoff with jitter is in place | Queue-based checkout for drops, so users see "you're in line" instead of 409s |
| API instances | Stateless; scale out horizontally | Distributed rate limiter, Redis for hot product cache |
| Live cart-update connections | Azure SignalR Service holds the WebSockets and fans pushes out across instances (ADR-12) | Change feed → notifier, so every write path pushes |
| Product photos | Front Door/CDN in front of Blob Storage; files are immutable, so cached for a year (ADR-11) | Resized thumbnails per card size |
| RU cost | Autoscale throughput at database level; opt-in indexing | Measure with diagnostics; move audit/commit history to a cheaper analytical store |

## What I would do next

1. BFF with HttpOnly cookie auth, refresh tokens, CSP.
2. OpenTelemetry exporter (traces + the existing metrics) and dashboards: checkout success rate, conflict retries, 412/409 rates, RU per operation.
3. Change feed processor → order/sales analytics per franchise (also the first step towards ADR-10 reports).
4. Payment integration with an outbox, so "order placed" and "payment captured" stay consistent.
5. Load test (k6) of a limited-edition drop to measure the real conflict and RU profile on an Azure account.
6. Order cancellation with stock restoration and audit.
