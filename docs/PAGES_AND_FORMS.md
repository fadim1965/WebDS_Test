# LibanPost eShop — pages and forms reference

A complete inventory of every screen in the application, grouped by area, with the route, the model
behind it, what it does, and every form and field it contains. Field names are the `asp-for` /
`name` attributes as rendered, so they double as the POST parameter names.

> Conventions used throughout:
> - Routes are shown as `Area/Controller/Action` (area omitted for the storefront).
> - `GET` forms (search/filter) submit via query string; `POST` forms carry an antiforgery token.
> - A field marked *(required)* is validated server-side; client-side validation mirrors it.
> - Hidden fields (`id`, `returnUrl`, `IdempotencyKey`, paging tokens) are listed too, because they
>   are part of the contract between page and controller.

---

## Contents

- [1. Storefront (public)](#1-storefront-public)
- [2. Account](#2-account)
- [3. Cart and checkout](#3-cart-and-checkout)
- [4. Orders](#4-orders)
- [5. Admin area (`/Admin`)](#5-admin-area-admin)
- [6. Vendor area (`/Vendor`)](#6-vendor-area-vendor)
- [7. Shared chrome and components](#7-shared-chrome-and-components)
- [8. Error and status pages](#8-error-and-status-pages)
- [Appendix A — field types at a glance](#appendix-a--field-types-at-a-glance)
- [Appendix B — non-form interactions](#appendix-b--non-form-interactions)
- [Appendix C — field validation rules](#appendix-c--field-validation-rules)

---

## 1. Storefront (public)

### Home — `/` (`Home/Index`)
- **Model:** `HomeViewModel`
- **Purpose:** Landing page. Hero, featured products, best sellers, category highlights, brand strip.
- **Forms:** none (all navigation is via links).

### Catalog — `/Catalog` (`Catalog/Index`)
- **Model:** `CatalogViewModel`
- **Purpose:** The main browse/search page. Every filter lives in the query string.
- **Form:** one `GET` form, `class="filter-panel"`.
  | Field | Type | Purpose |
  |---|---|---|
  | `filterSearch` | search | Free-text search (name, short description, brand, SKU). |
  | `filterCategory` | select | Department (includes sub-categories). |
  | `filterMinPrice` | number | Minimum price. |
  | `filterMaxPrice` | number | Maximum price. |
  | `filterBrand` | select | Brand (only brands present in results). |
  | `minRating` | radio | Minimum star rating. |
  | `filterSort` | select | Sort order (8 options). |
  | `filterInStock` | checkbox | In-stock only. |
- **Hidden:** none.

### Product details — `/product/{slug}` (`Products/Details`)
- **Model:** `ProductDetailViewModel`
- **Purpose:** Single product: gallery, variants, stock, reviews, related products.
- **Forms:** five.
  1. **Add to cart** (`Cart/Add`, POST)
     | Field | Type | Purpose |
     |---|---|---|
     | `returnUrl` | hidden | Where to go back after adding. |
     | `variantId` | radio | The chosen variant (required). |
     | `quantity` | number | How many. |
  2. **Toggle wishlist** (`Products/ToggleWishlist`, POST)
     | Field | Type | Purpose |
     |---|---|---|
     | `productId` | hidden | Product id. |
     | `returnSlug` | hidden | Product slug for redirect. |
  3. **Write a review** (`Reviews/Create`, POST)
     | Field | Type | Purpose |
     |---|---|---|
     | `ProductId` | hidden | Product being reviewed. |
     | `ReturnSlug` | hidden | Redirect target. |
     | `Rating` | radio (1–5) | Star rating. |
     | `reviewTitle` | input | Review title. |
     | `reviewBody` | textarea | Review body (min 10, max 4000 chars). |
  4. **Delete review** (`Reviews/Delete`, POST)
     | Field | Type | Purpose |
     |---|---|---|
     | `reviewId` | hidden | Review to delete. |
     | `returnSlug` | hidden | Redirect target. |
  5. **Vote on a review** (`Reviews/Vote`, POST)
     | Field | Type | Purpose |
     |---|---|---|
     | `reviewId` | hidden | Review voted on. |
     | `returnSlug` | hidden | Redirect target. |
     | `stars` | hidden | (unused placeholder) |
     | `reviewSort` | hidden | Preserve sort. |
     | `reviewPage` | hidden | Preserve page. |

### Shops directory — `/Shops` (`Shops/Index`)
- **Model:** `ShopDirectoryViewModel`
- **Purpose:** List of all approved shops.
- **Form:** one `GET` search form.
  | Field | Type | Purpose |
  |---|---|---|
  | `q` | search | Shop name search. |

### Shop storefront — `/shop/{slug}` (`Shops/Details`)
- **Model:** `ShopStorefrontViewModel`
- **Purpose:** A single shop's public storefront (its products in its chosen template).
- **Form:** one `GET` filter form.
  | Field | Type | Purpose |
  |---|---|---|
  | `shopSearch` | search | Search within this shop. |
  | `shopCategory` | select | Category filter. |
  | `shopSort` | select | Sort order. |

### Track an order — `/Orders/Track` (`Orders/Track`)
- **Model:** `TrackOrderViewModel`
- **Purpose:** Guest order lookup (no account needed).
- **Form:** one `POST` form.
  | Field | Type | Purpose |
  |---|---|---|
  | `OrderNumber` | input | The order number. |
  | `Email` | email | The email used at checkout. |

---

## 2. Account

### Sign in — `/Account/Login` (`Account/Login`)
- **Model:** `LoginViewModel`
- **Form:** one `POST` form.
  | Field | Type | Purpose |
  |---|---|---|
  | `ReturnUrl` | hidden | Redirect after login. |
  | `Email` | email | Account email. |
  | `Password` | password | Account password. |
  | `RememberMe` | checkbox | Keep signed in on this device. |

### Create an account — `/Account/Register` (`Account/Register`)
- **Model:** `RegisterViewModel`
- **Form:** one `POST` form.
  | Field | Type | Purpose |
  |---|---|---|
  | `DisplayName` | input | Full name. |
  | `Email` | email | Account email. |
  | `Phone` | tel | Optional phone. |
  | `Password` | password | At least 8 characters. |
  | `ConfirmPassword` | password | Must match. |
  | `AcceptTerms` | checkbox | Accepts shipping, returns and privacy terms. |

### Your account — `/Account/Profile` (`Account/Profile`)
- **Model:** `ProfileViewModel`
- **Forms:** three.
  1. **Sign out** (`Account/Logout`, POST) — no fields.
  2. **Update profile** (`Account/UpdateProfile`, POST)
     | Field | Type | Purpose |
     |---|---|---|
     | `DisplayName` | input | Display name. |
     | `Phone` | tel | Phone number. |
  3. **Change password** (`Account/ChangePassword`, POST)
     | Field | Type | Purpose |
     |---|---|---|
     | `CurrentPassword` | password | Current password. |
     | `NewPassword` | password | New password (min 8). |
     | `ConfirmPassword` | password | Confirm new password. |

### Address book — `/Account/Addresses` (`Account/Addresses`)
- **Model:** `IReadOnlyList<Address>`
- **Forms:** two (per address card).
  1. **Make default** (`Account/SetDefaultAddress`, POST) — `id` (hidden).
  2. **Delete** (`Account/DeleteAddress`, POST) — `id` (hidden), with a confirm dialog.

### Add / edit address — `/Account/EditAddress` (`Account/EditAddress`)
- **Model:** `AddressFormModel`
- **Form:** one `POST` form (`Account/EditAddress`).
  | Field | Type | Purpose |
  |---|---|---|
  | `Id` | hidden | 0 for new, otherwise the address id. |
  | `FullName` | input | Recipient name. |
  | `Phone` | tel | Phone. |
  | `Line1` | input | Address line 1. |
  | `Line2` | input | Address line 2 (optional). |
  | `City` | input | City. |
  | `State` | input | State / region. |
  | `PostalCode` | input | Postal code. |
  | `Country` | input | Country. |
  | `IsDefault` | checkbox | Use as default address. |

### Forgot password — `/Account/ForgotPassword` (`Account/ForgotPassword`)
- **Model:** `ForgotPasswordViewModel`
- **Form:** one `POST` form.
  | Field | Type | Purpose |
  |---|---|---|
  | `Email` | email | Account email. |

### Reset password — `/Account/ResetPassword` (`Account/ResetPassword`)
- **Model:** `ResetPasswordViewModel`
- **Form:** one `POST` form (opened from the email link).
  | Field | Type | Purpose |
  |---|---|---|
  | `Token` | hidden | The reset token from the link. |
  | `Password` | password | New password. |
  | `ConfirmPassword` | password | Confirm new password. |

### Wishlist — `/Account/Wishlist` (`Account/Wishlist`)
- **Model:** `IReadOnlyList<WishlistItem>`
- **Purpose:** Saved products; prices and stock always current.
- **Forms:** one per in-stock product (add to cart) — `variantId` (hidden), `quantity` (hidden, 1), `returnUrl` (hidden).

### Access denied — `/Account/AccessDenied`
- **Purpose:** Shown when a signed-in user lacks the required role.

---

## 3. Cart and checkout

### Cart — `/Cart` (`Cart/Index`)
- **Model:** `CartViewModel`
- **Forms:** five.
  1. **Update quantity** (`Cart/Update`, POST) — `cartItemId` (hidden), `quantity` (number).
  2. **Remove line** (`Cart/Remove`, POST) — `cartItemId` (hidden).
  3. **Empty cart** (`Cart/Clear`, POST) — confirm dialog.
  4. **Remove coupon** (`Coupons/Remove`, POST) — no fields.
  5. **Apply coupon** (`Coupons/Apply`, POST) — `Code` (text, upper-cased).

### Checkout — `/Checkout` (`Checkout/Index`)
- **Model:** `CheckoutViewModel`
- **Forms:** two.
  1. **Place order** (`Checkout/Place`, POST, `novalidate`)
     | Field | Type | Purpose |
     |---|---|---|
     | `IdempotencyKey` | hidden | Prevents duplicate orders. |
     | `SelectedAddressId` | select | Pick a saved address (copies into the fields). |
     | `Email` | input | Contact email. |
     | `CustomerName` | input | Name on order. |
     | `Phone` | input | Phone (optional). |
     | `ShippingName` | input | Recipient. |
     | `ShippingLine1` | input | Address line 1. |
     | `ShippingLine2` | input | Address line 2. |
     | `ShippingCity` | input | City. |
     | `ShippingState` | input | State / region. |
     | `ShippingPostalCode` | input | Postal code. |
     | `ShippingCountry` | input | Country. |
     | `CustomerNote` | textarea | Order note (optional). |
     | `SaveAddressToBook` | checkbox | Save address to address book. |
     | `PaymentMethod` | radio | Cash on delivery or card (if available). |
  2. **Remove coupon** (`Coupons/Remove`, POST) — `returnUrl` (hidden).

### Order confirmation — `/Checkout/Confirmation` (`Checkout/Confirmation`)
- **Model:** `Order`
- **Purpose:** Shown after a successful order. No forms.

---

## 4. Orders

### Your orders — `/Orders` (`Orders/Index`)
- **Model:** `IReadOnlyList<Order>`
- **Purpose:** Signed-in customer's order history. No forms (links to detail).

### Order detail — `/Orders/Details/{id}` (`Orders/Details`)
- **Model:** `Order`
- **Purpose:** Full order view with status timeline, lines, totals, tracking. Returns appear in
  their own panel — each with its reference (`RMA-…`), status badge, units, and, while the request
  is still open, a **Cancel request** button.
- **Form:** one `POST` per open return (`Orders/CancelReturn`) — `id` (route).

### Request a return — `/Orders/RequestReturn/{id}` (`Orders/RequestReturn`)
- **Model:** `RequestReturnViewModel` (`OrderId` + one `RequestReturnLineViewModel` per order line).
- **Purpose:** Raise an RMA on a paid/shipped/delivered order. Each line shows what was ordered, its
  price, and a quantity input capped at the number of units not already refunded by earlier returns
  (`max` attribute, re-checked server-side). One open request per order; an unpaid order or one with
  nothing left to refund is refused.
- **Form:** one `POST` form (`Orders/RequestReturn`).
  | Field | Type | Limits |
  |---|---|---|
  | `OrderId` | hidden | Must be the signed-in shopper's order, still returnable. |
  | `Lines[n].OrderLineId` | hidden | Which order line the row refunds. |
  | `Lines[n].Quantity` | number, 0–remaining | 0 skips the line; server enforces remaining units. |
  | `Note` | textarea | ≤ 1000 chars; the reason, seen by the shop. |


---

## 5. Admin area (`/Admin`)

All admin pages share `_AdminLayout` (sidebar). Antiforgery is global.

### Dashboard — `/Admin/Dashboard` (`Dashboard/Index`)
- **Model:** `DashboardViewModel`. No forms. Links to other admin pages.

### Products list — `/Admin/Products` (`Products/Index`)
- **Model:** `AdminProductListViewModel`
- **Forms:** four.
  1. **Filter** (`GET Products/Index`) — `q` (search), `categoryId` (select), `state` (select).
  2. **Toggle published** (`Products/TogglePublished`, POST) — `id` (hidden).
  3. **Restore** (`Products/Restore`, POST) — `id` (hidden).
  4. **Delete** (`Products/Delete`, POST) — `id` (hidden), confirm dialog.

### Product edit — `/Admin/Products/Edit` (and Create) (`Products/Edit` / `Save`)
- **Model:** `ProductEditViewModel`
- **Form:** one `POST` form (`Products/Save`), `enctype="multipart/form-data"`.
  | Field | Type | Purpose |
  |---|---|---|
  | `Id` | hidden | 0 for new. |
  | `Name` | input | Product name. |
  | `Slug` | input | URL slug (auto-derived if blank). |
  | `Brand` | input | Brand. |
  | `ShortDescription` | textarea | Short description. |
  | `Description` | textarea | Full description. |
  | `CategoryId` | select | Category. |
  | `IsPublished` | checkbox | Published. |
  | `IsFeatured` | checkbox | Featured. |
  | `ImageUrls` | textarea | One URL per line. |
  | `ImageFiles` | file (multiple) | Uploaded images. |
  | `Variants[i].Id` | hidden | Variant id (0 for new). |
  | `Variants[i].Sku` | input | SKU. |
  | `Variants[i].Name` | input | Variant name. |
  | `Variants[i].Price` | number | Price. |
  | `Variants[i].CompareAtPrice` | number | Compare-at price. |
  | `Variants[i].StockQuantity` | number | Stock. |
  | `Variants[i].LowStockThreshold` | number | Low-stock threshold. |
  | `Variants[i].IsActive` | checkbox | Active. |
  | `Variants[i].Delete` | checkbox | Mark for deletion. |

### Bulk import — `/Admin/Import` (`Import/Index`)
- **Model:** `ImportController.ImportViewModel`
- **Form:** one `POST` form, `enctype="multipart/form-data"`.
  | Field | Type | Purpose |
  |---|---|---|
  | `shopId` | select | Target shop. |
  | `file` | file | CSV file (max 5 MB). |
  | `createMissing` | checkbox | Create products that do not exist yet. |

### Categories — `/Admin/Categories` (`Categories/Index`, `Save`, `Delete`)
- **Model:** `CategoryTreeViewModel` (index), `CategoryEditViewModel` (edit).
- **Forms:**
  1. **Save** (`Categories/Save`, POST) — `Id` (hidden), `Name`, `Slug`, `Description`, `ParentId` (select), `DisplayOrder` (number), `IsActive` (checkbox).
  2. **Delete** (`Categories/Delete`, POST) — `id` (hidden), confirm dialog.

### Inventory — `/Admin/Inventory` (`Inventory/Index`, `Adjust`, `SetStock`)
- **Model:** `InventoryViewModel`
- **Forms:** three.
  1. **Filter** (`GET Inventory/Index`) — `q` (search), `filter` (select).
  2. **Adjust stock** (`Inventory/Adjust`, POST) — `variantId` (hidden), `returnUrl` (hidden), `delta` (number, +/-), `reason` (select), `note` (text).
  3. **Set stock** (`Inventory/SetStock`, POST) — `variantId` (hidden), `newQuantity` (number), `note` (text).

### Orders list — `/Admin/Orders` (`Orders/Index`)
- **Model:** `AdminOrderListViewModel`
- **Form:** one `GET` filter form — `q` (search), `status` (select).

### Order detail — `/Admin/Orders/Details/{id}` (`Orders/Details`)
- **Model:** `Order`
- **Form:** one `POST` form (`Orders/UpdateStatus`).
  | Field | Type | Purpose |
  |---|---|---|
  | `id` | hidden | Order id. |
  | `status` | select | New status (legal transitions only). |
  | `trackingNumber` | text | Tracking number (for shipped). |
- Also shown: **Refunded so far** and **Still owed to shopper** totals under the money summary, and
  the order's returns with their statuses (read-only here — the returns work at
  [the returns queue](#returns-queue--adminreturns-adminreturns-index)).

### Returns queue — `/Admin/Returns` (`Returns/Index`)
- **Model:** `IReadOnlyList<ReturnRequest>` (order, shop and lines included).
- **Forms:** two.
  1. **Filter** (`GET Returns/Index`) — `status` (select: all plus each status), `q` (text, matches
     RMA reference, shopper email or order number).
  2. **Export** — GET links (`Returns/Export?format=csv|excel`), honouring the current filter.
- Shows per-status workload counters above the table; each row links to the return detail. The page
  lists up to 200 rows.

### Return detail — `/Admin/Returns/Details/{id}` (`Returns/Details`)
- **Model:** `ReturnRequest` (with order, shop, lines, and the per-line product snapshots).
- **Purpose:** Act on one return. Shows the shopper's request, the shop's decision, the audit
  history for the return, and — depending on state — the administrator's actions: decide
  (approve/reject) on the shop's behalf, record the goods as received, and **Issue refund**.
- **Forms:** four (`POST`).
  1. **Approve** (`Returns/Decide`) — `id` (hidden), `approve=true` (hidden), `note` (textarea, the
     decision note the vendor and shopper see).
  2. **Reject** (`Returns/Decide`) — same shape, `approve=false`; a reason is expected.
  3. **Receive** (`Returns/Receive`) — `id` (hidden); restocks the goods and opens the refund step.
  4. **Refund** (`Returns/Refund`) — `id` (hidden), `note` (textarea). Issues the money: goods +
     proportional tax (+ shipping only for a whole-order return), capped by the ledger remainder and
     `GrandTotal − RefundedTotal`; refuses loudly when the sale is not on the ledger to reverse.

### Customers list — `/Admin/Customers` (`Customers/Index`)
- **Model:** `AdminCustomerListViewModel`
- **Form:** one `GET` search form — `q` (search).

### Customer detail — `/Admin/Customers/Details/{id}` (`Customers/Details`)
- **Model:** `AdminCustomerDetailViewModel`
- **Forms:** two (both `Customers/SetActive`).
  1. Deactivate (confirm dialog) — `id` (hidden), `isActive` (hidden, false).
  2. Activate — `id` (hidden), `isActive` (hidden, true).

### Coupons list — `/Admin/Coupons` (`Coupons/Index`)
- **Model:** `IReadOnlyList<Coupon>`
- **Form:** one `POST` per coupon (`Coupons/Delete`) — `id` (hidden), confirm dialog.

### Coupon create — `/Admin/Coupons/Create` (`Coupons/Create`)
- **Model:** `Coupon`
- **Form:** one `POST` form (`Coupons/Create`).

### Coupon edit — `/Admin/Coupons/Edit/{id}` (`Coupons/Edit`)
- **Model:** `Coupon`
- **Form:** one `POST` form (`Coupons/Edit`).

The shared partial `_Form.cshtml` (used by both create and edit) exposes:
  | Field | Type | Purpose |
  |---|---|---|
  | `Id` | hidden | 0 for new. |
  | `Code` | input | Coupon code. |
  | `Type` | select | Percentage or fixed amount. |
  | `Value` | number | Discount value. |
  | `MinSubtotal` | number | Minimum order subtotal. |
  | `MaxUses` | number | Global usage cap. |
  | `MaxUsesPerUser` | number | Per-shopper cap. |
  | `StartsUtc` | datetime-local | Valid from. |
  | `ExpiresUtc` | datetime-local | Valid until. |
  | `Description` | input | Internal description. |
  | `AppliesToAll` | checkbox | Applies to all shops. |
  | `ShopId` | select | Restrict to one shop. |
  | `IsActive` | checkbox | Active. |

### Tax classes — `/Admin/Tax` (`Tax/Index`, `Create`, `Deactivate`)
- **Model:** `TaxController.TaxListViewModel`
- **Forms:** two.
  1. **Create** (`Tax/Create`, POST) — `name` (input), `rate` (number, 0–1), `isExempt` (checkbox).
  2. **Deactivate** (`Tax/Deactivate`, POST) — `id` (hidden).

### Shops — `/Admin/Shops` (`Shops/Index`)
- **Model:** `IReadOnlyList<Shop>`
- **Forms:** four per shop row.
  1. **Approve** (`Shops/Approve`, POST) — `id` (hidden), `commissionPercent` (number, e.g. 10).
  2. **Reject** (`Shops/Reject`, POST) — `id` (hidden), `reason` (shown to the vendor).
  3. **Suspend** (`Shops/Suspend`, POST) — `id` (hidden), `reason` (shown to the vendor).
  4. **Reinstate** (`Shops/Reinstate`, POST) — `id` (hidden).
- The table also shows each shop's recorded **payout destination** (account name, bank, IBAN, or
  "Not provided") and a **history** link straight to `/Admin/Audit?entityType=Shop&entityId={id}`.

### Payouts list — `/Admin/Payouts` (`Payouts/Index`)
- **Model:** `AdminPayoutIndexViewModel`
- **Form:** one `POST` form (`Payouts/Create`) — `shopId` (hidden), `method` (input), `note` (input).
- The balances table shows what each shop is owed; the history table carries a **status** column
  (`Paid` / `Failed` / `Reversed` badges).

### Payout detail — `/Admin/Payouts/Details/{id}` (`Payouts/Details`)
- **Model:** `AdminPayoutDetailViewModel`.
- Read-only statement (reference, period, every ledger entry it settled), plus, **while the payout is
  still `Paid`**, two correction forms (both `POST`, `id` hidden):
  1. **Mark failed** (`Payouts/MarkFailed`) — `reason` (required, ≤ 500 chars); the bank refused.
     Releases every covered entry back to the unpaid balance.
  2. **Reverse** (`Payouts/Reverse`) — `note` (optional); recorded in error. The same release, with
     the row keeping its totals for the statement.
  A `Failed`/`Reversed` payout shows its reason, who handled it, and when — and no forms.

### Marketing — `/Admin/Marketing` (`Marketing/Index`)
- **Model:** `MarketingDashboardStats`. No forms (exports are GET links).

### Reviews moderation — `/Admin/Reviews` (`Reviews/Index`)
- **Model:** `AdminReviewListViewModel`
- **Forms:** five.
  1. **Filter** (`GET Reviews/Index`) — `status` (hidden), `ratingFilter` (select).
  2. Three **SetStatus** forms (`Reviews/SetStatus`, POST) — `id`, `status`, `filter`, `rating`, `page` (hidden).
  3. **Delete** (`Reviews/Delete`, POST) — `id`, `filter`, `rating`, `page` (hidden), confirm dialog.

### Audit trail — `/Admin/Audit` (`Audit/Index`, `Export`)
- **Model:** `IReadOnlyList<AuditLog>` (newest first, 200 rows per page).
- **Forms:** two.
  1. **Filter** (`GET Audit/Index`) — `actionName` (text with datalist suggestions derived from the
     data), `entityType` (datalist), `entityId` (number), `actor` (text).
  2. **Export** — GET links (`Audit/Export?actionName=…&entityType=……`, up to 500 rows,
     `?format=csv|excel`).
- Each row: timestamp, actor, dotted action (`shop.approved`, `refund.failed`, …), entity reference
  and the summary sentence. Per-entity links (e.g. from the shops list) land here pre-filtered.

### Emails outbox — `/Admin/Emails` (`Emails/Index`, `Details`, `Resend`, `SendTest`)
- **Model:** `AdminEmailListViewModel` (list), `EmailMessage` (detail).
- **Forms:**
  1. **Filter** (`GET Emails/Index`) — `status` (hidden), `kindFilter` (select).
  2. **Send test** (`Emails/SendTest`, POST) — `status` (hidden), `kind` (hidden), `page` (hidden), `testTo` (email).
  3. **Resend** (`Emails/Resend`, POST) — `id`, `status`, `kind`, `page` (hidden).
- Email detail page has a **Resend** form — `id` (hidden).

---

## 6. Vendor area (`/Vendor`)

### Shop onboarding — `/Vendor/Onboarding/Register` (`Onboarding/Register`)
- **Model:** `ShopRegistration`
- **Form:** one `POST` form (`Onboarding/Register`).
  | Field | Type | Purpose |
  |---|---|---|
  | `Name` | input | Shop name. |
  | `Tagline` | input | Short tagline. |
  | `Description` | textarea | Shop description. |
  | `PickupLocation` | input | Where the courier collects parcels. |
  | `BankAccountName` | input | Payout destination — account holder. |
  | `BankName` | input | Payout destination — bank. |
  | `BankIban` | input | Payout destination — IBAN. |
  | `ThemeKey` | select | Storefront template. |
  | `AcceptTerms` | checkbox | Accept terms. |
- Reapplying a rejected application pre-fills every field, the payout details included.

### Vendor dashboard — `/Vendor/Dashboard` (`Dashboard/Index`)
- **Model:** `ShopDashboard`. No forms.

### Vendor products list — `/Vendor/Products` (`Products/Index`)
- **Model:** `AdminProductListViewModel` (shared)
- **Forms:** four (same as admin products list: filter, toggle published, restore, delete).

### Vendor product edit — `/Vendor/Products/Edit` (and Create) (`Products/Save`)
- **Model:** `ProductEditViewModel` (shared)
- **Form:** one `POST` form (`Products/Save`), `enctype="multipart/form-data"` — same fields as the admin product edit form.

### Vendor orders list — `/Vendor/Orders` (`Orders/Index`)
- **Model:** `IReadOnlyList<Order>`
- **Form:** one `GET` filter form — `status` (select), `q` (search).

### Vendor order detail — `/Vendor/Orders/Details/{id}` (`Orders/Details`)
- **Model:** `Order`. Read-only except for dispatch: the shop can register its own shipments
  through the simulator. Returns against the order appear in their own panel, linking to the RMA
  detail where the shop acts on them.
- **Forms:** two (`POST`) — **Ship** (`Orders/Ship`) and **Refresh tracking** (`Orders/RefreshTracking`),
  each just `id` (hidden). Status changes beyond shipping stay staff-only.

### Vendor returns — `/Vendor/Returns` (`Returns/Index`)
- **Model:** `IReadOnlyList<ReturnRequest>` (the shop's own only; another shop's is a `404`).
- **Form:** none — two filter links (`All` / `Open only`) and **Review** buttons into the detail.
- Columns: reference, order, requested, units, goods value, status badge.

### Vendor return detail — `/Vendor/Returns/Details/{id}` (`Returns/Details`)
- **Model:** `ReturnRequest`.
- **Purpose:** The worklist decision: approve or reject the request (with a note the shopper will
  see), then record the goods as received when the parcel arrives. Restocked units show up in the
  shop's stock ledger. The refund itself is staff-only and never shown here.
- **Forms:** three (`POST`).
  1. **Approve** (`Returns/Decide`) — `approve=true` (hidden), `note` (textarea).
  2. **Reject** (`Returns/Decide`) — same shape, `approve=false`.
  3. **Receive goods** (`Returns/Receive`) — no fields; restocks and closes the handing-back step.

### Vendor reviews — `/Vendor/Reviews` (`Reviews/Index`)
- **Model:** `VendorReviewListViewModel`
- **Forms:** two (both `Reviews/Reply`, POST).
  1. Write reply — `reviewId`, `stars`, `sort`, `page` (hidden), `reply` (textarea).
  2. Clear reply — `reviewId`, `reply` (hidden, empty), `stars`, `sort`, `page` (hidden).

### Vendor payouts — `/Vendor/Payouts` (`Payouts/Index`, `SaveBank`)
- **Model:** `VendorPayoutViewModel`.
- **Balance and settlements are read-only.** Every settlement shows its status badge (`Paid`,
  `Failed`, `Reversed`), and failed or reversed rows explain why (reason, who handled it, when).
- **Form:** one `POST` form (`Payouts/SaveBank`) — the payout destination the marketplace will use:
  | Field | Type | Limits |
  |---|---|---|
  | `bankAccountName` | input | ≤ 120 chars; account holder. |
  | `bankName` | input | ≤ 120 chars. |
  | `bankIban` | input | ≤ 40 chars. |

### Vendor payout detail — `/Vendor/Payouts/Details/{id}` (`Payouts/Details`)
- **Model:** `VendorPayoutDetailViewModel`. No forms; the statement plus its status — a `Failed` or
  `Reversed` settlement shows the reason and who marked it so.

### Vendor appearance — `/Vendor/Appearance` (`Appearance/Index`)
- **Model:** `ShopAppearanceViewModel`
- **Form:** one `POST` form (`Appearance/Index`).
  | Field | Type | Purpose |
  |---|---|---|
  | `themeKey` | radio | Chosen storefront template (one of six). |

---

## 7. Shared chrome and components

### Layout (`_Layout.cshtml`)
- **Forms:** three (global).
  1. **Sign out** (`Account/Logout`, POST) — `returnUrl` (hidden).
  2. **Language switcher** (`GET /set-language`) — `langSelect` (select: en/ar/fr), `returnUrl` (hidden).
  3. **Site search** (`GET Catalog/Index`) — `siteSearch` (search).

### Admin layout (`_AdminLayout.cshtml`)
- **Form:** one — **Sign out** (`Account/Logout`, POST).

### Cart badge component (`ViewComponents/CartBadge`)
- No forms.

### Category menu component (`ViewComponents/CategoryMenu`)
- No forms.

### Product card partial (`_ProductCard.cshtml`)
- No forms (links only).

---

## 8. Error and status pages

| Page | Route | Model | Forms |
|---|---|---|---|
| Error | `/Home/Error` | `ErrorViewModel` | none |
| Not found | `/Home/NotFound` | `NotFoundViewModel` | none |
| Access denied | `/Account/AccessDenied` | — | none |
| Privacy / policies | `/Home/Privacy` | — | none |

---

## Appendix A — field types at a glance

| Type | Used for |
|---|---|
| `input` (text) | Names, slugs, SKUs, brands, cities, codes, notes, search. |
| `email` | Account email, contact email, guest tracking. |
| `password` | Login, registration, password change/reset. |
| `tel` | Phone numbers. |
| `number` | Prices, quantities, stock, thresholds, display order, commission percent, tax rate, min/max price, discount value, max uses. |
| `date` / `datetime-local` | Coupon validity windows. |
| `checkbox` | Published, featured, active, default address, save address, accept terms, in-stock-only, create-missing, delete flags. |
| `radio` | Variant selection, rating (1–5), payment method, min rating, theme selection. |
| `select` | Category, brand, sort, status, filter, shop, tax class, address, language, country. |
| `textarea` | Descriptions, short descriptions, image URLs, order note, review body, reply, address lines. |
| `file` | Product images (single/multiple), CSV import. |
| `hidden` | Ids, tokens, return URLs, idempotency key, paging/filter state. |
| `search` | Free-text search boxes. |

## Appendix B — non-form interactions

These are not `<form>` elements but are interactive controls worth knowing about:

- **Image gallery** on product pages — clicking a thumbnail swaps the main image (client-side).
- **Saved-address “Use this address” button** on checkout — copies the selected address into the shipping fields (JavaScript, no submit).
- **Variant selector** on product pages — changes displayed price and stock.
- **Language switcher** in the top bar — submits the `/set-language` form.
- **Confirm dialogs** on destructive actions (delete product, delete address, empty cart, delete coupon, delete category, deactivate account, delete review) — browser `confirm()` before submit.
- **Autofilter / sort** controls on list pages submit their `GET` form on change.
- **Export links** (CSV / Excel) on admin list and marketing pages — plain links, no form.

---

## Appendix C — field validation rules

The rules below are the server-side constraints (DataAnnotations on the backing view models
and entities). Client-side validation mirrors them. Fields not listed carry no explicit
constraint beyond their type.

### Account

**Sign in (`LoginViewModel`)**
- `Email` — required ("Enter your email address."); must be a valid email address.
- `Password` — required ("Enter your password.").
- `RememberMe` — boolean; defaults to `true`.
- `ReturnUrl` — optional redirect target.

**Register (`RegisterViewModel`)**
- `DisplayName` — required ("Enter your name."); max 120 characters.
- `Email` — required; valid email; max 256 characters.
- `Phone` — phone format; max 40 characters (optional).
- `Password` — required ("Choose a password."); 8–100 characters.
- `ConfirmPassword` — must match `Password` ("The passwords do not match.").
- `AcceptTerms` — boolean; the form's terms link points to the privacy page.

**Update profile (`ProfileViewModel`)**
- `DisplayName` — required; max 120 characters.
- `Phone` — phone format; max 40 characters (optional).

**Change password (`ChangePasswordViewModel`)**
- `CurrentPassword` — required ("Enter your current password.").
- `NewPassword` — required ("Choose a new password."); 8–100 characters.
- `ConfirmPassword` — must match `NewPassword`.

**Forgot password (`ForgotPasswordViewModel`)**
- `Email` — required; valid email; max 256 characters.

**Reset password (`ResetPasswordViewModel`)**
- `Token` — hidden, posted back unchanged from the reset link.
- `Password` — required; 8–100 characters.
- `ConfirmPassword` — must match `Password`.

**Address book entry (`AddressFormModel`)**
- `Id` — 0 for new, otherwise the existing id.
- `FullName` — required ("Enter a recipient name."); max 120.
- `Line1` — required ("Enter a street address."); max 200.
- `Line2` — optional; max 200.
- `City` — required ("Enter a city."); max 100.
- `State` — optional; max 100.
- `PostalCode` — required ("Enter a postal code."); max 20.
- `Country` — required ("Enter a country."); max 80.
- `Phone` — phone format; max 40 (optional).
- `IsDefault` — boolean; defaults to false.

### Cart and checkout

**Apply coupon (`ApplyCouponModel`)**
- `Code` — required; max 64 characters (upper-cased on submit).

**Checkout (`CheckoutViewModel`)**
- `Email` — required ("Enter an email address so we can send your receipt."); valid email; max 256.
- `CustomerName` — required ("Enter the name for this order."); max 120.
- `Phone` — phone format; max 40 (optional).
- `ShippingName` — required ("Enter a recipient name."); max 120.
- `ShippingLine1` — required ("Enter a street address."); max 200.
- `ShippingLine2` — optional; max 200.
- `ShippingCity` — required ("Enter a city."); max 100.
- `ShippingState` — optional; max 100.
- `ShippingPostalCode` — required ("Enter a postal code."); max 20.
- `ShippingCountry` — required ("Enter a country."); max 80.
- `CustomerNote` — optional; max 1000.
- `SaveAddressToBook` — boolean; defaults to `true`.
- `IdempotencyKey` — hidden; prevents duplicate orders on double submit.
- `SelectedAddressId` — optional saved-address id; choosing one copies its values into the shipping fields.
- `PaymentMethod` — radio; defaults to cash on delivery; card only when a gateway is configured.
- `CardAvailable` — read-only flag set by the server.

**Track an order (`TrackOrderViewModel`)**
- `OrderNumber` — required ("Enter your order number.").
- `Email` — required ("Enter the email used for the order."); valid email.

### Catalog and product

**Catalog filter (`CatalogViewModel`)** — no server-side validation attributes; values are parsed
defensively and out-of-range filters are ignored.

**Product editor (`ProductEditViewModel` / `VariantInput`)**
- `Name` — required ("Enter a product name."); max 160.
- `Slug` — optional; max 160 (auto-derived from the name if blank, kept unique within the shop).
- `ShortDescription` — optional; max 200.
- `Description` — optional (no length cap).
- `Brand` — optional; max 120.
- `CategoryId` — required ("Choose a category.").
- `IsPublished` — boolean; defaults to `true`.
- `IsFeatured` — boolean.
- `ImageUrls` — optional; one URL per line; first becomes the primary image.
- `ImageFiles` — optional uploaded files (JPEG/PNG/GIF/WebP, max 5 MB each, 10 per product).
- Per variant:
  - `Sku` — required ("Every variant needs a SKU."); max 64.
  - `Name` — optional; max 80 (the option label, e.g. "Large").
  - `Price` — required; range 0.01–1,000,000 ("Price must be greater than zero.").
  - `CompareAtPrice` — optional; range 0–1,000,000 ("Was").
  - `StockQuantity` — range 0–1,000,000.
  - `LowStockThreshold` — range 0–10,000; defaults to 5.
  - `IsActive` — boolean; defaults to `true`.
  - `Delete` — boolean; marks the row for removal on save.

### Categories

**Category editor (`CategoryEditViewModel`)**
- `Name` — required ("Enter a category name."); max 80.
- `Slug` — optional; max 80.
- `Description` — optional; max 500.
- `ParentId` — optional parent (cycle-guarded).
- `DisplayOrder` — integer.
- `IsActive` — boolean; defaults to `true`.

### Admin operations

**Inventory adjust (`Inventory/Adjust`)**
- `delta` — signed integer (positive to add, negative to remove).
- `reason` — select (received, damaged, correction, etc.).
- `note` — free text.
- `variantId`, `returnUrl` — hidden.

**Inventory set stock (`Inventory/SetStock`)**
- `newQuantity` — non-negative integer.
- `note` — free text.

**Order status update (`Orders/UpdateStatus`)**
- `status` — select; only legal transitions are permitted by the state machine.
- `trackingNumber` — text; used when moving to *Shipped*.

**Return request (`Orders/RequestReturn`, `RequestReturnViewModel`)**
- `Lines[n].Quantity` — integer, 0 or 1..remaining units; 0 skips the line. The remaining figure is
  what the earlier refunded returns have not already taken from the line. At least one line must be
  non-zero, and the order must still be open for returns (paid/shipped/delivered, not already
  fully refunded, and without another open return).
- `Note` — optional; max 1000 characters, seen by the shop.

**Return decision / receive / refund (`Returns/Decide`, `Returns/Receive`, `Returns/Refund`)**
- `id` — hidden; the return must be in the state that permits the step (only *Requested* can be
  decided, only *Approved* received, only *Received* refunded).
- `approve` — hidden boolean on `Decide`; a rejected return should carry a `note`.
- `note` — optional textarea; max 1000 characters for a decision (stored as the decision note),
  recorded verbatim on the refund history for a refund.

**Payout corrections (`Payouts/MarkFailed`, `Payouts/Reverse`)**
- `id` — hidden; only a payout still in `Paid` may be corrected.
- `MarkFailed` takes a required `reason` (max 500 characters) stored as the failure reason;
  `Reverse` takes an optional `note`. Both are shown to the shop on the vendor payouts pages.

**Payout destination (`Vendor/Payouts/SaveBank`)**
- `bankAccountName` — optional text, max 120 (trimmed server-side).
- `bankName` — optional text, max 120.
- `bankIban` — optional text, max 40.

**Audit filter (`Audit/Index`)**
- `actionName`, `entityType`, `entityId`, `actor` — all optional text/number; unknown values simply
  leave the trail empty.

**Customer activate/deactivate (`Customers/SetActive`)**
- `id` — hidden customer id.
- `isActive` — hidden boolean (false to deactivate, true to reactivate).

**Coupon editor (`Coupon`)**
- `Code` — required; max 64 (stored upper-cased, unique).
- `Description` — optional; max 256.
- `Type` — select (percentage or fixed).
- `Value` — percent 0–100 for percentage, or an amount for fixed.
- `MinSubtotal` — minimum order subtotal; 0 means no minimum.
- `MaxUses` — total redemptions allowed; blank means unlimited.
- `MaxUsesPerUser` — per-shopper redemptions; blank means unlimited.
- `StartsUtc` / `ExpiresUtc` — optional validity window (either bound may be left open).
- `IsActive` — boolean; defaults to `true`.
- `AppliesToAll` — boolean; defaults to `true`.
- `ShopId` — required when `AppliesToAll` is false.

**Tax class create (`Tax/Create`)**
- `name` — required text.
- `rate` — number in the range 0–1 (entered as a fraction, e.g. `0.11`).
- `isExempt` — checkbox; forces the rate to zero.

**Shop approve (`Shops/Approve`)**
- `id` — hidden shop id.
- `commissionPercent` — number entered as a percentage (e.g. `10` = 10%); stored as a fraction.

**Shop suspend (`Shops/Suspend`)**
- `id` — hidden shop id.
- `reason` — text (shown to the vendor).

**Payout create (`Payouts/Create`)**
- `shopId` — hidden.
- `method` — text (e.g. "Bank transfer").
- `note` — text.

**Import (`Import/Index`)**
- `shopId` — select (target shop).
- `file` — file (CSV, max 5 MB).
- `createMissing` — checkbox; when off, update-only (no new products).

**Reviews moderation (`Reviews/SetStatus`)**
- `id`, `status`, `filter`, `rating`, `page` — all hidden; `status` is the new moderation state.

**Emails (`Emails/SendTest`, `Emails/Resend`)**
- `testTo` — email address for the test message.
- `id`, `status`, `kind`, `page` — hidden paging/filter state.

### Vendor

**Shop registration (`ShopRegistration`)**
- `Name` — shop name (required by the service; not annotated in the model, enforced in `RegisterAsync`).
- `Tagline`, `Description`, `ContactName`, `Phone`, `Email`, `AddressLine1`, `City`,
  `PostalCode`, `PickupLocation` — optional text.
- `BankAccountName`, `BankName`, `BankIban` — optional payout details (≤ 120 / 120 / 40 chars,
  trimmed server-side), stored on the shop and editable later on the payouts page.
- `Country` — defaults to "Lebanon".
- `ThemeKey` — optional template key (falls back to the default if unknown).
- `AcceptTerms` — boolean.

**Vendor review reply (`Reviews/Reply`)**
- `reviewId` — hidden.
- `reply` — textarea; empty clears the reply.
- `stars`, `sort`, `page` — hidden (preserve the list view).

**Appearance (`Appearance/Index`)**
- `themeKey` — radio; one of the six template keys.

### Checkout and order fields (shared)

**Order note** — `CustomerNote` max 1000 characters; shown on the order, visible to staff.

---

*Validation rules extracted from the view models and entities in `Models/`, `Areas/Admin/Models/`,
`Areas/Admin/Controllers/`, `Services/` and `Domain/`.*