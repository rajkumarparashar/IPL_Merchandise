# IPL Franchise Merchandise Store

An e-commerce store for official IPL franchise merchandise (jerseys, caps, flags, limited-edition signed photographs) built for the Senior Software Engineer assessment.

**Stack:** ASP.NET Core 10 Web API · Azure Cosmos DB (SDK v3) · Azure Blob Storage (product photos) · Blazor WebAssembly + MudBlazor · xUnit · bUnit · Docker Compose · GitHub Actions.

**What to read first:** this README for running it, [docs/DESIGN.md](docs/DESIGN.md) for the decisions and trade-offs, [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md) for diagrams, [docs/TRACEABILITY.md](docs/TRACEABILITY.md) for requirement → code → test mapping, [docs/AI-USAGE.md](docs/AI-USAGE.md) for how AI was used.

---

## Contents

- [Quick start (one command)](#quick-start-one-command)
- [What it does](#what-it-does)
- [Architecture overview](#architecture-overview)
- [Solution structure](#solution-structure)
- [API reference](#api-reference)
- [Authentication and authorisation](#authentication-and-authorisation)
- [Data: Cosmos DB containers and partition keys](#data-cosmos-db-containers-and-partition-keys)
- [Concurrency, idempotency and checkout](#concurrency-idempotency-and-checkout)
- [Errors, logging and observability](#errors-logging-and-observability)
- [Local development](#local-development)
- [Running the tests](#running-the-tests)
- [Configuration and secrets](#configuration-and-secrets)
- [Production build and deployment](#production-build-and-deployment)
- [Assumptions and limitations](#assumptions-and-limitations)
- [Troubleshooting](#troubleshooting)

---

## Quick start (one command)

Prerequisite: Docker (Desktop or Engine) with Compose v2. Works on x64 and Apple Silicon.

```bash
docker compose up --build
```

| What | URL |
|---|---|
| Store (Blazor app) | http://localhost:5300 |
| API + Swagger UI | http://localhost:5200/swagger |
| API health | http://localhost:5200/health/ready |
| Cosmos DB emulator Data Explorer | http://localhost:1234 |
| Azurite (Blob Storage emulator, product photos) | http://localhost:10000 |

On first start the API creates the database and containers and seeds eight products: the walkthrough catalogue (MI Jersey ₹1,999.00 × 50, CSK Cap ₹499.00 × 100, RCB Flag ₹349.00 × 200, KKR Signed Photo ₹4,999.00 × 2) plus SRH Orange Army Jersey ₹1,799.00 × 40, RR Pink Cap ₹549.00 × 80, DC Supporters' Flag ₹399.00 × 150 and GT Signed Photo ₹5,499.00 × 3. It also seeds a **development** admin:

| Role | Email | Password |
|---|---|---|
| Admin (also a customer) | `admin@iplstore.local` | `Admin@12345` |
| Customer | register any account at http://localhost:5300/register | at least 8 characters, a letter and a digit |

These credentials exist only because `docker-compose.yml` sets `AdminSeed__*` for development. Override them in a `.env` file (see `.env.example`). Nothing is seeded when those variables are absent.

Stop with `Ctrl+C`, or `docker compose down`. The Cosmos emulator keeps data in memory, so a restart starts from the seed again. Uploaded photos live in the Azurite container and are removed by `docker compose down`.

---

## What it does

### Customer
- **Home** is a landing page: hero banner, "Shop by category" and "Shop by team" tiles (with product counts, each opening the Shop pre-filtered), featured products (chosen by admins with a "Featured" flag, and marked with a gold "Featured" ribbon on product cards everywhere) and store benefits; the teams sit on one scrollable row with previous/next buttons. **Shop** is the full catalogue. Search from the header or the filter bar (case-insensitive, partial name).
- Filter by product type and franchise from the dropdowns or the sidebar checkboxes (several values allowed; counts shown per option), sort by Featured (featured products first), price or name, Previous/Next paging (2, 10, 20 or 50 per page).
- Add to cart straight from a product card (one unit, with a confirmation and a "View cart" shortcut), or open the card for details. The add button is hidden when nothing more can be added (out of stock, or the cart already holds the maximum).
- Product pictures: the product's photo when an admin has uploaded one, otherwise an original illustration in the team's colours (also used if a photo fails to load).
- Product details page: breadcrumb, picture gallery (thumbnails, previous/next, zoom), availability, quantity stepper, copy-link sharing, delivery and checkout highlights, description.
- Site footer with shop shortcuts (e.g. `/shop?type=Cap`), account links and delivery/payment information.
- Product details with a quantity picker capped at the line limit (10, or 2 for limited editions) and available stock. The limit is shown wherever it applies (card, details, cart) with its reason, so "Only 3 left" next to a limit of 2 per order is not a surprise.
- Cart: product thumbnails, stock status, +/- quantity steppers, remove, clear the whole cart (with a confirmation step), an optional "Refresh Cart" (changes arrive by themselves, see below), and an order summary (subtotal, taxes included, shipping, total). Each change is idempotent. If a price or stock level changes after adding, the line is flagged and checkout is blocked until the customer accepts the new total. This is pushed live: an open cart or checkout page shows the change the moment an admin edits the price or stock or another shopper buys the item, with no refresh needed (SignalR; [DESIGN.md ADR-12](docs/DESIGN.md#adr-12-price-and-stock-changes-reach-open-carts-by-push-not-polling)). Checkout lets the customer accept the new total in place.
- Checkout in steps (Shipping & Payment → Review): validated Indian delivery address (10-digit mobile, 6-digit PIN, state list), cash on delivery (online methods shown as "coming soon"), terms acceptance, then a review of address, payment and items before placing the order. The address is saved on the order and shown on the order page.
- Checkout with a client-generated order id: safe to double-click, retry or reload. If any item can't be supplied, the whole order is rejected, the failing items are listed, no stock is taken and the cart is kept.
- My Orders: totals (orders, spent in the last 30 days, total spent), search by order number (e.g. `#IPL-1A2B3C4D`), date range presets or a custom range, status filter, rows with product thumbnails, status and View Details; newest first, times in IST by default (or UTC / device time). Order details page: order number (copyable), date, status, item list with thumbnails and the prices charged, order summary, delivery address and payment.

### Admin
- Product list including disabled products; create products.
- Product photos: choose a photo when creating a product, or replace or remove it while editing, with a preview before saving. JPEG, PNG or WebP, up to 2 MB, checked from the file's content. Photos are stored in Azure Blob Storage (Azurite locally); see [DESIGN.md ADR-11](docs/DESIGN.md#adr-11-product-photos-in-azure-blob-storage).
- Edit details, price and stock on one form with a single **Save changes** and **Cancel**. Save sends only the parts you changed (details, price, stock, photo), asks for a reason only when price or stock changed (recorded in the history), and chains each update on the version returned by the previous one. If one part is refused (e.g. someone else changed the product), the page says exactly which parts were saved and which were not; saving again resends only what is left. Cancel discards unsaved edits. Each save is conditional on the version on screen; if someone else changed it first, the admin sees a conflict and reloads instead of overwriting.
- Price and stock audit history: who, when, old → new, reason (order or manual adjustment).

### Walkthrough
The assessment's walkthrough is automated end to end in `tests/IPLMerchandise.IntegrationTests/WalkthroughTests.cs` (see the stock assumption in [DESIGN.md](docs/DESIGN.md#assumptions)).

---

## Architecture overview

```text
Browser (Blazor WASM) ──HTTPS, JWT──► API (ASP.NET Core) ──► Cosmos DB
   ▲    ▲     ▲                          │  │
   │    │     └── WebSocket (SignalR) ───┘  ├──► Blob Storage (product photos; Azurite locally)
   │    │         "products changed"        └── background: pending-checkout recovery
   │    └── product photos ◄── Blob Storage / CDN
   └── static files ── Web host
```

Clean Architecture: `Domain` (rules) ← `Application` (use cases, ports) ← `Infrastructure` (Cosmos) ← `API` (HTTP). The client shares only the `Contracts` project (wire DTOs) with the server. Details and diagrams: [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md).

---

## Solution structure

```text
IPLMerchandiseStore.sln
├── src/
│   ├── IPLMerchandise.Domain/          Entities (Product, Cart, Order, User), invariants, domain errors. No dependencies.
│   ├── IPLMerchandise.Contracts/       v1 request/response DTOs and problem codes, shared with the Blazor client.
│   ├── IPLMerchandise.Application/     Use cases (Catalog, ProductAdmin, Cart, Checkout, Orders, Auth), repository ports, paging tokens, telemetry.
│   ├── IPLMerchandise.Infrastructure/  Cosmos repositories, DB initialiser (partition keys, indexing), seeding, password hashing, blob photo store.
│   ├── IPLMerchandise.API/             Controllers, SignalR hub (live product changes), JWT auth, problem details, rate limiting, correlation ids, health checks, Dockerfile.
│   ├── IPLMerchandise.Web/             Thin host for the WebAssembly app; serves /client-config. Dockerfile.
│   └── IPLMerchandise.Web.Client/      Blazor WASM: pages, components, typed API clients, cart/checkout state, live cart updates, formatting.
├── tests/
│   ├── IPLMerchandise.UnitTests/       Domain rules at their boundaries. No infrastructure.
│   ├── IPLMerchandise.IntegrationTests/ Whole API via WebApplicationFactory against the Cosmos emulator and Azurite, incl. concurrency.
│   └── IPLMerchandise.Web.Tests/       bUnit component tests, API client contract tests.
├── docs/                               DESIGN.md, ARCHITECTURE.md, TRACEABILITY.md, AI-USAGE.md
├── scripts/provision-cosmos.sh         Azure CLI script that creates the production database and containers (and, optionally, photo storage).
├── docker-compose.yml                  Cosmos emulator + Azurite + API + web, one command.
└── .github/workflows/ci.yml            Build, all three test suites (emulators in CI), image build.
```

---

## API reference

Base path `/api/v1` (URL-segment versioning; every response carries `api-supported-versions`). The backwards-compatible change is the optional `maxQuantityPerLine` field on products; how existing clients are protected, and the tests that hold it, are in [DESIGN.md ADR-8](docs/DESIGN.md#adr-8-api-versioning-and-the-backwards-compatible-change). All errors are `application/problem+json` with a machine-readable `code`, `correlationId` and `traceId`. Interactive docs: `/swagger` (development, or `Api__EnableSwagger=true`); OpenAPI document: `/openapi/v1.json`.

| Method | Path | Auth | Purpose | Notable responses |
|---|---|---|---|---|
| POST | `/auth/register` | – | Create customer account, returns token | 201, 400, 409 `email-in-use`, 429 |
| POST | `/auth/login` | – | Sign in, returns token | 200, 401 `invalid-credentials`, 429 |
| GET | `/franchises` | – | Franchise codes and names | 200 |
| GET | `/products?search=&type=&franchise=&sort=&pageSize=&continuationToken=` | – | List/search, paged. `type` and `franchise` may repeat (OR within, AND between); `sort` = `Featured` (default: featured products first, then the rest, each oldest first), `PriceLowToHigh`, `PriceHighToLow`, `NameAToZ`; `featured=true` returns only products flagged as featured | 200, 400, 429 |
| GET | `/products/facets?search=` | – | Product counts per type and franchise (for filter checkboxes) | 200, 400, 429 |
| GET | `/products/{id}` | – | Details; `ETag` header | 200, 404 |
| POST | `/products` | Admin | Create | 201 + `ETag`, 400 |
| PUT | `/products/{id}` | Admin | Name, type, franchise, description, active. **`If-Match` required** | 200, 400, 404, 412, 428 |
| PUT | `/products/{id}/price` | Admin | `{price, reason}`. **`If-Match`** | 200, 400, 412, 428 |
| PUT | `/products/{id}/stock` | Admin | `{stockQuantity, reason}`. **`If-Match`** | 200, 400, 412, 428 |
| PUT | `/products/{id}/image` | Admin | Set or replace the photo: `multipart/form-data` with one `file` (JPEG/PNG/WebP, ≤ 2 MB, checked from content). **`If-Match`**. Returns the product with `imageUrl` | 200, 400, 412, 428, 503 |
| DELETE | `/products/{id}/image` | Admin | Remove the photo (illustration shown again). **`If-Match`** | 200, 412, 428 |
| WS | `/hubs/product-updates` | – | SignalR. `Watch(productIds[])` (≤ 20); server sends `ProductsChanged(productIds[])` after price, stock or availability changes. Ids only, never prices | – |
| GET | `/products/{id}/audit` | Admin | Price/stock history, newest first, paged | 200, 404 |
| GET | `/cart` | Customer | Cart with change flags and totals | 200 |
| POST | `/cart/items` | Customer | `{productId, quantity}`. **`Idempotency-Key` required** | 200, 400, 404, 409 |
| PUT | `/cart/items/{productId}` | Customer | `{quantity}`. **`Idempotency-Key`** | 200, 400, 404, 409 |
| DELETE | `/cart/items/{productId}` | Customer | Remove line. **`Idempotency-Key`** | 200, 404 |
| DELETE | `/cart` | Customer | Clear the cart (remove every item). **`Idempotency-Key`** | 200 |
| POST | `/cart/accept-changes` | Customer | Accept new prices / reduced stock. **`Idempotency-Key`** | 200 |
| POST | `/checkout` | Customer | `{orderId, expectedTotal, shippingAddress?, paymentMethod?}` (address and payment are optional for older clients; only `CashOnDelivery` exists) | 201 new, 200 replay, 400, 409 (`checkout-rejected` + `failingLines`, `cart-requires-review`, `total-mismatch`, `cart-empty`), 429 |
| GET | `/orders?from=&to=&orderId=&status=&pageSize=&continuationToken=` | Customer | Own orders, newest first; `from` inclusive, `to` exclusive (ISO-8601); `orderId` matches the start of the order id (the `#IPL-` display prefix is ignored); each row includes item previews | 200, 400 |
| GET | `/orders/summary` | Customer | Order count, total spent, spent in the last 30 days | 200 |
| GET | `/orders/{id}` | Customer | One own order (another customer's → 404) | 200, 404 |

Other endpoints: `GET /health/live` (process up), `GET /health/ready` (Cosmos reachable).

Headers: `Idempotency-Key` (request, cart changes), `Idempotent-Replay: true` (response, replayed result), `If-Match` / `ETag` (product versions), `X-Correlation-ID` (both ways), `Retry-After` (429/503), `api-supported-versions`.

Example:

```bash
TOKEN=$(curl -s -X POST localhost:5200/api/v1/auth/login -H 'Content-Type: application/json' \
  -d '{"email":"admin@iplstore.local","password":"Admin@12345"}' | jq -r .accessToken)

curl -s -X POST localhost:5200/api/v1/cart/items -H "Authorization: Bearer $TOKEN" \
  -H 'Content-Type: application/json' -H "Idempotency-Key: $(uuidgen)" \
  -d '{"productId":"csk-cap","quantity":3}' | jq .total
```

---

## Authentication and authorisation

- Register/login return a JWT (60 min, HMAC-SHA256; issuer, audience, signature and lifetime validated). Passwords are hashed with ASP.NET Core Identity's PBKDF2 hasher.
- Roles: **Customer** (cart, checkout, own orders) and **Admin** (product management, audit). Admin pages and endpoints use the `Admin` policy.
- The customer id always comes from the token's `sub` claim; no endpoint accepts it from the client.
- Login/register are rate-limited per IP. Unknown email and wrong password return the same 401.
- Trade-offs (token in `localStorage`, no refresh tokens) and the production upgrade path: [DESIGN.md ADR-7](docs/DESIGN.md#adr-7-authentication-and-authorisation).

---

## Data: Cosmos DB containers and partition keys

| Container | Partition key | Holds | Why |
|---|---|---|---|
| `catalog` | `/storeId` (`"main"`) | products, price/stock audit, checkout commit records | Stock deduction for every line + audit + commit in **one transactional batch** |
| `carts` | `/customerId` | cart + idempotency records (TTL 24 h) | Cart and its idempotency record saved atomically; point reads |
| `orders` | `/customerId` | orders | Order history is a single-partition query |
| `users` | `/id` (SHA-256 of email) | accounts | Login = point read; email uniqueness via id |

Created by `CosmosDbInitializer` at startup in development (`Cosmos__InitializeOnStartup=true`), which also brings an existing container's indexing policy in line with the code when it changes (system paths such as `_ts`/`_etag` are ignored in the comparison), and by `scripts/provision-cosmos.sh` in Azure. Indexing is opt-in (only queried paths and the composite indexes for `ORDER BY`). Hot-partition limits, RU estimates, consistency level and alternatives: [DESIGN.md ADR-3/ADR-4](docs/DESIGN.md#adr-3-cosmos-db-data-model-and-partition-keys).

Seed data is idempotent (`DataSeeder`): existing products and users are never overwritten.

---

## Concurrency, idempotency and checkout

- **Optimistic concurrency:** products carry their Cosmos ETag. Admin writes need `If-Match` (412 on mismatch, 428 if missing). Checkout writes every product conditionally on the version it validated and retries with backoff and jitter on conflict.
- **Idempotency:** cart changes need an `Idempotency-Key`; the key's record is written in the same transaction as the cart, so a repeat replays the first result. Checkout uses the client's order id; the commit record with that id is written in the same transaction as the stock deduction, so stock can't be taken twice.
- **Checkout:** validate everything first (failing lines → 409, nothing written) → one transaction (commit record + stock + audit) → write the order → tidy the cart. If the process dies between the transaction and the order write, a retry or the background recovery job completes it.

Full flow and reasoning: [DESIGN.md ADR-5/ADR-6](docs/DESIGN.md#adr-6-checkout-atomicity), sequence diagrams in [ARCHITECTURE.md](docs/ARCHITECTURE.md#6-checkout-flow).

---

## Errors, logging and observability

- **Errors:** one exception handler maps domain and application errors to RFC 9457 problem details (400/401/403/404/409/412/428/429/503). Each carries `code` (stable, for clients), `correlationId` and `traceId`. Database throttling or outage → 503 with `Retry-After`.
- **Logs:** JSON console logs (`Logging__Console__FormatterName=json`) with scopes; every log line in a request carries `CorrelationId`. Request logs contain method, path, status and duration only: no headers, bodies, tokens or passwords.
- **Correlation:** `X-Correlation-ID` accepted (if well-formed) or generated, returned on every response and shown to users on error messages as "Reference".
- **Metrics and tracing hooks:** `ActivitySource` and `Meter` named `IPLMerchandise` (`store.orders.placed`, `store.checkout.rejected`, `store.concurrency.conflicts`, `store.idempotency.replays`, `store.orders.recovered`, `store.checkout.attempts`). Watch locally with `dotnet-counters monitor -n IPLMerchandise.API --counters IPLMerchandise`; attach an OpenTelemetry exporter in production.
- **Health:** `/health/live` (no dependencies) and `/health/ready` (Cosmos, 3 s timeout).

---

## Local development

Prerequisites: .NET SDK 10.0.1xx (see `global.json`), Docker.

```bash
# 1. Cosmos DB emulator (Linux vNext image, x64 and Apple Silicon) and Azurite (product photos)
docker compose up -d cosmos azurite

# 2. API on http://localhost:5200 (creates DB, seeds data, Swagger at /swagger)
dotnet run --project src/IPLMerchandise.API --launch-profile http

# 3. Web app on http://localhost:5300 (in another terminal)
dotnet run --project src/IPLMerchandise.Web --launch-profile http
```

`appsettings.Development.json` (API) points at the emulator with its **public, well-known key**, and at Azurite (`UseDevelopmentStorage=true`), and uses a dev-only JWT key; it also seeds the dev admin. Nothing in it is a real secret.

### Cosmos DB emulator notes
- Image: `mcr.microsoft.com/cosmosdb/linux/azure-cosmos-emulator:vnext-preview`, started with `--protocol http` (no certificate to trust).
- The API uses Gateway mode with `LimitToEndpoint` for the emulator (`Cosmos__UseGatewayMode=true`).
- Data Explorer: http://localhost:1234.
- On Windows you can use the classic emulator instead; set `Cosmos__ConnectionString` to its HTTPS endpoint and trust its certificate.

---

## Running the tests

```bash
# Unit tests (domain + application). No dependencies.
dotnet test tests/IPLMerchandise.UnitTests

# Front-end component and API-client contract tests (bUnit). No dependencies.
dotnet test tests/IPLMerchandise.Web.Tests

# API + integration tests incl. concurrency and the walkthrough. Need the Cosmos emulator and Azurite:
docker compose up -d cosmos azurite
dotnet test tests/IPLMerchandise.IntegrationTests

# Everything
dotnet test
```

| Suite | Test methods | Covers |
|---|---|---|
| Unit | 19 | Domain rules at their boundaries: prices, stock of exactly 1, quantity 1/10/11, limited editions, change detection, snapshots, shipping, accepted photo formats |
| Integration | 40 | Real API + Cosmos + Azurite: concurrency, idempotency, partial failure and crash recovery, pagination, validation and problem details, auth, photo upload, live change pushes, the walkthrough |
| Front end | 62 | Rejected actions, double-submit and retry, loading/error/conflict states, accessibility, typed client contracts, photo picker and fallback, live cart updates |

### Test map (minimum scenarios from the brief)

| Scenario | Test |
|---|---|
| Stock of exactly 1; quantity 1 and 10; over the cap | `CartTests` (unit), `CartApiTests.Quantity_boundaries`, `Stock_of_exactly_one_allows_one_and_rejects_two_with_409` |
| Concurrent conflicting writes | `CheckoutApiTests.Concurrent_checkouts_for_the_last_unit_never_oversell`, `AdminApiTests.Concurrent_updates_with_the_same_etag_only_one_succeeds` |
| Duplicate / retried cart and checkout requests | `CartApiTests.Repeated_request_id…`, `Simultaneous_duplicates_are_applied_once`, `CheckoutApiTests.Retried_checkout…`, `Simultaneous_checkouts_with_the_same_order_id_create_one_order` |
| Optimistic concurrency → 412 | `AdminApiTests.Update_with_a_stale_etag_is_412_and_is_not_applied` |
| Partial failure leaves consistent state | `CheckoutApiTests.Rejected_order_deducts_nothing…`, `Interrupted_checkout_is_completed_by_recovery_without_deducting_twice` |
| Pagination and ordering on large lists | `ProductsApiTests.Paging_a_large_list…`, `OrdersApiTests.History_is_newest_first_and_paged` |
| Validation errors and problem details | `ProductsApiTests.Invalid_input_returns_400_problem_details_naming_the_field`, `Unknown_product_is_404_problem_details` |
| FE: validation and display of a rejected action | `LoginTests.Empty_submit_shows_field_errors…`, `ProductDetailsTests.Rejected_add_is_shown_as_an_announced_alert` |
| FE: double-submit and retry safety | `Double_click_on_add_sends_one_request`, `Double_click_places_one_order`, `Retry_after_a_lost_response_reuses_the_same_order_id` |
| FE: loading, error and conflict states | `CatalogueTests.Shows_a_loading_state…`, `Failure_is_announced_and_retry_recovers`, `AdminProductEditTests.Stale_data_conflict_is_shown…` |
| FE: accessibility | `LoginTests.Every_input_has_a_label`, `CartPageTests.Every_button_has_an_accessible_name…` |
| FE: typed API client contracts | `ApiClientContractTests` |
| The assessment walkthrough | `WalkthroughTests.Assessment_walkthrough` |

Each integration fixture creates its own throwaway database and blob container and deletes them afterwards. To use another emulator or account: `COSMOS_CONNECTION_STRING="AccountEndpoint=…;AccountKey=…;"` and/or `AZURITE_CONNECTION_STRING="…"` before `dotnet test tests/IPLMerchandise.IntegrationTests`.

---

## Configuration and secrets

Settings use standard ASP.NET Core configuration: `appsettings.json` → `appsettings.{Environment}.json` → user-secrets (Development) → environment variables (`Section__Key`).

| Setting (env var) | Default | Notes |
|---|---|---|
| `Cosmos__ConnectionString` | – (required) | **Secret.** User-secrets locally, Key Vault reference in Azure |
| `Cosmos__DatabaseName` | `ipl-merchandise` | |
| `Cosmos__StoreId` | `main` | Catalogue partition value |
| `Cosmos__UseGatewayMode` | `false` | `true` for the emulator |
| `Cosmos__InitializeOnStartup` / `Cosmos__SeedOnStartup` | `false` | `true` in Development / compose |
| `Cosmos__DatabaseAutoscaleMaxThroughput` | – | Shared autoscale RU/s ceiling when the API creates the DB |
| `Cosmos__RequestTimeoutSeconds`, `…MaxRetryAttemptsOnThrottling`, `…MaxRetryWaitSeconds` | 10, 5, 15 | SDK timeout and 429 retry policy |
| `ImageStorage__ConnectionString` | – (required) | **Secret.** Azure Storage connection string (`UseDevelopmentStorage=true` for local Azurite). Use a managed identity in Azure |
| `ImageStorage__ContainerName` | `product-images` | |
| `ImageStorage__PublicBaseUrl` | the container URL | Base URL browsers load photos from: a CDN, or `http://localhost:10000/devstoreaccount1/product-images` with Docker |
| `ImageStorage__CreateContainerIfMissing` | `false` | `true` in Development / compose |
| `Jwt__SigningKey` | – (required, ≥ 32 bytes) | **Secret.** The API refuses to start without it |
| `Jwt__Issuer`, `Jwt__Audience`, `Jwt__AccessTokenMinutes` | `ipl-merchandise-api`, `ipl-merchandise-web`, 60 | |
| `AdminSeed__Email`, `AdminSeed__Password` | – | Development only. Unset in production |
| `Cors__AllowedOrigins__0` | – | Public URL of the web app |
| `RateLimiting__SearchPerMinute`, `…CheckoutPerMinute`, `…AuthPerMinute` | 120, 10, 10 | Per user (or per IP when anonymous) |
| `Checkout__RecoveryIntervalSeconds` | 60 | `0` disables the recovery worker |
| `Api__EnableSwagger` | `false` | Swagger is always on in Development |
| Web host: `Client__ApiBaseUrl` | `http://localhost:5200/` | Served to the browser via `/client-config` |

User-secrets example (running the API outside Development config):

```bash
dotnet user-secrets --project src/IPLMerchandise.API set "Jwt:SigningKey" "$(openssl rand -base64 48)"
dotnet user-secrets --project src/IPLMerchandise.API set "Cosmos:ConnectionString" "AccountEndpoint=https://<account>.documents.azure.com:443/;AccountKey=<key>;"
```

---

## Production build and deployment

Build artifacts:

```bash
dotnet publish src/IPLMerchandise.API -c Release -o out/api
dotnet publish src/IPLMerchandise.Web -c Release -o out/web
# or container images
docker build -f src/IPLMerchandise.API/Dockerfile -t ipl-merch-api .
docker build -f src/IPLMerchandise.Web/Dockerfile -t ipl-merch-web .
```

Recommended Azure deployment (diagram in [ARCHITECTURE.md §9](docs/ARCHITECTURE.md#9-deployment-architecture-recommended-production)):

1. **Database:** `RESOURCE_GROUP=rg-ipl ACCOUNT=cosmos-ipl-prod ./scripts/provision-cosmos.sh`, which creates a Cosmos account (Session consistency, zone redundant, continuous backup), the database with shared autoscale throughput, and the four containers with their partition keys and indexing policies.
2. **Product photos:** a Storage account (ZRS) with a `product-images` container (add `STORAGE_ACCOUNT=<name>` to the script above), served through Front Door/CDN (`ImageStorage__PublicBaseUrl`), and a lifecycle rule for files nothing references (DESIGN ADR-11).
3. **Secrets:** put `Cosmos__ConnectionString`, `ImageStorage__ConnectionString` and `Jwt__SigningKey` in Key Vault; reference them from the hosting platform with a managed identity. No secrets in images or app settings files.
4. **Compute:** Azure Container Apps (or App Service for containers), 2+ replicas per app (with Azure SignalR Service so live cart updates reach every replica's shoppers, ADR-12), liveness `/health/live`, readiness `/health/ready`. Images run as the non-root `app` user.
5. **Edge:** Azure Front Door for TLS, WAF and caching of the static web app; HSTS is enabled outside Development.
6. **Production settings:** `ASPNETCORE_ENVIRONMENT=Production`, `Cosmos__InitializeOnStartup=false`, `Cosmos__SeedOnStartup=false`, `Cosmos__UseGatewayMode=false`, `ImageStorage__CreateContainerIfMissing=false`, no `AdminSeed__*` (create the first admin with a one-off seeded run or an ops script), `Cors__AllowedOrigins__0=https://<store domain>`, `Client__ApiBaseUrl=https://<api domain>/`.
7. **CI:** `.github/workflows/ci.yml` builds, runs all three suites (starting the Cosmos emulator and Azurite in the job) and builds the images. Add a deploy job per environment that promotes the same image.

---

## Assumptions and limitations

Highlights (full list with reasoning in [DESIGN.md](docs/DESIGN.md#assumptions)):

- The walkthrough's KKR photo stock (2) contradicts "the last one"; the test reduces it to 1 before the race.
- Payment, shipping carriers, cancellations and returns are out of scope; orders are `Confirmed` when stock is committed. Shipping is a flat ₹99.00, free from ₹999.00.
- Signed photographs are capped at 2 per order line; carts hold at most 20 distinct products (the transaction size limit).
- Name search scans the store partition: fine for a store catalogue, not for a marketplace.
- All stock writes share one logical partition: ~150–180 checkouts/s ceiling; multi-tenancy (ADR-10) splits it.
- Live cart updates reach only shoppers on the API instance that made the change until Azure SignalR Service is added; product photos are stored as uploaded (not re-encoded or resized).
- Token stored in `localStorage`; rate limiting is per instance; metrics/tracing are hooks without an exporter; the CI workflow has not been run on GitHub yet.

---

## Troubleshooting

| Symptom | Cause / fix |
|---|---|
| API logs "Cosmos not reachable … retrying" at start | The emulator takes 10–30 s to start. The API retries for a while; if it gives up, run `docker compose restart api`. |
| Products missing / API errors (404, 500) after restarting the emulator | The emulator keeps data in memory, so the database is gone. Restart the API so it recreates and seeds it: `docker compose restart api`. |
| Integration tests fail with "Integration tests need the Cosmos DB emulator…" | Start it: `docker compose up -d cosmos`, or set `COSMOS_CONNECTION_STRING`. |
| Integration tests fail with "Integration tests need Azurite…" | Start it: `docker compose up -d azurite`, or set `AZURITE_CONNECTION_STRING`. |
| An open cart doesn't update when a price changes | The browser couldn't open the WebSocket to `/hubs/product-updates` (check the API is reachable and `Cors__AllowedOrigins__0` is the web app's URL). The cart still catches up when you return to the tab, and checkout always re-checks prices. |
| Photo upload says "Image storage is unavailable" | Azurite isn't running: `docker compose up -d azurite`. The rest of the store keeps working. |
| Port already in use (8081, 1234, 10000, 5200, 5300) | Stop the other process or change the host ports in `docker-compose.yml` / `launchSettings.json`. If you change the API port, update `Client__ApiBaseUrl` and `Cors__AllowedOrigins__0`. |
| Browser shows "Connection problem" on every page | API not running, or `Client__ApiBaseUrl` / CORS origin mismatch. Check `http://localhost:5300/client-config` and the API's `Cors__AllowedOrigins__0`. |
| API won't start: "Jwt SigningKey…" validation error | Set `Jwt__SigningKey` (≥ 32 bytes). Development config provides a dev-only key. |
| 429 Too Many Requests while testing manually | Rate limits are per minute; raise `RateLimiting__*` locally or wait for `Retry-After`. |
| "Your session has expired" | Tokens last 60 minutes; sign in again. |
| Blank page in a published build | Make sure the web host is built from this repo's csproj (`RequiresAspNetWebAssets=true`, see the comment there). |
