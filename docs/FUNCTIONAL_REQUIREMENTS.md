# Functional Requirements: Savvori MVP

## 1. Shopping Lists

### User Stories
- As a user, I want to create multiple shopping lists so I can organize my grocery needs.
- As a user, I want to search for and add products from an available product list to my shopping lists.
- As a user, I want to see my shopping list optimized for the lowest total price across all stores.
- As a user, I want to see my shopping list grouped by the cheapest store for each item.

### Acceptance Criteria
- Users can create, view, update, and delete multiple shopping lists.
- Users can add/remove products to/from a shopping list from the available product catalog.
- The app displays the total cost of the shopping list, optimized for the lowest price (across stores).
- The app can display the shopping list grouped by store, showing the cheapest store for each item.
- Shopping lists are private to each user.

## 2. Product Discovery with Prices per Store

### User Stories
- As a user, I want to see available products and their prices at different stores (e.g., Continente, Pingo Doce).
- As an admin/developer, I want the system to automatically discover and update product prices from supported stores.

### Acceptance Criteria
- The product catalog includes products from Continente, Pingo Doce, Auchan, Lidl (via its JSON search API), and Celeiro (organic/health-food chain) (via web scraping). Only chains with a working scraper are listed as stores.
- Product prices are updated automatically up to twice daily.
- The system prefers APIs for price discovery, but uses web scraping if APIs are unavailable.
- The system is designed to easily add new stores in the future.

## 3. Access Model (No Authentication)

- The application has no user accounts, login, or roles. It is intended for a single household on a trusted network (e.g. a homelab).
- All API endpoints and pages, including the admin section, are open to any client that can reach the app. Access control, if needed, is provided outside the app (network rules or a reverse proxy).
- Shopping lists are shared by everyone using the instance.

## 4. Price Optimization Modes

### User Stories
- As a user, I want to choose how my shopping list is optimized so I can balance cost savings against the number of stores I visit.

### Acceptance Criteria
The `/api/shoppinglists/{id}/optimize` endpoint supports four modes via `?mode=`:

| Mode | Behaviour |
|------|-----------|
| `cheapest-total` | Splits the list across stores to achieve the lowest possible total cost |
| `cheapest-store` | Identifies the single store where the full list costs least |
| `balanced` | Uses `cheapest-total` only when savings exceed a configurable threshold (default: €2.00, passed via `?threshold=`); otherwise falls back to `cheapest-store` |
| `compare` | Returns a full price matrix (all stores × all items) with no recommendation — for user-side comparison |

- The default mode is `cheapest-total` if not specified.
- The `threshold` parameter is only meaningful for `balanced` mode.

## 5. Alternative Product Suggestions

### User Stories
- As a user, I want to see cheaper or equivalent alternatives for items in my optimization results so I can further reduce my spend.

### Acceptance Criteria
- Each item in an optimization result includes up to 3 alternative products.
- Alternatives are sorted by price ascending.
- Alternatives are also accessible directly via `GET /api/products/{id}/alternatives`.
- An alternative is a product in the same category with a similar name or unit type.

## 6. Location-Based Store Suggestions

### User Stories
- As a user, I want to find stores near me by entering my postal code so I only see stores I can realistically visit.

### Acceptance Criteria
- User provides a valid Portuguese postal code (format `XXXX-XXX`).
- The system resolves the postal code to GPS coordinates via [geoapi.pt](https://geo.iotech.pt) (`GET https://geo.iotech.pt/cp/{postalCode}?json=1`).
- The system returns all store locations within a configurable radius (default: 15 km).
- Distance is calculated using the Haversine formula.
- Postal code coordinates are cached for 24 hours to avoid redundant API calls.
- `GET /api/stores/nearby?postalCode=&radiusKm=` returns stores sorted by distance.
- `GET /api/stores/geocode?postalCode=` returns only the resolved coordinates.

## 7. Product Catalog API

### User Stories
- As a user, I want to search for products by name or category so I can find what I need quickly.
- As a user, I want to see a product's price across all stores and its price history.

### Acceptance Criteria
- `GET /api/products?search=&category=&page=` supports free-text search and category filtering with pagination.
- `GET /api/products/{id}` returns full product details including the current price at every store that carries it.
- `GET /api/products/{id}/alternatives` returns up to 3 cheaper or equivalent alternatives.
- `GET /api/products/{id}/pricehistory` returns a time-series of recorded prices.
- `GET /api/categories` returns the full category hierarchy.
- `GET /api/categories/{idOrSlug}/products` returns products in a given category.

## 8. Admin Scraping API

### User Stories
- As an administrator, I want to check the status of scraping jobs to verify data is being updated.
- As an administrator, I want to manually trigger a scrape for a specific store when needed.

### Acceptance Criteria
- `GET /api/admin/scraping/status` returns the last run time, next scheduled time, and success/failure status for each scraper.
- `POST /api/admin/scraping/trigger/{chainSlug}` enqueues an immediate scrape for the specified chain.

- Supported `chainSlug` values: `continente`, `pingodoce`, `auchan`, `lidl`, `celeiro`.

## 9. Category & Product Mapping Admin

### User Stories
- As an administrator, I want to see how much of the catalog is categorized and matched so I can gauge data quality.
- As an administrator, I want to review products with no category and store products that failed to match a canonical product, so I can repair the catalog.
- As an administrator, I want to bulk-repair mappings (backfill categories, rematch store products) and, when automation can't resolve an item, assign it manually.

### Acceptance Criteria
- `GET /api/admin/mapping/stats` returns total/categorized/uncategorized product counts, match-status breakdown, and match-method breakdown.
- `GET /api/admin/mapping/uncategorized-products` and `GET /api/admin/mapping/unmapped-categories` are paginated/listable and admin-only.
- `GET /api/admin/mapping/store-products?status=&chainSlug=` supports filtering by match status and store chain.
- `POST /api/admin/mapping/backfill-categories` assigns canonical categories to uncategorized products wherever a mapping now resolves, without erroring on unresolved ones.
- `POST /api/admin/mapping/rematch?chainSlug=` re-attempts EAN and brand/name/size/unit matching for unmatched or failed store products, optionally scoped to one chain, and never creates duplicate canonical products.
- `PUT /api/admin/mapping/products/{id}/category` and `PUT /api/admin/mapping/store-products/{id}/canonical` allow manual, per-item correction.

- The Web App admin area (`/Admin/Mapping`) provides a UI over this same API.

### Model backend status (optional feature)
- Admin/Mapping shows whether the optional model backend is enabled, its circuit-breaker state, queue depth, oldest pending job, dead-lettered jobs and stale embeddings. All model features are off by default and every existing feature works unchanged when the model is disabled or unreachable.

## 10. Web Application (Frontend UI)

### User Stories
- As a user, I want a web interface so I can use Savvori without writing API calls.
- As a user, I want to browse and search products visually, compare prices, and manage shopping lists in a browser.
- As an administrator, I want an admin section within the web app where I can view and trigger scraping jobs.

### Acceptance Criteria

**Catalog pages:**
- The home page displays a product search bar and a category grid.
- `/Products` shows a browseable, searchable product catalog with live HTMX search (no page reload).
- `/Products/Detail/{id}` shows full product details: prices across all stores, price history, and alternative suggestions.
- `/Categories` shows the full category tree; `/Categories/Detail/{slug}` shows paginated products in that category.
- `/Stores` lets users search for nearby stores by Portuguese postal code (XXXX-XXX format).

**Shopping list pages:**
- `/ShoppingLists` shows all lists with create, rename, and delete actions.
- `/ShoppingLists/Detail/{id}` allows adding/removing products via HTMX product search (no page reload).
- `/ShoppingLists/Optimize` displays optimization results (cheapest-total, cheapest-store, balanced) and a full store comparison matrix.

**Admin pages:**
- `/Admin` is the admin dashboard.
- `/Admin/Scraping` shows a live auto-refreshing (every 30 s) grid of scraping job statuses.
- `/Admin/Scraping/Detail/{chainSlug}` shows per-chain job history, recent logs, and a manual trigger button.
- `/Admin/Mapping` provides a UI over the category/product mapping admin API (see section 9): stats, uncategorized products, unmapped categories, store-product match status, and repair actions.
- `/Admin/Products` and `/Admin/Stores` provide admin views over the product and store catalogs.

---

This document describes the functional requirements for the Savvori project. See `TECHNICAL_ARCHITECTURE.md` for implementation details.
