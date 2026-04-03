# Functional Requirements: Savvori MVP

## 1. Shopping Lists

### User Stories
- As a registered user, I want to create multiple shopping lists so I can organize my grocery needs.
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
- The product catalog includes products from Continente, Pingo Doce, Auchan, and Minipreço (via web scraping). Lidl, Intermarché, and Mercadona are planned (stubs in place; no online grocery catalog currently available for those chains).
- Product prices are updated automatically up to twice daily.
- The system prefers APIs for price discovery, but uses web scraping if APIs are unavailable.
- The system is designed to easily add new stores in the future.

## 3. Account Creation and Login

### User Stories
- As a new user, I want to register with my email and password so I can have a private account.
- As a returning user, I want to log in securely to access my shopping lists.
- As a user, I want to delete my account and all associated data if I choose.

### Acceptance Criteria
- Users can register with email and password.
- Users can log in and log out securely.
- Passwords are stored securely (hashed and salted).
- Users can delete their account and all associated data.
- User data is not shared with third parties.
- The system supports GDPR compliance (data export and deletion on request).

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
- Both endpoints require the `admin` role.
- Supported `chainSlug` values: `continente`, `pingo-doce`, `auchan`, `minipreco` (stubs: `lidl`, `intermarche`, `mercadona`).

---

This document describes the functional requirements for the Savvori project. See `TECHNICAL_ARCHITECTURE.md` for implementation details.
