# Technical Architecture: Savvori MVP

## Overview
Savvori is an ASP.NET Core minimal API (.NET 10) that helps users find the cheapest way to fill their grocery shopping lists by comparing prices across major Portuguese supermarket chains. The MVP supports user accounts, private shopping lists, automatic price discovery via web scraping, location-based store lookup, and shopping list optimization.

## High-Level Architecture
- **Web API:** ASP.NET Core minimal API, authenticated via JWT. Exposes all business logic as REST endpoints. Uses Quartz.NET for background scraping jobs (twice daily).
- **Data Storage:** PostgreSQL (via Entity Framework Core or Dapper).
- **Authentication:** JWT tokens for API calls. Email/password registration and login. Secure password hashing (ASP.NET Identity or custom solution).
- **Product Data:** Populated via dedicated per-store web scrapers, normalised and upserted by a shared processor.
- **Location Services:** Portuguese postal code resolution via geoapi.pt; Haversine distance for nearby store lookup.
- **Testing:** xUnit (`tests/Savvori.Web.Tests`).
- **Orchestration/Deployment:** .NET Aspire for local development and service orchestration.

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

### 4. Web App (Frontend) — Planned
- Razor Pages project using HTMX for dynamic UI updates
- TailwindCSS for styling
- All data actions performed via API calls (JWT for auth)

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

## Open Questions / Decisions
- [x] Which DBMS to use for MVP? → PostgreSQL
- [x] How to schedule and run background jobs? → Quartz.NET jobs in Web API
- [x] Will the MVP have a web frontend? → Yes, Razor Pages web app (planned)
- [x] How to orchestrate/deploy? → .NET Aspire for local/dev

---

This document describes the technical architecture for the Savvori project. Update as implementation progresses.