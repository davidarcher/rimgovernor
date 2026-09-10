"""Pure UI report formatters copied from Snowstar38/rimworld-claude-harness.

Pinned revision: 89c2e90fedd51419a3db55a7f9865b0aef29b270, instruments/ui.py.
No transport, CLI, clicking, auto-dismissal or timers. See PROVENANCE.md.
"""
import re

_RICH = re.compile(r"<(?:/?[a-zA-Z]+)(?:=[^<>]*)?>")


_DUP_CELL = 4.0


_ROW_TOL = 6.0


_TRUNC = 160


def _norm(label):
    """The printed form of a label: markup stripped, whitespace collapsed, lines
    joined with " / ". Reading and clicking both go through this, so the text a
    reader is shown is the text that matches. Truncation is NOT part of it."""
    lines = [re.sub(r"\s+", " ", p).strip()
             for p in _RICH.sub("", label or "").splitlines()]
    return " / ".join(p for p in lines if p)


def _on_row(row_rect, tog_rect):
    """Does an unlabelled toggle belong to the row at `row_rect`?

    To the right of the row, and on the same line -- the row's vertical centre
    inside the toggle, or the two centres within `_ROW_TOL`. slim's read and
    row_toggle's click share this rule so they agree about what a row carries.
    """
    if tog_rect["x"] <= row_rect["x"]:
        return False
    rc = row_rect["y"] + row_rect["height"] / 2
    tc = tog_rect["y"] + tog_rect["height"] / 2
    return (tog_rect["y"] <= rc <= tog_rect["y"] + tog_rect["height"]
            or abs(rc - tc) <= _ROW_TOL)


def _captions(btn, lab):
    """A gizmo draws its NAME under the icon, so no rect contains the text.

    `Draft`'s label sits across the bottom edge of its 75x75 button, which
    `_covers` misses by six pixels -- and a reader who saw "Draft" listed as
    clickable would then be told there is no such element. Same test both sides.

    The caption must OVERLAP the bottom edge, not merely sit under it: in a
    table, every cell of row n+1 sits exactly under a cell of row n, and a
    "under it" rule ate the Wildlife tab's percentage columns.
    """
    b, l = btn.get("screenRect") or {}, lab.get("screenRect") or {}
    if not b or not l:
        return False
    lcx = l["x"] + l["width"] / 2
    gap = l["y"] - (b["y"] + b["height"])
    return (b["x"] <= lcx <= b["x"] + b["width"] and l["width"] <= b["width"]
            and -l["height"] < gap < 0)


_CONTAINER = ("group", "scroll_view")


def _text(e):
    # Inspect panes put multi-line strings in one label; a row is one line here.
    # `_norm` is the one place that transformation lives -- find/click match on
    # its output, so the printed text and the clickable text stay the same text.
    return _norm(e.get("label"))


def _rect(e):
    return e.get("screenRect") or e.get("rect") or {"x": 0, "y": 0, "width": 0, "height": 0}


def _cy(e):
    r = _rect(e)
    return r["y"] + r["height"] / 2


def _inside(outer, inner_rect):
    cx = inner_rect["x"] + inner_rect["width"] / 2
    cy = inner_rect["y"] + inner_rect["height"] / 2
    return (outer["x"] <= cx <= outer["x"] + outer["width"] and
            outer["y"] <= cy <= outer["y"] + outer["height"])


def slim_surface(s, full=False):
    """One surface's raw dict -> the reader's view. Pure; no bridge calls."""
    els = list(s.get("elements") or [])
    srect = s.get("screenRect") or s.get("rect") or {}
    notes = []

    # 1. drop the containers; their scroll data is reported once, per surface.
    containers = [e for e in els if e.get("kind") in _CONTAINER]
    els = [e for e in els if e.get("kind") not in _CONTAINER]

    # 2. collapse repeated text draws (outline/shadow passes).
    seen, labels, dupes = {}, [], 0
    for e in els:
        t = _text(e)
        if not t:
            continue
        r = _rect(e)
        key = (t, round(r["x"] / _DUP_CELL), round(r["y"] / _DUP_CELL))
        if key in seen:
            seen[key]["dupes"] += 1
            dupes += 1
            continue
        rec = {"el": e, "text": t, "dupes": 0}
        seen[key] = rec
        labels.append(rec)
    rich = sum(1 for rec in labels if _RICH.search(rec["el"].get("label") or ""))
    labels.sort(key=lambda r: (round(_rect(r["el"])["y"]), _rect(r["el"])["x"]))

    acts = [e for e in els if e.get("actionable")]

    # 3. each label belongs to the SMALLEST actionable whose rect contains it --
    #    that is the invisible button drawn over it (the float-menu triplet, a
    #    research node, a Wildlife name cell).
    owner = {}
    for i, rec in enumerate(labels):
        cands = [a for a in acts if _inside(_rect(a), _rect(rec["el"]))]
        if not cands:  # ...or the gizmo caption drawn under the icon.
            cands = [a for a in acts if a.get("kind") == "button"
                     and _captions(a, rec["el"])]
        if cands:
            owner[i] = min(cands, key=lambda a: _rect(a)["width"] * _rect(a)["height"])

    rows, by_act = [], {}
    for i, rec in enumerate(labels):
        a = owner.get(i)
        if a is None:
            row = {"text": rec["text"], "act": False, "role": None, "checked": None,
                   "disabled": None, "toggles": [], "dupes": rec["dupes"],
                   "_rect": _rect(rec["el"])}
            rows.append(row)
            continue
        key = id(a)
        row = by_act.get(key)
        if row is None:
            row = {"text": rec["text"], "act": True, "role": a.get("kind"),
                   "checked": a.get("isChecked"), "disabled": a.get("disabled"),
                   "toggles": [], "dupes": rec["dupes"], "_rect": _rect(a)}
            by_act[key] = row
            rows.append(row)
        else:
            row["text"] += " " + rec["text"]
            row["dupes"] += rec["dupes"]

    # (A labelled actionable -- `checkbox_labeled` and friends -- needs no case of
    # its own: it is its own label, so step 3 makes it the owner of itself.)

    rows.sort(key=lambda r: (round(r["_rect"]["y"]), r["_rect"]["x"]))

    # 4. an unowned text on the same row as a row to its left is a COLUMN of that
    #    row (Wildlife's "50%"), not a line of its own.
    merged = []
    for row in rows:
        if merged and not row["act"]:
            prev = merged[-1]
            pc = prev["_rect"]["y"] + prev["_rect"]["height"] / 2
            rc = row["_rect"]["y"] + row["_rect"]["height"] / 2
            if abs(pc - rc) <= _ROW_TOL and prev["_rect"]["x"] <= row["_rect"]["x"]:
                prev["text"] += "  " + row["text"]
                prev["dupes"] += row["dupes"]
                continue
        merged.append(row)
    rows = merged

    # 5. text-free actionables: attach as a toggle to the nearest row on the left
    #    that `_on_row` puts them on (row_toggle's own rule), else bucket them.
    free = []
    for a in acts:
        if id(a) in by_act:
            continue
        ar = _rect(a)
        host = None
        for row in rows:
            if _on_row(row["_rect"], ar):
                if host is None or row["_rect"]["x"] > host["_rect"]["x"]:
                    host = row
        if host is not None:
            host["toggles"].append({"x": ar["x"], "role": a.get("kind"),
                                    "checked": a.get("isChecked"),
                                    "disabled": a.get("disabled")})
        else:
            free.append(a)
    for row in rows:
        row["toggles"].sort(key=lambda t: t["x"])
        for n, t in enumerate(row["toggles"]):
            t["col"] = n
            t.pop("x")
            for k in ("checked", "disabled"):
                if t[k] is None:
                    del t[k]

    # 6. flags: repeated text (click-by-text is ambiguous), off the surface rect.
    counts = {}
    for row in rows:
        counts[row["text"]] = counts.get(row["text"], 0) + 1
    trunc = 0
    for row in rows:
        row["ambiguous"] = counts[row["text"]] > 1
        row["offscreen"] = bool(srect) and not _inside(srect, row["_rect"])
        if not full and len(row["text"]) > _TRUNC:
            row["text"] = row["text"][:_TRUNC] + "...(+%d chars)" % (len(row["text"]) - _TRUNC)
            trunc += 1
        row.pop("_rect")
        # Prune the falsey keys: absent means no, and `checked: null` is not the
        # same as absent -- it is the indeterminate a parent checkbox reports, so
        # that one key survives when the element is a checkbox at all.
        for k in ("act", "ambiguous", "offscreen", "disabled", "dupes"):
            if not row[k]:
                del row[k]
        for k in ("role", "toggles"):
            if not row[k]:
                del row[k]
        if row["checked"] is None and row.get("role") != "checkbox":
            del row["checked"]

    scroll = []
    for c in containers:
        sc = c.get("scroll") or {}
        if sc.get("canScrollX") or sc.get("canScrollY"):
            scroll.append({
                "axis": ("x" if sc.get("canScrollX") else "") + ("y" if sc.get("canScrollY") else ""),
                "offsetX": sc.get("offsetX"), "maxOffsetX": sc.get("maxOffsetX"),
                "offsetY": sc.get("offsetY"), "maxOffsetY": sc.get("maxOffsetY"),
                "content": [sc.get("contentRect", {}).get("width"),
                            sc.get("contentRect", {}).get("height")],
                "viewport": [sc.get("viewportRect", {}).get("width"),
                             sc.get("viewportRect", {}).get("height")]})

    freekinds = {}
    for a in free:
        freekinds[a.get("kind")] = freekinds.get(a.get("kind"), 0) + 1
    freerows = len({round(_cy(a) / _ROW_TOL) for a in free})

    if dupes:
        notes.append("%d duplicate text draws collapsed (same text within %gpx)"
                     % (dupes, _DUP_CELL))
    if rich:
        notes.append("%d labels had rich-text markup stripped" % rich)
    if trunc:
        notes.append("%d texts truncated at %d chars (pass full=True)" % (trunc, _TRUNC))
    if containers:
        notes.append("%d layout containers dropped (geometry only)" % len(containers))
    notes.append("dropped from every row: targetId, rect, screenRect, clipCapable, "
                 "depth, source -- ids are per-capture, re-find by text")

    out = {"id": s.get("surfaceTargetId"), "label": s.get("label") or s.get("title"),
           "kind": s.get("surfaceKind"), "type": s.get("type"),
           "elements": s.get("elementCount"), "actionable": s.get("actionableElementCount"),
           "rows": rows, "scroll": scroll,
           "textFree": {"count": len(free), "kinds": freekinds, "rows": freerows},
           "hidden": notes}
    sem = s.get("semanticDetails") or {}
    if sem.get("tabs"):
        out["tabs"] = [{"label": t.get("label"), "open": t.get("isOpen")}
                       for t in sem["tabs"]]
    return out

