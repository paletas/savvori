# Technical Architecture: Savvori MVP

## Overview
Savvori is an ASP.NET Core minimal API (.NET 10) that helps users find the cheapest way to fill their grocery shopping lists by comparing prices across major Portuguese supermarket chains. The MVP supports shopping lists (single-user, no authentication), automatic price discovery via web scraping, location-based store lookup, and shopping list optimization.

## High-Level Architecture
- **Web API:** ASP.NET Core minimal API with no authentication (intended for a trusted single-user network). Exposes all business logic as REST endpoints. Uses Quartz.NET for background scraping jobs (twice daily).
- **Data Storage:** SQLite (via Entity Framework Core). Database file defaults to `data/savvori.db` (`ConnectionStrings:savvori`); migrations are applied at startup. `decimal` properties are mapped to REAL so ordering/aggregates work in SQLite.
- **Product Data:** Populated via dedicated per-store web scrapers, normalised and upserted by a shared processor.
- **Location Services:** Portuguese postal code resolution via geoapi.pt; Haversine distance for nearby store lookup.
- **Testing:** xUnit (`tests/Savvori.Web.Tests`).
- **Orchestration/Development:** .NET Aspire 13.x for local development. Run with `aspire run` from the repo root. The AppHost (`Savvori.AppHost`) starts the Web API and Web App. OpenTelemetry, health checks, and service discovery are provided by `Savvori.ServiceDefaults`.
- **HTTP Resilience:** `Microsoft.Extensions.Http.Resilience` (replaces deprecated `Polly.Extensions.Http`) wired via `ServiceDefaults` for all HttpClients.

## Key Components

### 1. Access Model
- There are no user accounts and no authentication. All endpoints and pages (including admin) are open, so deploy only on a trusted network or behind a reverse proxy that provides its own access control.

### 2. Shopping Lists
- ShoppingList entity: id, name, created/updated timestamps
- ShoppingListItem entity: id, shoppingListId, productId, quantity
- CRUD endpoints for shopping lists and items (Web API)

### 3. Product Catalog & Price Discovery
- Product entity: id, name, brand, category, normalizedName, size, unit
- Store entity: id, chainSlug, name, latitude, longitude
- ProductPrice entity: id, productId, storeId, price, lastUpdated
- Product catalog is populated/updated by background scraping jobs (Quartz.NET, twice daily)
- Modular scraper design for easy addition of new store connectors
- Price history retained per product/store combination

### 4. Web App (Frontend)
- **Framework:** Razor Pages (`src/Savvori.WebApp`), .NET 10
- **Styling:** TailwindCSS v4 + DaisyUI v5. CSS built from `wwwroot/css/input.css` via `npm run build:css` (MSBuild target runs automatically before every build; skipped in CI via `$(CI) != 'true'`). Generated `site.css` is git-ignored.
- **Interactivity:** HTMX v2.0.4 (CDN) for in-page dynamic updates. No full-page reloads for search/filtering. Key patterns:
  - Product search: `hx-get="/Products?handler=Search"` with 400 ms debounce → HTML fragment
  - Shopping list product search: `hx-get="/ShoppingLists/Detail?…&handler=SearchProducts"` → rows fragment with IAntiforgery token
  - Admin scraping dashboard: `hx-trigger="every 30s"` auto-refresh via `_ScrapingStatusTable` partial
- **API communication:** `SavvoriApiClient` typed HttpClient (registered via DI). Wraps all 28+ Web API endpoints. Base address resolved from Aspire service discovery keys (`services:webapi:https:0` / `services:webapi:http:0`); falls back to `http://localhost:5000`. All methods handle exceptions gracefully (log + return null/empty).
- **Pages implemented:**

| Section | Pages |
|---------|-------|
| Public | Home (`/`), Products browse + detail, Categories (tree + products), Stores (postal code search) |
| Shopping lists | Index (create/rename/delete), Detail (item management, product search), Optimize (results + comparison matrix) |
| Admin | Dashboard, Scraping jobs (status grid + per-chain detail + manual trigger) |

- **Admin area:** Lives under `Pages/Admin/` with its own `_AdminLayout.cshtml` (DaisyUI drawer sidebar). Not access-restricted (no authentication).
- **Service defaults:** `builder.AddServiceDefaults()` + `app.MapDefaultEndpoints()` wired in for OpenTelemetry, health checks, and resilience. `AddStandardResilienceHandler()` applied to the `SavvoriApiClient` HttpClient.

### 5. Scraper Infrastructure
- `IStoreScraper` interface — contract for all store-specific scrapers
- `BaseHttpScraper` — shared HTTP client with Polly v8 retry policy and AngleSharp HTML parsing
- `ScraperResultProcessor` — normalises raw scraper output and upserts products/prices into the database
- `StoreScrapeJob` — Quartz.NET job that runs all registered scrapers; scheduled twice daily

**Store scrapers:**

| Chain | Technology | Notes |
|-------|-----------|-------|
| Continente | SFCC JSON endpoint | Internal JSON API |
| Pingo Doce | SFCC JSON endpoint | |
| Auchan | SFCC + `data-gtm` JSON attribute | Page-based pagination |
| Lidl | JSON search API (`/q/api/search`) | Empty query + grocery terms, `offset`/`fetchsize` paging; keeps `category == "Food"` items with a price; base price parsed from `1 kg = 2,95` text. Strict `Accept: application/json` returns 406 |
| Celeiro | Magento | `.product-item-info` microdata; category paging via `?p=N` (stops when a page adds nothing new); unit price parsed from `.apresentacao` |

Chains dropped from `Scraping:Chains` are pruned at startup by `StoreChainSeeder`: deleted if they have no data, otherwise deactivated. Minipreço (domain gone), Intermarché (DataDome bot protection) and Mercadona (no Portuguese online shop) were removed on this basis.

### 6. Product Normalization
- `ProductNormalizer` class responsible for:
  - Size/unit extraction (e.g., `1L`, `500g`, `6x1.5L`)
  - Text normalization: lowercase, strip diacritics/accents
  - Brand extraction from product name

### 6a. Category Taxonomy & Product Matching
- `CategoryTaxonomy` / `CategorySeeder` define and seed a canonical category tree, independent of each store's own category labels.
- `CategoryMapper.MapToSlug(rawCategory)` maps a scraped/raw category string to a canonical category slug.
- `ProductMatcher` links a scraped `StoreProduct` to a canonical `Product` in tiers: Tier 1 by EAN, Tier 2 by brand + normalized name + size/unit; unmatched products are flagged (`MatchStatus.Unmatched`/`Failed`) rather than auto-created.
- `MappingAdminController` (`/api/admin/mapping/*`) exposes mapping statistics, uncategorized products, unmapped category strings, and store-product match status, plus repair actions: `backfill-categories` (re-runs `CategoryMapper` over uncategorized products), `rematch` (re-runs Tier 1/2 matching over unmatched/failed store products), and manual per-item category/canonical-product assignment.
- Web App: `Pages/Admin/Mapping/Index.cshtml` provides an admin UI over this same API for reviewing and repairing mappings.

### 6b. Remote Model Backend (feature-flagged, off by default)

Optional model-assisted matching/categorisation runs against a remote, unreliable server (Ollama today). Design rules: the model is only ever called from background jobs; scraping, pages, optimisation and admin actions never depend on it; deterministic matching stays first.

- `Modeling/` in `Savvori.WebApi`: `IEmbeddingClient` / `IPairJudge` (Ollama implementations use `/api/embed` and `/api/chat`), configured under `Model:*` (base URL, model names, timeouts, breaker and queue settings). `Model:Enabled` defaults to `false`.
- `ModelCircuitBreaker`: opens after N consecutive transport failures, refuses calls during a cool-down, then lets one probe through. All clients are wrapped by it (`BreakerEmbeddingClient`, `BreakerPairJudge`). Malformed responses do not count as outages.
- `ModelJobs` table + `ModelQueueDrainJob` (Quartz, every `Model:Queue:PollSeconds`): idempotent durable queue, exponential backoff with jitter, dead-letter after `MaxAttempts`. While the breaker is open, jobs are deferred without consuming attempts, so an outage never dead-letters healthy work. The drain job only probes while the breaker is open.
- The model HTTP client opts out of the ServiceDefaults standard resilience handler (retries belong to the queue; timeouts to `Model:ConnectTimeoutSeconds` / `RequestTimeoutSeconds`).
- Stored embeddings (Phase 2) must record model name, digest, dimension and input-text hash; `EmbeddingFreshness.IsStale` decides when to recompute.
- Observability: `GET /api/admin/model/status` and a panel on Admin/Mapping. Plan and later phases: `docs/MODEL_MATCHING_PLAN.md`.

### 7. Location Services
- `ILocationService` / `GeoApiLocationService`
- Integrates with [geoapi.pt](https://geo.iotech.pt) (free, no auth): `GET https://geo.iotech.pt/cp/{postalCode}?json=1`
- Coordinate results cached with `IMemoryCache` (TTL: 24 h)
- Haversine formula used to compute distance between user coordinates and store locations
- Default search radius: 15 km (configurable)

### 8. Price Optimization Engine
- `IShoppingOptimizer` / `ShoppingOptimizer`
- Four optimization modes:
  - **`cheapest-total`** — splits the list across stores to minimize total spend
  - **`cheapest-store`** — finds the single best store for the entire list
  - **`balanced`** — balances cost savings vs. trip convenience; switches to cheapest-total only when savings exceed a configurable threshold (default: €2.00, passed via `?threshold=`)
  - **`compare`** — returns a full price matrix (all stores × all items) for user-side comparison
- Per-item alternative product suggestions: sorted by price ascending, up to 3 shown

## API Endpoints


### Products
- `GET /api/products?search=&category=&page=` – Search/browse product catalog
- `GET /api/products/{id}` – Product details with prices across all stores
- `GET /api/products/{id}/alternatives` – Alternative product suggestions (up to 3, sorted by price ascending)
- `GET /api/products/{id}/pricehistory` – Historical price data

### Stores & Locations
- `GET /api/stores` – List all store chains
- `GET /api/stores/{chainSlug}/locations` – Physical locations for a store chain
- `GET /api/stores/nearby?postalCode=&radiusKm=` – Stores near a Portuguese postal code (default radius: 15 km)
- `GET /api/stores/geocode?postalCode=` – Resolve Portuguese postal code to coordinates via geoapi.pt

### Categories
- `GET /api/categories` – Full category tree
- `GET /api/categories/{idOrSlug}/products` – Products in a category

### Shopping Lists
- `GET /api/shoppinglists` – List user's shopping lists
- `POST /api/shoppinglists` – Create shopping list
- `PUT /api/shoppinglists/{id}` – Update shopping list
- `DELETE /api/shoppinglists/{id}` – Delete shopping list
- `POST /api/shoppinglists/{id}/items` – Add item to list
- `DELETE /api/shoppinglists/{id}/items/{itemId}` – Remove item
- `GET /api/shoppinglists/{id}/optimize?mode={mode}&threshold=2.00` – Optimize list (modes: `cheapest-total`, `cheapest-store`, `balanced`, `compare`)

### Admin — Scraping
- `GET /api/admin/scraping/status` – Current status of all scraping jobs
- `POST /api/admin/scraping/trigger/{chainSlug}` – Manually trigger a scrape for a store chain

### Admin — Model Backend
- `GET /api/admin/model/status` — breaker state, last success/error, queue depth, oldest pending job, dead-lettered and stale-embedding counts (never calls the model).
- `POST /api/admin/model/requeue-dead-letters` — gives dead-lettered jobs a fresh attempt budget.

### Admin — Category & Product Mapping
- `GET /api/admin/mapping/stats` – Aggregate category/match statistics
- `GET /api/admin/mapping/uncategorized-products?page=&pageSize=` – Canonical products with no category
- `GET /api/admin/mapping/unmapped-categories` – Distinct raw category strings with no canonical mapping, with a suggested slug
- `GET /api/admin/mapping/store-products?status=&chainSlug=&page=&pageSize=` – Store products filtered by match status/chain
- `POST /api/admin/mapping/backfill-categories` – Re-run `CategoryMapper` over uncategorized products
- `POST /api/admin/mapping/rematch?chainSlug=` – Re-run Tier 1/2 matching over unmatched/failed store products
- `PUT /api/admin/mapping/products/{id}/category` – Manually assign a category to a product
- `PUT /api/admin/mapping/store-products/{id}/canonical` – Manually link a store product to a canonical product

## Security & Privacy
- No authentication: every API endpoint and page is open. Run only on a trusted network or behind a reverse proxy that enforces access control.
- No personal data is stored.

## Extensibility
- New stores can be added by implementing `IStoreScraper` and registering it with DI
- Product and price models are store-agnostic
- Optimization modes are pluggable via `IShoppingOptimizer`

## Testing

- **Test projects:** `tests/Savvori.Api.Tests` (integration tests against `Savvori.WebApi` via `WebApplicationFactory`), `tests/Savvori.Web.Tests` (unit tests covering `Savvori.WebApi` and `Savvori.WebApp`), `tests/Savvori.E2E.Tests` (end-to-end tests against `Savvori.WebApp`). All three use xUnit v3 (`xunit.v3` 4.x) running on Microsoft.Testing.Platform, NSubstitute v6, EF Core InMemory (unit tests) and shared in-memory SQLite (API integration tests).
- **Coverage areas:** scraper correctness (per-chain), price normaliser, category mapping/matching, shopping optimizer (all 4 modes), location service, `SavvoriApiClient`, admin mapping/scraping endpoints, full web app page flows (shopping lists, admin).
- **Total tests:** ~390 across the three projects, all passing except `LiveScraperTests` (in `Savvori.Web.Tests`), which make real HTTP calls to live grocery-store websites and are expected to be slow/flaky independent of code changes.
- **Running tests:** `dotnet test Savvori.sln` runs all three projects via the unified `dotnet test` MTP mode (enabled by the `test.runner` section in `global.json` — required because `xunit.v3` 4.x no longer supports the legacy VSTest bridge on .NET 10 SDK+). Run a single project with `dotnet test tests/<Project>/<Project>.csproj`, or a single test with `--filter "FullyQualifiedName~ClassName.MethodName"`.
- **Dependency note:** `Quartz` is pinned to the 3.x line (not 4.0) because `Quartz.Extensions.Hosting` has no 4.x-compatible release yet; bumping `Quartz` alone without `Quartz.Extensions.Hosting` breaks the `IJob.Execute` contract and `AddQuartzHostedService` resolution.

## Open Questions / Decisions
- [x] Which DBMS to use? → SQLite (single-user homelab deployment; previously PostgreSQL)
- [x] Authentication? → None (single-user homelab deployment; previously JWT + cookies)
- [x] How to schedule and run background jobs? → Quartz.NET jobs in Web API
- [x] Will the MVP have a web frontend? → Yes, Razor Pages web app (implemented — TailwindCSS v4, DaisyUI v5, HTMX)
- [x] How to orchestrate/deploy? → .NET Aspire for local/dev; container images published to GHCR for the homelab (see *Container images*)
- [x] How does the WebApp talk to the WebApi? → Typed `SavvoriApiClient` HttpClient with Aspire service discovery

---

This document describes the technical architecture for the Savvori project. Update as implementation progresses.

## Container images & CI/CD

`.github/workflows/build.yml` runs the tests (excluding `Category=Live`) and builds two images, `ghcr.io/paletas/savvori-webapi` and `ghcr.io/paletas/savvori-webapp` (`Dockerfile.webapi`, `Dockerfile.webapp`):

| Branch | Tags |
|---|---|
| `develop` | `beta`, `beta-<sha>` |
| `main` | `latest`, `sha-<sha>` |
| manual dispatch | `branch-<name>`, `branch-<name>-<sha>` |

Pull requests build the images without pushing.

**Data safety.** The SQLite database is never inside an image. `savvori-webapi` reads `ConnectionStrings__savvori=Data Source=/data/savvori.db`; mount a host folder or volume at `/data`. Beta and production must use different data folders. On startup, if migrations are pending against an existing database, `DatabaseBackup` writes a consistent `VACUUM INTO` snapshot to `/data/backups/` (last 10 kept) before migrating.

**Runtime config.** `savvori-webapp` finds the API via `services__webapi__http__0=http://<api-host>:8080`. TLS is expected to be terminated by a reverse proxy: forwarded headers are honoured and HTTPS redirection is disabled in the image (`HttpsRedirection__Enabled=false`).
