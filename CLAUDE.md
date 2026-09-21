# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## What this is

Savvori is a grocery price-comparison API + web app for Portugal. It scrapes product prices from major Portuguese supermarket chains (Continente, Pingo Doce, Auchan, Lidl, Celeiro; chains without a working scraper are not kept as stubs), and helps users build shopping lists and optimize them for cheapest cost across stores.

ASP.NET Core / .NET 10, orchestrated locally with .NET Aspire, SQLite via EF Core. There is no authentication (single-user homelab deployment).

## Build, run, test

Use Windows PowerShell syntax (this is a Windows dev environment).

- **Run everything (recommended)**: `aspire run` from repo root — starts the Aspire dashboard (http://localhost:15888), the Web API, and the Web App. If `aspire` isn't on PATH, use `& "$env:USERPROFILE\.dotnet\tools\aspire.exe" run`.
- **Build**: `dotnet build Savvori.sln`
- **Run Web API directly** (creates the SQLite file at `ConnectionStrings:savvori`, default `data/savvori.db`): `dotnet run --project src/Savvori.WebApi/Savvori.WebApi.csproj`
  - OpenAPI (dev): `http://localhost:5000/openapi/v1.json`, Health: `/health`
- **Test all**: `dotnet test Savvori.sln` (runs in Microsoft.Testing.Platform mode via `global.json` `test.runner`; `LiveScraperTests` hit the live network and are a known flake)
- **Test a single project**: `dotnet test tests/Savvori.Api.Tests/Savvori.Api.Tests.csproj` (same for `Savvori.Web.Tests`, `Savvori.E2E.Tests`)
- **Test a single test**: `dotnet test tests/Savvori.Web.Tests/Savvori.Web.Tests.csproj --filter "FullyQualifiedName~ClassName.MethodName"`
- **Aspire MCP tools** (list_resources, console_logs, traces): available once `aspire run` is active; configured in `.vscode/mcp.json`.

## Solution structure

```
src/
  Savvori.AppHost/         Aspire orchestration (AppHost.cs wires up services)
  Savvori.ServiceDefaults/ Shared OpenTelemetry, health checks, HTTP resilience wiring
  Savvori.Shared/          EF Core entity models (User, Product, Store, ShoppingList, ...)
  Savvori.WebApi/          ASP.NET Core Web API — the actual DbContext, controllers, scraping, business logic
  Savvori.WebApp/          Razor Pages frontend (TailwindCSS v4 + DaisyUI v5, HTMX)
tests/
  Savvori.Api.Tests/       Integration tests against Savvori.WebApi (WebApplicationFactory)
  Savvori.Web.Tests/       Unit tests covering both WebApi and WebApp
  Savvori.E2E.Tests/       End-to-end tests against Savvori.WebApp
```

**Gotcha**: `Savvori.Shared` contains an unused, stale `SavvoriDbContext.cs` and `Class1.cs` left over from project scaffolding. The DbContext actually wired into DI (`SavvoriDbContext` in namespace `Savvori.WebApi`) lives in `src/Savvori.WebApi/SavvoriDbContext.cs` and references the entity classes from `Savvori.Shared`. Don't confuse the two when looking for "the" DbContext.

## Architecture

### Web API (`src/Savvori.WebApi`)

- Minimal-API style bootstrap in `Program.cs`, but endpoints are grouped into MVC controllers under `Controllers/` (Products, Categories, Stores, ShoppingLists, Optimize, ScrapingAdmin, MappingAdmin).
- Database: `SavvoriDbContext` over SQLite (`Microsoft.EntityFrameworkCore.Sqlite`). Tests (`ASPNETCORE_ENVIRONMENT=Testing`) use a shared in-memory SQLite database created with `EnsureCreated`. `decimal` columns are stored as REAL (see `ConfigureConventions`) so ordering and aggregates work in SQLite. There is a single migration (`InitialCreate`); the partial unique index on `StoreProductPrices.IsLatest` is declared in the model.
- EF migrations are applied automatically at startup, before seeding. There is no authentication or user model: every endpoint is open, including the admin ones.

### Scraping (`src/Savvori.WebApi/Scraping`)

- `IStoreScraper` — contract each store chain scraper implements (`Scrapers/*Scraper.cs`).
- `BaseHttpScraper` — shared HttpClient + Polly v8 retry policy + AngleSharp HTML parsing.
- `ScraperResultProcessor` — normalizes raw scraper output and upserts Products/Prices.
- `ProductNormalizer` — size/unit extraction, text normalization, brand extraction.
- `ProductMatcher` / `CategoryMapper` / `CategoryTaxonomy` / `CategorySeeder` — category taxonomy assignment and product-to-category mapping (added in the "Phase 4" work).
- `StoreScrapeJob` — Quartz.NET job running all registered scrapers, scheduled twice daily.
- New store chain = implement `IStoreScraper` + register with DI; scrapers differ by underlying platform (SFCC JSON, SAP Hybris HTML, etc.) — check an existing scraper for the closest-matching platform before writing a new one from scratch.

### Model-assisted matching and categories (`src/Savvori.WebApi/Modeling`)

- Optional, feature-flagged (`Model:Enabled`, default off) and never on a request or scrape path: a remote Ollama model embeds product text, proposes cross-chain matches and category predictions in background Quartz jobs. Everything degrades to the deterministic behaviour when the model is down (circuit breaker + durable `ModelJobs` queue). The model only ever suggests: every merge or category it proposes is applied by a person (one at a time or in bulk, undoable); there is no dry-run switch. Design, rules and per-phase reports: `docs/MODEL_MATCHING_PLAN.md`; the category tree is taxonomy v2, applied automatically at startup: `docs/TAXONOMY_V2.md`.

### Optimization (`src/Savvori.WebApi/Services`)

- `IShoppingOptimizer` / `ShoppingOptimizer` implement four modes: `cheapest-total`, `cheapest-store`, `balanced` (configurable savings threshold, default €2.00), `compare` (full price matrix). Modes are pluggable.
- `ILocationService` / `GeoApiLocationService` — postal code → coordinates via geoapi.pt, Haversine distance for "stores nearby", `IMemoryCache` with 24h TTL.

### Web App (`src/Savvori.WebApp`)

- Razor Pages, styled with TailwindCSS v4 + DaisyUI v5. CSS is built from `wwwroot/css/input.css` via `npm run build:css`, wired as an MSBuild target that runs before every build (skipped when `$(CI) == 'true'`). Generated `site.css` is git-ignored — run `npm install` in `src/Savvori.WebApp` before first build if `node_modules` is missing.
- HTMX (CDN) drives in-page updates without full reloads (debounced product search, admin auto-refreshing status tables, etc.).
- Talks to the Web API exclusively through `SavvoriApiClient` (typed HttpClient, DI-registered), which wraps every Web API endpoint. Base address comes from Aspire service discovery keys, falling back to `http://localhost:5000`.
- Admin pages live under `Pages/Admin/` (own layout).

### Aspire (`src/Savvori.AppHost`, `src/Savvori.ServiceDefaults`)

- `AppHost.cs` is the single place new services/infrastructure get registered — add `ProjectReference` in the AppHost csproj, then `builder.AddProject<Projects.X>("name")`, wire dependencies with `.WithReference(...).WaitFor(...)`.
- Every executable project must call `builder.AddServiceDefaults()` / `app.MapDefaultEndpoints()` for OpenTelemetry, health checks, and HTTP resilience (`AddStandardResilienceHandler()`) to stay consistent.

## Docs

- `docs/FUNCTIONAL_REQUIREMENTS.md` and `docs/TECHNICAL_ARCHITECTURE.md` are the living spec/architecture docs for this project — check them for feature scope and design rationale, and update them when functional behavior or architecture changes (this repo's contributing convention, see `.github/copilot-instructions.md`).
- Keep package versions consistent across all `net10.0` projects (currently EF Core / ASP.NET packages on 10.0.11, Aspire packages on 13.5.x; Quartz stays on 3.20.x until Quartz.Extensions.Hosting ships a 4.x release) when bumping dependencies.
