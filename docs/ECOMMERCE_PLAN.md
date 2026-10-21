# WebDS_Test → E-Commerce Platform: plan and record

**Status: delivered.** This document was originally a proposal written before any code existed.
It is kept as the architectural record, updated to say what was actually built and, more
usefully, where the original plan was wrong.

Baseline at the start: stock ASP.NET Core MVC template (`net9.0`), `HomeController` +
`ErrorViewModel`, no data layer, no authentication, no persistence.

See [`../README.md`](../README.md) for how to run the application and what it does.

---

## 0. The original blocking prerequisite — resolved by not needing it

The plan opened by declaring the work blocked: the build "needs NuGet packages that are not in the
local cache" and that under `Workspace Write` this "**cannot** work".

Half of that was right. The cache does lack packages, and **nuget.org is unreachable from this
machine** — a TLS handshake failure, not a sandbox restriction:

```
error: NU1301: Failed to retrieve information about
  'Microsoft.AspNetCore.Identity.EntityFrameworkCore' from remote source ...
  The SSL connection could not be established ... Authentication failed
```

Re-probed with full file access, the restore still fails. So the conclusion "we cannot download
`Identity.EntityFrameworkCore`" was correct, while the conclusion "therefore the work is blocked"
was not. The error was treating one package as the only way to build the feature.

**What actually happened:** `Microsoft.AspNetCore.Identity` is not required for authentication —
`Microsoft.AspNetCore.Authentication.Cookies` is in the `net9.0` shared framework, and
`Microsoft.EntityFrameworkCore.Sqlite` 9.0.0 plus `Microsoft.EntityFrameworkCore` 9.0.0 were
already in `~/.nuget/packages`. A probe confirmed an offline restore and build of an
EF Core + SQLite web project in about 10 seconds. Requirement met, zero downloads.

The lesson worth keeping: **probe the assumption before declaring a blocker.** The original plan
recorded three probes (all about *writing* to `~/.nuget`), but never probed whether the packages it
actually needed were already cached, and never considered implementing the feature without the
package.

---

## 1. Decisions, as taken

| # | Decision | Original recommendation | Taken |
|---|---|---|---|
| D1 | Database | EF Core 9 + SQLite, provider-swappable | ✅ As recommended |
| D2 | Identity | Identity with EF stores, custom MVC auth controllers | ⚠️ Superseded — cookie auth with hand-rolled PBKDF2, because the EF store package is undownloadable |
| D3 | Project layout | Single web project, layered in folders | ✅ `Domain/`, `Data/`, `Services/`, `Areas/Admin/` |
| D4 | Payments | `IPaymentGateway` + dev fake gateway | ✅ `SandboxPaymentGateway`, provider-swappable, fails loudly if misconfigured |
| D5 | Admin UI | MVC `Areas/Admin` guarded by an `Admin` role | ✅ As recommended |

---

## 2. Domain model, as built

**Catalogue** — `Category` (self-referencing tree, `Slug`, `IsActive`), `Product` (`Slug`,
`IsPublished`, soft delete, `IsFeatured`, `Brand`), `ProductVariant` (`Sku`, `Price`,
`CompareAtPrice`, `StockQuantity`, `LowStockThreshold`, `RowVersion`), `ProductImage`
(ordered gallery, one primary).

**Cart** — `Cart` (owned by a user id *or* an anonymous session key), `CartItem` (variant +
quantity, unique per cart, price read live from the variant).

**Orders** — `Order` (`OrderNumber`, `Status`, money totals, flat shipping-address snapshot,
timestamps, `IdempotencyKey`), `OrderLine` (**name/SKU/price snapshot**), `Payment`,
`StockMovement` (append-only ledger).

**Customer** — `ApplicationUser` (PBKDF2 `PasswordHash`, `SecurityStamp`, lockout state),
`UserRole`, `Address` (single default enforced), `WishlistItem`.

**Reviews** — `Review` (rating 1–5, optional title and body, `Status`, `IsVerifiedPurchase`, helpful
and not-helpful tallies, one optional seller reply), `ReviewVote` (one per shopper per review,
uniquely indexed). The relationships are chosen deliberately: a product or shop carrying reviews
cannot be deleted out from under them (`Restrict`), while deleting a shopper leaves the review
standing with no author (`SetNull`).

**Cross-cutting rules — all honoured:**
- Money is `decimal(18,2)`, never `double`/`float`. ✅
- Products are soft-deleted; orders are never deleted. ✅
- `ProductVariant.RowVersion` gives optimistic concurrency and prevents overselling. ✅
- Every inventory change is recorded with a reason. ✅ (added beyond the original plan)
- Checkout is idempotent. ✅ (the plan listed this as a risk mitigation; it is implemented)

---

## 3. Roadmap: planned vs delivered

| Phase | Scope | Status |
|---|---|---|
| **0. Toolchain** | Verify restore, add EF Core, `ApplicationDbContext`, schema, seed data | ✅ Done without Identity or SQL Server |
| **1. Catalog** | Entities + admin CRUD; category browse, product detail, search/filter/paging | ✅ Plus brand filter, eight sort orders, minimum-rating filter, soft delete + restore |
| **2. Identity** | Register / login / logout / roles, profile, address book | ✅ Plus lockout, security stamp, wishlist |
| **3. Cart & Checkout** | Anonymous + authenticated cart, checkout, order creation, inventory decrement under concurrency control | ✅ Plus idempotency and cart merging on sign-in |
| **4. Orders** | Customer history and detail; admin fulfilment queue and status transitions | ✅ Plus guest order tracking and a legal-transition state machine |
| **5. Payments** | Gateway abstraction, fake gateway, payment records | ✅ Gateway abstraction and records, **plus a hosted redirect-and-webhook flow**: `IHostedPaymentGateway`, a bundled `simulator` (HMAC-SHA256 signed events posted back through the real `/Payments/Webhook/{provider}` endpoint), an idempotent `PaymentEvents` audit table keyed on `(Provider, EventId)`, and card-vs-cash selection at checkout. Both outcomes verified live: capture → order `Paid`; decline → order stays `Pending`. Swapping in a real provider is one class plus one line in `Program.cs`. |
| **6. Hardening** | SEO slugs, email notifications, admin dashboard, rate limiting, logging, tests | ✅ slugs, dashboard, logging (console, debug and the store's `Log` table), a full **transactional-email outbox** (templates, File/Smtp transports, background dispatcher, password reset, admin outbox page), **rate limiting** on auth/checkout/review/**webhook**, and an **xUnit test suite** (197 tests over the order state machine, stock/refunds, the email outbox, password reset, paging, templates, rate-limit config, coupons, the export writers, the payment webhook/hosted-gateway contract, image-upload validation, and the vendor ledger/payout rules) |
| **7. Reputation** | *(added after the plan)* Ratings and reviews as one vertical | ✅ Reviews with verified-purchase detection, helpful votes, seller replies, an admin moderation queue, ratings-driven filters and sorts, seller reputation badges, and header search suggestions |
| **8. Promotions** | *(added after the plan)* Discount codes: cart/checkout application, admin CRUD, redemption tracking | ✅ Percent and fixed-amount coupons with minimum spend, validity windows, global and per-shopper use caps and optional shop scoping; session-held code re-priced on every cart render; redemption recorded per order; admin `/Admin/Coupons` |
| **9. Reporting** | *(added after the plan)* Admin CSV/Excel export of the operational lists | ✅ Orders, products and customers each download as CSV or Excel honouring the on-screen filters; one `TabularData` description, two writers (RFC 4180 CSV with a UTF-8 BOM and formula-injection guards, and a ClosedXML workbook with typed cells, a frozen header and an autofilter) |
| **10. Media** | *(added after the plan)* Product image uploads | ✅ An `IImageStore` abstraction over `LocalImageStore`: files are validated by extension **and** by sniffed magic bytes, bounded in size, named by the store (never the client), written under `App_Data/uploads` and served read-only at `/uploads`. The admin and vendor editors gained a multipart file input that merges uploads with the existing URL textarea; a rejected upload fails the whole save and rolls back anything already written, and removing a URL deletes its file when no other product still uses it. |
| **11. Marketplace money** | *(added after the plan)* Vendor commission and payouts | ✅ An append-only `VendorLedgerEntry` ledger is credited by a single shared writer (`IVendorLedgerService`) at the moment money is actually collected — card capture, cash-on-delivery delivery, or staff marking an order paid — and reversed on cancellation or refund; every write is idempotent per order and commits as it writes. `VendorPayout` records a settlement (`PAY-2026-0001`) that stamps the entries it covers rather than writing earnings, so the outstanding balance is always the derived sum of unsettled entries. Admin `/Admin/Payouts` settles and exports a statement (CSV/Excel); `/Vendor/Payouts` is a read-only view scoped to the caller's own shop. |

---

## 4. Risks: what actually bit

The plan's risk list was mostly about the intended design. The problems that actually occurred
were environment-specific and none of them were on the list:

1. **The Windows Event Log is not writable by this process.** The default logger provider writes
   there, and a *log warning* — specifically EF's model-validation warning about a filtered
   required navigation — became a fatal `AggregateException` during startup. Two fixes: clear the
   default logging providers in favour of console/debug, and drop the `Product` global query filter
   in favour of explicit `!p.IsDeleted` predicates.
2. **SQLite cannot aggregate `decimal`.** `Min`/`Max`/`Sum` over a `decimal` column throws
   `NotSupportedException` at runtime, not compile time — so the catalogue browse page returned
   500 on first run. Aggregates now run over `double` and convert back, and the dashboard was
   restructured to project and aggregate in memory instead of fanning out into a query per tile.
3. **`Microsoft.EntityFrameworkCore.Design` is also not cached**, so migrations and
   `dotnet ef` are unavailable. Schema creation uses `EnsureCreatedAsync`. This is the known
   limitation to address first when tooling allows.
4. **SQL Server 2014 has no `OPENJSON`, and EF Core 9 emits it.** EF translates a captured-collection
   `Contains` — `ids.Contains(x.Id)` — into `IN (SELECT value FROM OPENJSON(@ids))`. The server
   hosting the store is version 12.0 (compatibility level 120), so the statement does not parse and
   *every* browse surface returned 500 the moment ratings were added to the product card. Fixed by
   marking each such collection with `EF.Constant`, which renders a literal `IN (…)` list. The
   landmine is invisible until a query takes a collection parameter, which is why the first feature
   that did detonated it.
5. **One view model declared in two areas.** `AdminProductListViewModel` existed twice — once in the
   admin controllers namespace and once in the vendor's — and the vendor view pointed at the admin
   copy while its controller constructed the vendor copy. `/Vendor/Products` therefore failed with a
   `ViewDataDictionary` type mismatch. The two definitions are now one, in
   `Areas/Admin/Models/AdminViewModels.cs`, which both areas already import.
6. **A negative `OFFSET` from an arbitrary page number.** `(page - 1) * pageSize` overflows to a
   negative offset for large `?page=` values, and SQL Server rejects a negative offset outright, so
   `/shop/{slug}?page=999999999` returned 500. `Paging` (`Services/CatalogModels.cs`) now clamps the
   page to one that exists and computes the offset in `long`.

Risks the plan identified that were genuinely mitigated: overselling (optimistic concurrency plus
in-transaction stock validation), double submission (per-form idempotency key plus a unique
index), NT/scope (phase gates — each phase built, compiled and smoke-tested before the next).

---

## 5. What a production deployment would still need

In rough order of priority:

1. **Migrations** — replace `EnsureCreated` with a real migration baseline; the model will change.
   The `Log` table is the first thing that needs this: a store created before the sink has no
   `Log` table, and `EnsureCreated` cannot add one, so `Data/LogTable.cs` patches it with guarded
   DDL until migrations exist. `Data/ReviewSchema.cs`, `Data/EmailSchema.cs`,
   `Data/CouponSchema.cs`, `Data/PaymentSchema.cs` and `Data/VendorSchema.cs` do the same for the
   review tables, the email
   outbox, the coupon tables, the payment-event audit table (plus the new `Orders` columns) and the
   vendor ledger and payout tables, so all
   six patches can be retired the day migrations land.
2. **Automated tests** — ✅ **done.** `Tests\WebDS_Test.Tests.csproj` is an xUnit suite (EF Core
   in-memory + Moq) over the order state machine, stock arithmetic and refunds, the email outbox,
   the password-reset token lifecycle, paging, templates, rate-limit configuration, the coupon rules,
   the CSV/Excel export writers, image-upload validation, the vendor ledger and payout rules
   (commission maths, exactly-once crediting, reversals, settlement, minimum amounts and statement
   scoping), and the payment webhook's signature,
   idempotency and capture rules.
   It already paid for itself: the refund test caught that
   `UpdateStatusAsync` never loaded `Order.Payments`, so a Paid→Refunded transition reversed nothing.
   Checkout concurrency still has no integration test — that needs a real database and
   `WebApplicationFactory`, which is not available offline here.
3. ~~**A real payment provider**~~ — ✅ **done, minus the provider account.** `IHostedPaymentGateway`
   plus the `/Payments/Webhook/{provider}` endpoint, an idempotent `PaymentEvents` audit table, and a
   bundled `simulator` that signs and posts its own events through the real endpoint. Registering a
   real provider is a new `IPaymentGateway`/`IHostedPaymentGateway` pair and one `case` in
   `Program.cs`; the signature header and secret come from config (`Payments:WebhookSecret`).
4. ~~**Transactional email**~~ — ✅ **done.** An outbox (`EmailMessages`, guarded DDL), six
   pure-function templates each rendering HTML + text, a background dispatcher with retry/backoff
   and idempotent `DedupeKey`s, `File` and `Smtp` transports, password reset on an expiring Data
   Protection token, and an admin outbox page with preview and resend.
5. ~~**Rate limiting** on sign-in and checkout~~ — ✅ **done** on `auth`, `checkout`, `review` and
   `webhook`, partitioned by user else IP, with 429 + `Retry-After` and switchable via
   `RateLimiting:Enabled`. Account lockout already existed from phase 2.
6. **A production database** — the provider is swappable, but the two SQLite workarounds noted
   above should be revisited (they can simply be removed for SQL Server).
7. ~~**Image uploads**~~ — ✅ **done, local storage only.** `IImageStore` + `LocalImageStore` validate
   the extension, the sniffed format and the size, write files under `App_Data/uploads` served at
   `/uploads`, and merge uploads with the URL textarea in both product editors. A production deployment
   swaps in a store backed by shared/blob storage; there is still no resizing, dedup or CDN.
8. **Full-text search** — `LIKE` is fine at this catalogue size and would not be at a large one.
9. **Exports at scale** — the admin reports (orders, products, customers, and now payout history and
   a payout statement) assemble the whole CSV/XLSX
   in memory and cap rows at the page's own limit. A high-volume store would stream the CSV and page
   the workbook, and would likely add tax exports alongside them.
10. **Settlement is recorded, not executed** — there is no banking or payout rail. `/Admin/Payouts`
    marks a balance settled and keeps the statement; moving the money is out of band. Wiring an
    automated transfer would make `CreatePayoutAsync` call a provider instead of (or before) stamping
    the entries, and the ledger's "settled" stamp is the natural place to record the transfer's own
    reference.
