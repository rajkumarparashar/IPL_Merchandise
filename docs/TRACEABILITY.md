# Requirement traceability

Status: ✅ done and tested · 🟡 done, with a documented gap · 📄 design only (as required)

Test projects: **U** = `tests/IPLMerchandise.UnitTests` (domain rules), **I** = `tests/IPLMerchandise.IntegrationTests` (real API + Cosmos emulator), **W** = `tests/IPLMerchandise.Web.Tests` (bUnit).
Each scenario is tested at the layer where it proves something: concurrency and idempotency against the real database, business rules in the domain, UI behaviour in bUnit.

## Catalogue

| Requirement | Implementation | Tests | Status |
|---|---|---|---|
| Product fields; seed ≥ 4 products, ≥ 2 franchises | `Domain/Catalog/Product.cs`, `Infrastructure/Seeding/DataSeeder.cs` | I `Assessment_walkthrough` | ✅ |
| INR, tax-inclusive, ≤ 2 decimals | `CatalogRules.EnsureValidPrice`, client `Formatting/Money.cs` | U `Invalid_prices_are_rejected`; W `Rupees_use_two_decimals_and_indian_grouping` | ✅ |
| Shipping (good to have) | `ShippingPolicy` (₹99, free from ₹999) | U `Shipping_is_free_from_999` | ✅ |
| Stock never below zero, whatever the load | `Product.DeductStock`, ETag-guarded batch in `CosmosCheckoutRepository` | U `Selling_more_than_in_stock…`; I `Concurrent_checkouts_for_the_last_unit_never_oversell` | ✅ |
| Optimistic concurrency on price/stock, conflict not applied | `ProductAdminService`, `CosmosProductRepository.SaveAsync` (412 / 428) | I `Update_with_a_stale_etag_is_412_and_is_not_applied`, `Concurrent_updates_with_the_same_etag_only_one_succeeds`; W `Stale_data_conflict_is_shown…` | ✅ |
| Product photos (added): admin upload/remove, JPEG/PNG/WebP ≤ 2 MB checked from content, Blob Storage, illustration fallback | `ProductImageRules`, `ProductAdminService.SetImageAsync`, `BlobProductImageStore`, `ImagePicker.razor`, `ProductPhoto.razor` (DESIGN ADR-11) | U `The_format_is_read_from_the_file_content`, `Anything_else_is_rejected`; I `ProductImageApiTests`; W `New_product_is_created_first_then_its_photo_is_uploaded…`, `A_new_photo_is_part_of_the_single_save…`, `Shows_the_photo_and_falls_back…` | ✅ |
| Audit: who, when, old/new, reason | `PriceStockChange`, written in the same batch as the change | U `Price_change_returns_an_audit_entry…`; I `Price_and_stock_changes_are_audited…` | ✅ |

## Search and listing

| Requirement | Implementation | Tests | Status |
|---|---|---|---|
| Paged, stable order, capped page size, continuation token | Keyset on `(createdAtUtc, id)`, max 50, `ContinuationToken.cs` | I `Paging_a_large_list_returns_every_item_once…`, `Invalid_input_returns_400…` (bad token); W `Pages_forward_and_back_with_continuation_tokens` | ✅ |
| Name (case-insensitive, partial), type, franchise, combined; price + stock status | `CosmosProductRepository.BuildFilter`, `StockBadge.razor` | I `Search_combines_name_type_and_franchise`, `Sorting_multi_value_filters_and_facet_counts`, `Featured_filter_returns_only_featured_products_and_edits_keep_the_flag`, `Featured_order_lists_featured_products_first_across_page_boundaries`; W `Lists_products_with_rupee_prices_and_stock_status`, `Sidebar_checkboxes_show_counts_and_filter_the_list` | ✅ (search is a partition scan, see DESIGN) |

## Cart

| Requirement | Implementation | Tests | Status |
|---|---|---|---|
| Add, change quantity, remove | `CartService`, `CartController` | I `Change_quantity_and_remove` | ✅ |
| 1 ≤ quantity ≤ 10, ≤ stock | `Cart.EnsureQuantityAllowed` + DataAnnotations | U `Quantities_1_and_10_are_allowed`, `Quantities_outside_1_to_10_are_rejected`, `Adding_to_an_existing_line_cannot_go_over_10`, `With_stock_of_exactly_one…`; I `Quantity_boundaries`, `Stock_of_exactly_one_allows_one_and_rejects_two_with_409` | ✅ |
| Per-order limit (2 for limited editions, else 10) visible with its reason, next to the stock (added) | `ProductCard.razor`, `ProductDetails.razor`, `CartPage.razor` | W `A_limited_edition_card_shows_its_per_order_limit_next_to_the_stock`, `When_nothing_more_can_be_added_the_card_says_which_rule_applies`, `With_more_in_stock_than_one_order_may_hold…`, `A_line_at_the_per_order_limit_says_so…` | ✅ |
| Client request id; repeat applied once, original result | `Idempotency-Key`; record + cart saved in one batch | I `Repeated_request_id_is_applied_once…`, `Simultaneous_duplicates_are_applied_once`; W `Retry_after_a_network_failure_reuses_the_same_request_id` | ✅ |
| Price/stock change flagged; user accepts new total before checkout | `Cart.Review`, `AcceptChanges`, checkout guard; pushed to open carts over SignalR, no refresh needed (`ProductUpdatesHub`, `CartLiveUpdates`, DESIGN ADR-12) | U `Price_and_stock_changes_are_flagged…`, `Accepting_changes_moves…`; I `Price_change_is_flagged_and_checkout_waits…`, `Price_changes_and_checkouts_are_pushed_to_watchers_of_that_product_only`; W `Price_change_warning_blocks_checkout_until_accepted`, `LiveCartUpdatesTests` | ✅ |

## Checkout and orders

| Requirement | Implementation | Tests | Status |
|---|---|---|---|
| Client order id; retry returns same order, never a second | Order lookup → commit lookup → commit record in the stock batch | I `Retried_checkout_returns_the_same_order…`, `Simultaneous_checkouts_with_the_same_order_id_create_one_order`; W `Retry_after_a_lost_response_reuses_the_same_order_id`, `Double_click_places_one_order` | ✅ |
| Concurrent checkouts never oversell | ETag-conditioned `TransactionalBatch` + retry | I `Concurrent_checkouts_for_the_last_unit_never_oversell` | ✅ |
| Reject whole order, deduct nothing, name failing lines, keep cart | Validation before any write; `failingLines` | I `Rejected_order_deducts_nothing_names_the_failing_line_and_keeps_the_cart`; W `Out_of_stock_rejection_lists_the_failing_lines` | ✅ |
| Partial failure leaves consistent state | Commit record + idempotent completion + recovery worker | I `Interrupted_checkout_is_completed_by_recovery_without_deducting_twice` | ✅ |
| Delivery address captured, validated and stored (added) | `ShippingAddressDto`, `Order.ShippingAddress`, checkout steps | I `Shipping_address_is_validated_and_stored_on_the_order`; W `Invalid_address_keeps_the_customer_on_the_details_step`, `Terms_must_be_accepted`, `Address_is_reviewed_and_sent_with_the_order` | ✅ |
| Order lines keep name and price at purchase | `OrderLine` snapshot | U `Later_price_changes_do_not_alter_a_placed_order`; I `Order_keeps_the_price_paid_after_a_price_change` | ✅ |
| History newest first, paged, date range | `OrderService.ListAsync`, `OrderHistory.razor` | I `History_is_newest_first_and_paged`, `Date_range_filters_on_utc_instants`, `Summary_order_number_search_and_item_previews`; W `Stats_and_order_rows_show_number_items_and_status`, `Order_details_show_confirmation_items_and_totals` | ✅ |
| UTC storage, shown in local zone (IST default) | `CosmosJson` UTC converter, `UserTimeZone` | W `Utc_times_are_shown_in_ist_by_default`, `Date_filter_sends_whole_ist_days_as_utc_instants` | ✅ |

## API

| Requirement | Implementation | Tests | Status |
|---|---|---|---|
| Versioned API (`/api/v1`) | URL-segment versioning with `Asp.Versioning`; every controller is `[ApiVersion("1.0")]` on `api/v{version:apiVersion}/…`; `api-supported-versions` response header (DESIGN ADR-8) | I `Product_response_has_etag_version_header_and_the_backwards_compatible_field` (calls `/api/v1`, checks the header) | ✅ |
| One backwards-compatible change to an existing contract | New optional, nullable response field `ProductDto.maxQuantityPerLine` (`Contracts/V1/Products.cs`); later additions follow the same rules (DESIGN ADR-8 point 5) | I `Product_response_has_etag_version_header_and_the_backwards_compatible_field` (field present: 2 for a limited edition) | ✅ |
| How existing clients are protected (documented) | DESIGN ADR-8 "How existing clients are protected": additive only, tolerant readers, server still enforces the rule, client falls back to the documented default when the field is absent (`State/ProductLimits.cs`), breaking changes only as `/api/v2` | I `Original_v1_product_fields_keep_their_names_and_types` (old clients still get every original field, same name and type), `Featured_filter_returns_only_featured_products_and_edits_keep_the_flag` (an older admin client that omits a new optional request field cannot clear it); W `Against_an_api_that_does_not_send_the_optional_limit_the_documented_defaults_apply` (new client, older server) | ✅ |
| Validation; problem details with 400/404/409/412/429 | DataAnnotations + `ApplicationExceptionHandler` + rate limiter | I `Invalid_input_returns_400…`, `Unknown_product_is_404_problem_details`, 409/412/428 tests above, `Search_over_the_limit_is_429_with_retry_after` | ✅ |
| (Optional) Rate limits with Retry-After | `Infrastructure/RateLimiting.cs` | I `Search_over_the_limit_is_429_with_retry_after` | ✅ (checkout/auth limits configured, not separately tested) |
| Authentication / authorisation (added) | JWT, roles, ownership from `sub` | I `Only_admins_can_change_products`, `Customers_cannot_see_each_others_orders`, `Expired_and_forged_tokens_are_rejected_with_401` | ✅ |

## Front end

| Requirement | Implementation | Tests | Status |
|---|---|---|---|
| Pages: list + search/filters/paging, details, cart, checkout, history | `Pages/*.razor` (+ home landing page, login, register, confirmation, admin) | W `HomeTests`, `CatalogueTests`, `ProductDetailsTests`, `CartPageTests`, `CheckoutTests`, `OrderHistoryTests` | ✅ |
| Validation and display of a rejected action | `ProblemAlert` (`role=alert`), DataAnnotations forms | W `Empty_submit_shows_field_errors…`, `Rejected_add_is_shown_as_an_announced_alert` | ✅ |
| Double-submit and retry safety | `CartState`, `CheckoutSession`, `RetryHandler` | W `Double_click_on_add_sends_one_request`, `Double_click_places_one_order`, `Retry_after_a_network_failure_reuses_the_same_request_id`, `Retry_after_a_lost_response_reuses_the_same_order_id`, `Transient_failures_are_retried_only_for_idempotent_requests` | ✅ |
| Loading, error and conflict (stale data) states | `LoadState`, admin conflict banner | W `Shows_a_loading_state_while_waiting`, `Failure_is_announced_and_retry_recovers`, `Stale_data_conflict_is_shown…` | ✅ |
| Accessibility checks | Labels, `role=alert`/`status`, button names, table captions, skip link | W `Every_input_has_a_label`, `Every_button_has_an_accessible_name_and_the_table_has_a_caption` | ✅ |
| Typed API client contract tests | `Api/Clients.cs` | W `ApiClientContractTests` | ✅ |

## Evolution (design only)

| Requirement | Where | Status |
|---|---|---|
| Multi-tenancy: data model, partition key, API, authorisation, migration, first step | DESIGN ADR-10 | 📄 |

## Non-functional

| Requirement | Implementation | Verification | Status |
|---|---|---|---|
| Layers, DI, testable design; patterns used/avoided | 5 projects; DESIGN ADR-2 | Build; domain unit tests have no infrastructure | ✅ |
| Partition key, hot partitions, cross-partition, RU, consistency | DESIGN ADR-3/4; `CosmosDbInitializer`; `scripts/provision-cosmos.sh` | Emulator probe + integration tests | ✅ (RU figures are estimates) |
| Timeouts, retries with backoff, idempotent writes, degradation, health checks | Cosmos timeout/429 retries; client `RetryHandler`; 503 + Retry-After; `/health/live`, `/health/ready`; photo storage and live-update pushes are optional (the shop keeps selling without them, DESIGN ADR-11/12) | Manual Cosmos-outage check (DESIGN) | 🟡 outage behaviour verified manually, not automated |
| Security: authN/Z, secrets, validation, no sensitive data in logs | DESIGN ADR-7; `ValidateOnStart`; HTTP logging fields; `no-store` | Auth integration tests above; log check during the compose run | ✅ |
| Structured logging, correlation ids, metrics/tracing hooks | JSON logs with scopes; `CorrelationIdMiddleware`; `StoreTelemetry` | Manual | 🟡 no exporter wired (hooks only), no automated test |
| (Optional) Containers, CI, per-environment config | Dockerfiles, `docker-compose.yml`, `.github/workflows/ci.yml`, `/client-config` | `docker compose up --build` run locally | 🟡 CI workflow not yet run on GitHub |

## Deliverables

| Item | Location |
|---|---|
| Source | `src/` |
| README (setup, run, test) | `README.md` |
| DB/container creation incl. partition keys | `src/IPLMerchandise.Infrastructure/Cosmos/CosmosDbInitializer.cs`, `scripts/provision-cosmos.sh` (optionally also the photo storage) |
| Automated tests | `tests/`: 19 unit, 40 integration and 62 front-end test methods |
| DESIGN.md / ADRs, assumptions, extra scenarios, limitations, next steps | `docs/DESIGN.md` |
| AI-USAGE.md | `docs/AI-USAGE.md` |
| Docker Compose + CI | `docker-compose.yml`, `.github/workflows/ci.yml` |
