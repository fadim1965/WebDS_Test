# -*- coding: utf-8 -*-
"""Generate the LibanPost eShop user guide as a PDF.

Uses only reportlab (installed). Run:  python docs/build_user_guide.py
Output: docs/LibanPost_eShop_User_Guide.pdf
"""
import re, hashlib
from reportlab.lib import colors
from reportlab.lib.pagesizes import A4
from reportlab.lib.styles import getSampleStyleSheet, ParagraphStyle
from reportlab.lib.units import mm
from reportlab.lib.enums import TA_JUSTIFY, TA_LEFT, TA_CENTER
from reportlab.platypus import (
    SimpleDocTemplate, Paragraph, Spacer, PageBreak, Table, TableStyle,
    ListFlowable, ListItem, KeepTogether, HRFlowable,
)
from reportlab.platypus.tableofcontents import TableOfContents
from reportlab.platypus.doctemplate import PageTemplate, BaseDocTemplate
from reportlab.platypus.frames import Frame

PAGE_W, PAGE_H = A4
MARGIN = 18 * mm
USABLE = PAGE_W - 2 * MARGIN

NAVY = colors.HexColor("#006db7")
GOLD = colors.HexColor("#fed300")
INK = colors.HexColor("#1a1a1a")
MUTED = colors.HexColor("#6b6b6b")
LIGHT = colors.HexColor("#f6f6f6")
BORDER = colors.HexColor("#d7d7d7")

styles = getSampleStyleSheet()
def S(name, parent=None, **kw):
    if parent is None:
        parent = styles["Normal"]
    return ParagraphStyle(name, parent=parent, **kw)

TITLE = S("Title", fontName="Helvetica-Bold", fontSize=26, leading=32,
          textColor=NAVY, alignment=TA_CENTER, spaceAfter=6)
SUBTITLE = S("Subtitle", fontName="Helvetica", fontSize=13, leading=17,
             textColor=MUTED, alignment=TA_CENTER, spaceAfter=4)
H1 = S("H1", fontName="Helvetica-Bold", fontSize=17, leading=22,
       textColor=NAVY, spaceBefore=16, spaceAfter=6, keepWithNext=1)
H2 = S("H2", fontName="Helvetica-Bold", fontSize=13, leading=17,
       textColor=INK, spaceBefore=12, spaceAfter=4, keepWithNext=1)
H3 = S("H3", fontName="Helvetica-Bold", fontSize=11, leading=14,
       textColor=INK, spaceBefore=8, spaceAfter=3, keepWithNext=1)
BODY = S("Body", fontName="Helvetica", fontSize=10.5, leading=14.5,
         alignment=TA_JUSTIFY, spaceAfter=6, textColor=INK)
BULLET = S("Bullet", fontName="Helvetica", fontSize=10.5, leading=14.5,
           alignment=TA_LEFT, spaceAfter=3, textColor=INK, leftIndent=0)
SMALL = S("Small", fontName="Helvetica", fontSize=9, leading=12,
          textColor=MUTED, spaceAfter=4)
CODE = S("Code", fontName="Courier", fontSize=9.5, leading=12.5,
         textColor=INK, spaceAfter=6, leftIndent=6)
CAPTION = S("Caption", fontName="Helvetica-Oblique", fontSize=9,
            leading=11.5, textColor=MUTED, spaceAfter=8, spaceBefore=2)

def _toc_plain(text):
    """Plain, XML-safe heading text for TOC entries."""
    t = re.sub(r"<[^>]+>", "", text)
    t = t.replace("`", "")
    t = re.sub(r"\*\*(.+?)\*\*", r"\1", t)
    t = re.sub(r"\*(.+?)\*", r"\1", t)
    return t.replace("&", "&amp;").replace("<", "&lt;").replace(">", "&gt;")

TOC_LEVELS = {"H1": 0, "H2": 1, "H3": 2}
TOC_L0 = S("TocL0", fontName="Helvetica-Bold", fontSize=10.5, leading=15,
           textColor=NAVY, spaceBefore=8, leftIndent=0)
TOC_L1 = S("TocL1", fontName="Helvetica", fontSize=10, leading=13.5,
           textColor=INK, leftIndent=10)
TOC_L2 = S("TocL2", fontName="Helvetica", fontSize=9.5, leading=12.5,
           textColor=MUTED, leftIndent=22)


class GuideDocTemplate(SimpleDocTemplate):
    """SimpleDocTemplate that records heading flowables for the TOC and
    adds matching PDF outline (bookmark) entries."""

    def beforeDocument(self):
        self._seen = {}
        self._prev_olevel = -1

    def afterFlowable(self, flowable):
        if not isinstance(flowable, Paragraph):
            return
        toc_text = getattr(flowable, "_toc_text", None)
        if toc_text is None:
            return
        level = TOC_LEVELS[flowable.style.name]
        occ = self._seen.get((level, toc_text), 0) + 1
        self._seen[(level, toc_text)] = occ
        # Deterministic key: identical across multiBuild passes, else the TOC
        # never converges (its entries embed the key).
        key = "h" + hashlib.sha1(
            ("%d|%d|%s" % (level, occ, toc_text)).encode("utf-8")).hexdigest()[:10]
        try:
            # The PDF outline must start at level 0 and may never skip a level.
            olevel = 0 if self._prev_olevel < 0 else min(level, self._prev_olevel + 1)
            self.canv.bookmarkPage(key)
            self.canv.addOutlineEntry(re.sub(r"<[^>]+>", "", toc_text), key, olevel, 0)
            self._prev_olevel = olevel
        except Exception as ex:
            if not getattr(self, "_outline_err_shown", False):
                print("outline warn: %r" % ex)
                self._outline_err_shown = True
        self.notify("TOCEntry", (level, toc_text, self.page, key))


def md(text):
    """Tiny markdown-ish -> reportlab markup converter."""
    t = text.replace("&", "&amp;").replace("<", "&lt;").replace(">", "&gt;")
    t = re.sub(r"`([^`]+)`", r'<font name="Courier" size="9">\1</font>', t)
    t = re.sub(r"\*\*(.+?)\*\*", r"<b>\1</b>", t)
    t = re.sub(r"\*(.+?)\*", r"<i>\1</i>", t)
    t = t.replace("\n", "<br/>")
    return t

def para(text, style=BODY):
    return Paragraph(md(text), style)

def h1(text):
    p = Paragraph(md(text), H1)
    p._toc_text = _toc_plain(text)
    return p
def h2(text):
    p = Paragraph(md(text), H2)
    p._toc_text = _toc_plain(text)
    return p
def h3(text):
    p = Paragraph(md(text), H3)
    p._toc_text = _toc_plain(text)
    return p

def bullets(items, style=BULLET):
    flow = []
    lf = ListFlowable(
        [ListItem(Paragraph(md(i), style), leftIndent=12, value="bulletchar")
         for i in items],
        bulletType="bullet", start="circle", leftIndent=14,
        bulletFontName="Helvetica", bulletFontSize=8,
    )
    flow.append(lf)
    flow.append(Spacer(1, 4))
    return flow

def numbered(items, style=BULLET):
    lf = ListFlowable(
        [ListItem(Paragraph(md(i), style), leftIndent=12) for i in items],
        bulletType="1", leftIndent=18, bulletFontName="Helvetica-Bold",
    )
    return [lf, Spacer(1, 4)]

def table(data, widths=None, header=True, font_size=9.5):
    if widths is None:
        widths = [USABLE / len(data[0])] * len(data[0])
    # Cells must be Paragraphs — raw strings never wrap and overflow the page.
    cell = S("Cell", fontName="Helvetica", fontSize=font_size,
             leading=font_size + 3, textColor=INK)
    cellh = S("CellH", fontName="Helvetica-Bold", fontSize=font_size,
              leading=font_size + 3, textColor=colors.white)
    pdata = [
        [Paragraph(md(str(c)), cellh if (header and r == 0) else cell)
         for c in row]
        for r, row in enumerate(data)
    ]
    t = Table(pdata, colWidths=widths, repeatRows=1 if header else 0)
    ts = [
        ("VALIGN", (0, 0), (-1, -1), "TOP"),
        ("LEFTPADDING", (0, 0), (-1, -1), 6),
        ("RIGHTPADDING", (0, 0), (-1, -1), 6),
        ("TOPPADDING", (0, 0), (-1, -1), 4),
        ("BOTTOMPADDING", (0, 0), (-1, -1), 4),
        ("GRID", (0, 0), (-1, -1), 0.4, BORDER),
    ]
    if header:
        ts += [
            ("BACKGROUND", (0, 0), (-1, 0), NAVY),
            ("ROWBACKGROUNDS", (0, 1), (-1, -1), [colors.white, LIGHT]),
        ]
    t.setStyle(TableStyle(ts))
    return t

def hr():
    return HRFlowable(width="100%", thickness=0.6, color=BORDER,
                      spaceBefore=4, spaceAfter=6)

def on_page(canvas, doc):
    canvas.saveState()
    # footer
    canvas.setFont("Helvetica", 8.5)
    canvas.setFillColor(MUTED)
    canvas.drawString(MARGIN, 12 * mm,
                     "LibanPost eShop — User Guide (demo platform)")
    canvas.drawRightString(PAGE_W - MARGIN, 12 * mm,
                           "Page %d" % doc.page)
    # top accent bar
    canvas.setFillColor(NAVY)
    canvas.rect(0, PAGE_H - 6 * mm, PAGE_W, 6 * mm, fill=1, stroke=0)
    canvas.setFillColor(GOLD)
    canvas.rect(0, PAGE_H - 6 * mm, PAGE_W * 0.35, 6 * mm, fill=1, stroke=0)
    canvas.restoreState()

# ------------------------------------------------------------------ content
story = []

# ---- title page
story.append(Spacer(1, 40 * mm))
story.append(Paragraph("LibanPost eShop", TITLE))
story.append(Paragraph("User Guide", S("t2", parent=SUBTITLE, fontSize=18, leading=22, textColor=NAVY)))
story.append(Spacer(1, 6 * mm))
story.append(hr())
story.append(Spacer(1, 6 * mm))
story.append(Paragraph(
    "A complete guide to using the platform — for shoppers, vendors (shop owners) and "
    "store staff. Written against the running application, so what is described here is "
    "what the software actually does.", SUBTITLE))
story.append(Spacer(1, 14 * mm))
story.append(table([
    ["Audience", "Where to start"],
    ["Shoppers (customers)", "Part 1 — Shopping on the storefront"],
    ["Vendors / shop owners", "Part 2 — Running your shop"],
    ["Store staff / administrators", "Part 3 — Administering the store"],
], widths=[USABLE * 0.4, USABLE * 0.6]))
story.append(Spacer(1, 14 * mm))
story.append(Paragraph(
    "Demo platform note: this is a fully working demonstration store. No real money moves and "
    "no real parcel is sent. Card payments run through a bundled simulator that behaves like a "
    "provider without being one. See \u201CKnown limitations\u201D at the end.", SMALL))
story.append(Paragraph("Version: current (2026)  \u00b7  Generated from the application source.",
                       SMALL))
story.append(PageBreak())

# ---- table of contents (generated: page numbers + clickable links + bookmarks)
story.append(Paragraph("Contents", H1))
story.append(Spacer(1, 4))
_toc = TableOfContents()
_toc.levelStyles = [TOC_L0, TOC_L1, TOC_L2]
_toc.dotsMinLevel = 0
story.append(_toc)
story.append(PageBreak())

# =====================================================================
# PART 1
# =====================================================================
story.append(h1("Part 1 \u2014 Shopping on the storefront"))

story.append(h2("1. Finding your way around"))
story.append(para("The header is the same on every page."))
story.append(table([
    ["What", "Where it takes you"],
    ["Search box", "Type a few characters; matching products and departments appear beneath it. Press Enter or pick a suggestion."],
    ["All departments", "Every category in the catalogue."],
    ["Shops", "The shop directory \u2014 every seller on the marketplace."],
    ["Track an order", "Look up an order without signing in."],
    ["Your cart", "The basket, with a running item count."],
    ["My account", "Your profile, orders, addresses and wishlist."],
], widths=[USABLE * 0.3, USABLE * 0.7]))
story.append(para("Signing in is optional. You can browse, fill a cart and check out as a guest; an account is "
                  "only needed for order history, a wishlist, saved addresses and posting reviews."))

story.append(h2("2. Browsing and searching"))
story.append(para("The catalogue (`/Catalog`) shows every published product and is where all filtering happens. "
                  "Search covers the product name, description, brand and SKU. Filters on offer:"))
story.append(table([
    ["Filter", "Notes"],
    ["Department", "Includes sub-categories."],
    ["Price range", "From and to."],
    ["Brand", "Only brands that actually exist in the results are offered."],
    ["In stock only", "Hides anything you could not buy today."],
    ["Minimum rating", "Hides products below the star rating you choose."],
], widths=[USABLE * 0.3, USABLE * 0.7]))
story.append(para("Eight sort orders are available, including relevance (useful when you searched), top rated "
                  "and most reviewed."))
story.append(para("Every filter lives in the web address. The result set you are looking at can be bookmarked, "
                  "shared or sent to yourself, and it will come back exactly as you left it."))
story.append(para("Products that have been withdrawn or are still drafts never appear anywhere in the storefront."))

story.append(h2("3. A product page"))
story.append(para("A product page collects everything about one item:"))
story.extend(bullets([
    "**Image gallery** \u2014 click a thumbnail to change the main image. Missing photographs show a placeholder.",
    "**Variants** \u2014 size, weight, colour or whatever the seller defined. Choose one before adding to the cart; "
    "the price and stock shown follow the variant you pick.",
    "**Stock messaging** \u2014 \u201Cin stock\u201D, or a warning such as \u201COnly 3 left\u201D when it is running low.",
    "**Sale price** \u2014 a struck-through original price next to the current one when discounted.",
    "**Add to cart** and **wishlist** buttons.",
    "**Related products** \u2014 other items from the same seller or department.",
    "**Reviews** \u2014 the rating summary, a per-star breakdown, and every published review.",
]))

story.append(h2("4. Your cart"))
story.append(para("The cart is the one place where the order\u2019s arithmetic is shown in full."))
story.extend(bullets([
    "Change the quantity or remove a line, and the totals update immediately.",
    "Per-line stock warnings appear if something in your basket has become unavailable or you asked for more than remains.",
    "A free-shipping hint tells you how far you are from the free-delivery threshold.",
    "The running total shows subtotal, shipping, tax and the amount due.",
]))
story.append(para("Tax is calculated per item, from each product\u2019s own tax class. In a basket holding both a "
                  "standard-rated and a zero-rated item, each is taxed at its own rate \u2014 you are not charged one "
                  "flat rate across the whole order."))
story.append(para("Your cart is remembered between visits, and it survives signing in \u2014 items added as a guest "
                  "are merged into your account rather than discarded."))

story.append(h2("5. Discount codes"))
story.append(para("Apply a code in the cart. One code at a time: entering a second replaces the first rather than stacking."))
story.extend(bullets([
    "The code is remembered, not the amount. The discount is recalculated against your current basket on every page view.",
    "If a code stops being valid \u2014 it expires, or reaches its limit, while sitting in your cart \u2014 it simply drops off with a notice.",
    "Every rule is re-checked when you place the order. What the order charges is decided server-side, never from what your browser last displayed.",
    "A discount is capped at the value of the goods it applies to, so a total can never go negative.",
]))
story.append(para("Seeded demo codes: `WELCOME10` (10% off, once per shopper), `POST5` ($5 off orders over $30) "
                  "and `EXPIRED` (a code that no longer works, so you can see the rejection)."))

story.append(h2("6. Checkout"))
story.append(para("Checkout is a single form:"))
story.extend(numbered([
    "**Contact** \u2014 name, email and optionally a phone number. The email is what the order confirmation is sent to.",
    "**Delivery address** \u2014 fill it in, or pick one you have saved. Tick a box to save a new address to your address book.",
    "**Payment** \u2014 cash on delivery, or card if a card gateway is configured.",
    "**Review** the order summary, then **place the order**.",
]))
story.append(para("Placing an order is idempotent: if your connection stutters and the form is submitted twice, "
                  "you get one order, not two."))

story.append(h2("7. Paying for an order"))
story.append(para("**Cash on delivery** is the default. Nothing is taken up front; you pay when the parcel reaches you."))
story.append(para("When a card gateway is configured, **card** is offered as well. Choosing it sends you to the "
                  "provider\u2019s own payment page, and the order is confirmed by the provider telling the store the "
                  "payment succeeded \u2014 not by your browser reporting it. That distinction is what makes it safe: "
                  "closing the tab or losing signal mid-payment cannot mark an order paid."))
story.append(para("If a card payment is declined, the order is **not** failed. It stays pending and you are offered "
                  "a link to try again \u2014 the same order is captured when a later attempt succeeds."))
story.append(para("On the demonstration store, the card option runs through a **simulator** that signs and verifies "
                  "its own messages exactly as a real provider would. It moves no money."))

story.append(h2("8. Tracking an order"))
story.append(para("Two ways, and neither needs the other:"))
story.extend(bullets([
    "**Signed in** \u2014 *My orders* lists your history, and each order opens onto its detail page with a status timeline.",
    "**As a guest** \u2014 *Track an order* takes your order number and the email you used, and shows the same status without an account.",
]))
story.append(para("An order moves through these states:"))
story.append(Paragraph("Pending  \u2192  Paid  \u2192  Shipped  \u2192  Delivered     (also: Cancelled, Refunded)",
                      CODE))
story.append(para("Only legal moves are allowed \u2014 a cancelled order cannot quietly become delivered."))

story.append(h2("9. Your account"))
story.append(para("Register or sign in from the header. An account gives you:"))
story.extend(bullets([
    "**Order history** and order detail.",
    "**A wishlist.**",
    "**An address book** \u2014 as many addresses as you like, one marked as the default, editable and removable.",
    "**Profile and password change.**",
    "**Password reset by email** if you forget it. The reset link works exactly once and expires; the request page always reports success.",
]))
story.append(para("Passwords are stored using PBKDF2-HMAC-SHA256 with 210,000 iterations. Repeated failed sign-ins "
                  "lock the account for a short window, and a wrong password never reveals whether the account exists."))

story.append(h2("10. Wishlist"))
story.append(para("Save a product from its page with the wishlist button; it is stored against your account and listed "
                  "at your wishlist page. Press the button again to remove it."))

story.append(h2("11. Reviews"))
story.append(para("Reviews exist to help the next shopper, so they are tied to real purchases."))
story.extend(bullets([
    "**Who may review** \u2014 you must be signed in and have a paid, shipped or delivered order line for that product. A verified-purchase badge is decided by the store from your order history.",
    "**One review per shopper per product.** After you post one, the form becomes an edit form.",
    "**Reviews publish immediately.** Moderation exists to remove the minority that break the rules.",
    "**Vote helpful or not helpful** on someone else\u2019s review. One vote each; vote again to change your mind.",
    "**Sellers may reply once** under a review, publicly.",
]))
story.append(para("Product scores and shop scores are calculated from published reviews every time they are shown, "
                  "so a score can never drift away from the reviews behind it."))

story.append(h2("12. Language"))
story.append(para("The store is available in English, Arabic and French. The switcher is in the top bar."))
story.extend(bullets([
    "Your choice is remembered on your device.",
    "If you have not chosen, the store follows your browser\u2019s preference, then falls back to English.",
    "Arabic renders right-to-left, and the layout mirrors with it.",
]))
story.append(para("At present the chrome \u2014 navigation, cart, checkout, account and footer labels \u2014 is translated. "
                  "Product names, descriptions and some deeper screens are still English only."))

story.append(h2("13. Shopper FAQ"))
story.append(table([
    ["Question", "Answer"],
    ["Do I need an account to buy?", "No. Check out as a guest. An account is needed for history, wishlist, saved addresses and reviews."],
    ["Why did my discount code disappear?", "It stopped being valid \u2014 expired or reached its limit. The store re-checks on every page."],
    ["Why can I not add this item?", "Pick a variant first. A product with options cannot be added until one is chosen."],
    ["Why is the item in my cart now out of stock?", "Stock is reserved at order placement, not at add-to-cart. The cart tells you which line is affected."],
    ["My payment was declined \u2014 did I lose the order?", "No. The order stays pending with a failed attempt; a retry link is offered."],
    ["Can I use two discount codes together?", "No. One code per order; a second replaces the first."],
    ["Can I change or cancel an order?", "Cancelling is a staff action. Contact the store; stock is restored on cancellation."],
], widths=[USABLE * 0.42, USABLE * 0.58]))

# =====================================================================
# PART 2
# =====================================================================
story.append(PageBreak())
story.append(h1("Part 2 \u2014 Running your shop (vendors)"))

story.append(h2("14. Opening a shop"))
story.append(para("Your workspace lives at `/Vendor`. On first visit, if you do not yet have a shop, you are sent to "
                  "the registration form (`/Vendor/Onboarding/Register`) for your shop\u2019s name, contact details and "
                  "branding."))
story.append(para("Once submitted, the shop is **pending** until a staff member approves it at `/Admin/Shops`. On "
                  "approval your shop appears in the storefront\u2019s shop directory and your products can be published. "
                  "Staff also set your commission rate at that point."))
story.append(para("If a shop is ever suspended, you keep your data and your workspace; only the public storefront stops "
                  "showing you."))

story.append(h2("15. Your dashboard"))
story.append(para("The dashboard (`/Vendor/Dashboard`) answers \u201Chow is my shop doing right now?\u201D at a glance:"))
story.extend(bullets([
    "Key figures \u2014 products, orders, gross sales, and the amount currently owed to you.",
    "A 30-day revenue trend and daily figures.",
    "Best-selling products by revenue and by units.",
    "Sales by payment method.",
    "Low-stock alerts \u2014 what needs restocking before it sells out.",
    "Recent orders, linking straight into the order detail.",
]))
story.append(para("Every figure is scoped to your shop only. A brand-new shop shows an honest empty state."))

story.append(h2("16. Listing products"))
story.append(para("Products (`/Vendor/Products`) is your catalogue, scoped to your own shop. You can search it, filter "
                  "by category and state (active, draft, out of stock, deleted), and sort."))
story.append(h3("Creating and editing a product"))
story.extend(bullets([
    "**Name and slug** \u2014 the slug is what appears in the web address. Leave it blank and it is derived from the name; kept unique within your shop.",
    "**Category** \u2014 chosen from the marketplace\u2019s category tree (shared across all sellers).",
    "**Brand**, **short description** and **full description**.",
    "**Variants** \u2014 the matrix of options you actually sell. Each has its own SKU, price, optional compare-at price (for a struck-through sale price) and stock quantity. A product with no options still has one variant, usually named *Default*.",
    "**Images** \u2014 either typed URLs or uploaded files; you can mix the two.",
    "**Published** \u2014 a draft is invisible on the storefront. **Featured** surfaces a product in the storefront\u2019s featured strip.",
]))
story.append(para("You can soft-delete a product, which hides it without destroying the order history that refers to it, "
                  "and restore it later."))
story.append(h3("Image rules"))
story.extend(bullets([
    "Allowed types: JPEG, PNG, GIF and WebP. The file\u2019s actual leading bytes must match its extension.",
    "SVG is deliberately not accepted, because an SVG is a document that can carry a script.",
    "Per-file size limit 5 MB; gallery limit 10 images per product.",
    "The first image is the primary one \u2014 the order in the list is the order in the gallery.",
    "A bad upload fails the whole save rather than being silently dropped, and any file written during that failed attempt is cleaned up.",
    "Removing an image URL deletes the stored file, unless another product still points at the same URL.",
]))

story.append(h2("17. Bulk-importing products"))
story.append(para("Bulk import is currently a **staff** tool at `/Admin/Import`. Vendors cannot reach it directly; ask "
                  "staff to run an import against your shop, or add products through the editor."))
story.append(para("The importer takes a CSV whose columns are matched by name, so column order does not matter. Only "
                  "`Name`, `Price` and `SKU` are required. Full header:"))
story.append(Paragraph("Name,Slug,Category,Brand,ShortDescription,Description,SKU,VariantName,Price,CompareAtPrice,StockQuantity,IsPublished,IsFeatured,Images",
                      CODE))
story.extend(bullets([
    "`SKU` is the key. An existing product is matched by SKU first, then slug, then name.",
    "`Category` must already exist, matched case-insensitively.",
    "`Images` is a semicolon-separated list of URLs; the first becomes the primary image. Omitting the column entirely leaves the existing gallery untouched.",
    "An import is all-or-nothing. Every row is validated before anything is written; one bad row rejects the whole file and reports the line.",
    "`IsPublished` accepts `true`/`yes`/`1`. Prices use a decimal point regardless of your machine\u2019s locale.",
]))

story.append(h2("18. Orders"))
story.append(para("Your order book is at `/Vendor/Orders`."))
story.extend(bullets([
    "Filter by status, or search by order number, customer name or email.",
    "Open an order to see its items, delivery address, customer contact, totals, payment method, collection status and tracking number.",
]))
story.append(para("**Your orders only.** An order belonging to another shop returns a not-found page. Order status is "
                  "changed by staff, not by vendors. If you need an order cancelled or refunded, contact the store."))

story.append(h2("19. Reviews"))
story.append(para("Reviews (`/Vendor/Reviews`) shows every review left against your products, along with:"))
story.extend(bullets([
    "Your aggregate score across all published reviews.",
    "Your reputation badge, earned from that score.",
    "A reply box under each review.",
]))
story.append(para("**You may post one public reply per review**, and edit or clear it afterwards. The rating and the "
                  "wording belong to the customer."))
story.append(table([
    ["Badge", "Threshold"],
    ["Star seller", "average 4.2 or better over 5 or more reviews"],
    ["Top rated seller", "average 4.5 or better over 15 or more reviews"],
], widths=[USABLE * 0.35, USABLE * 0.65]))
story.append(para("A shop that does not yet meet either threshold shows no badge. The exact numbers are configuration, "
                  "not code. Badges appear in the shop directory, on your storefront and in your workspace."))

story.append(h2("20. Payouts and commission"))
story.append(para("Your money page is `/Vendor/Payouts`. It is read-only \u2014 you see exactly what you are owed and what "
                  "has been paid, and you cannot settle yourself. Staff make settlements from the admin area."))
story.extend(bullets([
    "The marketplace holds the shopper\u2019s money and owes you your share.",
    "Commission is withheld at the gate, on goods only \u2014 never on the shipping the carrier keeps. It is rounded half-away-from-zero to the cent.",
    "You are credited when the money is actually collected: a card order on capture, a cash-on-delivery order at delivery, a counter payment when staff mark it paid.",
    "A cancellation or refund reverses the credit.",
    "Crediting happens exactly once \u2014 a redelivered payment message cannot pay you twice.",
]))
story.append(para("**What you are owed is never a stored number.** Your balance is always the sum of your unsettled "
                  "earnings, so it cannot drift away from the orders and refunds behind it. A settlement merely marks "
                  "the earnings it covers as settled and gives you a statement \u2014 reference, period, and the gross, "
                  "commission and net figures \u2014 downloadable as CSV or Excel."))
story.append(para("One edge case: if a refund lands after a settlement has already paid you, your balance goes negative "
                  "and stays negative until new sales cover it. That is the correct accounting, not an error."))

story.append(h2("21. Your storefront\u2019s appearance"))
story.append(para("Every shop gets its own themed storefront. Choose the template at `/Vendor/Appearance`. Six templates "
                  "are available, each previewed live:"))
story.append(table([
    ["Template", "Character"],
    ["Postal Classic", "The house palette \u2014 blue and yellow. The default."],
    ["Midnight", "Dark."],
    ["Sunrise", "Warm and light."],
    ["Boutique", "Elegant, with serif headings."],
    ["Forest", "Natural, green."],
    ["Mono", "Minimal, black and white."],
], widths=[USABLE * 0.3, USABLE * 0.7]))
story.append(para("Changing the template re-skins the whole storefront \u2014 header, product cards, buttons and footer "
                  "\u2014 without touching a single product. A template that is somehow unknown falls back to the default."))

story.append(h2("22. Vendor FAQ"))
story.append(table([
    ["Question", "Answer"],
    ["How do I become a vendor?", "Register a shop at /Vendor/Onboarding/Register; staff approve it and set your commission."],
    ["My product is saved but not visible.", "Check it is published, has a variant with stock, and your shop is approved."],
    ["Why can I not change an order\u2019s status?", "Status changes are staff-only. Ask the store."],
    ["Why has my balance not been paid?", "Money may not be collected yet, or the balance is below the minimum, or it is negative after a refund."],
    ["Why does my balance show less than my sales?", "Commission is withheld, on goods only."],
    ["Can I edit or delete a customer\u2019s review?", "No. You may post one public reply."],
    ["Can I see another shop\u2019s orders?", "No. Your workspace is scoped to your own shop."],
    ["My imported file was rejected and nothing changed.", "Imports are all-or-nothing. Fix the listed lines and re-upload."],
], widths=[USABLE * 0.42, USABLE * 0.58]))

# =====================================================================
# PART 3
# =====================================================================
story.append(PageBreak())
story.append(h1("Part 3 \u2014 Administering the store"))

story.append(h2("23. The dashboard"))
story.append(para("`/Admin/Dashboard` is the operational summary: revenue (lifetime and the last 30 days), order counts "
                  "by state, the value of stock on hand, and stock alerts for anything at or below its low-stock threshold. "
                  "It is the \u201Cis anything on fire?\u201D page; the Marketing page is the \u201Chow are we doing?\u201D page."))

story.append(h2("24. Products"))
story.append(para("`/Admin/Products` is the catalogue. It offers search, category and state filters (active, draft, out "
                  "of stock, deleted), and the same editor a vendor uses \u2014 full variant matrix, image gallery that takes "
                  "typed URLs and uploaded files, publish toggle, and soft delete with restore."))
story.append(para("Staff differ from vendors in one way that matters: staff see the whole catalogue and the soft-deleted "
                  "rows, so a withdrawn product can be found and restored."))
story.append(para("Because orders snapshot the product name, variant name, SKU and unit price onto each order line, "
                  "editing or deleting a product never rewrites the history of an order that already contains it."))

story.append(h2("25. Bulk import"))
story.append(para("`/Admin/Import` loads a CSV of products into a chosen shop. All-or-nothing, validates every row "
                  "before writing anything, and reports failures by line number. Column order does not matter; headers are "
                  "matched by name. `Name`, `Price` and `SKU` are required."))
story.append(para("The importer is the exact inverse of the store\u2019s CSV export, so you can export products, edit them "
                  "in a spreadsheet, and import the file straight back."))
story.extend(bullets([
    "Matching is by SKU, then slug, then name.",
    "`Category` must already exist.",
    "Omitting the `Images` column leaves the existing gallery untouched; supplying it replaces the gallery.",
    "Turning off \u201Ccreate products that do not exist yet\u201D makes it an update-only import.",
]))

story.append(h2("26. Categories"))
story.append(para("`/Admin/Categories` manages the catalogue\u2019s tree with indentation and per-category product counts. "
                  "Categories are global to the marketplace, shared across every shop. The form guards against cycles \u2014 "
                  "you cannot make a category its own ancestor."))

story.append(h2("27. Inventory"))
story.append(para("`/Admin/Inventory` shows stock levels across products and lets you adjust them. Every change is written "
                  "to an append-only stock-movement ledger, so any movement can be explained after the fact."))
story.append(para("Stock is committed under optimistic concurrency. If two customers check out the last item at the same "
                  "instant, one gets a conflict rather than both being sold something that does not exist; the order "
                  "service retries with a fresh basket and, if it still cannot be filled, tells the customer plainly."))

story.append(h2("28. Orders"))
story.append(para("`/Admin/Orders` is the fulfilment queue."))
story.extend(bullets([
    "Filter by status and search by customer or order number.",
    "Open an order for its detail: lines, addresses, payment, collection status and tracking.",
    "Change status with `UpdateStatus`, subject to the order state machine \u2014 only legal transitions are permitted.",
]))
story.append(table([
    ["Transition", "Effect"],
    ["\u2192 Paid", "Credits the vendor ledger (money collected)."],
    ["\u2192 Shipped", "Queues a shipping notification email, with tracking."],
    ["\u2192 Cancelled", "Restores stock and reverses the vendor\u2019s earning."],
    ["\u2192 Refunded", "Calls the payment gateway; reverses the vendor\u2019s earning. Stock is not restored."],
], widths=[USABLE * 0.3, USABLE * 0.7]))
story.append(para("Export the current filter selection as CSV or Excel."))

story.append(h2("29. Customers"))
story.append(para("`/Admin/Customers` searches accounts and shows a detail page with lifetime spend. Accounts can be "
                  "activated or deactivated \u2014 a deactivated account cannot sign in, but its order history stays intact. "
                  "Deactivating is preferred over deleting."))

story.append(h2("30. Coupons"))
story.append(para("`/Admin/Coupons` creates and edits discount codes:"))
story.extend(bullets([
    "Type \u2014 percentage or fixed amount.",
    "Minimum subtotal, and a validity window (starts / expires).",
    "`MaxUses` (global) and `MaxUsesPerUser` (per shopper).",
    "Optional restriction to one shop.",
    "An activate toggle.",
]))
story.append(para("Validation refuses nonsense at the form: a percentage over 100, an out-of-range fixed amount, a start "
                  "after the expiry, and a shop-scoped code with no shop are all rejected."))
story.append(para("A code that has already been redeemed is deactivated rather than deleted, so order history stays "
                  "honest. Every redemption is recorded as a `CouponUsage` row holding the user and the order \u2014 which "
                  "is how the per-shopper cap is enforced without trusting the browser."))
story.append(para("Coupons do not stack, and they are not line-item aware \u2014 one code per order, applied to the goods subtotal."))

story.append(h2("31. Tax classes"))
story.append(para("`/Admin/Tax` defines named tax rates. A product may point at one; a product with no class is taxed at "
                  "the store-wide rate (`Store:TaxRate`)."))
story.extend(bullets([
    "Rates are entered as a fraction: `0.11` means eleven percent. The form refuses anything outside 0\u20131.",
    "A class can be marked exempt \u2014 always zero, whatever the rate says.",
    "Deleting is guarded: if products still reference a class, it is deactivated rather than deleted, because deleting it would silently move those products onto the store rate.",
    "The page shows how many live products are on each class and how many are on the store default.",
]))
story.append(para("Tax is applied per line, from each line\u2019s own class, with any order-level discount apportioned across "
                  "the lines first. Products with no tax class behave exactly as they did before this feature existed \u2014 "
                  "the store rate applies, unchanged. A fresh store is seeded with one *Standard rate* class and leaves "
                  "products unassigned."))
story.append(para("Export the class list as CSV or Excel."))

story.append(h2("32. Shops"))
story.append(para("`/Admin/Shops` is where the marketplace\u2019s sellers are governed:"))
story.append(table([
    ["Action", "Meaning"],
    ["Approve", "The shop goes live and appears in the directory. Also where you set its commission rate."],
    ["Reject", "Declines a pending application."],
    ["Suspend", "Takes a live shop off the storefront, keeping its data and workspace."],
    ["Reinstate", "Puts a suspended shop back."],
], widths=[USABLE * 0.25, USABLE * 0.75]))
story.append(para("Commission is entered as a percentage in the form (`10` = 10%); the store divides by 100. Commission "
                  "is withheld on goods only, never on shipping."))

story.append(h2("33. Payouts"))
story.append(para("`/Admin/Payouts` is where vendor money is settled."))
story.extend(bullets([
    "The list shows every shop\u2019s outstanding balance \u2014 gross, commission withheld, and net.",
    "Pay out settles the whole balance in one action, producing a statement.",
    "A statement, and the settlement history, can be downloaded as CSV or Excel.",
]))
story.append(para("The vendor\u2019s balance is derived \u2014 always the sum of their unsettled ledger entries, never a stored "
                  "figure that could drift. A settlement marks those entries as settled and records a reference and period; "
                  "it does not write new earnings. A payout is recorded, not transferred \u2014 there is no banking "
                  "integration. A balance below `Payouts:MinimumAmount` is not settled. If a refund lands after a "
                  "settlement, the shop\u2019s balance goes negative."))

story.append(h2("34. Marketing analytics"))
story.append(para("`/Admin/Marketing` answers \u201Chow are we doing?\u201D \u2014 revenue over time, top products by revenue and by "
                  "units, revenue by category, best customers, payment-method mix, coupon performance and fast-moving stock."))
story.append(para("Every section exports as CSV or Excel, through the same eight section names the page presents: "
                  "`daily-revenue`, `top-products-revenue`, `top-products-units`, `revenue-by-category`, `top-customers`, "
                  "`payment-methods`, `coupons`, `fast-moving`."))
story.extend(bullets([
    "Only collected money counts as revenue. A pending, cancelled or refunded order is not revenue.",
    "Coupon performance reports the discount checkout actually applied, read from the order itself, not reconstructed from the coupon\u2019s rules.",
]))

story.append(h2("35. Reviews moderation"))
story.append(para("`/Admin/Reviews` is the moderation queue, filterable by state (pending / published / rejected) and by "
                  "star rating."))
story.append(para("Reviews publish immediately by default. The queue is reactive \u2014 it exists to hide the minority that "
                  "break the rules, not to gate every review. From here you can publish, hide, return to the queue, or "
                  "delete outright. Seller replies are not moderated in the queue."))

story.append(h2("36. Email outbox"))
story.append(para("`/Admin/Emails` is the transactional outbox: every message the store intended to send, including the "
                  "ones that failed."))
story.extend(bullets([
    "Filter by state (queued, sent, failed, skipped) and by type.",
    "Open a message for a rendered preview of what the customer receives.",
    "Resend a failed message, which returns it to the queue with a clean history.",
    "Send a test message to confirm configuration.",
]))
story.extend(bullets([
    "Nothing sends on the request path. A checkout queues a row and returns; a background dispatcher delivers it.",
    "Messages are idempotent by a dedupe key, so a retried request cannot send the same notification twice.",
    "Failures retry with backoff, then park as Failed with the full error preserved.",
    "With `Email:Enabled = false`, messages are recorded as Skipped rather than dropped.",
    "The default transport writes real `.eml` and `.html` files instead of sending, so email works with no mail server.",
]))

story.append(h2("37. Exports"))
story.append(para("Orders, products, customers, payouts, tax classes and every marketing section export as CSV or Excel."))
story.extend(bullets([
    "The export runs the same query the page does, so the file and the screen cannot disagree.",
    "CSV is UTF-8 with a byte-order mark, so Excel reads Arabic and accented names correctly.",
    "A cell beginning `=`, `+`, `-` or `@` is prefixed with an apostrophe, so a customer whose name starts with `=` cannot smuggle a formula into your spreadsheet.",
    "The Excel output is a real workbook: numbers Excel can sum, real dates, a bold frozen header row and an autofilter.",
    "Exports are capped (orders 200, products 5,000) to match the page and bound memory.",
]))

story.append(h2("38. The application log"))
story.append(para("Everything the application logs goes to the console and to the `Log` table in the configured database. "
                  "The console is gone when the window closes; the table is what a deployment keeps."))
story.append(Paragraph("SELECT TOP 50 Id, Logged, Level, Logger, Message FROM [Log] ORDER BY Id DESC;", CODE))
story.append(para("`Error` and `Critical` rows carry the full exception and stack trace. A logging call can never fail, "
                  "block or throw a request \u2014 callers enqueue, and a background writer batches the insert. Retention is "
                  "off by default, so the table grows until retention is configured."))

story.append(h2("39. Admin FAQ"))
story.append(table([
    ["Question", "Answer"],
    ["I changed a product\u2019s price \u2014 why do old orders still show the old price?", "Order lines snapshot at sale time. Editing a product cannot rewrite history."],
    ["Why can\u2019t I delete this tax class?", "Products still reference it. The page deactivated it instead; reassign the products, then delete."],
    ["Why is a coupon still listed after it was used up?", "A redeemed coupon is deactivated rather than deleted. It will not apply."],
    ["Why did the vendor\u2019s balance go negative?", "A refund landed after a settlement had already paid them. New sales will cover it."],
    ["Why does marketing revenue differ from the order list total?", "Marketing counts only collected money."],
    ["Do coupon discounts show the right figure?", "Yes \u2014 read from the order, not recalculated from the coupon\u2019s rules."],
    ["A bulk import failed \u2014 did it write anything?", "No. All-or-nothing; errors are listed by line."],
    ["Why is a customer\u2019s order missing from their history?", "It may have been placed as a guest with an email not tied to an account."],
], widths=[USABLE * 0.46, USABLE * 0.54]))

story.append(h2("40. Returning something (shoppers)"))
story.append(para("When a purchase does not work out you can send it back. The flow is the same for every shop:"))
story.extend(bullets([
    "Ask - open the order on My orders and press Request a return. The form lists every line with the units you may still return (units an earlier return already refunded are gone) and a box for the reason. You get an RMA number such as RMA-2026-0001.",
    "The shop decides - it approves or rejects the request, with a note. Both outcomes email you.",
    "The goods go back - once approved, hand the parcel back. The shop records it as received; anything returned goes back on their shelf.",
    "The refund - issued by store staff. The maths is deliberately simple: the price of what you return (your share of any discount included) plus the proportionate tax. Shipping is refunded only when the whole order goes back. Part of an order can be returned; the order page keeps its running total of what has been refunded so far.",
]))
story.append(para("Statuses on the order page:"))
story.append(para("Requested  ->  Approved  ->  Received  ->  Refunded     (or) Rejected"))
story.extend(bullets([
    "You can cancel a return yourself at any point before it is received, by pressing Cancel request on the order page.",
    "An order may hold only one open return at a time; once one is settled you can raise another.",
    "Guest checkouts cannot raise returns - sign in first so the shop knows who you are.",
    "A full return also moves the order to Refunded and flips a cash-on-delivery collection to refunded.",
]))

story.append(h2("41. Returns (vendors)"))
story.append(para("When a shopper asks to send something back, it arrives on your worklist at /Vendor/Returns - Open only shows what needs a decision today. A return for another shop's order is invisible; opening one by typing its id gives a not-found page."))
story.extend(bullets([
    "Decide - open the RMA and approve or reject it, with a note the shopper sees. A rejection needs a reason.",
    "Receive - when the parcel returns, press Record goods received. The units go straight back onto the shelf of your listing (or the shared variant stock if the listing was withdrawn), with a return-stamped entry in the stock ledger.",
    "The customer cannot raise a second open return on the same order, and units a completed return already refunded cannot be asked back again - the form enforces that, and the server enforces it too.",
]))
story.append(para("What you deliberately cannot see or do from this page: the refund itself. Only store staff move money. Every decision you make is recorded on the store's audit trail with your name on it."))

story.append(h2("42. Returns (administrators)"))
story.append(para("/Admin/Returns is the marketplace's RMA desk. Shops decide their own returns day to day, but staff can act for them and are the only role that issues the actual refund."))
story.extend(bullets([
    "The queue - filter by status, or search by RMA reference, shopper email or order number. Counters above the table show how many sit at requested, approved and received. Export the current view as CSV or Excel.",
    "Deciding - a return can be approved or rejected from here, with a note, exactly as the vendor would.",
    "Receiving - goods received restocks the returned units into the shop's listing stock (or the shared variant pool) and writes a return-stamped stock movement.",
    "Refunding - press Issue refund on a received return. The amount is computed, not typed: the goods actually returned (with their share of any order discount) plus the proportionate tax, shipping only when the whole order comes back. It is capped by whatever the ledger still owes the shop for that sale and by GrandTotal minus RefundedTotal. A card refund goes back against the original charge; a cash-on-delivery refund is a staff bookkeeping row.",
]))
story.append(para("A refund that cannot go through (no sale on the ledger to reverse, a state that forbids the step) is refused with a plain reason and recorded on the audit trail as a failed attempt. The order's page keeps Refunded so far and Still owed to shopper totals."))

story.append(h2("43. Audit trail (administrators)"))
story.append(para("/Admin/Audit is the store's administrative memory: who did what, to which record, and when."))
story.extend(bullets([
    "What is recorded - every shop decision (approved, rejected, suspended, reinstated), every order status change staff make, every return decision, received-goods entry, refund issued and every refund attempt that failed, and every payout created, marked failed or reversed. Failed attempts are kept.",
    "What a row says - timestamp, actor, a dotted action (shop.approved, refund.failed, payout.reversed), the subject as entity type plus id, and a one-sentence summary written by the action itself.",
    "Filter by action, entity type, entity id or actor; the suggestion lists come from the data. Each shop row on the shops page links to this page pre-filtered to that shop.",
    "Export the filtered view as CSV or Excel (up to 500 rows).",
]))
story.append(para("The trail is append-only and references are deliberately loose (a name string and a numeric id, not foreign keys) - deleting an account or a row never rewrites the history that mentions them. It is also written best-effort: a failing audit write is logged, never allowed to undo the action it was documenting."))

story.append(h2("44. Payout outcomes (administrators)"))
story.append(para("A payout is Paid, Failed or Reversed. While it is still Paid, staff can mark it failed (with the bank's reason) or reverse it (recorded in error). Either action releases every entry it covered back to the unpaid balance so the shop can be settled again, while the payout row keeps its totals - a statement is corrected by another row, never rewritten. Both actions are guarded, audited, and shown to the vendor with the reason and the handler's name."))
story.append(para("The payout destination (account name, bank, IBAN) is recorded per shop - collected at registration, editable by the vendor on their payouts page, visible to staff on the shop list. The shop is emailed when a settlement is recorded against it (when it has a contact email on file)."))

# =====================================================================
# PART 4
# =====================================================================
story.append(PageBreak())
story.append(h1("Part 4 \u2014 Configuration and demo data"))

story.append(h2("Key settings (`appsettings.json`)"))
story.append(table([
    ["Setting", "Default / notes"],
    ["Store:Theme", "1 (Amazon), 2 (eBay), 3 (Etsy), 4 (Midnight & Gold). Currently 4."],
    ["Store:TaxRate", "0.11 (11%). Used when a product has no tax class."],
    ["Store:FlatShippingRate", "4.50; free above Store:FreeShippingThreshold (60.00)."],
    ["Email:Enabled / Transport", "true / File (writes .eml + .html to App_Data/outbox)."],
    ["Email:MaxAttempts", "4, with exponential backoff."],
    ["Payments:Provider", "simulator (card payments move no money)."],
    ["Payouts:MinimumAmount", "0.00 (no minimum by default)."],
    ["Images:MaxBytes / MaxPerProduct", "5 MB / 10 images."],
    ["Database:Provider", "Sqlite by default; switch to SqlServer by configuration."],
], widths=[USABLE * 0.4, USABLE * 0.6]))

story.append(h2("Demo accounts"))
story.append(table([
    ["Role", "Email", "Password"],
    ["Administrator", "admin@libanpost.example", "Admin#12345"],
    ["Customer", "customer@libanpost.example", "Customer#123"],
], widths=[USABLE * 0.3, USABLE * 0.4, USABLE * 0.3]))
story.append(para("Vendors are created by registering a shop at `/Vendor/Onboarding/Register`; staff approve them. "
                  "The demonstration store is also seeded with approved shops you can sign in as.", SMALL))

story.append(h2("Known limitations"))
story.extend(bullets([
    "Email is written to files, not sent \u2014 nothing reaches a real inbox until an SMTP host is configured.",
    "Card payments run through the bundled simulator \u2014 no money moves.",
    "Only the storefront chrome is translated (English / Arabic / French); product content and the admin area are English only.",
    "Payouts are recorded, not transferred \u2014 there is no banking integration.",
    "No currency conversion; a single currency per store.",
    "The application log table grows until retention is configured (`Logging:Database:RetentionDays`).",
]))

story.append(h2("Support"))
story.append(para("For operator and developer detail \u2014 architecture, configuration reference, database providers, "
                  "payments, logging, deployment constraints and the full feature inventory \u2014 see the project\u2019s "
                  "`README.md` and `docs/FEATURES.md`."))

story.append(Spacer(1, 8 * mm))
story.append(hr())
story.append(Paragraph("End of guide.", SMALL))

# ------------------------------------------------------------------ build
doc = GuideDocTemplate(
    "docs/LibanPost_eShop_User_Guide.pdf",
    pagesize=A4,
    leftMargin=MARGIN, rightMargin=MARGIN,
    topMargin=MARGIN + 6 * mm, bottomMargin=MARGIN,
    title="LibanPost eShop - User Guide",
    author="LibanPost eShop",
)
doc.multiBuild(story, onFirstPage=on_page, onLaterPages=on_page)
print("written")
