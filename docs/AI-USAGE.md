# AI usage

## Tools

| Tool | Model | Used for |
|---|---|---|
| Claude Code (VS Code extension, agentic mode) | Claude Opus 5.5 | Analysis of the brief, architecture, code, tests and documentation |

No other AI tools were used.

## How it was used

The candidate wrote a phased brief (architecture first, then backend, backend tests, front end, front-end tests, docs, final review) with explicit constraints: no placeholder logic, no trivial tests, no silent simplification of concurrency, no claims of verification that did not happen. Claude Code worked through those phases in this repository, running the build, the tests, the emulator and the containers itself.

## Where AI helped

| Area | What AI produced | How it was checked |
|---|---|---|
| Requirements analysis | Requirement list, extra scenarios, the walkthrough stock inconsistency (KKR 2 → "the last one") | Reflected in DESIGN.md Assumptions and the walkthrough test |
| Domain + application code | Entities, rules, use cases, idempotency, checkout, recovery | Domain unit tests; use cases tested end to end against the emulator |
| Cosmos infrastructure | Repositories, batch error mapping, initialiser, seeding | Integration tests against the emulator, repeated 5× to check for flakiness; log inspection confirmed the race test really produced ETag conflicts (5 losers retried once, then got 409) |
| Docker / CI | Dockerfiles, compose, GitHub Actions workflow | `docker compose up --build` run end to end; the CI workflow file has **not** been executed on GitHub |
| Documentation | README, DESIGN, ARCHITECTURE, TRACEABILITY, this file | README run/test/build commands and the curl example were executed; **not** executed: the `user-secrets` examples and `scripts/provision-cosmos.sh` (needs an Azure subscription). Test names cited in the docs were checked against the code by a script |

## Problems found during verification (and fixed)

These came out of running things, not out of the first draft, and are worth knowing in the interview:

1. **Variable-length timestamps broke ordering.** System.Text.Json writes `…00Z` and `…00.5Z`; Cosmos compares strings, so keyset paging would skip or repeat rows. Fixed with a fixed-width UTC converter.
2. **Emulator rejected targeted index exclusions.** Switched to opt-in indexing (exclude `/*`, include queried paths), which is also cheaper in production.
3. **Rate-limit settings read too early** made test overrides ineffective (every test hit 429). Now read per request via `IOptionsMonitor`.
4. **`blazor.web.js` missing from the published container.** The SDK decides to include it based on `.razor` files present at restore time; the Dockerfile restores with only `.csproj` files. Fixed with `RequiresAspNetWebAssets=true`.
5. **Full page reload of an `[Authorize]` page returned 500** from the web host. The host now maps page endpoints with `AllowAnonymous()`; the API remains the security boundary.
6. **Client never read its API URL** (`appsettings.json` is only fetched if it is in the static-asset manifest). Replaced with an explicit `/client-config` endpoint.
7. **Order placed but request reported failure** if cart tidy-up failed afterwards. Tidy-up is now best-effort, with recovery finishing it.
8. **Signing in again after "session expired" did not work until a page refresh.** `HttpClientFactory` builds message handlers in their own DI scope, so the handler that adds the bearer token had its own *scoped* `TokenStore` with the expired token cached. Fixed by registering `TokenStore` as a singleton (one per browser tab); reproduced and verified in Chrome by rotating the API signing key mid-session, plus a regression test (`ClientServicesTests`).
9. **AngleSharp security advisory:** pinned to a patched version (1.5.2) that is still compatible with bUnit 1.40.
10. **Live cart updates first polled every 15 s.** The candidate rejected this in review (it adds delay and costs RUs for nothing); replaced with event-driven SignalR pushes (DESIGN ADR-12). Kept as an example of a design changed by human review.
11. **Uploaded photos were cropped in non-square frames.** Percentage heights don't resolve inside CSS grid cells, so the image rendered at full size. Found from a screenshot; fixed by pinning the photo to its frame.
12. **"Only 3 left" but only 2 could be added.** The limited-edition cap (2 per order) was enforced but never shown. Found by the candidate; the limit and its reason are now shown on the card, details page and cart.
