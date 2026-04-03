# Technical Architecture: Savvori MVP

## Overview
Savvori is an ASP.NET Core Web API application designed to help users find the cheapest way to fill their pantries by comparing grocery prices across major Portuguese stores. The MVP supports user accounts, private shopping lists, and automatic product price discovery.

## High-Level Architecture
- **Web App:** ASP.NET Core Razor Pages, using HTMX for dynamic interactions and minimal JavaScript. TailwindCSS for styling. Interacts with the Web API for all data actions.
- **Web API:** ASP.NET Core WebApi controllers, authenticated, exposes endpoints for all business logic. Uses Quartz.NET for background scraping jobs.
- **Data Storage:** PostgreSQL (via Entity Framework Core or Dapper).
- **Authentication:** Cookie-based for the web app, with JWT tokens used for API calls. Email/password registration and login. Secure password hashing (ASP.NET Identity or custom solution).
- **Product Data:** Discovered via APIs (preferred) or web scraping (fallback), modular scrapers per store.
- **Testing:** xUnit (`tests/Savvori.Web.Tests`).
- **Orchestration/Deployment:** .NET Aspire for local development and service orchestration.

## Key Components

### 1. User Accounts & Authentication
- User entity: email, password hash, created/updated timestamps
- Registration, login, logout endpoints (Web API)
- Cookie-based authentication for web app; JWT tokens for API calls
- Account deletion endpoint (GDPR compliance)
- Secure password storage (hash + salt)

### 2. Shopping Lists
- ShoppingList entity: id, userId, name, created/updated timestamps
- ShoppingListItem entity: id, shoppingListId, productId, quantity
- CRUD endpoints for shopping lists and items (Web API)
- Shopping lists are always scoped to the authenticated user

### 3. Product Catalog & Price Discovery
- Product entity: id, name, brand, category, etc.
- Store entity: id, name, location (optional)
- ProductPrice entity: id, productId, storeId, price, lastUpdated
- Product catalog is populated/updated by background jobs (APIs or scrapers)
- Modular scraper/connector design for easy addition of new stores
- Price update scheduler: Quartz.NET jobs run as part of the Web API project

### 4. Web App (Frontend)
- Razor Pages project using HTMX for dynamic UI updates
- TailwindCSS for styling
- All data actions performed via API calls (using cookies/JWT for auth)

## API Endpoints (MVP)
- `POST /register` – Create account
- `POST /login` – Authenticate
- `DELETE /account` – Delete account
- `GET /products` – List products with prices per store
- `GET /shoppinglists` – List user’s shopping lists
- `POST /shoppinglists` – Create shopping list
- `PUT /shoppinglists/{id}` – Update shopping list
- `DELETE /shoppinglists/{id}` – Delete shopping list
- `POST /shoppinglists/{id}/items` – Add item to list
- `DELETE /shoppinglists/{id}/items/{itemId}` – Remove item
- `GET /shoppinglists/{id}/optimize?mode=total|store` – Get optimized list (cheapest total or grouped by store)

## Security & Privacy
- All API endpoints (except register/login) require authentication
- Passwords never stored in plain text
- User data is never shared with third parties
- Users can export or delete their data (GDPR)
- Web app uses cookies for session, API uses JWT for secure calls

## Extensibility
- New stores can be added by implementing a new scraper/connector module
- Product and price models are store-agnostic
- Web app and API are structured for future separation into distinct projects

## Open Questions / Decisions
- [x] Which DBMS to use for MVP? → PostgreSQL
- [x] How to schedule and run background jobs? → Quartz.NET jobs in Web API
- [x] Will the MVP have a web frontend? → Yes, Razor Pages web app
- [x] How to orchestrate/deploy? → .NET Aspire for local/dev

---

This document describes the technical architecture for the Savvori MVP. Update as implementation progresses.
