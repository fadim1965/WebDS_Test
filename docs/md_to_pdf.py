# -*- coding: utf-8 -*-
"""Generic Markdown -> PDF converter using reportlab.

Usage:  python docs/md_to_pdf.py <input.md> <output.pdf> [title] [footer-title] [--toc]

Supports headings, paragraphs, bullet/numbered lists, tables, fenced code,
horizontal rules and images:  ![alt](relative-or-absolute-path.png)

--toc  adds a click-able table of contents after the title and PDF bookmarks.
"""
import os, re, sys
from reportlab.lib import colors
from reportlab.lib.pagesizes import A4
from reportlab.lib.styles import getSampleStyleSheet, ParagraphStyle
from reportlab.lib.units import mm
from reportlab.lib.enums import TA_JUSTIFY, TA_LEFT, TA_CENTER
from reportlab.lib.utils import ImageReader
from reportlab.platypus import (
    SimpleDocTemplate, Paragraph, Spacer, PageBreak, Table, TableStyle,
    ListFlowable, ListItem, HRFlowable, Image, KeepTogether,
)
from reportlab.platypus.tableofcontents import TableOfContents

PAGE_W, PAGE_H = A4
MARGIN = 16 * mm
USABLE = PAGE_W - 2 * MARGIN
NAVY = colors.HexColor("#006db7")
INK = colors.HexColor("#1a1a1a")
MUTED = colors.HexColor("#6b6b6b")
LIGHT = colors.HexColor("#f6f6f6")
BORDER = colors.HexColor("#d7d7d7")

styles = getSampleStyleSheet()
def S(name, parent=None, **kw):
    if parent is None:
        parent = styles["Normal"]
    return ParagraphStyle(name, parent=parent, **kw)

TITLE = S("Title", fontName="Helvetica-Bold", fontSize=22, leading=28,
          textColor=NAVY, alignment=TA_CENTER, spaceAfter=6)
H1 = S("H1", fontName="Helvetica-Bold", fontSize=16, leading=20,
       textColor=NAVY, spaceBefore=14, spaceAfter=5, keepWithNext=1)
H2 = S("H2", fontName="Helvetica-Bold", fontSize=13, leading=17,
       textColor=INK, spaceBefore=10, spaceAfter=4, keepWithNext=1)
H3 = S("H3", fontName="Helvetica-Bold", fontSize=11, leading=14,
       textColor=INK, spaceBefore=8, spaceAfter=3, keepWithNext=1)
BODY = S("Body", fontName="Helvetica", fontSize=10, leading=13.5,
         alignment=TA_JUSTIFY, spaceAfter=5, textColor=INK)
BULLET = S("Bullet", fontName="Helvetica", fontSize=10, leading=13.5,
           alignment=TA_LEFT, spaceAfter=2, textColor=INK)
CODE = S("Code", fontName="Courier", fontSize=8.5, leading=11,
         textColor=INK, spaceAfter=5, leftIndent=6,
         backColor=LIGHT)
SMALL = S("Small", fontName="Helvetica", fontSize=8.5, leading=11,
          textColor=MUTED, spaceAfter=3)

# Table-of-contents entry styles (levels 0/1/2 = H1/H2/H3)
TOC_L0 = S("TOCL0", fontName="Helvetica-Bold", fontSize=10.5, leading=15,
           textColor=NAVY, spaceBefore=8, leftIndent=0)
TOC_L1 = S("TOCL1", fontName="Helvetica", fontSize=9.5, leading=13,
           textColor=INK, leftIndent=10)
TOC_L2 = S("TOCL2", fontName="Helvetica", fontSize=9, leading=12.5,
           textColor=MUTED, leftIndent=22)

# Heading level -> TOC level / bookmark level
TOC_LEVELS = {"H1": 0, "H2": 1, "H3": 2}
TOC_STYLE_BY_LEVEL = {0: TOC_L0, 1: TOC_L1, 2: TOC_L2}


class TocDocTemplate(SimpleDocTemplate):
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
        import hashlib
        occ = self._seen.get((level, toc_text), 0) + 1
        self._seen[(level, toc_text)] = occ
        # Deterministic key: identical across multiBuild passes, else the TOC
        # never converges (its entries embed the key).
        key = "h" + hashlib.sha1(
            ("%d|%d|%s" % (level, occ, toc_text)).encode("utf-8")).hexdigest()[:10]
        try:
            # The PDF outline must start at level 0 and may never skip a level.
            # The TOC itself keeps the true levels; only bookmarks are clamped.
            olevel = 0 if self._prev_olevel < 0 else min(level, self._prev_olevel + 1)
            self.canv.bookmarkPage(key)
            self.canv.addOutlineEntry(re.sub(r"<[^>]+>", "", toc_text), key, olevel, 0)
            self._prev_olevel = olevel
        except Exception as ex:
            if not getattr(self, "_outline_err_shown", False):
                print("outline warn: %r" % ex)
                self._outline_err_shown = True
        self.notify("TOCEntry", (level, toc_text, self.page, key))


def make_toc():
    toc = TableOfContents()
    toc.levelStyles = [TOC_L0, TOC_L1, TOC_L2]
    toc.dotsMinLevel = 0
    return toc

def md_inline(text):
    t = text.replace("&", "&amp;").replace("<", "&lt;").replace(">", "&gt;")
    t = re.sub(r"`([^`]+)`", r'<font name="Courier" size="9">\1</font>', t)
    t = re.sub(r"\*\*(.+?)\*\*", r"<b>\1</b>", t)
    t = re.sub(r"\*(.+?)\*", r"<i>\1</i>", t)
    return t

def on_page(canvas, doc):
    canvas.saveState()
    canvas.setFont("Helvetica", 8)
    canvas.setFillColor(MUTED)
    canvas.drawString(MARGIN, 10 * mm, FOOTER_TITLE)
    canvas.drawRightString(PAGE_W - MARGIN, 10 * mm, "Page %d" % doc.page)
    canvas.setFillColor(NAVY)
    canvas.rect(0, PAGE_H - 5 * mm, PAGE_W, 5 * mm, fill=1, stroke=0)
    canvas.setFillColor(colors.HexColor("#fed300"))
    canvas.rect(0, PAGE_H - 5 * mm, PAGE_W * 0.35, 5 * mm, fill=1, stroke=0)
    canvas.restoreState()

def col_widths(rows):
    n = len(rows[0])
    lens = [1] * n
    for r in rows:
        for i, c in enumerate(r):
            lens[i] = max(lens[i], len(c))
    total = sum(lens) or 1
    ws = [max(30, USABLE * l / total) for l in lens]
    # normalize to USABLE
    s = sum(ws)
    ws = [w * USABLE / s for w in ws]
    return ws

IMG_PATHS = {}
USABLE_H = PAGE_H - (MARGIN + 4 * mm) - MARGIN
MAX_IMG_H = USABLE_H - 34        # minus frame padding + room for the caption line

def make_image_flowable(img_path, alt=""):
    """Fit an image to the page width, capping height so tall shots stay sane."""
    try:
        if not os.path.isabs(img_path):
            img_path = os.path.join(BASE_DIR, img_path)
        ir = ImageReader(img_path)
        iw, ih = ir.getSize()
        scale = USABLE / iw
        w, h = USABLE, ih * scale
        if h > MAX_IMG_H:
            w, h = w * MAX_IMG_H / h, MAX_IMG_H
        img = Image(img_path, width=w, height=h)
        img.hAlign = "LEFT"
        cap = Paragraph(("<i>%s</i>" % md_inline(alt)) if alt else "", SMALL)
        out = []
        if alt:
            out.append(KeepTogether([img, Spacer(1, 2), cap]))
        else:
            out.append(img)
        out.append(Spacer(1, 4))
        return out
    except Exception as ex:
        return [Paragraph("<font color='#c00'>[missing image: %s]</font>" % img_path, SMALL),
                Spacer(1, 3)]

def _toc_plain(text):
    """Plain, XML-safe heading text for TOC entries."""
    t = text.replace("`", "")
    t = re.sub(r"\*\*(.+?)\*\*", r"\1", t)
    t = re.sub(r"\*(.+?)\*", r"\1", t)
    return t.replace("&", "&amp;").replace("<", "&lt;").replace(">", "&gt;")


def build(md_text, include_toc=False):
    story = []
    lines = md_text.split("\n")
    i = 0
    n = len(lines)
    # skip leading title handling: render first H1 as TITLE
    first_h1 = True
    while i < n:
        line = lines[i]
        stripped = line.strip()

        # blank line
        if stripped == "":
            i += 1
            continue

        # image
        m = re.match(r'^!\[([^\]]*)\]\(([^)]+)\)$', stripped)
        if m:
            story.extend(make_image_flowable(m.group(2), m.group(1)))
            i += 1
            continue

        # horizontal rule
        if re.match(r"^-{3,}$", stripped):
            story.append(HRFlowable(width="100%", thickness=0.5, color=BORDER,
                                    spaceBefore=4, spaceAfter=6))
            i += 1
            continue

        # fenced code block
        if stripped.startswith("```"):
            code_lines = []
            i += 1
            while i < n and not lines[i].strip().startswith("```"):
                code_lines.append(lines[i])
                i += 1
            i += 1
            code = "\n".join(code_lines).rstrip()
            if code:
                story.append(Paragraph(md_inline(code).replace("\n", "<br/>"), CODE))
                story.append(Spacer(1, 3))
            continue

        # headings
        m = re.match(r"^(#{1,3})\s+(.*)$", stripped)
        if m:
            level = len(m.group(1))
            text = m.group(2).strip()
            if level == 1:
                if first_h1:
                    story.append(Paragraph(md_inline(text), TITLE))
                    first_h1 = False
                    if include_toc:
                        story.append(Paragraph("Contents", H2))
                        story.append(make_toc())
                        story.append(PageBreak())
                else:
                    story.append(PageBreak())
                    p = Paragraph(md_inline(text), H1)
                    p._toc_text = _toc_plain(text)
                    story.append(p)
            elif level == 2:
                p = Paragraph(md_inline(text), H2)
                p._toc_text = _toc_plain(text)
                story.append(p)
            else:
                p = Paragraph(md_inline(text), H3)
                p._toc_text = _toc_plain(text)
                story.append(p)
            i += 1
            continue

        # table
        if "|" in stripped and i + 1 < n and re.match(r"^\|?[\s\-:|]+\|?$", lines[i + 1].strip()):
            # collect table rows
            rows = []
            while i < n and "|" in lines[i].strip():
                r = [c.strip() for c in lines[i].strip().strip("|").split("|")]
                rows.append(r)
                i += 1
            # remove separator row
            rows = [r for r in rows if not all(re.match(r"^[-:]+$", c) for c in r)]
            if rows:
                # normalize column count
                ncols = max(len(r) for r in rows)
                for r in rows:
                    while len(r) < ncols:
                        r.append("")
                data = [[Paragraph(md_inline(c), S("tc", fontSize=8.5, leading=10.5,
                                                  textColor=INK)) for c in r] for r in rows]
                ws = col_widths(rows)
                t = Table(data, colWidths=ws, repeatRows=1)
                ts = [
                    ("FONTNAME", (0, 0), (-1, -1), "Helvetica"),
                    ("FONTSIZE", (0, 0), (-1, -1), 8.5),
                    ("LEADING", (0, 0), (-1, -1), 10.5),
                    ("VALIGN", (0, 0), (-1, -1), "TOP"),
                    ("LEFTPADDING", (0, 0), (-1, -1), 4),
                    ("RIGHTPADDING", (0, 0), (-1, -1), 4),
                    ("TOPPADDING", (0, 0), (-1, -1), 3),
                    ("BOTTOMPADDING", (0, 0), (-1, -1), 3),
                    ("GRID", (0, 0), (-1, -1), 0.3, BORDER),
                    ("BACKGROUND", (0, 0), (-1, 0), NAVY),
                    ("TEXTCOLOR", (0, 0), (-1, 0), colors.white),
                    ("FONTNAME", (0, 0), (-1, 0), "Helvetica-Bold"),
                    ("ROWBACKGROUNDS", (0, 1), (-1, -1), [colors.white, LIGHT]),
                ]
                t.setStyle(TableStyle(ts))
                story.append(t)
                story.append(Spacer(1, 4))
            continue

        # bullet list
        if re.match(r"^[-*]\s+", stripped):
            items = []
            while i < n and re.match(r"^[-*]\s+", lines[i].strip()):
                items.append(re.sub(r"^[-*]\s+", "", lines[i].strip()))
                i += 1
            lf = ListFlowable(
                [ListItem(Paragraph(md_inline(it), BULLET), leftIndent=10) for it in items],
                bulletType="bullet", start="circle", leftIndent=12,
                bulletFontName="Helvetica", bulletFontSize=7,
            )
            story.append(lf)
            story.append(Spacer(1, 3))
            continue

        # numbered list
        if re.match(r"^\d+\.\s+", stripped):
            items = []
            while i < n and re.match(r"^\d+\.\s+", lines[i].strip()):
                items.append(re.sub(r"^\d+\.\s+", "", lines[i].strip()))
                i += 1
            lf = ListFlowable(
                [ListItem(Paragraph(md_inline(it), BULLET), leftIndent=10) for it in items],
                bulletType="1", leftIndent=16, bulletFontName="Helvetica-Bold",
            )
            story.append(lf)
            story.append(Spacer(1, 3))
            continue

        # blockquote-ish / normal paragraph (collect consecutive non-special lines)
        para_lines = []
        while (i < n and lines[i].strip() != "" and
               not lines[i].strip().startswith("#") and
               not lines[i].strip().startswith("```") and
               not re.match(r"^-{3,}$", lines[i].strip()) and
               not re.match(r"^!\[[^\]]*\]\([^)]+\)$", lines[i].strip()) and
               not (lines[i].strip().startswith("|") and i + 1 < n and
                    re.match(r"^\|?[\s\-:|]+\|?$", lines[i + 1].strip())) and
               not re.match(r"^[-*]\s+", lines[i].strip()) and
               not re.match(r"^\d+\.\s+", lines[i].strip())):
            para_lines.append(lines[i].strip())
            i += 1
        if para_lines:
            text = " ".join(para_lines)
            story.append(Paragraph(md_inline(text), BODY))
            story.append(Spacer(1, 2))
        # else i advanced? ensure progress
        if not para_lines and i < n:
            i += 1

    return story

def main():
    global BASE_DIR, FOOTER_TITLE
    if len(sys.argv) < 3:
        print("usage: md_to_pdf.py <input.md> <output.pdf> [title] [footer-title] [--toc]")
        sys.exit(1)
    inp, outp = sys.argv[1], sys.argv[2]
    title = sys.argv[3] if len(sys.argv) > 3 else "Document"
    FOOTER_TITLE = sys.argv[4] if len(sys.argv) > 4 else "LibanPost eShop"
    include_toc = ("--toc" in sys.argv)
    BASE_DIR = os.path.dirname(os.path.abspath(inp))
    md_text = open(inp, encoding="utf-8").read()
    story = build(md_text, include_toc=include_toc)
    doc = TocDocTemplate(
        outp, pagesize=A4,
        leftMargin=MARGIN, rightMargin=MARGIN,
        topMargin=MARGIN + 4 * mm, bottomMargin=MARGIN,
        title=title, author="LibanPost eShop",
    )
    doc.multiBuild(story, onFirstPage=on_page, onLaterPages=on_page)
    print("written", outp)

if __name__ == "__main__":
    main()
