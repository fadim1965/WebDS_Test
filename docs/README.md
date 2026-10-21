# LibanPost eShop — documentation

Four audiences, four documents. Start with the one that matches why you are here.

| If you are… | Read |
|---|---|
| **A shopper** — browsing, buying, tracking an order, leaving a review | [User guide](USER_GUIDE.md) |
| **A vendor** — selling on the marketplace | [Vendor guide](VENDOR_GUIDE.md) |
| **Store staff** | [Administrator guide](ADMIN_GUIDE.md) |
| **A developer or operator** | [`../README.md`](../README.md) |

---

## The whole set

| Document | Audience | What it covers |
|---|---|---|
| [`../README.md`](../README.md) | Developers, operators | Architecture, configuration, database providers, payments, logging, deployment constraints, and the known limitations. |
| [`USER_GUIDE.md`](USER_GUIDE.md) | Shoppers | Browsing, the cart, discounts, checkout, paying, tracking, returns, accounts, wishlist, reviews, language. |
| [`LibanPost_eShop_User_Guide.pdf`](LibanPost_eShop_User_Guide.pdf) | Everyone | The same guide as a downloadable PDF, covering shoppers, vendors and administrators in one document. All three PDFs have a clickable table of contents and PDF bookmarks. |
| [`VENDOR_GUIDE.md`](VENDOR_GUIDE.md) | Vendors | Opening a shop, products, the CSV importer, orders, returns, reviews, commission and payouts, storefront appearance. |
| [`ADMIN_GUIDE.md`](ADMIN_GUIDE.md) | Store staff | Every `/Admin` page: catalogue, import, categories, inventory, orders, returns, customers, coupons, tax, shops, payouts, marketing, reviews, email, the audit trail, exports, the log. |
| [`FEATURES.md`](FEATURES.md) | Everyone | The complete feature inventory, as a checklist of what exists. |
| [`PAGES_AND_FORMS.md`](PAGES_AND_FORMS.md) | Everyone | A page-by-page and form-by-form reference: every screen, route, model and field. |
| [`DATABASE_SCHEMA.md`](DATABASE_SCHEMA.md) | Developers, operators | The SQL Server schema: every table, column, type, nullability, primary and foreign keys, and the relationships between them. |
| [`LibanPost_eShop_Database_Schema.pdf`](LibanPost_eShop_Database_Schema.pdf) | Developers, operators | The same schema reference as a downloadable PDF. |
| [`LibanPost_eShop_Pages_and_Forms.pdf`](LibanPost_eShop_Pages_and_Forms.pdf) | Everyone | The same page/form reference as a downloadable PDF. |
| [`FORMS_ILLUSTRATED.md`](FORMS_ILLUSTRATED.md) | Everyone | A screenshot walkthrough — every form-bearing page shown, with its fields explained. |
| [`LibanPost_eShop_Forms_Illustrated.pdf`](LibanPost_eShop_Forms_Illustrated.pdf) | Everyone | The illustrated walkthrough as a downloadable PDF (all 47 screenshots embedded; the Part 6 return, refund and audit pages are described in text). |
| [`ECOMMERCE_PLAN.md`](ECOMMERCE_PLAN.md) | Everyone | The original plan and the reasoning behind the design decisions. |

---

## A note on these guides

They describe **what the software does** — the behaviour you will actually observe — and they are
deliberately blunt about the places where the store is a demonstration rather than a production
system. Where a feature stops short, the guide says so rather than implying more than the code
delivers. The authoritative source is always the application itself; if a guide and the store
disagree, the store is right and the guide is a bug.
