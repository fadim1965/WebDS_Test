# LibanPost eShop — ASP.NET Core 9 e-commerce platform

A complete, runnable e-commerce application built on the stock ASP.NET Core MVC template, themed to
match the LibanPost web identity. It has a real domain model, a real database, real authentication,
a working cart and checkout, inventory that cannot be oversold, an admin back office, and a seed
catalogue drawn from the brand's own domain — so it is demonstrable the moment it starts.

```
dotnet run
```

Then open the URL printed in the console. The store, its schema and the demo catalogue are created
automatically on first run. The store is **SQLite by default** and can be switched to **SQL Server**
by configuration alone — see [Choosing the store](#choosing-the-store-sqlite-or-sql-server).

A complete, grouped inventory of every feature is in [`docs/FEATURES.md`](docs/FEATURES.md); the
architectural record, and where the platform deliberately stops, is in
[`docs/ECOMMERCE_PLAN.md`](docs/ECOMMERCE_PLAN.md).

Guides for the people who *use* the platform rather than build it are in
[`docs/`](docs/README.md): a [shopper guide](docs/USER_GUIDE.md),
a [vendor guide](docs/VENDOR_GUIDE.md) and an
[administrator guide](docs/ADMIN_GUIDE.md). This README is the developer and operator reference.

---

## Demo accounts

| Role | Email | Password |
|---|---|---|
| Administrator | `admin@libanpost.example` | `Admin#12345` |
| Customer | `customer@libanpost.example` | `Customer#123` |

The administrator reaches the back office at **`/Admin`**.

---

## Two catalogue providers

The catalogue is the one part of the app that can be served from either of two places. Everything
else — cart, checkout, orders, inventory — stays on the local implementation.

| Mode | Configuration | What serves the catalogue |
|---|---|---|
| **Local** (default) | `Shopify:Enabled = false` | EF Core + the configured store (SQLite by default, SQL Server when selected) |
| **Shopify, mocked** | `Shopify:Enabled = true`, `Shopify:UseMockData = true` | `MockShopifyStorefrontClient` — real Shopify response shapes from a local fixture, no token or network needed |
| **Shopify, live** | `Shopify:Enabled = true`, `UseMockData = false`, domain + token set | `ShopifyStorefrontClient` → Storefront API GraphQL |

```jsonc
"Shopify": {
  "Enabled": true,
  "ShopDomain": "your-store.myshopify.com",
  "StorefrontAccessToken": "…",     // Storefront API token from a Headless channel — NOT the Admin token
  "ApiVersion": "2026-10",          // pinned deliberately; Shopify breaks things quarterly
  "UseMockData": false
}
```

Switching providers changes one service registration. See
[`Program.cs`](Program.cs) and [`Services/Shopify/`](Services/Shopify).

### What the Shopify adapter actually does

- Maps `ICatalogService` onto Storefront API GraphQL — browse, search, category browsing, product
  detail, featured, best sellers, related products, brands and price bounds.
- Translates our `ProductQuery` into Shopify's search syntax (`product_type:`, `vendor:`, `tag:`,
  `available_for_sale:`, quoted free text) and our sort orders onto `ProductSortKeys`.
- Handles GraphQL `errors` before deserializing, and retries throttling (`THROTTLED`) and 5xx with
  exponential backoff.
- Caches collections, brands and price bounds in `IMemoryCache`, because each is a billed API call
  that the SQLite version got for free.

### Three mismatches it handles explicitly rather than hiding

1. **Paging.** `PagedResult<T>` is page-number based; the Storefront API is cursor-only. Reaching
   page *N* means walking the cursors, bounded by `MaxItemsWalked` so rate-limit cost stays
   predictable.
2. **Price filtering.** Shopify cannot filter by price range in the `query` argument. When a price
   filter is present the window is over-fetched and filtered in memory, and the reported total is
   therefore an **estimate**. The SQLite provider reports an exact count.
3. **Identifiers.** Shopify uses global IDs (`gid://shopify/ProductVariant/123`). The domain model
   uses `int` keys, so the numeric tail is parsed into `Id`. Non-numeric IDs fall back to a
   deterministic hash — never `string.GetHashCode()`, which is randomised per process.

`ICatalogService` is read-only, which is exactly why this port is contained. Cart and checkout still
run on the local implementation; porting those means adopting Shopify's cart tokens and hosted
checkout, which would delete `OrderService`, `IInventoryService`, `IPaymentGateway` and the order
state machine rather than translate them.

---

## Choosing the store: SQLite or SQL Server

One shared `ApplicationDbContext` model serves both providers, so the choice is configuration only
and nothing below the DbContext registration is provider-specific.

| `Database:Provider` | Store | Connection used |
|---|---|---|
| `Sqlite` (default) | `App_Data/store.db`, created on first run | `ConnectionStrings:DefaultConnection` |
| `SqlServer` | the `MVStores` database | `ConnectionStrings:MultiVendorsStoresConn` |

```jsonc
"ConnectionStrings": {
  "DefaultConnection": "Data Source=App_Data/store.db",
  "MultiVendorsStoresConn": "Data Source=.;Initial Catalog=MVStores;User ID=sa;Password=…;Connect Timeout=240;TrustServerCertificate=True;"
},
"Database": {
  "Provider": "SqlServer",          // "Sqlite" (default) or "SqlServer"
  "MigrateFromSqlite": true,        // one-time import into an empty SQL Server store
  "SqliteSource": "App_Data/store.db"
}
```

The SQL credentials themselves are **not** committed: the real
`ConnectionStrings:MultiVendorsStoresConn` (and `Payments:WebhookSecret`) live in
`appsettings.Local.json`, which is gitignored. The committed files carry empty
placeholders, and `Program.cs` loads the Local file last so it overrides them. On a
machine without that file the empty connection string makes the app fall back to the
SQLite store — a fresh clone still boots.

The schema is created with `EnsureCreatedAsync` for whichever provider is selected, so switching
back and forth is a configuration change only (or an environment variable, e.g.
`Database__Provider=SqlServer`).

### One-time import of an existing SQLite store

When `Database:Provider` is `SqlServer` **and** `Database:MigrateFromSqlite` is `true`, the app
imports `Database:SqliteSource` into SQL Server at startup, before seeding:

- Rows are copied through the **same** EF model — read with the SQLite provider, written with the
  SQL Server provider — so every relationship and column mapping is already the one the app uses.
- Primary keys are preserved, so foreign keys stay intact. The import runs inside a single
  transaction with `IDENTITY_INSERT` enabled per identity table, which also makes it
  all-or-nothing.
- It **self-skips when the destination already holds data**, so leaving the flag on is harmless:
  later startups log *"SQL Server store already contains data; skipping the SQLite import."* and
  do nothing.
- The SQLite file is only ever read, so it remains a working fallback.

### Connecting to a local SQL Server

`Data Source=.` connects over **shared memory** and works without TCP/IP. Where TCP/IP is enabled
(the usual case) `Data Source=localhost` or `Data Source=.\SQLEXPRESS` also works; where it has
been disabled, `Data Source=.` is the one that connects.

---

## Logging

Everything the application logs goes to two places: the console, and the `Log` table in the
configured store. The table is what a deployment keeps — the console is gone as soon as the window
is, and an error that only ever appeared there is an error nobody can diagnose.

| Level | Written to `Log` |
|---|---|
| `Information` | ✅ the application's own events — start-up, seeding, the import, the sink itself |
| `Warning` | ✅ including framework warnings (a failed HTTPS redirect, EF model validation) |
| `Error`, `Critical` | ✅ with the **full exception and stack trace** in the `Exception` column |
| `Debug`, `Trace` | only if `Logging:Database:LogLevel:Default` is lowered |

The sink is a normal `ILoggerProvider` (`Services/Logging/`), so it composes with the console
instead of replacing it, and the standard filtering applies. Filtering the **table** does not have
to mean filtering the console:

```jsonc
"Logging": {
  "LogLevel": {                                            // console + everything
    "Default": "Information",
    "Microsoft.AspNetCore": "Warning"
  },
  "Database": {                                            // the Log table alone
    "Enabled": true,
    "Application": "WebDS_Test",                           // written to every row
    "QueueCapacity": 10000,                                // drop-and-report if the queue fills
    "BatchSize": 100,                                      // rows per insert
    "FlushSeconds": 2,                                     // how long a line may wait
    "RetentionDays": 0,                                    // 0 keeps everything; >0 deletes daily
    "LogLevel": {
      "Default": "Information",
      "Microsoft.AspNetCore": "Warning",
      "Microsoft.EntityFrameworkCore.Database.Command": "Warning"
    }
  }
}
```

`Logging:Database:LogLevel` works because the sink declares `Database` as its provider alias. The
defaults keep request-level framework noise out of the table while the application's own
`Information` events and every `Warning`/`Error` are kept. Set `Logging:Database:Enabled` to `false`
to remove the sink entirely; logging then behaves exactly as it did before the table existed.

Design points worth knowing:

- **A logging call cannot fail, block, or throw.** The caller formats the line and puts it on a
  bounded queue; a background service (`DbLogWriter`) writes it in batches. A slow or unreachable
  database costs log lines, never a request.
- **The sink never logs itself.** Every insert into `Log` is made with the sink suppressed, so the
  SQL that writes the rows is not itself queued for writing — without that, EF's own command
  logging would make the table grow forever.
- **The table is guaranteed.** `EnsureCreated` only builds the whole schema for an empty store, so
  `Data/LogTable.cs` creates `Log` on demand for a store that predates the sink. That is how the
  SQLite development file — created long before this feature — got its table.
- **Rows are never inserted one at a time** while the queue is busy: a batch is written per flush,
  one statement, so a burst of logging is one round-trip per hundred lines.
- **The `Log` table is not store data.** It is excluded from the SQLite import (the source may not
  even have the table), and nothing in the domain model references it.

To read the trail:

```sql
SELECT TOP 50 Id, Logged, Level, Logger, Message FROM [Log] ORDER BY Id DESC;
SELECT Id, Logged, Logger, Exception FROM [Log] WHERE Level = 'Error' ORDER BY Id DESC;
```

---

## Visual identity

The theme is built from the brand's own stylesheet (`css-EN.css`), not approximated. Two colours
dominate it and are used throughout:

| Token | Value | Evidence |
|---|---|---|
| Primary blue | `#006db7` | 91 occurrences — links, top bar, active states, iconography |
| Accent yellow | `#fed300` | 34 occurrences — buttons, link hover, logo mark |
| Footer | `#015a97` | with the brand's diagonal chevron gradient |
| Header bar | `#f1f1f1` | |
| Surfaces | `#f6f6f6`, `#efefef` | |
| Muted text | `#2a4c62`, `#6b6b6b` | |
| Borders | `#e6e6e6`, `#d7d7d7` | |
| Danger | `#e33c3c` | |

Brand conventions reproduced deliberately:

- **Buttons are yellow with black text**, a 2px yellow border, and on hover the fill drops out to
  transparent while the border stays. This outline-on-hover behaviour is distinctive and is applied
  to `.btn-brand`.
- **Links are blue and turn yellow on hover.**
- **Corners are tight** — 2–4px, matching the brand's own scale rather than a soft pill look.
- The active channel tab uses the brand's rounded top corners (`20px 20px 0 0`).

The palette is emitted as CSS custom properties from configuration, so colours change in
`appsettings.json` and nowhere else. `wwwroot/css/brand.css` is the single stylesheet.

### Two things to supply

1. **The logo.** `Branding:LogoUrl` is `null`, so a text wordmark (`LibanPost` in blue/black) renders
   in the header and footer. Drop the official file into `wwwroot/img/` and set
   `"LogoUrl": "/img/logo.png"`. The layout already renders an `<img>` at the configured
   `LogoHeight` when the path is set.
2. **The licensed typeface.** The live site uses Gotham, which cannot be redistributed here, so
   `BrandingOptions.FontStack` declares a free geometric stack instead. For exact type matching, put
   the webfonts in `wwwroot/lib/fonts/` and add an `@font-face` block to `brand.css`.

### Shop template themes

Vendors can choose one of several **storefront template themes** for their shop. A theme is data, not
a stylesheet: `ShopTheme` carries the same palette tokens `brand.css` already consumes, plus a font
stack, corner radius and shadow. Applying one writes those `--brand-*` custom properties onto the
`<html>` element as an inline style, which outranks the `:root` defaults — so the *entire* page
(chrome, hero, cards, buttons, footer) re-skins with no markup changes and no per-theme CSS.

| Theme key | Name | Mood | Signature |
|---|---|---|---|
| `postal-classic` | Postal Classic | Corporate | The LibanPost blue/yellow default |
| `midnight` | Midnight | Dark | Slate surfaces, cyan accent, 10px radius |
| `sunrise` | Sunrise | Warm | Orange/cream, 14px radius |
| `boutique` | Boutique | Elegant | Plum + gold, serif face, square corners |
| `forest` | Forest | Natural | Deep green + ochre, soft surfaces |
| `mono` | Mono | Minimal | Black/white, square corners, no shadow |

- The catalogue lives in `Services/ThemeCatalog.cs` (`IThemeCatalog`). Resolution always goes through
  `Get`, which falls back to the default, so an unknown or blank `Shop.ThemeKey` can never render an
  unstyled storefront.
- A vendor picks one at **`/Vendor/Appearance`** (also offered during shop registration). Each
  preview card carries its own theme's variables inline, so the picker shows six live previews while
  the surrounding admin page keeps the vendor's current theme.
- `Views/Shops/Details.cshtml` renders the themed storefront: a hero (banner + scrim when
  `BannerUrl` is set, else a palette gradient), a filter rail and the product grid.
- The theme is resolved **in the controller**, not the view, because the layout writes `<html>`
  before the body view runs — `ViewData["ShopTheme"]` must be set before layout rendering.
- Adding a theme is a single entry in `ThemeCatalog`; no view or CSS changes are required.

---

## What is implemented

### Storefront
- **Home** — hero, department tiles, featured products, best sellers, order tracking entry.
- **Catalogue** — browse by department (including sub-categories), free-text search across product
  name, description, brand and SKU, price-range filter, brand filter, in-stock filter,
  minimum-rating filter, eight sort orders (including top-rated, most-reviewed and relevance) and
  paging. Every filter lives in the query string, so any result set is bookmarkable. Soft-deleted
  and unpublished products never appear.
- **Search suggestions** — the header search box offers type-ahead products and departments from
  `GET /Catalog/Suggest`, with keyboard navigation; without JavaScript it is an ordinary search box.
- **Product detail** — image gallery with thumbnail switching, variant selection, live stock
  messaging including "only N left", sale pricing with a strike-through, add-to-cart, wishlist,
  related products, and the full ratings and reviews panel (see
  [Reviews and ratings](#reviews-and-ratings)).
- **Cart** — line editing, per-line stock warnings, free-shipping progress hint, discount codes
  (apply/remove, re-priced on every render), live totals, empty state, and a JSON summary endpoint
  (`GET /Cart/Summary`).
- **Checkout** — validated contact and shipping form, saved-address picker, optional save-to-address-
  book, payment choice (cash on delivery, or card when a hosted gateway is configured), order summary,
  and a confirmation page with a status timeline. Card orders hand off to the provider's page and are
  confirmed by its webhook (see [Payments](#payments)).
- **Orders** — order history and detail, plus guest tracking by order number + email.
- **Account** — register, sign in, sign out, profile, password change, reset by emailed link,
  wishlist, address book.

### Admin back office (`/Admin`)
- **Dashboard** — revenue (lifetime and 30-day), order counts by state, inventory value, stock alerts.
- **Products** — search/category/state filters (active, draft, out of stock, deleted); create and edit
  including the full variant matrix and an image gallery that accepts typed URLs *and* uploaded files;
  publish toggle; soft delete and restore.
- **Categories** — full tree with indentation, product counts, and guards against cycles.
- **Inventory** — stock levels, manual adjustments, and an append-only stock-movement ledger.
- **Orders** — filterable queue, detail, and status transitions that only legal transitions allow.
  Cancelling restores stock; refunding calls the payment gateway. The order page carries the order's
  returns plus *Refunded so far* / *Still owed to shopper* figures.
- **Returns** — the returns desk for the whole marketplace: the RMA queue with exportable CSV/Excel,
  deciding or receiving returns on a shop's behalf, and the **Issue refund** step that is only
  available here (see [Returns and refunds](#returns-and-refunds)).
- **Customers** — search, detail with lifetime spend, and activate/deactivate.
- **Exports** — the orders, products and customers lists download as CSV or Excel, honouring the
  filters currently applied (see [Exports](#exports-csv-and-excel)).
- **Coupons** — create and edit discount codes: percent or fixed amount, minimum spend, validity
  window, total and per-shopper use caps, optional single-shop scoping, and an activate toggle. A code
  that has already been redeemed is deactivated rather than deleted, so order history stays honest.
- **Payouts** — what each shop is owed, and the record of settling it. The page lists every shop's
  outstanding balance (gross, commission withheld, net) and a **Pay out** button that settles the
  whole balance in one move; the resulting statement can be downloaded as CSV or Excel, and the
  settlement history is exportable. A payout can be **marked failed** (the bank refused) or
  **reversed** (recorded in error) while it is still *Paid* — both release the covered entries back
  to the balance without editing any financial row (see
  [Vendor commission and payouts](#vendor-commission-and-payouts)).
- **Shops** — approve, reject, suspend and reinstate sellers, set commission, see the payout
  destination recorded per shop, and jump to that shop's audit history.
- **Audit trail** — the administrative history (who did what, to which record, when) with filters,
  per-entity history links and CSV/Excel export (see [The audit trail](#the-audit-trail)).
- **Reviews** — the moderation queue: filter by state (pending / published / rejected) and by star
  rating, then publish, hide, return to the queue, or delete outright.
- **Emails** — the transactional outbox: every message the store has queued or sent, filterable by
  state and type, with a rendered preview and a one-click resend for anything that failed.

### Vendor area (`/Vendor`)
- **Dashboard** — product, order, gross-sales and outstanding-payable figures for the vendor's shop.
- **Products** — the catalogue CRUD scoped to the vendor's own shop, including image uploads.
- **Orders** — the shop's order book; the shop registers its own shipments through the built-in
  shipping simulator.
- **Returns** — the shop's RMA worklist: approve or reject requests with a note the shopper sees,
  then record the goods back on the shelf. Another shop's return is a `404`. The refund itself is
  staff-only and never shown here.
- **Reviews** — every review left against the shop's products, with the shop's aggregate score and
  badge, and one public reply box under each review.
- **Payouts** — the vendor's own money page: the outstanding balance, every settlement with its
  statement and its outcome badge (`Paid` / `Failed` / `Reversed`, with the reason), and the recent
  ledger entries behind both. Money is read-only — a vendor cannot settle itself — but the **payout
  destination** (account name, bank, IBAN) is theirs to keep current.
- **Appearance** — choose the storefront template theme from the catalogue (see
  [Shop template themes](#shop-template-themes)); each option previews live before it is applied.

---

### Reviews and ratings

The enhancement is one vertical rather than a scatter of features, because reputation is what makes a
multi-vendor marketplace feel different from a catalogue: it gives every product a score and every
seller a scoreboard.

- **Who may review** — a signed-in shopper with a *paid, shipped or delivered* order line for that
  product. One review per shopper per product; the form becomes an edit form once a review exists.
  Whether a review counts as a **verified purchase** is decided server-side from the order history and
  is never trusted from the form.
- **Publication** — reviews publish immediately (`Reviews:AutoPublish`). The moderation queue is
  reactive: it exists to hide the minority that break the rules, not to gate every review.
- **Votes** — shoppers mark a review helpful or not helpful, one vote each, changing their mind by
  voting again. "Most helpful" ordering reflects that tally, adjusted incrementally so seeded counts
  are never recounted away.
- **Seller replies** — the shop that sold the product may post one public reply, and edit or clear it.
  A reply is the only thing a seller can change; the rating and the words belong to the customer.
- **Aggregates are computed at query time.** There is no denormalised rating column on `Product`, so a
  score can never drift out of step with the reviews it is derived from. Star histograms, per-product
  averages and shop scores all come from grouped `SUM`/`AVG` queries.
- **Seller reputation** — a shop's score is the average across every published review of its products.
  Above the configured thresholds it earns a **Star seller** or **Top rated seller** badge, shown in
  the shop directory, on the shop's own storefront and in the vendor workspace.
- **Where it shows** — stars on every product card and on the product page (with the per-star
  histogram and a filterable, sortable review list), shop scores throughout the shop surfaces, a
  moderation queue in the admin area and a reply workspace in the vendor area.

The maths, the eligibility rule and the vote accounting all live behind `IReviewService`; the admin,
vendor and storefront surfaces are thin façades over it, and the catalogue keeps its own private
rating lookup so `ICatalogService` stays independent of the review subsystem.

---

### Discount codes

A shopper can apply a code in the cart; it follows them to checkout and is written onto the order.
Codes are ordinary rows (`Coupon`) that staff manage at **`/Admin/Coupons`**, and every redemption is
recorded as a `CouponUsage` row.

- **One code at a time.** Applying a second code replaces the first — there is no stacking.
- **The code, not the amount, is what is remembered.** Session holds the bare code; the cart is
  re-priced against the current basket on every render. Editing the cart, or a code expiring or
  hitting its cap between apply and place-order, cannot leave a stale discount on screen — the code
  simply drops off with a notice.
- **Every rule is re-checked at order time**, so nothing relies on what the browser last showed.
  A percentage over 100, an out-of-range fixed amount, a start after the expiry, and a shop-scoped
  code with no shop are all refused at the admin form.
- **The constraints**: percent or fixed amount, minimum subtotal, `StartsUtc`/`ExpiresUtc`, a global
  `MaxUses` and a per-shopper `MaxUsesPerUser`, and optional restriction to one shop. A discount is
  capped at the goods it applies to, so a total can never go negative.
- **Redemption is recorded, not just counted.** `CouponUsage` keeps the user and the order, which is
  how a per-shopper cap is enforced without trusting the client; guests (no account) are held only to
  the global limit.
- **Seeded for demonstration** — `WELCOME10` (10%, once per shopper), `POST5` ($5 over $30) and an
  `EXPIRED` code, so both the accept and reject paths are visible on a fresh store.

---

### Transactional email

Nothing sends mail on the request path. A request that needs to notify somebody writes a row to the
store's own outbox and returns; a background dispatcher delivers it. That keeps a slow or unavailable
mail server from ever slowing down a checkout, and it means every message the store *intended* to
send is auditable — including the ones that failed.

- **The outbox** (`EmailMessages`) is created on demand with guarded DDL, the same way the review
  tables and the `Log` table are, so an existing store gains it on the next start.
- **Idempotent by design.** Each notification carries a `DedupeKey` (`order-shipped:123`); a retried
  request, or a double status change, cannot send the same message twice. Verified: marking an order
  shipped twice produced exactly one shipping email.
- **Retry and failure are first-class.** A failed attempt is retried with exponential backoff up to
  `Email:MaxAttempts`, and is then parked as **Failed** with its `LastError` in full. Nothing is lost
  and nothing crashes the host — the dispatcher logs and moves on.
- **Resend** from the admin Emails page returns a failed message to the queue with a clean history.
- **Templates** — welcome, order confirmation (with the line items, totals and delivery address),
  shipping notice (with tracking), cancellation, password reset and a test message. Each is built as
  a pure function returning HTML **and** a plain-text alternative, so a message renders without a
  database or a request and can be unit-tested.
- **Transports** are configuration only: `File` (the default) writes a real RFC 822 `.eml` — correct
  MIME headers, base64 parts — plus a browser-openable `.html` copy, so email works with no mail
  server; `Smtp` uses the BCL `SmtpClient` once a host is configured. `Email:Enabled = false`
  records messages as **Skipped** rather than dropping them.

**Password reset** is a signed, expiring token rather than a database row: the user id and their
security stamp, sealed with ASP.NET Core Data Protection for `Email:PasswordResetMinutes`. Because
resetting the password rotates the stamp, a link works exactly once and a replayed or expired link is
refused at the form — the request step always reports success, so the endpoint cannot be used to
discover which addresses are registered.

### Rate limiting

Brute-force, spam and checkout-abuse surfaces are throttled by a fixed-window limiter built into the
ASP.NET Core shared framework (no package). Four named policies — `auth`, `checkout`, `review` and
`webhook` — partition by the signed-in user when there is one and by IP address otherwise, so one
shopper cannot exhaust another's allowance. Limits live under `RateLimiting` and the whole thing can be
switched off with `RateLimiting:Enabled = false`, which removes the middleware while leaving the
policies registered. A rejected request gets **429** with `Retry-After`, is logged, and does not
disturb any other policy. Verified: a 40-request sign-in burst let exactly 30 through and rejected the
rest while unrelated pages stayed `200`.

The `webhook` policy is deliberately generous (120/minute): a provider redelivering after an outage
must not be throttled into giving up. The endpoint is defended by its **signature**, not by the rate
limit — a bad signature is refused before anything in the body is trusted.

---

### Payments

The store takes **cash on delivery** by default. When a *hosted* gateway is configured, checkout also
offers **card**, which redirects to the provider's own payment page and is confirmed by a signed
webhook rather than by anything the shopper's browser reports.

- **A provider is two small interfaces.** `IPaymentGateway` charges a card; `IHostedPaymentGateway`
  adds the redirect-and-webhook half (`CreateIntentAsync` → a URL, `ParseWebhook` → a
  `PaymentWebhookEvent`). The application never references a provider SDK, so a real integration is a
  new class plus one line in `Program.cs`.
- **The bundled `simulator` is both sides of the exchange.** It signs its own events with HMAC-SHA256
  over the raw body and posts them back through the *same* `/Payments/Webhook/{provider}` endpoint a
  real provider would call — so the signature check, the idempotency check and the order update are all
  exercised in-process, with no HTTP call to itself and no vendor account. `Payments:Provider` is
  `simulator` by default; set it to `sandbox` to restore a cash-on-delivery-only store.
- **The signature is checked before the body is trusted.** The webhook reads the raw request body (a
  signature covers bytes, and model binding would rewrite them), verifies `X-Payment-Signature`, and
  only then deserialises. An unverified delivery is recorded with `SignatureValid = 0` and answered
  **401**; nothing else in the request is believed.
- **Deliveries are idempotent.** Every event is written to `PaymentEvents`, keyed uniquely by
  `(Provider, EventId)`. A redelivery is answered **200** with `duplicate` and changes nothing, so a
  provider's at-least-once retry cannot capture twice. `MarkPaidAsync` is likewise a no-op on an order
  that is already paid, and cannot revive a cancelled or refunded one.
- **Declining is not failing the order.** A declined event marks the payment attempt `Failed` and
  leaves the order `Pending`, so the shopper can try again (`/Payments/Return/{id}` offers the link);
  a later success captures the same order. Verified end to end: card checkout → placed pending →
  redirect to the simulator → pay → order `Paid`, payment `Succeeded`, one `paid` event row; decline →
  order still `Pending` with a `Failed` row; replayed webhook → `duplicate`.
- **Unknown orders are acknowledged, not retried forever.** An event for an order the store does not
  have is recorded and answered **200** with `order-not-found`; a permanent 4xx would make a provider
  redeliver it indefinitely. An unknown *provider* is a genuine **404**.

---

### Vendor commission and payouts

The marketplace holds the shopper's money and owes the vendor its share. That makes two things
non-negotiable: a sale must be credited **exactly once**, whenever the money is actually collected,
and the amount owed must never be a stored number that can drift from the orders behind it.

- **The ledger is the truth.** `VendorLedgerEntry` is an append-only record of what a shop earned.
  A **sale** credits the vendor's share; a **reversal** takes it back on a cancellation or refund.
  The commission is withheld at the gate — goods only, never the shipping the carrier keeps — and
  rounded half-up (`Math.Round(…, MidpointRounding.AwayFromZero)`), so 49.00 at 5% is 44.10 net,
  not "about 44".
- **Credited when the money lands, not when the order is placed.** A card order credits on the
  signed webhook that confirms capture; a cash-on-delivery order credits at delivery, when the
  carrier actually holds the cash; a counter payment credits when staff mark it paid. All three go
  through one writer, `IVendorLedgerService`, so the arithmetic lives in exactly one place.
- **Idempotent per order.** Every credit and reversal checks the ledger and commits as it writes, so
  a redelivered webhook, a replayed tracking event or a double-clicked button cannot credit twice.
- **A payout is a settlement, not a transaction.** `VendorPayout` records that staff paid a shop:
  a reference (`PAY-2026-0001`), the period, the entry count and the gross/commission/net totals.
  It writes **no** earning — it stamps the entries it covers with `SettledUtc` and its own reference.
  The statement is therefore reproducible from the ledger alone, and "outstanding" is always just
  *the unsettled entries*, a derived figure that cannot disagree with the rows it summarises.
- **Nothing to settle is refused.** An empty, zero or non-positive balance cannot produce a payout,
  and `Payouts:MinimumAmount` sets the threshold below which a balance is not worth a transfer. A
  refund that arrives *after* a settlement leaves the shop owing the marketplace, and the next
  balance shows it as a negative that no payout will clear until new sales cover it.
- **Balances are summed in memory.** Money is `decimal`, and SQLite — one of the two supported
  providers — cannot aggregate a `decimal` in SQL, so the outstanding figure is totalled in C# from
  the unsettled rows. The same rule the dashboard's payable figure already followed.
- **Vendors see, staff settle.** `/Vendor/Payouts` shows the balance, the history with each payout's
  outcome badge and the recent ledger entries; `/Admin/Payouts` is where a settlement is made.
  Payout ids are sequential, so the vendor page treats "belongs to another shop" as `404`, not as a
  redacted something.
- **Settlement outcomes are part of the record.** A payout is `Paid`, `Failed` or `Reversed`. While
  it is still *Paid*, staff can **mark it failed** (with the bank's reason) or **reverse** it
  (recorded in error); either action releases every entry it covered back to the unpaid balance so
  the shop can be settled again, while the payout row keeps its totals — a statement is corrected by
  another row, never rewritten. Both actions are guarded, audited, and shown to the vendor with the
  reason and the handler's name.
- **The payout destination is data, not paper.** Each shop records the account name, bank and IBAN
  settlements go to — collected at registration, editable by the vendor on their payouts page, and
  visible to staff on the shop list. The shop is emailed when a settlement is recorded against it
  (when it has a contact email on file).

---

### Returns and refunds

Sending something back is a lifecycle of its own, not a status flag on an order. It is deliberately
split across three roles: the **shopper** asks, the **shop** decides (and receives the goods), and
the **administrator** — the only role allowed to move money — issues the refund.

- **The lifecycle.** `Requested → Approved → Received → Refunded`, with `Rejected` and the shopper's
  own `Cancelled` as exits. Transitions are validated in the service layer, and an order holds at
  most **one open return** at a time.
- **Per-unit scope.** A return names order lines and unit counts — everything ordered minus what
  earlier returns already took. The request form shows the figure, and the server re-enforces it.
- **The refund is computed, not typed.** Returned goods (their share of any order-level discount
  apportioned by value) plus the proportional tax; shipping comes back only with a whole-order
  return. The total is capped by the ledger amount still owed for that sale and by
  `GrandTotal − RefundedTotal`, so a return can never pay out more than the order received. It is
  demo arithmetic — a real acquirer will not necessarily honour a partial refund this way.
- **The ledger reverses proportionally.** A refund takes back only the refunded goods from the
  shop's sale entry, commission included, idempotent per `(order, type, return)`. A sale that was
  never credited does not refund — loudly.
- **Goods go back on the shelf.** Recording receipt restocks the shop's listing pool (falling back
  to the shared variant stock) with a return-stamped stock movement.
- **Everybody can see it.** The shopper's order page lists the returns with badges and a cancel
  button while one is open; the vendor's order page shows the same; the admin order page shows the
  refunded/owed running totals. References are human-facing (`RMA-2026-0001`).
- **Exportable.** The admin queue filters by status and search, export as CSV or Excel, with the
  open-workload counters above the table.

---

### Product images

A product's gallery is still a list of URLs — that is all the catalogue has ever needed — but the editor
can now **produce** those URLs by uploading files, so putting a picture on a product no longer means
having somewhere else to host it first.

- **A URL is the contract; storage is a detail.** `IImageStore` validates a file and returns a URL;
  `LocalImageStore` (the default) writes it under `Images:Folder` (default `App_Data/uploads`) and the
  app serves that folder as static files at `Images:RequestPath` (default `/uploads`). Pointing at blob
  storage later is one new registration.
- **The bytes are checked, not the name.** The extension must be allowed, the file must be within
  `Images:MaxBytes`, and the leading bytes must really be a JPEG, PNG, GIF or WebP whose format agrees
  with the extension — so a script renamed `logo.png` is rejected. SVG is deliberately not accepted: an
  SVG is a document that can carry a script, and an uploaded one would be served from the store's own
  origin.
- **The store names the file.** Every upload becomes a fresh GUID plus the validated extension inside a
  folder the store owns, so a crafted file name cannot choose a path, overwrite a file, or escape the
  folder. The read is bounded too, so an oversized upload is dropped without being buffered whole.
- **Typed URLs and uploads merge.** Uploaded files are appended after the URLs already in the textarea,
  so line one still decides the primary image. A product may have no images at all — the storefront
  falls back to a placeholder — so the textarea is optional.
- **A bad upload fails the whole save.** Rejection re-renders the editor with the reason and stores
  nothing; files already written for that request are removed again, so a failed save leaves no orphans.
  Removing an image URL deletes the file it pointed at, guarded by whether any other product still
  references the same URL.

---

### Exports (CSV and Excel)

The orders, products and customers lists each carry **Export CSV** and **Export Excel** buttons that
download exactly what the filters on screen select — the export runs the same query the page does, so
the two cannot disagree about what "the current list" means.

- **One description, two writers.** A report is a header list plus rows of typed values
  (`TabularData`); `ExportBuilder` renders that to RFC 4180 CSV or, through ClosedXML, to an `.xlsx`
  workbook. A new export is one description, not two implementations.
- **CSV that opens correctly.** UTF-8 with a byte-order mark, so Excel reads Arabic and accented names
  as UTF-8 instead of the legacy code page, and money and dates written with the invariant culture so
  a machine with a comma decimal separator still parses them. A cell beginning `=`, `+`, `-` or `@` is
  prefixed with an apostrophe, so a customer whose name starts with `=` cannot smuggle a formula into
  a staff member's spreadsheet.
- **A workbook, not a table of strings.** Numbers are real numbers Excel can sum, dates are real
  dates, the header row is bold and frozen, and an autofilter spans the used range.
- **Bounded.** The export takes the same cap as the page (orders: 200; products: 5,000), so a large
  catalogue cannot exhaust memory building a file nobody asked to be unbounded.

---

### The audit trail

Staff actions on money and storefronts are recorded on an append-only `AuditLog` — who did what, to
which record, when, and a one-sentence summary per action.

- **Deliberate entries, not a diff.** Each writer records the action it performed:
  `shop.approved / rejected / suspended / reinstated`, `order.status`,
  `return.approved / rejected / received`, `refund.issued / refund.failed`,
  `payout.created / failed / reversed`. A refund attempt that failed is recorded too — a trail that
  hides the failures cannot explain the retries.
- **Loose references.** The subject is `(entity type, entity id)` and the actor the displayed name,
  not foreign keys, so deleting a row or an account never rewrites the history that mentions them.
- **Written best-effort.** A failing audit write is logged and swallowed, never allowed to undo the
  already-committed action it was documenting.
- **The page.** `/Admin/Audit` filters by action, entity, id and actor with suggestions derived from
  the data, exports the filtered view, and is linked per-shop from the shops page.

---

## Architecture

```
Domain/            entities, enums and configuration models
Data/              ApplicationDbContext + DbSeeder + guarded create-on-demand DDL (Log, reviews, outbox, coupons, payments, vendor ledger)
Services/          application services behind interfaces
  Email/           the transactional outbox: templates, transports, dispatcher, password reset
  Export/          reports as data, rendered to RFC 4180 CSV or a ClosedXML workbook
  Images/          product image uploads: validation, local storage and the URL it hands back
  Logging/         the database log sink: provider, queue and background writer
  Shopify/         Storefront API adapter, client, query translator, mock provider
Controllers/       storefront MVC controllers
Areas/Admin/       back-office controllers, models and views
Views/             Razor views
wwwroot/css/brand.css   the single stylesheet, driven by CSS custom properties
Tests/             xUnit suite (in-memory EF + Moq); excluded from the web project's compile globs
```

### Invariants the code takes seriously

- **Money is `decimal`.** Never `double` or `float`. Configured `decimal(18,2)`, which maps to a real
  decimal column when the provider is switched to SQL Server.
- **Orders snapshot everything.** `OrderLine` stores its own product name, variant name, SKU and unit
  price, so editing or deleting a product can never rewrite an order's history.
- **Products are soft-deleted.** Sold variants are deactivated rather than removed.
- **Stock is committed under optimistic concurrency.** `ProductVariant.RowVersion` is a concurrency
  token refreshed on save, so concurrent checkouts raise `DbUpdateConcurrencyException` instead of
  overselling. `OrderService` retries with a freshly loaded cart and fails with a customer-facing
  message.
- **Checkout is idempotent.** A per-form key is issued into session and must match on POST, and
  `Order.IdempotencyKey` is uniquely indexed.
- **Every stock change is explainable** via the append-only `StockMovement` ledger.
- **Antiforgery is global.** A POST without a token returns 400.
- **Ratings are derived, never stored.** Product and shop scores are computed from published reviews
  at query time, so a score cannot drift out of step with the reviews it describes.
- **A vendor's balance is derived, never stored.** What a shop is owed is the sum of its unsettled
  ledger entries, and a payout only *marks* those entries as settled — so the balance cannot drift
  from the orders and refunds behind it.
- **A page number cannot break a query.** Every paged query clamps the requested page to one that
  exists and computes its offset in `long`, so a hostile `?page=` yields a page rather than a 500.

---

## Configuration

```jsonc
{
  "Store": {
    "Name": "LibanPost eShop",
    "CurrencySymbol": "$",
    "DefaultCountry": "Lebanon",
    "FlatShippingRate": 4.50,
    "FreeShippingThreshold": 60.00,   // free at or above this subtotal
    "TaxRate": 0.11,                  // fraction, so 0.11 is 11%
    "ProductsPerPage": 12,
    "Theme": 1                        // 1 = Amazon-style, 2 = eBay-style, 3 = Etsy-style, 4 = Midnight & Gold (elegant custom)
  },
  "Branding": { "Primary": "#006db7", "Accent": "#fed300", "LogoUrl": null },
  "Reviews": {
    "AutoPublish": true,             // reviews go live at once; the queue is reactive
    "MinBodyLength": 10,
    "MaxBodyLength": 4000,
    "PageSize": 5,                   // reviews per page on the product page
    "StarSellerMinCount": 5,         // badge thresholds, applied to a shop's aggregate score
    "StarSellerMinAverage": 4.2,
    "TopRatedMinCount": 15,
    "TopRatedMinAverage": 4.5
  },
  "Email": {
    "Enabled": true,                 // false records messages as Skipped instead of dropping them
    "Transport": "File",             // File (no mail server needed) or Smtp
    "FromAddress": "no-reply@libanpost.example",
    "FromName": "LibanPost eShop",
    "ReplyToAddress": "support@libanpost.example",
    "DropFolder": "App_Data/outbox", // File transport: where the .eml and .html copies land
    "PollSeconds": 5,                // dispatcher pass interval
    "BatchSize": 25,                 // messages attempted per pass
    "MaxAttempts": 4,                // then the message is parked as Failed
    "PasswordResetMinutes": 120,     // lifetime of a reset link
    "Smtp": { "Host": null, "Port": 587, "UseStartTls": true, "UserName": null, "Password": null }
  },
  "RateLimiting": {
    "Enabled": true,
    "Auth":     { "PermitLimit": 30, "WindowSeconds": 60 },   // sign-in, register, password reset
    "Checkout": { "PermitLimit": 30, "WindowSeconds": 60 },   // placing an order
    "Review":   { "PermitLimit": 30, "WindowSeconds": 60 },   // posting a review or voting
    "Webhook":  { "PermitLimit": 120, "WindowSeconds": 60 }   // provider callbacks (retry-tolerant)
  },
  "Shopify": { "Enabled": false },
  "Payments": {
    "Provider": "simulator",        // sandbox (cash on delivery only) or simulator (hosted card flow)
    "WebhookSecret": "…"            // HMAC-SHA256 key shared with the provider — appsettings.Local.json (gitignored)
  },
  "Images": {
    "Folder": "App_Data/uploads",   // relative paths resolve against the content root
    "RequestPath": "/uploads",      // where the store serves the folder
    "MaxBytes": 5242880,            // largest single upload, in bytes (5 MB)
    "MaxPerProduct": 10,            // gallery size cap
    "AllowedExtensions": [ ".jpg", ".jpeg", ".png", ".gif", ".webp" ]
  },
  "Payouts": {
    "MinimumAmount": 0.00,          // smallest balance worth settling; 0 settles any positive one
    "DefaultMethod": "Bank transfer" // pre-filled on the settlement form
  },
  "Logging": { "Database": { "Enabled": true } }   // see Logging
}
```

`Payments:Provider` accepts `sandbox` (charges nothing, no hosted page — the store is cash on delivery)
or `simulator` (the bundled hosted provider used by the card flow). Any other value throws at startup
telling you to register a real `IPaymentGateway` (and, if it hosts the payment page, an
`IHostedPaymentGateway`) first. That is deliberate — an unconfigured production deployment should fail
loudly rather than silently not charge anyone. The simulator's `WebhookSecret` is a development default;
a real provider supplies its own key and its own `X-Payment-Signature` header in `ParseWebhook`.

Product-image uploads are written to `Images:Folder` and served from `Images:RequestPath`; each file is
checked against `MaxBytes` and `AllowedExtensions`, and a gallery is capped at `MaxPerProduct`. Because a
whole gallery travels in one multipart request, the application raises the form and Kestrel body limits
to `MaxPerProduct × MaxBytes` so a legitimate multi-file save is not mistaken for an oversized request.

`Payouts:MinimumAmount` is policy, not plumbing: it is the marketplace's answer to *"is this balance
worth a bank transfer?"*, so a shop a few cents in credit is not paid a fee to receive it. The default
of `0.00` settles any positive balance; set it higher to batch small balances up.

### Why authentication is not ASP.NET Core Identity

`Microsoft.AspNetCore.Identity.EntityFrameworkCore` is absent from the local NuGet cache and
**nuget.org is unreachable** from this machine (`NU1301`, TLS handshake failure). Authentication is
therefore built on `Microsoft.AspNetCore.Authentication.Cookies`, which ships in the `net9.0` shared
framework. The security-relevant behaviour was not skipped: PBKDF2-HMAC-SHA256 with 210,000
iterations and constant-time comparison, a five-attempt lockout with a 15-minute window, a security
stamp rotated on credential change, and generic sign-in failure messages.

### Why some database operations are provider-specific

SQLite cannot apply `Min`/`Max`/`Sum` to a `decimal`, and has no server-generated rowversion. Both
are worked around and documented in code: aggregates run over `double` and convert back, and the
concurrency token is refreshed client-side. Schema creation uses `EnsureCreatedAsync` rather than
migrations because `Microsoft.EntityFrameworkCore.Design` is not cached either.

**The local SQL Server is 2014 (compatibility level 120), which predates `OPENJSON`.** EF Core 9
translates a captured-collection `Contains` — `ids.Contains(x.Id)` — into
`IN (SELECT value FROM OPENJSON(@ids))`, and that statement does not parse on this server: every
browse page fails with *Incorrect syntax near the keyword 'WITH'*. Marking the collection with
`EF.Constant` bakes the values in as a literal `IN (…)` list instead, which every supported provider
understands. It is easy to reintroduce, so every `Contains` over a captured collection inside a query
is written `EF.Constant(ids).Contains(…)` and carries a comment saying why; the same rule applies to
any new query.

**The same server rejects a negative `OFFSET`.** A page number arrives from the query string and can
be any `int` at all; `(page - 1) * pageSize` overflows to a negative offset long before a caller runs
out of plausible pages. `Paging.ClampPage` and `Paging.Skip` (`Services/CatalogModels.cs`) clamp the
page to the last one that exists and compute the offset in `long`, so `?page=999999999` returns the
final page rather than a 500.

A third environment constraint: the Windows Event Log is not writable by this process, and the
default logger turns any log *warning* into a fatal `AggregateException`. `Program.cs` therefore
clears the default providers and uses console and debug logging — plus the database sink, which
writes the same lines to the `Log` table (see [Logging](#logging)).

---

## Verifying the build

```
dotnet build
dotnet test Tests\WebDS_Test.Tests.csproj
```

`Tests\` is an xUnit suite over EF Core's in-memory provider and Moq — no database and no server are
needed. It covers the order state machine, stock arithmetic and refunds, the email outbox (dedupe,
retry/backoff, skip, resend, counts), the password-reset token lifecycle, paging maths, the email
templates, the rate-limit configuration, the coupon rules (percentage/fixed maths, minimum spend,
validity windows, use caps, per-shopper limits and redemption), the vendor ledger and payouts
(commission maths, exactly-once crediting, reversal, settlement, minimum thresholds and statement
scoping) and the export writers (CSV escaping, BOM, formula neutralisation, and the Excel round-trip).

> **Running the tests on this machine.** VSTest normally starts its test host through the
> per-platform app host (`testhost.exe`). This sandbox denies that host the right to open a handle to
> its parent process, so it dies before the first test runs. `Tests\WebDS_Test.Tests.csproj` has a
> `RemoveTestHostAppHost` target that drops the app host after build; VSTest then falls back to its
> portable `dotnet exec testhost.dll` path, which works everywhere. Pass
> `-p:RemoveTestHostAppHost=false` to keep the standard app host (for example, for Visual Studio's
> Test Explorer).

To exercise the Shopify adapter without credentials, set `Shopify:Enabled` and
`Shopify:UseMockData` to `true` and run — the storefront then serves the fixture catalogue through
the real GraphQL code path.

## Known limitations

- **No real payment provider is wired up.** The card flow is proven against the bundled `simulator`,
  which signs and verifies its own webhooks but moves no money. A production deployment registers a
  real `IHostedPaymentGateway` (and its secret) — the endpoint, the idempotency table and the order
  updates are provider-agnostic and already tested.
- **Email is queued and audited, but the default transport only writes files.** `Transport: "File"`
  produces genuine `.eml` and `.html` copies; switch to `Smtp` (and set `Email:Smtp:Host`) to reach a
  real inbox.
- **No migrations.** Use migrations before changing the model against an existing database. New
  tables and columns are added instead by guarded, idempotent DDL at start-up (see
  `Data/ReviewSchema.cs`, `Data/EmailSchema.cs`, `Data/CouponSchema.cs`, `Data/PaymentSchema.cs`,
  `Data/VendorSchema.cs`, `Data/ReturnsSchema.cs`) — a stop-gap, not a replacement for migrations.
- **The demo refund assumes the sale is on the ledger.** A return refund reverses the shop's sale
  entry; an order created before the ledger existed (older demo data) is refused loudly rather than
  refunded without reversing. Fresh demo data is seeded with ledger entries, so the full round trip
  works out of the box.
- **A payout is recorded, not transferred.** There is no banking integration; "Pay out" means staff
  moved the money by their own means and are recording it against the shop's balance. The ledger and
  the statement are exact, but the money movement itself is out of band.
- **Coupons do not stack and are not line-item aware.** One code per order, applied to the goods
  subtotal; there is no "buy two get one free", no category scoping and no automatic promotion engine.
- **Exports are capped and built in memory.** Orders mirror the page's 200-row cap and products stop
  at 5,000 rows; the whole file is assembled before it is sent. Fine at this store's size, but a
  high-volume deployment would stream and page instead.
- **Shopify adapter covers the catalogue only.** Cart, checkout and orders remain local; going fully
  headless means adopting Shopify's cart tokens and hosted checkout, which deletes rather than
  translates the order and inventory code.
- **Shopify price filtering is client-side**, so the reported result count is an estimate when a price
  filter is active.
- **No full-text search.** `LIKE` locally; Shopify's own search syntax when enabled.
- **Uploaded product images live on local disk.** They are validated (type, format, size) and named by
  the store, but there is no resizing, no CDN and no dedup — the default `LocalImageStore` writes under
  the content root, so a multi-instance deployment should register an `IImageStore` backed by shared
  storage instead.
- **The `Log` table stores level, message, logger and exception, and nothing else.** The sink's
  columns were fixed by the table already in the database, so there is no request path, trace id or
  user column; richer per-event detail would need the table extended. Retention is off by default,
  so the table grows until `Logging:Database:RetentionDays` is set.
