# Technical Architecture: Savvori MVP

## Overview
Savvori is an ASP.NET Core minimal API (.NET 10) that helps users find the cheapest way to fill their grocery shopping lists by comparing prices across major Portuguese supermarket chains. The MVP supports user accounts, private shopping lists, automatic price discovery via web scraping, location-based store lookup, and shopping list optimization.

## High-Level Architecture
- **Web API:** ASP.NET Core minimal API, authenticated via JWT. Exposes all business logic as REST endpoints. Uses Quartz.NET for background scraping jobs (twice daily).
- **Data Storage:** PostgreSQL (via Entity Framework Core). Managed by Aspire in development (container via Podman).
- **Authentication:** JWT tokens for API calls. Email/password registration and login. Secure password hashing (ASP.NET Identity or custom solution).
- **Product Data:** Populated via dedicated per-store web scrapers, normalised and upserted by a shared processor.
- **Location Services:** Portuguese postal code resolution via geoapi.pt; Haversine distance for nearby store lookup.
- **Testing:** xUnit (`tests/Savvori.Web.Tests`).
- **Orchestration/Development:** .NET Aspire 13.x for local development. Run with `aspire run` from the repo root. The AppHost (`Savvori.AppHost`) starts all services and a PostgreSQL container (via Podman). OpenTelemetry, health checks, and service discovery are provided by `Savvori.ServiceDefaults`.
- **HTTP Resilience:** `Microsoft.Extensions.Http.Resilience` (replaces deprecated `Polly.Extensions.Http`) wired via `ServiceDefaults` for all HttpClients.

## Key Components

### 1. User Accounts & Authentication
- User entity: email, password hash, created/updated timestamps
- Registration, login, logout endpoints (Web API)
- JWT tokens for API calls
- Account deletion endpoint (GDPR compliance)
- Secure password storage (hash + salt)

### 2. Shopping Lists
- ShoppingList entity: id, userId, name, created/updated timestamps
- ShoppingListItem entity: id, shoppingListId, productId, quantity
- CRUD endpoints for shopping lists and items (Web API)
- Shopping lists are always scoped to the authenticated user

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
- **Authentication:** Dual-cookie scheme:
  - `savvori_auth` — ASP.NET Core cookie auth (7-day sliding expiry); governs Razor Page `[Authorize]` enforcement
  - `savvori_token` — HTTP-only cookie holding the raw JWT from the Web API login response
  - `AuthCookieHandler` (DelegatingHandler) reads `savvori_token` and injects `Authorization: Bearer {token}` on every outbound API call, bridging page auth to API auth
- **API communication:** `SavvoriApiClient` typed HttpClient (registered via DI). Wraps all 28+ Web API endpoints. Base address resolved from Aspire service discovery keys (`services:webapi:https:0` / `services:webapi:http:0`); falls back to `http://localhost:5000`. All methods handle exceptions gracefully (log + return null/empty).
- **Pages implemented:**

| Section | Pages |
|---------|-------|
| Public | Home (`/`), Products browse + detail, Categories (tree + products), Stores (postal code search) |
| Auth | Login, Register, Logout, Account settings + deletion |
| Shopping lists | Index (create/rename/delete), Detail (item management, product search), Optimize (results + comparison matrix) |
| Admin | Dashboard, Scraping jobs (status grid + per-chain detail + manual trigger) |

- **Admin area:** Lives under `Pages/Admin/` with its own `_AdminLayout.cshtml` (DaisyUI drawer sidebar). Access restricted by `[Authorize(Roles = "admin")]`.
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
| Minipreço | SAP Hybris | `.product-list__item` selectors; regex unit-price parsing |
| Lidl | Stub | No online grocery catalog |
| Intermarché | Stub | No online grocery catalog |
| Mercadona | Stub | No online grocery catalog |

### 6. Product Normalization
- `ProductNormalizer` class responsible for:
  - Size/unit extraction (e.g., `1L`, `500g`, `6x1.5L`)
  - Text normalization: lowercase, strip diacritics/accents
  - Brand extraction from product name

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

### Auth & Accounts
- `POST /api/auth/register` – Create account
- `POST /api/auth/login` – Authenticate
- `POST /api/auth/logout` – Log out
- `DELETE /api/account` – Delete account (GDPR)

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

### Shopping Lists (auth required)
- `GET /api/shoppinglists` – List user's shopping lists
- `POST /api/shoppinglists` – Create shopping list
- `PUT /api/shoppinglists/{id}` – Update shopping list
- `DELETE /api/shoppinglists/{id}` – Delete shopping list
- `POST /api/shoppinglists/{id}/items` – Add item to list
- `DELETE /api/shoppinglists/{id}/items/{itemId}` – Remove item
- `GET /api/shoppinglists/{id}/optimize?mode={mode}&threshold=2.00` – Optimize list (modes: `cheapest-total`, `cheapest-store`, `balanced`, `compare`)

### Admin — Scraping (admin role required)
- `GET /api/admin/scraping/status` – Current status of all scraping jobs
- `POST /api/admin/scraping/trigger/{chainSlug}` – Manually trigger a scrape for a store chain

## Security & Privacy
- All API endpoints (except register/login) require authentication
- Passwords never stored in plain text
- User data is never shared with third parties
- Users can export or delete their data (GDPR)
- API uses JWT for secure calls

## Extensibility
- New stores can be added by implementing `IStoreScraper` and registering it with DI
- Product and price models are store-agnostic
- Optimization modes are pluggable via `IShoppingOptimizer`

## Testing

- **Test project:** `tests/Savvori.Web.Tests` (xUnit v3, NSubstitute v5, EF Core InMemory)
- **Coverage areas:** scraper correctness (per-chain), price normaliser, shopping optimizer (all 4 modes), location service, `SavvoriApiClient` (24 tests via `FakeHttpMessageHandler`), `AuthCookieHandler` (4 tests)
- **Total tests:** 165 — all passing

## Open Questions / Decisions
- [x] Which DBMS to use for MVP? → PostgreSQL
- [x] How to schedule and run background jobs? → Quartz.NET jobs in Web API
- [x] Will the MVP have a web frontend? → Yes, Razor Pages web app (implemented — TailwindCSS v4, DaisyUI v5, HTMX)
- [x] How to orchestrate/deploy? → .NET Aspire for local/dev
- [x] How does the WebApp talk to the WebApi? → Typed `SavvoriApiClient` HttpClient with Aspire service discovery; JWT forwarded via `AuthCookieHandler`

---

This document describes the technical architecture for the Savvori project. Update as implementation progresses.