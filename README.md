# Savvori

Savvori is a smart grocery shopping list API for Portugal. It compares product prices across major Portuguese supermarkets — **Continente**, **Pingo Doce**, **Auchan**, **Minipreço**, **Lidl**, **Intermarché**, and **Mercadona** — and helps users find the cheapest way to fill their shopping lists.

Built with ASP.NET Core .NET 10 minimal API.

## Project structure

```
src/
    Savvori.WebApi/
        Program.cs
        appsettings.json
        appsettings.Development.json
        Savvori.WebApi.csproj
tests/
    Savvori.Web.Tests/
        UnitTest1.cs
        Savvori.Web.Tests.csproj
Savvori.sln
```

## Local testing accounts

On first startup the API seeds two test users into the database. These are for **local development only** — never use them in production.

| Role | Email | Password |
|------|-------|----------|
| Admin | `admin@savvori.dev` | `Admin123!` |
| User | `user@savvori.dev` | `User123!` |

Log in via `POST /api/auth/login` to get a JWT token, then pass it as `Authorization: Bearer <token>`.

The admin account has access to the `/api/admin/scraping/*` endpoints. The normal user account has access to authenticated shopping-list and optimization endpoints.

## Build

Use Windows PowerShell:

```powershell
dotnet build Savvori.sln
```

## Run

Run the web app (development profile):

```powershell
dotnet run --project src/Savvori.WebApi/Savvori.WebApi.csproj
```

OpenAPI description is available in development at:

- http://localhost:5000/openapi/v1.json

## Test

```powershell
dotnet test Savvori.sln
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
| GET | `/api/shoppinglists/{id}/optimize?mode=&threshold=` | Optimize a shopping list (auth required) |
| GET | `/api/admin/scraping/status` | Scraping job status (admin only) |
| POST | `/api/admin/scraping/trigger/{chainSlug}` | Trigger a scrape (admin only) |

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
| `ConnectionStrings__DefaultConnection` | PostgreSQL connection string | `Host=localhost;Database=savvori;Username=sa;Password=...` |

Configuration can be set via `appsettings.json`, `appsettings.Development.json`, or environment variables.

## Background jobs

Product prices are scraped from store websites **twice daily** via Quartz.NET background jobs running inside the Web API process.

| Scraper | Technology | Status |
|---------|-----------|--------|
| Continente | SFCC JSON endpoint | ✅ Implemented |
| Pingo Doce | SFCC JSON endpoint | ✅ Implemented |
| Auchan | SFCC + `data-gtm` attribute, page-based pagination | ✅ Implemented |
| Minipreço | SAP Hybris, `.product-list__item` selectors | ✅ Implemented |
| Lidl | — | 🔜 Stub (no online catalog) |
| Intermarché | — | 🔜 Stub (no online catalog) |
| Mercadona | — | 🔜 Stub (no online catalog) |

## Notes

- Targets .NET 10 (RC). Keep package versions consistent across projects.
- Adjust ports or Kestrel settings via `appsettings.*.json` as needed.
- Location lookups use [geoapi.pt](https://geo.iotech.pt) (free, no auth required).