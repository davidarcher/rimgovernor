"""Conservative admission to the native combat clock's unchanged injury limits."""
from math import isfinite


def combat_health_hold(observation):
    pawns = observation.get('pawns')
    if observation.get('success') is False or not isinstance(pawns, list) or not pawns:
        return 'Combat health is unavailable; inspect colonists before resuming.'
    for pawn in pawns:
        health = (pawn.get('health') or {}).get('summaryPct')
        if (pawn.get('dead') is not False or pawn.get('downed') is not False
                or type(health) not in (int, float) or not isfinite(health)
                or not 0 <= health <= 1):
            return 'Combat requires a fresh living, conscious colonist health census.'
        # Native summaryPct is rounded to three decimals. Include its rounding
        # interval so a value just below the native 0.5 limit cannot pass.
        if health <= 0.5005:
            return 'Colonist '+str(pawn.get('thingId'))+' remains at the combat health limit; treatment or player direction is required.'
    return None
