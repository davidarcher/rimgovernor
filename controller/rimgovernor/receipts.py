"""Adapted from Snowstar38/rimworld-claude-harness instruments/build.py.
Pinned source 89c2e90fedd51419a3db55a7f9865b0aef29b270; local research reuse.
One verdict per placement: a preview or duplicate is not a placed blueprint.
"""

def reason(r):
    """The refusal text, wherever the bridge put it.

    `BridgeCommon.Failure` returns `{success, tool, error}` and carries no
    `message`, so reading only `message` yielded `FAILED: None`. Stock RimBridge
    tools do use `message`, so it is tried first. Never returns None.
    """
    if not isinstance(r, dict):
        return "the bridge did not return a payload: %.200r" % (r,)
    for key in ("message", "error", "detail", "reason"):
        v = r.get(key)
        if isinstance(v, str) and v.strip():
            return v.strip()
    return "no reason given by the bridge (reply keys: %s)" % (", ".join(sorted(r)) or "none")

def _outcome(r):
    """What happened to THIS call, in one word. `outcome` is the companion's own
    field (preview / placed / already_present / refused / error); the fallbacks
    below cover a DLL older than it.

    The 2026-09-07 bug was that the headline read `PLACED` off `dryRun` alone,
    so a call the game refused printed PLACED and a rotation row printed REFUSED
    in the same breath, and only `buildings.py --pending` said which was true.
    """
    word = r.get("outcome")
    if word in ("preview", "placed", "already_present", "refused", "error"):
        return word
    if r.get("dryRun"):
        return "preview"
    if r.get("placed"):
        return "placed"
    if r.get("alreadyPlaced"):
        return "already_present"
    return "refused" if r.get("success") is False else "error"

def verdict_line(r):
    """The one line that says what this call did. Printed last, always, and
    never contradicted by anything above it."""
    out = _outcome(r)
    if out == "preview":
        ok = r.get("acceptedRotations") or [row.get("rotation")
                                            for row in (r.get("rotations") or [])
                                            if row.get("accepted")]
        n = r.get("rotationsEvaluated") or len(r.get("rotations") or [])
        return ("== DRY RUN -- nothing was placed. %d of %d rotation(s) accepted%s"
                % (len(ok), n, (": " + ", ".join(str(o) for o in ok)) if ok else ""))
    if out == "placed":
        p = r.get("placed") or {}
        return ("== PLACED -- blueprint id %s, %s at %s,%s facing %s"
                % (p.get("thingIDNumber"), p.get("label") or p.get("defName"),
                   (p.get("position") or {}).get("x"),
                   (p.get("position") or {}).get("z"),
                   p.get("rotation") or p.get("rotationWord")))
    if out == "already_present":
        return ("== ALREADY THERE -- an identical blueprint or the finished thing "
                "stands at this cell and rotation; NOTHING was placed and no new "
                "blueprint id exists.")
    return "== %s -- NOTHING was placed. %s" % (
        "REFUSED" if out == "refused" else "ERROR", reason(r))
