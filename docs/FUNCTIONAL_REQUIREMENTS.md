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
- The product catalog includes products from at least Continente and Pingo Doce.
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

---

This document describes the functional requirements for the Savvori MVP. See technical documentation for architecture and implementation details.
