# LibanPost eShop — shopper guide

Everything a customer can do on the storefront, in the order they are likely to do it.

> This is a demonstration store. It is fully working, but no real money moves and no real parcel is
> sent. A card payment goes through the bundled payment **simulator**, which behaves like a provider
> without being one.

---

## Contents

- [Finding your way around](#finding-your-way-around)
- [Browsing and searching](#browsing-and-searching)
- [A product page](#a-product-page)
- [Your cart](#your-cart)
- [Discount codes](#discount-codes)
- [Checkout](#checkout)
- [Paying for an order](#paying-for-an-order)
- [Tracking an order](#tracking-an-order)
- [Returning something](#returning-something)
- [Your account](#your-account)
- [Wishlist](#wishlist)
- [Reviews](#reviews)
- [Language](#language)
- [Questions shoppers usually ask](#questions-shoppers-usually-ask)

---

## Finding your way around

The header is the same on every page.

| What | Where it takes you |
|---|---|
| **Search box** | Type a few characters and matching products and departments appear beneath it. Press Enter or pick a suggestion. |
| **All departments** | Every category in the catalogue. |
| **Shops** | The shop directory — every seller on the marketplace. |
| **Track an order** | Look up an order without signing in. |
| **Your cart** | The basket, with a running item count. |
| **My account** | Your profile, orders, addresses and wishlist. |

Signing in is optional. You can browse, fill a cart and check out as a guest; an account is only
needed for order history, a wishlist, saved addresses and posting reviews.

---

## Browsing and searching

**The catalogue** (`/Catalog`) shows every published product and is where all filtering happens.

Search covers the product **name, description, brand and SKU**. Filters on offer:

| Filter | Notes |
|---|---|
| Department | Includes sub-categories. |
| Price range | From and to. |
| Brand | Only brands that actually exist in the results are offered. |
| In stock only | Hides anything you could not buy today. |
| Minimum rating | Hides products below the star rating you choose. |

Eight sort orders are available, including **relevance** (useful when you searched), **top rated**
and **most reviewed**.

**Every filter lives in the web address.** The result set you are looking at can be bookmarked,
shared or sent to yourself, and it will come back exactly as you left it.

Products that have been withdrawn or are still drafts never appear anywhere in the storefront.

---

## A product page

A product page (`/product/…`) collects everything about one item:

- **Image gallery** — click a thumbnail to change the main image. If a product has no photograph the
  store shows a placeholder rather than a broken image.
- **Variants** — size, weight, colour or whatever the seller defined. **Choose one before adding to
  the cart**; the price and stock shown follow the variant you pick.
- **Stock messaging** — "in stock", or a warning such as *"Only 3 left"* when it is running low.
- **Sale price** — a struck-through original price next to the current one when the item is discounted.
- **Add to cart** and **wishlist** buttons.
- **Related products** — other items from the same seller or department.
- **Reviews** — the rating summary, a per-star breakdown, and every published review.

---

## Your cart

The cart (`/Cart`) is the one place where the order's arithmetic is shown in full.

- **Change the quantity** or remove a line, and the totals update immediately.
- **Per-line stock warnings** appear if something in your basket has become unavailable or you have
  asked for more than remains.
- **A free-shipping hint** tells you how far you are from the free-delivery threshold.
- **The running total** shows subtotal, shipping, tax and the amount due.

The **tax** line is calculated per item, from each product's own tax class. In a basket holding both
a standard-rated and a zero-rated item, each is taxed at its own rate — you are not charged one flat
rate across the whole order.

Your cart is remembered between visits, and it survives signing in — items added as a guest are
merged into your account rather than discarded.

---

## Discount codes

Apply a code in the cart. There is a **one-code-at-a-time** rule: entering a second code replaces the
first rather than stacking.

A few things worth knowing, because they explain what you will see:

- **The code is remembered, not the amount.** The discount is recalculated against your current
  basket on every page view. If you change the cart, the figure moves with it.
- **If a code stops being valid** — it expires, or reaches its limit, while it is sitting in your
  cart — it simply drops off with a notice. You will never be shown a discount you will not get.
- **Every rule is re-checked when you place the order.** What the order charges is decided server-side,
  never from what your browser last displayed.
- A discount is **capped at the value of the goods it applies to**, so a total can never go negative.

The demonstration store is seeded with `WELCOME10` (10% off, once per shopper), `POST5` ($5 off orders
over $30) and `EXPIRED` (a code that no longer works, so you can see the rejection).

---

## Checkout

Checkout (`/Checkout`) is a single form:

1. **Contact** — name, email and optionally a phone number. The email is what the order confirmation
   is sent to.
2. **Delivery address** — fill it in, or pick one you have saved. You can tick a box to save a new
   address to your address book for next time.
2. **Payment** — cash on delivery, or card if a card gateway is configured (see
   [Paying for an order](#paying-for-an-order)).
3. **Review** the order summary, then **place the order**.

Placing an order is **idempotent**: if your connection stutters and the form is submitted twice,
you get one order, not two.

---

## Paying for an order

**Cash on delivery** is the default. Nothing is taken up front; you pay when the parcel reaches you.

When a card gateway is configured, **card** is offered as well. Choosing it sends you to the
provider's own payment page, and the order is confirmed by the provider telling the store the payment
succeeded — **not** by your browser reporting it. That distinction is what makes it safe: closing the
tab or losing signal mid-payment cannot mark an order paid.

**If a card payment is declined**, the order is **not** failed. It stays pending and you are offered a
link to try again — the same order is captured when a later attempt succeeds.

On the demonstration store, the card option runs through a **simulator** that signs and verifies its
own messages exactly as a real provider would. It moves no money.

---

## Tracking an order

Two ways, and neither needs the other:

- **Signed in** — *My orders* (`/Orders`) lists your history, and each order opens onto its detail
  page with a **status timeline**.
- **As a guest** — *Track an order* (`/Orders/Track`) takes your **order number** and the **email you
  used**, and shows the same status without an account.

An order moves through these states:

```
Pending  →  Paid  →  Shipped  →  Delivered
   └─────────┴──────── ┴──────→  Cancelled
                              →  Refunded
```

Only legal moves are allowed — a cancelled order cannot quietly become delivered.

---

## Returning something

When a purchase does not work out, you can send it back — the flow is the same for every shop.

**How it works:**

1. **Ask** — open the order on *My orders* and press **Request a return**. The form lists every line
   with the units you may still return (units a previous return already refunded are gone) and a box
   for the reason. Submit it; you get an **RMA number** (for example `RMA-2026-0001`).
2. **The shop decides** — the shop approves or rejects your request, with a note. Both outcomes email
   you.
3. **The goods go back** — once approved, hand the parcel back. The shop records it as **received**;
   anything you send back goes onto their shelf again.
4. **The refund** is issued by the store staff.** The maths is deliberately simple: the price of
   what you return (your share of any discount included), plus the proportionate tax. Shipping is
   refunded only when the whole order goes back. Part of an order can be returned; the order page
   keeps its running total of what has been refunded so far.

**Watching it move** — the return's badge on the order page follows the same path:

```
Requested  →  Approved  →  Received  →  Refunded
     (or) Rejected                    (you can also cancel while it is open)
```

You can cancel a return yourself at any point before it is received, by pressing **Cancel request**
on the order page. An order may hold only **one open return** at a time; once one is settled you can
raise another. Guest checkouts cannot raise returns — sign in first so the shop knows who you are.

---

## Your account

Register or sign in from the header. An account gives you:

- **Order history** and order detail.
- **A wishlist**.
- **An address book** — as many addresses as you like, one marked as the default, editable and
  removable.
- **Profile and password change**.
- **Password reset by email** if you forget it. The reset link works exactly once and expires; the
  request page always reports success, so it cannot be used to find out which addresses are
  registered here.

Passwords are stored using PBKDF2-HMAC-SHA256 with 210,000 iterations. Repeated failed sign-ins lock
the account for a short window, and a wrong password never reveals whether the account exists.

---

## Wishlist

Save a product from its page with the wishlist button; it is stored against your account and listed
at `/Account/Wishlist`. Press the button again to remove it.

---

## Reviews

Reviews exist to help the next shopper, so they are tied to real purchases.

- **Who may review** — you must be signed in and have a **paid, shipped or delivered** order line for
  that product. A **verified purchase** badge is decided by the store from your order history and is
  never something the form can claim.
- **One review per shopper per product.** After you post one, the form becomes an edit form.
- **Reviews publish immediately.** Moderation exists to remove the minority that break the rules, not
  to hold up everybody's.
- **Vote helpful or not helpful** on someone else's review. One vote each; vote again to change your
  mind. The "most helpful" ordering follows those votes.
- **Sellers may reply once** under a review, publicly. A reply is the only thing a seller can change —
  your rating and your words stay yours.

Product scores and shop scores are calculated from published reviews every time they are shown, so a
score can never drift away from the reviews behind it.

---

## Language

The store is available in **English**, **Arabic** and **French**. The switcher is in the top bar.

- Your choice is remembered on your device.
- If you have not chosen, the store follows your browser's preference, and falls back to English.
- **Arabic renders right-to-left**, and the layout mirrors with it.

At present the *chrome* — navigation, the cart, checkout, account and footer labels — is translated.
Product names, descriptions and some deeper screens are still English only, so expect a partly
translated page in Arabic or French.

---

## Questions shoppers usually ask

**Do I need an account to buy?**
No. You can check out as a guest. An account is needed for order history, a wishlist, saved addresses
and reviews.

**Why did my discount code disappear?**
It stopped being valid — it expired, or it reached its usage limit. The store re-checks the code
against your basket on every page, so a code that is no longer usable simply drops off. Nothing is
charged on a discount you cannot have.

**Why can I not add this item to my cart?**
Pick a variant first. A product with options cannot be added until one is chosen.

**Why is the item in my cart now out of stock?**
Stock is reserved when the order is placed, not when it is added to the cart, so something can sell
out while it waits in your basket. The cart will tell you which line is affected.

**My payment was declined — did I lose the order?**
No. The order stays pending with its payment attempt marked failed, and you are offered a retry link.
A later successful payment captures the same order.

**Can I use two discount codes together?**
No. One code per order, and a second replaces the first.

**Can I change or cancel an order after placing it?**
Cancelling is a staff action. Contact the store; if the order is cancelled, stock is returned to the
shelf.

**Can I give one item of a multi-item order back?**
Yes — the return form takes quantities per line. The store refunds what you return plus its tax;
shipping comes back only with a whole order.

**My return was approved. When does the money arrive?**
The refund is issued by store staff once the shop records the goods as received. The order page
shows the running refund total for the order.

**Where did my order confirmation email go?**
The demonstration store writes email to files rather than sending it (see the developer README under
*Known limitations*). Nothing reaches a real inbox until an SMTP host is configured.
