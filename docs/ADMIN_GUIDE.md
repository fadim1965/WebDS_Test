# LibanPost eShop — administrator guide

For store staff. Everything behind `/Admin`: running the catalogue, handling orders and customers,
moderating reviews, settling vendors, reading the analytics, and reading the store's own log.

> Sign in at `/Account` with a staff account, then go to **`/Admin`**. The demonstration store's admin
> account is documented in the README.

---

## Contents

- [The dashboard](#the-dashboard)
- [Products](#products)
- [Bulk import](#bulk-import)
- [Categories](#categories)
- [Inventory](#inventory)
- [Orders](#orders)
- [Returns](#returns)
- [Customers](#customers)
- [Coupons](#coupons)
- [Tax classes](#tax-classes)
- [Shops](#shops)
- [Payouts](#payouts)
- [Marketing analytics](#marketing-analytics)
- [Reviews moderation](#reviews-moderation)
- [Email outbox](#email-outbox)
- [Audit trail](#audit-trail)
- [Exports](#exports)
- [Where the store's own log lives](#where-the-stores-own-log-lives)
- [Questions staff usually ask](#questions-staff-usually-ask)

---

## The dashboard

`/Admin/Dashboard` is the operational summary: revenue (lifetime and the last 30 days), order counts
by state, the value of stock on hand, and stock alerts for anything at or below its low-stock
threshold. It is the "is anything on fire?" page; the Marketing page is the "how are we doing?" page.

---

## Products

`/Admin/Products` is the catalogue. It offers search, category and state filters (active, draft, out
of stock, deleted), and the same editor a vendor uses — full variant matrix, image gallery that takes
typed URLs *and* uploaded files, publish toggle, and soft delete with restore.

**Staff differ from vendors in one way that matters: staff see the whole catalogue and the
soft-deleted rows**, so a withdrawn product can be found and restored rather than being invisible
forever.

Because orders snapshot the product name, variant name, SKU and unit price onto each order line,
**editing or deleting a product never rewrites the history of an order that already contains it.**

Use **Export CSV** / **Export Excel** to download exactly what the filters currently select.

---

## Bulk import

`/Admin/Import` loads a CSV of products into a chosen shop. It is all-or-nothing, validates every row
before writing anything, and reports failures by line number. Column order does not matter; headers
are matched by name. `Name`, `Price` and `SKU` are required.

The importer is the exact inverse of the store's CSV export, so you can **export products, edit them
in a spreadsheet, and import the file straight back**. Key behaviours:

- Matching is by **SKU → slug → name**, in that order.
- `Category` must already exist.
- Omitting the `Images` column **leaves the existing gallery untouched**; supplying it replaces the
  gallery.
- Turning **off** "create products that do not exist yet" makes it an update-only import.

Download the template from the same page for a correct starting header. See the vendor guide for the
full column reference.

---

## Categories

`/Admin/Categories` manages the catalogue's tree with indentation and per-category product counts.
Categories are global to the marketplace, so they are shared across every shop. The form guards
against cycles — you cannot make a category its own ancestor.

---

## Inventory

`/Admin/Inventory` shows stock levels across products and lets you adjust them. Every change is
written to an **append-only stock-movement ledger**, so any movement can be explained after the fact:
who changed what, by how much, and why.

Stock is committed under **optimistic concurrency**. If two customers check out the last item at the
same instant, one of them gets a conflict rather than both being sold something that does not exist;
the order service retries with a fresh basket and, if it still cannot be filled, tells the customer
plainly.

---

## Orders

`/Admin/Orders` is the fulfilment queue.

- **Filter** by status and search by customer or order number.
- **Open an order** for its detail: lines, addresses, payment, collection status and tracking.
- **Change status** with `UpdateStatus`, subject to the order state machine — only legal transitions
  are permitted.

What the transitions actually do:

| Transition | Effect |
|---|---|
| → **Paid** | Credits the vendor ledger (money has been collected). |
| → **Shipped** | Queues a shipping notification email, with tracking. |
| → **Cancelled** | **Restores stock** and reverses the vendor's earning. |
| → **Refunded** | Calls the payment gateway; reverses the vendor's earning. **Stock is not restored** (the goods left). |

Order-by-order refunds are whole-order actions. Per-line refunds with goods coming back run through
the returns desk (see [Returns](#returns)); a full-return refund there also moves the order to
*Refunded* and flips the cash-on-delivery collection to *refunded*.

Export the current filter selection as CSV or Excel.

---

## Customers

`/Admin/Customers` searches accounts and shows a detail page with **lifetime spend**. Accounts can be
**activated or deactivated** — a deactivated account cannot sign in, but its order history stays
intact. Deactivating is preferred over deleting, for exactly that reason.

---

## Returns

`/Admin/Returns` is the marketplace's RMA desk. The marketplace's job here is money and covers: the
shops decide their own returns day to day, but staff can act for them and are the only role that
issues the actual refund.

- **The queue** — filter by status, or search by RMA reference, shopper email or order number.
  Counters above the table show how many requests sit at *requested*, *approved* and *received* —
  the unfinished work. Export the current view as CSV or Excel.
- **Deciding** — a return can be approved or rejected from here, with a note, exactly as the vendor
  would; handy when a shop is slow to answer and a shopper is waiting. Staff decisions are what the
  audit trail credits to the *administrator's* name, not the shop's.
- **Receiving** — "goods received" restocks the returned units into the shop's listing stock (or the
  shared variant pool if the listing was withdrawn) and writes a return-stamped stock movement.
- **Refunding** — press **Issue refund** on a *received* return. The amount is computed, not typed:
  the goods actually returned (with their share of any order discount) plus the proportionate tax,
  and shipping only when the whole order comes back. It is capped by whatever the ledger still owes
  the shop for that sale and by `GrandTotal − RefundedTotal`, so it can never exceed what the order
  received. A card refund goes back against the original charge; a cash-on-delivery refund is a
  staff bookkeeping row.

A refund that cannot go through (no sale on the ledger to reverse, a state that forbids the step) is
refused with a plain reason and **recorded on the audit trail as a failed attempt** — the trail
should explain the retries, not hide them. The order's page keeps *Refunded so far* and *Still owed
to shopper* totals, and the vendor sees the same return from their side at `/Vendor/Returns`.

---

## Coupons

`/Admin/Coupons` creates and edits discount codes:

- **Type** — percentage or fixed amount.
- **Minimum subtotal**, and a **validity window** (`StartsUtc` / `ExpiresUtc`).
- **`MaxUses`** (global) and **`MaxUsesPerUser`** (per shopper).
- Optional restriction to **one shop**.
- An **activate toggle**.

Validation refuses nonsense at the form: a percentage over 100, an out-of-range fixed amount, a start
after the expiry, and a shop-scoped code with no shop are all rejected.

**A code that has already been redeemed is deactivated rather than deleted**, so order history that
references it stays honest. Every redemption is recorded as a `CouponUsage` row holding the user and
the order — which is how the per-shopper cap is enforced without trusting the browser. Guests are
held only to the global limit.

Note the deliberate limits: coupons **do not stack**, and they are **not line-item aware** — one code
per order, applied to the goods subtotal.

---

## Tax classes

`/Admin/Tax` defines named tax rates. A product may point at one; a product with no class is taxed at
the store-wide rate (`Store:TaxRate`).

- Rates are entered as a **fraction**: `0.11` means eleven percent. The form refuses anything outside
  0–1, so nobody can type `11` and charge 1100%.
- A class can be marked **exempt** — always zero, whatever the rate says — for VAT-registered buyers
  and resale certificates.
- **Deleting is guarded.** If products still reference a class, it is **deactivated rather than
  deleted**, because deleting it would silently move those products onto the store rate — a real
  change to what a shopper pays, made without anyone deciding to make it.
- The page shows how many live products are on each class and how many are on the store default.

Tax is applied **per line, from each line's own class**, with any order-level discount apportioned
across the lines first. That means a basket holding both standard-rated and zero-rated goods is taxed
correctly, and the figure shown at checkout is the same one the order records.

**Products with no tax class behave exactly as they did before this feature existed** — the store rate
applies, unchanged. A fresh store is seeded with one *Standard rate* class and leaves products
unassigned, so introducing classes changes no existing order total.

Export the class list as CSV or Excel.

---

## Shops

`/Admin/Shops` is where the marketplace's sellers are governed:

| Action | Meaning |
|---|---|
| **Approve** | The shop goes live and appears in the directory. Also where you set its **commission rate**. |
| **Reject** | Declines a pending application, with a reason the vendor sees. |
| **Suspend** | Takes a live shop off the storefront, keeping its data and workspace, with a reason. |
| **Reinstate** | Puts a suspended shop back. |

Commission is entered as a percentage in the form (`10` = 10%); the store divides by 100 to store the
fraction. Commission is withheld on **goods only**, never on shipping.

Every action on this page — approve, reject, suspend, reinstate — both **emails the shop's owner**
and **writes an audit entry**, so the storefront directory's changes always have a history you can
read. The list also shows each shop's recorded **payout destination** (account name, bank, IBAN) at
a glance; the vendor maintains it on their payouts page or at registration. The **history** link on
each row opens the audit trail pre-filtered to that shop.

---

## Payouts

`/Admin/Payouts` is where vendor money is settled.

- The list shows every shop's **outstanding balance** — gross, commission withheld, and net — and its
  settlement history with a **status badge** per payout.
- **Pay out** settles the whole balance in one action, producing a statement.
- A statement, and the settlement history, can be downloaded as CSV or Excel.

What a settlement *is* matters here:

- The vendor's balance is **derived** — always the sum of their unsettled ledger entries, never a
  stored figure that could drift.
- A settlement **marks** those entries as settled and records a reference and period. It does not
  write new earnings, so a statement can always be reproduced from the ledger alone.
- **A payout is recorded, not transferred.** There is no banking integration: "Pay out" means staff
  moved the money by their own means and are recording it against the shop's balance.
- A balance below `Payouts:MinimumAmount` is not settled, so a shop a few cents in credit is not paid
  a fee to receive it.
- If a refund lands **after** a settlement, the shop's balance goes negative and no payout clears it
  until new sales cover it. That is the correct accounting.

**When the bank refuses a transfer** — a payout that was recorded but did not arrive is not deleted
and its figures are not edited. Open it while it is still *Paid* and choose **Mark failed** with the
bank's reason. The payout keeps its totals for the statement, gains a `Failed` badge with the reason,
and **releases every entry it covered back to the unpaid balance**, so the shop can be settled again
immediately. The same release runs through **Reverse**, for a payout recorded in error. Both actions
write audit entries, and the shop is emailed when a settlement is recorded against a shop with a
contact email; the vendor's own payouts page shows them the badge, the reason and the handler.

---

## Marketing analytics

`/Admin/Marketing` answers "how are we doing?" — revenue over time, top products by revenue and by
units, revenue by category, best customers, payment-method mix, coupon performance and fast-moving
stock.

**Every section exports** as CSV or Excel, through the same eight section names the page presents:

`daily-revenue`, `top-products-revenue`, `top-products-units`, `revenue-by-category`,
`top-customers`, `payment-methods`, `coupons`, `fast-moving`.

Two things worth knowing before you trust a figure:

- **Only collected money counts as revenue.** A pending, cancelled or refunded order is not revenue.
- **Coupon performance reports the discount checkout actually applied**, read from the order itself,
  not reconstructed from the coupon's rules.

Vendors see a scoped version of the same picture on their own dashboard.

---

## Reviews moderation

`/Admin/Reviews` is the **moderation queue**, filterable by state (pending / published / rejected) and
by star rating.

Reviews **publish immediately** by default. The queue is **reactive** — it exists to hide the minority
that break the rules, not to gate every review. From here you can publish, hide, return to the queue,
or delete outright.

Seller replies are not moderated in the queue; a seller's reply belongs to the seller. If a reply
itself breaches the rules, delete the review it sits under.

---

## Email outbox

`/Admin/Emails` is the transactional outbox: every message the store **intended** to send, including
the ones that failed.

- Filter by **state** (queued, sent, failed, skipped) and by **type**.
- Open a message for a **rendered preview** of what the customer receives.
- **Resend** a failed message, which returns it to the queue with a clean history.
- **Send a test message** to confirm configuration.

Design points that explain what you see:

- **Nothing sends on the request path.** A checkout queues a row and returns; a background dispatcher
  delivers it. A slow mail server can never slow down a checkout.
- **Messages are idempotent** by a dedupe key, so a retried request cannot send the same notification
  twice.
- **Failures retry with backoff**, then park as *Failed* with the full error preserved.
- With `Email:Enabled = false`, messages are recorded as **Skipped** rather than dropped, so the audit
  trail stays complete.
- The default transport writes real `.eml` and `.html` files instead of sending, so email works with
  no mail server. Nothing reaches a real inbox until an SMTP host is configured.

---

## Exports

Orders, products, customers, returns, the audit trail, payouts, tax classes and every marketing
section export as **CSV** or **Excel**.

- The export runs **the same query the page does**, so the file and the screen cannot disagree about
  what "the current list" means — filters included.
- CSV is **UTF-8 with a byte-order mark**, so Excel reads Arabic and accented names correctly.
- A cell beginning `=`, `+`, `-` or `@` is **prefixed with an apostrophe**, so a customer whose name
  starts with `=` cannot smuggle a formula into your spreadsheet.
- The Excel output is a real workbook: numbers Excel can sum, real dates, a bold frozen header row and
  an autofilter.
- Exports are **capped** (orders 200, products 5,000) to match the page and bound memory.

---

## Audit trail

`/Admin/Audit` is the store's administrative memory: who did what, to which record, and when.

- **What is recorded** — every shop decision (approved, rejected, suspended, reinstated), every order
  status change staff make, every return decision, received-goods entry, refund issued **and every
  refund attempt that failed**, and every payout created, marked failed or reversed. Failed attempts
  are kept — a trail that hides the failures cannot explain the retries.
- **What a row says** — timestamp, actor (the signed-in name as shown in the admin area), a dotted
  action (`shop.approved`, `refund.failed`, `payout.reversed`, …), the subject as *entity type + id*,
  and a one-sentence summary written by the action itself.
- **Filter** by action, entity type, entity id or actor; the suggestion lists come from the data, so
  a new action is filterable without any configuration. Each shop row on the shops page links to
  this page pre-filtered to that shop.
- **Export** the filtered view as CSV or Excel (up to 500 rows).

Design notes: the trail is **append-only** and references are deliberately **loose** (a name string
and a numeric id, not foreign keys) — deleting an account or a row never rewrites the history that
mentions them. It is also written **best-effort**: a failing audit write is logged, never allowed to
undo the action it was documenting.

---

## Where the store's own log lives

Everything the application logs goes to the console **and** to the `Log` table in the configured
database. The console is gone when the window closes; the table is what a deployment keeps.

```sql
SELECT TOP 50 Id, Logged, Level, Logger, Message FROM [Log] ORDER BY Id DESC;
SELECT Id, Logged, Logger, Exception FROM [Log] WHERE Level = 'Error' ORDER BY Id DESC;
```

`Error` and `Critical` rows carry the **full exception and stack trace**. The log is designed so that
**a logging call can never fail, block or throw a request** — callers enqueue, and a background writer
batches the insert. Retention is off by default, so the table grows until retention is configured.

---

## Questions staff usually ask

**I changed a product's price — why do old orders still show the old price?**
Because they should. Order lines snapshot the product name, variant, SKU and unit price at the moment
of sale, so editing a product cannot rewrite history.

**Why can't I delete this tax class?**
Products still reference it. Deleting would silently move them onto the store rate. The page
deactivated it instead; reassign the products, then delete.

**Why is a coupon still listed after it was used up?**
A redeemed coupon is deactivated rather than deleted, so order history stays honest. It will not apply.

**Why did the vendor's balance go negative?**
A refund landed after a settlement had already paid them. New sales will cover it; no payout will
clear it until they do.

**The bank refused a payout — can I just delete it?**
No. Mark it **failed** with the bank's reason on the payout's detail page. The covered entries return
to the shop's balance, the statement keeps its figures, and every step is on the audit trail.

**A shop says it refunded a customer but I see nothing.**
RMA refunds are issued here, not by the shop — check `/Admin/Returns` for the request's state. If it
is still *received*, the refund has not been issued yet; the audit trail shows who did what so far.

**Why does the marketing revenue differ from the order list total?**
Marketing counts only **collected** money — pending, cancelled and refunded orders are excluded.

**Do coupon discounts show the right figure?**
Yes. Coupon performance reports the discount the checkout actually applied, read from the order, not
recalculated from the coupon's rules.

**A bulk import failed — did it write anything?**
No. An import is all-or-nothing: every row is validated first, and nothing is written if any row is
rejected. The errors are listed by line.

**Why is a customer's order missing from their history?**
Check whether the order was placed as a **guest** with an email that is not tied to an account. Guest
orders are tracked by order number and email, not by account.
