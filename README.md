# Savvori

Savvori is a smart grocery shopping list API and web app for Portugal. It compares product prices across major Portuguese supermarkets — **Continente**, **Pingo Doce**, **Auchan**, **Lidl**, and **Celeiro** — and helps users find the cheapest way to fill their shopping lists.

Built with ASP.NET Core .NET 10, orchestrated locally with .NET Aspire, SQLite via EF Core. There is no authentication — it is designed to run as a single-user app on a trusted network (e.g. a homelab).

## Project structure

```
src/
    Savvori.AppHost/         Aspire orchestration (starts WebApi, WebApp)
    Savvori.ServiceDefaults/ Shared OpenTelemetry/health-checks/HTTP-resilience wiring
    Savvori.Shared/          EF Core entity models
    Savvori.WebApi/          ASP.NET Core Web API — controllers, scraping, optimization, DbContext
    Savvori.WebApp/          Razor Pages frontend (TailwindCSS v4 + DaisyUI v5, HTMX)
tests/
    Savvori.Api.Tests/       Integration tests against Savvori.WebApi
    Savvori.Web.Tests/       Unit tests covering WebApi and WebApp
    Savvori.E2E.Tests/       End-to-end tests against Savvori.WebApp
Savvori.sln
```

See `CLAUDE.md` for a fuller architecture walkthrough, and `docs/TECHNICAL_ARCHITECTURE.md` / `docs/FUNCTIONAL_REQUIREMENTS.md` for the living design and feature specs.

## Build

Use Windows PowerShell:

```powershell
dotnet build Savvori.sln
```

## Run

Recommended — starts the Aspire dashboard, the Web API, and the Web App:

```powershell
aspire run
```

Aspire dashboard: http://localhost:15888

Or run the Web API directly (creates the SQLite database file on first start):

```powershell
dotnet run --project src/Savvori.WebApi/Savvori.WebApi.csproj
```

OpenAPI description is available in development at:

- http://localhost:5000/openapi/v1.json

## Test

```powershell
dotnet test Savvori.sln
```

Run a single test project directly:

```powershell
dotnet test tests/Savvori.Api.Tests/Savvori.Api.Tests.csproj
```

## Key API Endpoints

| Method | Endpoint | Description |
|--------|----------|-------------|
| GET | `/api/products?search=&category=&page=` | Search/browse product catalog |
| GET | `/api/products/{id}` | Product details with prices per store |
| GET | `/api/products/{id}/alternatives` | Alternative product suggestions (up to 3) |
| GET | `/api/products/{id}/pricehistory` | Price history |
| GET | `/api/stores` | List all store chains |
| GET | `/api/stores/{chainSlug}/locations` | Locations for a store chain |
| GET | `/api/stores/nearby?postalCode=&radiusKm=` | Stores near a postal code (default 15 km) |
| GET | `/api/stores/geocode?postalCode=` | Resolve postal code to coordinates |
| GET | `/api/categories` | Category tree |
| GET | `/api/categories/{idOrSlug}/products` | Products in a category |
| GET | `/api/shoppinglists/{id}/optimize?mode=&threshold=` | Optimize a shopping list |
| GET | `/api/admin/scraping/status` | Scraping job status|
| POST | `/api/admin/scraping/trigger/{chainSlug}` | Trigger a scrape|
| GET | `/api/admin/mapping/stats` | Category/match mapping statistics|
| GET | `/api/admin/mapping/uncategorized-products` | Canonical products with no category|
| GET | `/api/admin/mapping/unmapped-categories` | Scraped category strings with no canonical mapping|
| GET | `/api/admin/mapping/store-products?status=&chainSlug=` | Store products filtered by match status/chain|
| POST | `/api/admin/mapping/backfill-categories` | Re-run category mapping for uncategorized products|
| POST | `/api/admin/mapping/rematch?chainSlug=` | Re-run EAN/name matching for unmatched store products|
| PUT | `/api/admin/mapping/products/{id}/category` | Manually assign a category to a product|
| PUT | `/api/admin/mapping/store-products/{id}/canonical` | Manually link a store product to a canonical product|

### Optimization modes

| Mode | Description |
|------|-------------|
| `cheapest-total` | Splits the list across stores to minimize total cost |
| `cheapest-store` | Finds the single best store for the whole list |
| `balanced` | Balances cost vs. convenience; configurable savings threshold (default €2.00) |
| `compare` | Returns a full price matrix across all stores |

## Configuration

| Variable | Description | Example |
|----------|-------------|---------|
| `ConnectionStrings__savvori` | SQLite connection string (default `Data Source=data/savvori.db`, relative to the API working directory; the directory is created on startup) | `Data Source=/data/savvori.db` |

Configuration can be set via `appsettings.json`, `appsettings.Development.json`, or environment variables. EF Core migrations are applied automatically at startup, creating the database file if it does not exist.

## Background jobs

Product prices are scraped from store websites **twice daily** via Quartz.NET background jobs running inside the Web API process.

| Scraper | Technology | Status |
|---------|-----------|--------|
| Continente | SFCC JSON endpoint | ✅ Implemented |
| Pingo Doce | SFCC JSON endpoint | ✅ Implemented |
| Auchan | SFCC + `data-gtm` attribute, page-based pagination | ✅ Implemented |
| Lidl | JSON search API (`/q/api/search`) | ✅ Implemented |
| Celeiro | Magento, `.product-item-info` microdata | ✅ Implemented |

## Notes

- Targets .NET 10. Keep package versions consistent across projects.
- Adjust ports or Kestrel settings via `appsettings.*.json` as needed.
- Location lookups use [geoapi.pt](https://geo.iotech.pt) (free, no auth required).
- `tests/Savvori.Web.Tests/LiveScraperTests.cs` makes real HTTP calls to live grocery-store websites — it's expected to be slow/flaky in sandboxed or offline environments and is unrelated to code regressions.