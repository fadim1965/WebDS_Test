# WebDS_Test vs. Kwikby Requirements — Gap Analysis

**Date:** 2026-10-22  
**Source Documents:**
- `Kwikby-processes business rules.pdf` (Cancellation, Return/Refund, Exchange, Order Delivery, RTS, Defect under Warranty processes)
- `Ecommerce LP platform requirement document+eddress_20180607.xlsx` (130+ requirements across platform, frontend, backend, integrations, payments, shipping, reports)

---

## Executive Summary

WebDS_Test is a solid **multi-vendor marketplace foundation** (vendors, products, orders, payouts, moderation, 2FA, translations, dual-currency).  
Kwikby is a **Logistics-first e-commerce platform** built around LibanPost's physical network: the requirements are dominated by **order fulfilment lifecycle**, **shipper integration**, **cash-on-delivery reconciliation**, **international shipping rules**, and **post-sale processes (cancellation, return, exchange, RTS, warranty)**.

**Bottom line:** WebDS_Test covers ~55% of Kwikby's functional scope. The missing 45% is almost entirely **post-checkout operations** and **LibanPost-specific logistics**.

---

## 1. Process Lifecycle Gaps (from Kwikby Process PDFs)

| Kwikby Process | Status in WebDS_Test | Gap Details |
|---|---|---|
| **Cancellation** (Process 1–3) | ❌ Partial | `ReturnService` exists but only handles *returns*. No cancellation-before-packaging / after-packaging / after-TR# logic. No "cancel/return = one button" UX. |
| **Return & Refund** (Process 4-A/B/C) | ❌ Partial | `ReturnRequest` entity + `ReturnController` exist, but rules matrix (who pays pickup/return/replacement fees per reason: unsatisfied/wrong/damaged/changed-mind/shipper-damage) is not encoded. No promo-code-only refund policy for local orders. |
| **Exchange** (Process 6-A/B/C) | ❌ Missing | No exchange workflow. Kwikby treats exchange-before-packaging, after-packaging, after-delivery (→ return process) as distinct. |
| **Order Delivery** (Local CC, standard shipping) | ❌ Partial | `FulfilmentService` + `SimulatorShippingCarrier` + `LibanPostCarrier` stub exist, but **no seller fulfilment flow**: seller email with order summary, 3pm deadline reply, package count → label generation, nylon-pocket docs, pending-shipment queue. |
| **RTS (Return to Sender)** (Process 5) | ❌ Missing | No "customer unreachable → EPOD scans → 2 call attempts → manifest → quality report → redeliver sticker → resend or return-to-seller → finance refund → manual return order" flow. |
| **Defect under Warranty** (Process 7/7-A) | ❌ Missing | No 3-day grace period, repair/replace vs refund logic, seller-charged fees for defective items. |

**Key missing domain concepts:**
- `OrderStatus` enum lacks: `InProgress`, `Completed`, `CancelledFullyReturned`, `PartiallyReturned`, `ReturnOrder`
- No `Shipment` / `TrackingNumber` / `Manifest` / `QualityReport` / `EPOD` entities
- No `SellerFulfilment` (package count, label generation, nylon-pocket docs, 3pm deadline)
- No `RTSProcess` / `RedeliveryAttempt` / `Manifest` / `QualityReport`
- No `WarrantyClaim` / `RepairOrReplace` / `GracePeriod` logic

---

## 2. Platform Requirements Gaps (from Excel — 130 rows)

### 2.1 Frontend / Customer Experience

| Requirement | WebDS_Test | Gap |
|---|---|---|
| **Multi-currency (USD/LBP + auto-detect country → currency popup)** | ✅ Dual currency (USD primary + LBP display, admin rate) | No auto-detect + popup; rate is admin-set only (no periodic FX feed) |
| **CMS (edit content/layout for promotions)** | ❌ | No CMS; content is Razor views |
| **Personalisation (cross-sell/up-sell based on search/bought)** | ❌ | No recommendation engine |
| **Product images: thumbnails, zoom, multiple, video** | ✅ Multiple images | No zoom, no video |
| **On-sale / Sold-out badges (auto)** | ✅ `IsFeatured`, `IsPublished` | No automatic "On sale" badge from `CompareAtPrice` |
| **Limited-time offer countdown** | ❌ | |
| **Clickable spec buttons (color, size)** | ❌ | Variants exist but not rendered as clickable spec pills |
| **Brand/merchant profile page (logo, info, products)** | ✅ `ShopsController` + `Details.cshtml` | Template-driven merchant page not built |
| **Combo packs / bundles** | ❌ | |
| **Compare products** | ❌ | |
| **Wishlist categories (customer-created folders)** | ❌ | Flat wishlist only |
| **Stock notifications (back-in-stock alerts)** | ❌ | |
| **Live chat / chatbot** | ❌ | |
| **FAQs** | ❌ | |
| **Click-to-call (mobile)** | ❌ | |
| **Social sharing per product** | ❌ | |
| **Product reviews & ratings** | ✅ `ReviewService` + `ReviewsController` | |
| **Polls/surveys with export** | ❌ | |
| **Non-intrusive popups (side/bottom)** | ❌ | |
| **Newsletter / new-product alert / discount alert** | ❌ | |
| **Gift-wrap / surprise button** | ❌ | |
| **One-page checkout (minimal fields for guest)** | ❌ | Multi-step checkout |
| **Payment options: COD, Credit Card, Domestic LP Card, PO Retail** | ⚠️ COD + Simulator | No domestic LP card, no PO retail |
| **Multiple shipping options (regular/express)** | ❌ | Single simulator carrier |
| **Shipping fee calculator per product (LibanPost API)** | ❌ | `LibanPostCarrier` stub only |
| **International shipping: DHL/EMS/RR, insurance option, billing≠delivery address, country restrictions per item** | ❌ | |
| **Taxable items at checkout** | ⚠️ `TaxClass` + `TaxCalculator` | No per-item tax at checkout UI |
| **Order consolidation (multi-supplier → single shipment)** | ❌ | |
| **Track & trace (by order, by supplier)** | ⚠️ `Shipment` entity | No customer-facing tracking page with supplier breakdown |
| **Cancellation by customer (up to X hrs, reverse entry, bank refund policy)** | ❌ | |
| **After-sales messaging (complaints/inquiries per order)** | ✅ `ConversationService` | But not tied to order/return context |
| **Dashboard: order history, transactions, reward points, complaints at item level, advertising** | ⚠️ `OrdersController` + `Profile` | No reward points, no item-level complaints, no ads |
| **Return policy: local = promo code only (no cash), international = complaint → promo code or replacement on LP** | ❌ | `ReturnService` allows refund to payment method |
| **SEO: sitemaps, clean URLs, analytics** | ❌ | |

### 2.2 Backend / Management Tools

| Requirement | WebDS_Test | Gap |
|---|---|---|
| **ERP integration (Create/Update products via API, unique item ID, serial numbers for warranty, expiry dates for perishables)** | ❌ | `ProductImportService` (CSV only) |
| **Prohibited items management (export restrictions per country)** | ❌ | |
| **Orders management: fulfil & track until delivery, customer tracking/history/printable invoices, approximate ship date/time** | ⚠️ `OrdersController` | No fulfilment workflow, no printable invoices, no ship-date estimate |
| **Reports: marketing/BD/sales, big spenders, audit trail** | ✅ `ReportingService` + `AuditTrail` | Missing specific sales reports below |
| **Shipment management: LP decides exportability, endpoint not LP's concern** | ❌ | |
| **Merchant/store management: LP operators manage small merchants; large merchants self-manage** | ✅ `ShopService` + `Onboarding` | Role split exists but UI for LP operator vs large merchant not separated |
| **Admin: view/edit/create orders on behalf of customers** | ⚠️ `OrdersController` | Create/edit not implemented |
| **Customer multi-address book** | ✅ `Address` entity + `Addresses.cshtml` | |
| **Wishlist category organization (customer folders)** | ❌ | |
| **Customizable email notifications (wishlist sale, registration gifts, tracking wishlist for gifts)** | ⚠️ `EmailService` + templates | No trigger-based campaigns |
| **Verified payment badges** | ❌ | |
| **Credit card integration (Amex, Mastercard) — chargeable per item in backend** | ❌ | Simulator only |
| **Video support for items & banners** | ❌ | |
| **Sortable/filterable customer reviews** | ❌ | |
| **Discount for cart threshold (e.g., 20% off >$200 excl. delivery)** | ❌ | Coupons exist but not cart-threshold auto-discount |
| **Payment installments** | ❌ | |
| **International items: export flag per item, filter by delivery address, DHL/EMS/RR choice, insurance option, billing≠delivery** | ❌ | |
| **Loyalty schemes, wishlists, gift tokens on first purchase** | ❌ | |
| **Sales reports (day/week/month/year with ERP code, qty, price, total, category, subcategory, compare periods)** | ❌ | Generic `ReportingService` only |
| **Products info report (export individually or combined, with category/subcategory)** | ❌ | |
| **Best-selling / Most-viewed / By-rating / Wishlist / Cart / Search-keyword reports** | ❌ | |
| **Reconciliation with suppliers (supplier statement of account on platform)** | ⚠️ `PayoutService` + `VendorLedger` | No supplier self-service portal |
| **Activity tracking / CRM / stock management / sales audit trail** | ✅ `AuditTrail` + `LogEntry` | CRM integration missing |
| **Shipping fees calculator calling LibanPost API (insurance, volumetric weight, destination, delivery time tiers)** | ❌ | Stub only |
| **Local delivery fees: weight/volume, order-amount or subscription (Prime-style)** | ❌ | |
| **Exchange rate: fixed periodically, validate with finance, auto-detect country → currency** | ⚠️ Admin-set rate | No periodic fix, no auto-detect |
| **Basket: edit quantity, remove item** | ⚠️ CartService | Remove works; "cannot remove, need to remove all" is a UX bug in Kwikby req |
| **Taxable items at checkout** | ⚠️ TaxClass | Not surfaced at checkout |
| **Consolidation of orders (multi-supplier → one shipment, optional)** | ❌ | |
| **Track & trace by order & by supplier** | ⚠️ | Partial |

---

## 3. Data Model Gaps

| Kwikby Entity | WebDS_Test Equivalent | Missing Fields / Entities |
|---|---|---|
| **Order** | `Order` | `TrackingNumber`, `ManifestId`, `EstimatedShipDate`, `ActualShipDate`, `DeliveryStatus` (Open/Invoiced/InProgress/Completed/CancelledFully/PartiallyReturned/ReturnOrder), `CancellationReason`, `CancellationRequestedUtc`, `PackagingStatus`, `SellerConfirmedPackageCountUtc` |
| **Shipment / Tracking** | `Shipment` (basic) | `ManifestId`, `EPODScanEvents`, `CallAttempts`, `QualityReport`, `RedeliverySticker`, `RTSManifest`, `DistributionCenter` |
| **SellerFulfilment** | ❌ | `OrderId`, `SellerId`, `PackageCount`, `ConfirmedAt`, `LabelsGenerated`, `NylonPocketDocsAttached`, `InvoiceAttached` |
| **ReturnOrder** | `ReturnRequest` | `ReturnType` (Refund/Exchange/Repair/Replace), `FeeAllocation` (Pickup/Return/Replacement paid by Customer/Seller/Shipper), `PromoCodeIssued`, `PromoCodeValue`, `GracePeriodEndUtc`, `RepairOrReplaceDecision` |
| **ExchangeRequest** | ❌ | Entire entity |
| **RTSProcess** | ❌ | `ManifestId`, `CallAttempts`, `QualityReport`, `RedeliverySticker`, `ResendDecision` (Customer/Seller), `FinanceRefundTriggered` |
| **WarrantyClaim** | ❌ | `ProductVariantId`, `SerialNumber`, `PurchaseDate`, `GracePeriodEndUtc`, `RepairOrReplace`, `SellerCharged` |
| **MerchantProfile** | `Shop` | `TemplatePageContent`, `ExportableCountries[]`, `ShippingCarriers[]` (DHL/EMS/RR), `InsuranceOptionEnabled` |
| **CustomerProfile** | `ApplicationUser` | `RewardPoints`, `LoyaltyTier`, `GiftTokens`, `WishlistFolders[]` |
| **ShippingRateCard** | ❌ | Per-carrier, per-weight/volume, per-zone, per-service-level (same-day/next-day/1hr) with insurance, volumetric calc |
| **PromoCodeCampaign** | `Coupon` | Trigger types (wishlist-sale, registration-gift, cart-threshold, back-in-stock), auto-apply rules |
| **SupplierPortal** | ❌ | Statement of account, self-service reconciliation |

---

## 4. Integration Gaps

| Integration | Kwikby Requirement | WebDS_Test |
|---|---|---|
| **LibanPost API (shipping rates, tracking, EPOD, RTS manifests)** | Core | Stub only (`LibanPostCarrier`) |
| **ERP (product sync, serial numbers, expiry dates)** | Required | CSV import only |
| **Payment Gateway (Amex, Mastercard, domestic LP card, PO retail, installments)** | Required | Simulator + COD only |
| **DHL / EMS / RR (international)** | Required | ❌ |
| **CRM / Audit trail / Stock management** | Required | `AuditTrail` + `LogEntry` (no CRM) |
| **Analytics / Sitemaps / SEO** | Required | ❌ |
| **Single sign-on across LP website, e-commerce, in-store** | Required | ❌ (separate Identity) |

---

## 5. Business Rule Implementation Status

| Rule Area | Implemented? | Notes |
|---|---|---|
| **Cancellation before packaging → full refund (item + shipping + CC fees)** | ❌ | |
| **Cancellation after packaging → refund item + shipping, no CC fees** | ❌ | |
| **Cancellation after TR# → decline** | ❌ | |
| **Return: unsatisfied/wrong/damaged → seller pays all fees** | ❌ | `ReturnService` refunds to payment method |
| **Return: changed mind → customer pays all fees, promo code only** | ❌ | |
| **Return: shipper damage → shipper pays all fees** | ❌ | |
| **Exchange before/after packaging → no fees, allowed** | ❌ | No exchange flow |
| **Exchange after delivery → treat as return (Process 4)** | ❌ | |
| **RTS: 2 call attempts → manifest → quality report → redeliver or return-to-seller** | ❌ | |
| **Warranty: 3-day grace → full refund; outside grace → repair/replace or refund; seller charged** | ❌ | |
| **Local returns → promo code only (no cash)** | ❌ | |
| **International returns → complaint → promo code or LP-paid replacement** | ❌ | |
| **Shipping fees: weight + volumetric + destination + time tier + insurance** | ❌ | |
| **Commission per category (override shop default)** | ✅ | Feature 5 done |
| **Multi-currency display (USD primary + LBP indicative)** | ✅ | Feature 2 done |
| **Admin translation UI (DB-backed, seeded from files)** | ✅ | Feature 3 done |
| **Listing moderation (vendor submits → admin approves)** | ✅ | Feature 4 done |
| **2FA (TOTP + backup codes)** | ✅ | Feature 6 done |

---

## 6. Prioritised Roadmap to Close Gaps

### Phase 1 — Post-Sale Lifecycle (Highest business value)
1. **Cancellation workflow** — add `CancellationRequest` with status matrix (before/after packaging, after TR#), fee rules, stock replenishment triggers.
2. **Return/Refund rules engine** — encode the 4-A/B/C matrix (who pays pickup/return/replacement per reason), promo-code-only for local, complaint→replacement for international.
3. **Exchange workflow** — 6-A/B/C with auto-fallback to return after delivery.
4. **RTS process** — EPOD scan events, call attempts, manifest, quality report, redelivery sticker, resend/return-to-seller, finance refund trigger.

### Phase 2 — Fulfilment & Shipping (Logistics core)
5. **Seller fulfilment flow** — order email → 3pm deadline → package count → label generation → nylon-pocket docs → pending-shipment queue.
6. **LibanPostCarrier implementation** — real API for rates, tracking, EPOD, RTS manifests.
7. **Shipping rate cards** — weight/volume, zones, service levels (same-day/next-day/1hr), insurance, volumetric calc.
8. **International shipping** — DHL/EMS/RR, export flags per item, country restrictions, billing≠delivery, insurance option.

### Phase 3 — Customer Experience & Marketing
9. **CMS for promotions** — editable banners, landing pages, countdown timers.
10. **Recommendation engine** — cross-sell/up-sell based on behaviour.
11. **Wishlist folders, back-in-stock alerts, gift-wrap, surprise button.**
12. **Loyalty points, gift tokens, referral codes.**
13. **One-page guest checkout, payment installments, domestic LP card, PO retail.**
14. **Auto-detect country → currency popup, periodic FX rate feed.**

### Phase 4 — Reports & Supplier Portal
15. **Sales reports (day/week/month/year with ERP code, category hierarchy, period compare).**
16. **Product info, best-selling, most-viewed, ratings, wishlist, cart, search-keyword exports.**
17. **Supplier self-service portal (statement of account, reconciliation).**

### Phase 5 — Integrations & Platform
18. **ERP API (product sync, serial numbers, expiry dates, prohibited items per country).**
19. **Payment gateways (Amex, Mastercard, domestic LP card, PO retail, installments).**
20. **DHL/EMS/RR integration.**
21. **SSO across LP properties.**
22. **SEO (sitemaps, clean URLs, analytics).**

---

## 7. Quick Wins (Low Effort, High Visibility)

| Item | Effort | Value |
|---|---|---|
| Add "On sale" badge when `CompareAtPrice > Price` | 1 day | Merchandising |
| Auto "Sold out" badge when all variants `StockQuantity == 0` | 1 day | UX |
| Clickable variant pills (color/size) on product page | 2 days | UX |
| Printable invoice from order details | 3 days | Operations |
| Cart-threshold auto-discount (e.g., 20% off >$200) | 2 days | Conversion |
| Wishlist folders (customer-defined categories) | 3 days | Retention |
| Back-in-stock email trigger | 2 days | Revenue recovery |

---

## 8. Architecture Notes for Implementation

- **Domain-driven**: Model each Kwikby process as a distinct aggregate (`Cancellation`, `Return`, `Exchange`, `RTS`, `WarrantyClaim`) with its own state machine.
- **Event-sourcing ready**: Emit domain events (`OrderCancelled`, `ReturnRequested`, `ExchangeApproved`, `RTSInitiated`, `WarrantyClaimed`) for audit trail and async projections (reports, supplier portal).
- **Shipper integration**: Abstract `IShippingCarrier` with `LibanPostCarrier`, `DHLCarrier`, `EMSCarrier` implementations; `CarrierRegistry` selects by destination/service-level.
- **Multi-tenancy**: `Shop.IsGlobal` + `Shop.ManagedByOperator` flag to distinguish LP-managed vs self-managed merchants.
- **Idempotency**: All webhook handlers (payment, EPOD, RTS) keyed on external reference + idempotency key (already present in `EmailService`/`PaymentWebhookService` pattern).

---

## 9. Conclusion

WebDS_Test is an **excellent marketplace core** (vendors, catalog, cart, checkout, payouts, moderation, i18n, 2FA).  
Kwikby is a **logistics-intensive e-commerce platform** where the marketplace is a thin layer on top of LibanPost's physical network.

**To become Kwikby**, WebDS_Test needs ~6–9 months of focused work on **fulfilment, shipper integration, post-sale processes, and international shipping** — the areas where LibanPost's operational reality differs from a generic marketplace.

The six features already delivered (messaging, dual-currency, translations, moderation, per-category commission, 2FA) are **exactly the right marketplace foundations**; the next investment should shift to **logistics and fulfilment**.
---

## 10. Implementation Status (post-sale phase)

This section records what has shipped since the analysis above; the tables in sections 1-7 remain
the honest description of what was missing when the analysis was written.

### Delivered

- **One post-sale document, five processes.** `PostSaleRequest` covers cancellation, return,
  exchange, RTS and warranty with a status timeline, a message thread, carrier attempts, warranty
  assessment and pickup/warehouse timestamps.
- **Fee rules encoded.** `PostSaleRules` maps Kwikby processes 1-7 to a `FeeAllocation` per fee
  line (item price, shipping, credit card, pickup, return, replacement) and to who bears each.
  The split is stored on the request, so a decision stays explainable.
- **Local refund policy.** Changed-mind and wrong-option returns issue a single-use promo code for
  the goods value instead of cash (`IsPromoCodeRefund`); the code is shown on the customer page.
- **Customer intake.** Raise, follow, message and withdraw from the order page, with per-order
  history (`/PostSale`).
- **Admin desk.** Queue with status/type/search filters and counts, approve/reject, pickup and
  receipt transitions, RTS recording, warranty triage (repairable/replaceable, fee split
  recomputed for the chosen remedy) and remedy processing (cash refund capped at the order's
  remaining total, promo code, replacement shipped, repair closed). Every action is audited.
- **Seller fulfilment (packing process).** The seller confirms package count, nylon pockets and
  invoice; the back office generates one label per package and records shipper pickup. Vendors see
  their own queue; the confirmation form lives on the vendor order page.
- **Schema.** Tables are created by an idempotent guard for SQL Server and SQLite, matching the
  EF model (including the `Users.Id` length and the replacement-variant foreign key).
- **Tests.** 8 rules tests for the fee matrix and 9 service tests for the lifecycle
  (cancellation, return/promo code, exchange, warranty triage, RTS, fulfilment).

### Still open (highest value first)

| Gap | Why it matters | Suggested next step |
|---|---|---|
| Post-sale refunds do not reverse the vendor ledger | The vendor is still credited for goods the marketplace refunded | Add a `PostSaleRequestId` discriminator to `VendorLedgerEntries` and reverse in `PostSaleService.ProcessAsync`, mirroring `ReturnService.RefundAsync` |
| Promo codes are not emailed | The customer must visit the request page to see the code | Queue a `post-sale.remedy` email from `ProcessAsync` through the existing outbox |
| No payment-gateway refund in the post-sale path | Card refunds are recorded as rows but not sent back through the gateway | Route cash refunds through `IPaymentGateway.RefundAsync` the way returns do |
| No seller 3 pm packing deadline / reminder email | Kwikby's daily cut-off drives the pickup schedule | Scheduled hosted service plus a reminder template |
| No manifest, EPOD scans or quality-report document | RTS and shipper reconciliation still rely on manual notes | Carrier webhook plus `ManifestNumber`/`QualityReport` surfaced on the RTS page |
| Warranty grace period is order-date based only | Kwikby measures from delivery | Store `DeliveredUtc` as the warranty start when present |

---

## 11. Verification of the external gap checklist (2026-10-26)

An external reviewer supplied eleven "potential gaps". They were checked one by one against the
working tree — model, services, controllers, views and configuration — before any fix was assigned.
Three labels are used:

- **Confirmed** — the code shows the gap, and a shipped path can reach it.
- **Latent** — the gap is real in the model or a controller, but no shipped feature can exercise it yet.
- **Blocked** — the code path is complete and tested; only a third party (credentials, contract) is missing.

| # | Checklist item | Verdict | Evidence in the current tree | Priority |
|---|---|---|---|---|
| 1 | Mixed-shop cart / multi-vendor checkout | **Confirmed, structural** | `Cart.ShopId` derives one shop from `Items`; `CartService.AddAsync` refuses a second shop with a clear message; `OrderService.PlaceOrderAsync` already groups by `ShopId` and suffixes the idempotency key per order, but `CheckoutController.Place` persists and redirects to `result.Order` only, so later orders are invisible to a guest; the coupon is validated against the whole cart yet applied only when `shopIndex == 0` | P1 |
| 2 | Line-level / partial shipment | **Confirmed** | `OrderShipment` carries `OrderId` and `ShopId`, not an order line or quantity; `FulfilmentService.DispatchAsync` is order-wide; `ShipmentStatus` and `OrderStatus` have no partial state; `SellerFulfilment.PackageCount` counts labels, not dispatched lines. Returns and post-sale, by contrast, *are* line-scoped (`ReturnRequestLine.OrderLineId`, `PostSaleRequest.OrderLineId`) | P2 |
| 3 | Verified LibanPost carrier integration | **Blocked** | `LibanPostCarrier` implements create/track/cancel/label/COD against an **assumed** contract (its own XML comment says so); `Shipping:Provider` is `simulator` and no credentials are configured. The adapter and the simulator are interchangeable behind `IShippingCarrier` | P0 for go-live |
| 4 | Real payment provider | **Blocked** | Only `SandboxPaymentGateway` and `SimulatorPaymentGateway` exist; signature verification, idempotent event log, capture and `RefundAsync` are complete and tested. Choice of processor and credentials are outstanding | P0 for go-live |
| 5 | Carrier COD remittance reconciliation | **Confirmed** | Cash is tracked per order (`CodAmount`, `CollectionStatus`), and the vendor ledger is credited on delivery for COD and on payment for prepaid. There is no remittance, deposit-batch or carrier-statement entity, so a carrier shortfall cannot be matched to orders | P1 |
| 6 | Vendor role enforcement | **Confirmed, latent** | `ShopAccess` exposes `CanManageCatalogue`, `CanManageSettings`, `CanFulfilOrders`; checks exist only in `Products`, `Appearance` and `Dashboard`. `Orders`, `Returns`, `PostSale`, `Messages`, `Payouts`, `Reports` and `Reviews` resolve the shop with `GetPrimaryShopForUserAsync` and never test the role, and `Payouts.SaveBank` has no owner check. However `IShopService.AddUserAsync`/`RemoveUserAsync` have **no caller** and no team screen exists, so a non-owner membership cannot currently be created through the application | P1 |
| 7 | Notifications: low stock, SMS, WhatsApp | **Confirmed** | The outbox is email-only (`EmailMessage`, `EmailKind`); kinds cover order, return, payout, post-sale remedy and verification. No low-stock or back-in-stock kind, and no SMS/WhatsApp transport | P2 |
| 8 | Support desk: priority, assignment, SLA, disputes | **Confirmed** | `Conversation` has per-side read markers, `LastAuthor`, a preview and an optional order link; there is no priority, assignee, SLA clock or escalation state | P2 |
| 9 | Reports: profitability, fulfilment SLA, retention | **Confirmed** | 15 admin and 10 vendor report keys (sales, products, categories, shops, orders, customers, returns, payouts, commission, ledger, statement, inventory, stock movements, coupons, tax, reviews). None ranks vendor profitability or measures dispatch/delivery SLA or repeat purchase | P2 |
| 10 | Product comparison / recently viewed | **Confirmed** | Catalogue has facets, sorts, related products and reviews; there is no comparison list and no recently-viewed history | P3 |
| 11 | Outbound API keys / webhooks / ERP | **Confirmed** | Inbound integrations exist (CSV product import, Shopify catalogue client, CSV/PDF export). No API-key model, no outbound event webhook, no ERP connector | P2 |

### Findings the checklist missed

- **The consolidation requirement contradicts the order model.** The Excel sheet asks for
  *multi-supplier to single shipment* consolidation; the platform deliberately writes **one order and
  one shipment per shop**. Enabling a mixed cart without deciding this point would produce several
  consignments for one basket — the opposite of the requirement.
- **No team management surface.** Role checks can be added cheaply, but until `AddUserAsync` is
  reachable (vendor "Team" screen or admin shop detail) the three roles are schema-only.
- **Guest confirmation is single-order.** Even after the cart allows several shops, a guest will be
  able to open only the first order unless `Place` records every id in the viewable-orders session key.
- **Coupon scope vs. split orders.** `ValidateAndComputeAsync` prices the discount across the whole
  cart, while only the first shop's order stores it; on a mixed cart this silently over-discounts
  shop 0 and ignores the rest.
- **Back-in-stock alerts** are absent as a customer-facing feature, distinct from the vendor's
  low-stock threshold.

### Recommended order of work

1. **Vendor authorization + team surface** (P1, small): apply `ShopAccess` to every vendor action,
   make `SaveBank` owner-only, and add the screen that creates the roles being enforced.
2. **COD remittance** (P1, medium): a deposit/remittance document that groups collected shipments,
   records the carrier's actual transfer and surfaces the variance, mirrored by a vendor-ledger
   adjustment when the carrier underpays.
3. **Multi-shop checkout** (P1, medium): only after the consolidation question above is answered —
   lift the cart restriction, apply coupons per shop or as a marketplace-funded discount, and show
   every order on confirmation and in history.
4. **Live carrier and payments** (P0, blocked): obtain the LibanPost interface specification and
   credentials plus the chosen payment processor's keys; the adapters are ready for them.
5. **Notifications, support SLA, reports, discovery, outbound integration** (P2/P3): each is additive
   and can ship independently.
