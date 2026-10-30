# Architecture

Diagrams use Mermaid (rendered by GitHub and most IDEs). The reasoning behind each choice is in [DESIGN.md](DESIGN.md).

## 1. System context and runtime

```mermaid
flowchart LR
    user([Fan / Admin<br/>browser])
    subgraph web["Web host (ASP.NET Core) :5300"]
      static["Static files<br/>Blazor WebAssembly app"]
      cfg["/client-config<br/>(API URL per environment)"]
    end
    subgraph api["API (ASP.NET Core) :5200"]
      mw["Middleware<br/>correlation id · exception → problem details<br/>auth (JWT) · rate limiting · HTTP logging"]
      ctl["Controllers /api/v1"]
      app["Application services"]
      worker["PendingCheckoutRecoveryWorker"]
      health["/health/live · /health/ready"]
      hub["SignalR hub<br/>/hubs/product-updates"]
    end
    cosmos[("Azure Cosmos DB<br/>catalog · carts · orders · users")]
    blob[("Azure Blob Storage :10000<br/>product photos")]

    user -- "1. download app" --> static
    user -- "2. read API URL" --> cfg
    user -- "3. HTTPS + Bearer JWT<br/>Idempotency-Key · If-Match" --> mw
    user -- "4. load product photos" --> blob
    user <-. "5. WebSocket: products changed" .-> hub
    app -. "notify after price / stock change" .-> hub
    mw --> ctl --> app --> cosmos
    app -- "admin uploads" --> blob
    worker --> app
    health -. ready probe .-> cosmos
```

The browser talks to the API directly (CORS allow-list) and loads product photos straight from storage. The web host never sees tokens and holds no business logic. Locally, Blob Storage is Azurite.

## 2. Project dependencies

```mermaid
flowchart TD
    API["IPLMerchandise.API<br/>controllers, middleware, auth, composition root"]
    INF["IPLMerchandise.Infrastructure<br/>Cosmos repositories, blob image store, initialiser, seeding, hashing"]
    APP["IPLMerchandise.Application<br/>use cases, ports, paging, telemetry"]
    DOM["IPLMerchandise.Domain<br/>entities, rules, domain errors"]
    CON["IPLMerchandise.Contracts<br/>v1 DTOs, problem codes"]
    WEB["IPLMerchandise.Web<br/>static host"]
    WC["IPLMerchandise.Web.Client<br/>Blazor WASM: pages, typed API clients, state"]

    API --> INF --> APP --> DOM
    APP --> CON
    API --> APP
    WEB --> WC --> CON
```

Arrows point inwards: Domain depends on nothing; Infrastructure implements interfaces declared in Application. The client shares only `Contracts` with the server.

Test projects: `UnitTests` → domain rules only (no infrastructure); `IntegrationTests` → the whole API via `WebApplicationFactory` against the Cosmos emulator; `Web.Tests` → client components with bUnit and fake API clients.

## 3. Request pipeline (API)

```mermaid
sequenceDiagram
    autonumber
    participant B as Browser
    participant C as CorrelationId
    participant E as ExceptionHandler
    participant A as AuthN/AuthZ
    participant R as RateLimiter
    participant K as Controller
    participant S as Application service
    participant D as Cosmos

    B->>C: request (+ X-Correlation-ID?)
    C->>C: accept safe id or create one, add to log scope + trace tag
    C->>E: next
    E->>A: next
    A->>A: validate JWT (issuer, audience, signature, lifetime), then policy (Customer/Admin)
    A->>R: next
    R->>R: per-user / per-IP fixed window (search, checkout, auth)
    R->>K: next
    K->>S: call use case with user id from `sub` claim
    S->>D: point reads / single-partition queries / transactional batch
    D-->>S: result or CosmosException
    S-->>K: DTO or domain/application exception
    K-->>B: 200/201 + ETag / Idempotent-Replay
    Note over E: exceptions become RFC 9457 problem details:<br/>400 · 401 · 403 · 404 · 409 · 412 · 428 · 429 · 503
```

## 4. Authentication flow

```mermaid
sequenceDiagram
    participant U as Browser (Blazor)
    participant API
    participant Users as users container

    U->>API: POST /api/v1/auth/login {email, password}
    API->>Users: point read id = sha256(normalised email)
    API->>API: verify PBKDF2 hash (Identity PasswordHasher)
    API-->>U: {accessToken (JWT 60 min), user, roles}
    U->>U: store token in localStorage, JwtAuthStateProvider → ClaimsPrincipal for UI
    U->>API: GET /api/v1/cart  Authorization: Bearer …
    API->>API: validate token, customerId = sub claim
    alt token expired / invalid
        API-->>U: 401 problem {code: token-expired}
        U->>U: SessionEvents → sign out, "session expired" banner
    end
```

## 5. Cart update flow (idempotent)

```mermaid
sequenceDiagram
    participant UI as CartState (browser)
    participant API as CartService
    participant DB as carts/{customerId}

    UI->>UI: new Idempotency-Key for this user action
    UI->>API: POST /cart/items {productId, qty}  Idempotency-Key: K
    API->>DB: read idem:K
    alt record exists
        API-->>UI: stored response (Idempotent-Replay: true)<br/>or 409 if K was used for a different change
    else new key
        API->>DB: read cart (+ETag) and products (catalog)
        API->>API: Cart.AddItem: 1–10 per line, limited-edition cap, ≤ stock, ≤ 20 lines
        API->>DB: TransactionalBatch [ create idem:K (TTL 24h) , replace cart IfMatch ETag ]
        alt batch ok
            API-->>UI: 200 cart
        else idem:K conflict (concurrent duplicate)
            API->>DB: read idem:K → replay winner's result
        else cart ETag conflict (other tab)
            API->>API: re-read and re-apply (max 3)
        end
    end
    Note over UI: network error → Retry button resends with the same K
```

## 6. Checkout flow

```mermaid
sequenceDiagram
    participant UI as Checkout page
    participant S as CheckoutService
    participant O as orders/{customerId}
    participant Cat as catalog/"main"
    participant Ca as carts/{customerId}

    UI->>UI: orderId = sessionStorage ?? new Guid (kept until success)
    UI->>S: POST /checkout {orderId, expectedTotal}
    S->>O: read order orderId
    alt exists
        S-->>UI: 200 same order (replay)
    end
    S->>Cat: read checkout:{orderId}
    alt commit exists (earlier attempt crashed)
        S->>S: CompleteAsync → 200 same order
    end
    loop up to 5 attempts
        S->>Ca: read cart
        S->>Cat: read cart's products (one query)
        S->>S: any line short/unavailable → 409 checkout-rejected + failingLines (no writes)
        S->>S: price changed → 409 cart-requires-review · total ≠ expected → 409 total-mismatch
        S->>Cat: TransactionalBatch:<br/>create checkout:{orderId} (Pending, order snapshot)<br/>replace product[i] IfMatch etag (stock − qty)<br/>create audit[i] (reason Order)
        alt committed
            S->>O: create order (idempotent)
            S->>Ca: remove bought units (idempotent per order)
            S->>Cat: mark commit Completed (TTL 30 days)
            S-->>UI: 201 Created + Location
        else ETag conflict (someone else bought / admin edit)
            S->>S: backoff + jitter, re-read, re-validate
        else checkout:{orderId} already exists (racing duplicate)
            S-->>UI: 200 same order
        end
    end
    Note over S,Cat: Commits left Pending > 1 min are completed by PendingCheckoutRecoveryWorker
```

## 7. Concurrency handling at a glance

| Conflict | Mechanism | Outcome |
|---|---|---|
| Two buyers, last unit | Batch with `IfMatchEtag` per product | One commits; the other retries, sees 0 stock → 409 with failing line |
| Buyer vs admin stock/price edit | Same ETag on the product | Whichever writes second retries (buyer) or gets 412 (admin) |
| Two admins | `If-Match` required (428 if missing) | Second gets 412 + current ETag; UI offers reload |
| Same cart, two tabs | Cart ETag | Re-read and re-apply (bounded) |
| Double-click / retry, cart | Idempotency record created in the same batch | Applied once; duplicates replay |
| Double-click / retry, checkout | Order id = commit record id, created in the stock batch | One order, one deduction |

## 8. Stored documents

```jsonc
// catalog  (pk /storeId)
{ "id": "csk-cap", "storeId": "main", "docType": "product", "name": "CSK Cap", "searchName": "csk cap",
  "type": "Cap", "franchiseCode": "CSK", "description": "…", "price": 499.00, "stockQuantity": 97,
  "isActive": true, "createdAtUtc": "2026-01-01T00:01:00.0000000Z", "updatedAtUtc": "…", "_etag": "\"…\"",
  // only when a photo was uploaded; the file itself is in Blob Storage
  "image": { "key": "csk-cap/3f9c….jpg", "url": "https://<storage or CDN>/product-images/csk-cap/3f9c….jpg", "contentType": "image/jpeg" } }

{ "id": "audit:4f0c…", "storeId": "main", "docType": "audit", "productId": "csk-cap", "field": "Stock",
  "oldValue": 100, "newValue": 97, "changedBy": "customer:9b1e…", "changedAtUtc": "…",
  "reason": "Order", "note": "Order 2f6a…", "orderId": "2f6a…" }

{ "id": "checkout:2f6a…", "storeId": "main", "docType": "checkout", "customerId": "9b1e…",
  "status": "Completed", "createdAtUtc": "…", "ttl": 2592000, "order": { /* full order snapshot */ } }

// carts  (pk /customerId)
{ "id": "cart", "customerId": "9b1e…", "docType": "cart", "updatedAtUtc": "…", "appliedOrderIds": ["2f6a…"],
  "lines": [ { "productId": "csk-cap", "productName": "CSK Cap", "franchiseCode": "CSK",
               "unitPrice": 499.00, "quantity": 3, "addedAtUtc": "…" } ] }

{ "id": "idem:5c2d…", "customerId": "9b1e…", "docType": "idempotency", "requestHash": "…",
  "response": "{…cart json…}", "createdAtUtc": "…", "ttl": 86400 }

// orders  (pk /customerId)
{ "id": "2f6a…", "customerId": "9b1e…", "docType": "order", "createdAtUtc": "…", "status": "Confirmed",
  "itemCount": 6, "subtotal": 10494.00, "shippingFee": 0, "total": 10494.00,
  "lines": [ { "productId": "csk-cap", "productName": "CSK Cap", "franchiseCode": "CSK",
               "unitPrice": 499.00, "quantity": 3, "lineTotal": 1497.00, "type": "Cap",
               "imageUrl": "https://…/csk-cap/3f9c….jpg" /* photo at the time of purchase, if any */ } ] }

// users  (pk /id)
{ "id": "<sha256 of normalised email>", "docType": "user", "userId": "9b1e…", "email": "asha@example.com",
  "normalizedEmail": "ASHA@EXAMPLE.COM", "displayName": "Asha", "passwordHash": "AQAAAAIAAYag…",
  "roles": ["Customer"], "createdAtUtc": "…" }
```

## 9. Deployment architecture (recommended production)

```mermaid
flowchart LR
    fd["Azure Front Door<br/>TLS, WAF, CDN for static files"]
    subgraph aca["Azure Container Apps (or App Service), central India, zone-redundant"]
        webc["web container<br/>(2+ replicas)"]
        apic["api container<br/>(2+ replicas, autoscale on HTTP + CPU)"]
    end
    kv["Key Vault<br/>Jwt__SigningKey · Cosmos__ConnectionString"]
    cos[("Cosmos DB for NoSQL<br/>Session consistency · autoscale RU<br/>continuous backup · zone redundant")]
    stg[("Storage account (ZRS)<br/>product-images container")]
    sig["Azure SignalR Service<br/>live product-change pushes"]
    mon["Azure Monitor / App Insights<br/>JSON logs, traces, metrics"]

    fd --> webc
    fd -- /api --> apic
    fd -- product photos --> stg
    apic --> cos
    apic -- uploads via managed identity --> stg
    apic -- products changed --> sig
    sig -. WebSocket .-> fd
    apic -. secrets via managed identity .-> kv
    apic -. telemetry .-> mon
    webc -. telemetry .-> mon
```

- Containers run as the non-root `app` user; liveness `/health/live`, readiness `/health/ready`.
- Production config: `ASPNETCORE_ENVIRONMENT=Production`, `Cosmos__InitializeOnStartup=false` (schema created by `scripts/provision-cosmos.sh`), `Cosmos__UseGatewayMode=false` (Direct mode), CORS origin = the public web URL, Swagger off.
- Same image is promoted through environments; only configuration differs.
- Photos: `ImageStorage__CreateContainerIfMissing=false` (container provisioned up front), `ImageStorage__PublicBaseUrl` = the Front Door/CDN URL for the container, and a lifecycle rule for files that nothing references any more.

## 10. Live price and stock changes in open carts

```mermaid
sequenceDiagram
    participant C as Cart / checkout page
    participant H as SignalR hub
    participant A as Admin or another buyer
    participant API as ProductAdminService / CheckoutService

    C->>H: Watch [ids in the cart]  (joins product:{id} groups)
    A->>API: change price / stock, or check out
    API->>API: save (ETag-guarded)
    API-)H: ProductsChanged [ids]  (best effort)
    H-)C: ProductsChanged [ids]
    C->>API: GET /cart  (Bearer token)
    API-->>C: cart with flagged lines, requiresReview, totalAfterAccept
    C->>C: banner + "Accept new total", checkout blocked
    Note over C,H: After a reconnect or when the tab becomes visible again, the page re-reads the cart once (missed pushes).
```

## 11. Product photo upload

```mermaid
sequenceDiagram
    participant UI as Admin form (browser)
    participant API as ProductsController / ProductAdminService
    participant Cat as catalog/"main"
    participant Blob as Blob Storage

    UI->>API: PUT /products/{id}/image  multipart "file"  If-Match: E1
    API->>Cat: read product
    alt no If-Match / stale ETag
        API-->>UI: 428 / 412 (nothing stored)
    end
    API->>API: ≤ 2 MB, format from the first bytes: JPEG · PNG · WebP (else 400 invalid-image)
    API->>Blob: upload {id}/{random}.{ext}  (If-None-Match *, content type from the format, cache 1 year)
    alt storage unavailable
        API-->>UI: 503 + Retry-After (product unchanged)
    end
    API->>Cat: replace product IfMatch E1 (image = key + URL)
    alt 412 (changed meanwhile)
        API->>Blob: delete the new file
        API-->>UI: 412
    else saved
        API-->>UI: 200 product {imageUrl, ETag E2}
    end
    Note over UI,Blob: Browsers load imageUrl straight from storage/CDN. Old files are kept, because orders reference them.
```
