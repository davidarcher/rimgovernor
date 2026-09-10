"""Join exact native construction lineage to confirmed autonomous plan receipts."""
from .colony_plan import Buildings, RoomShell
from .spatial import room_placements


def owned_buildings(plan, facts, *, include_missing=False):
    raw = facts.get('upkeep') or {}
    rows = raw.get('construction')
    if (raw.get('version') != 1 or raw.get('tick') != facts.get('tick')
            or 'construction' in raw.get('errors', {}) or not isinstance(rows, list)):
        return None
    if any(not isinstance(r.get('origin'), str) or not isinstance(r.get('current'), str)
           or type(r.get('present')) is not bool for r in rows):
        return None
    if len({r['origin'] for r in rows}) != len(rows) or len({r['current'] for r in rows}) != len(rows):
        return None
    lineage = {r['origin']: r for r in rows}
    owned = {}
    rotations = dict(north=0, east=1, south=2, west=3)
    for step in plan.spec.steps:
        goal = plan.colony_goals.get(step.goal_id)
        progress = plan.progress[step.id]
        if (step.source != 'AUTOPILOT' or goal is None or goal.source != 'AUTOPILOT' or goal.cancelled
                or progress.state not in ('complete', 'waiting', 'blocked') or not isinstance(step.action, (Buildings, RoomShell))):
            continue
        placements = room_placements(step.action) if isinstance(step.action, RoomShell) else step.action.placements
        for i, placement in enumerate(placements):
            receipt = progress.issued.get(str(i), {})
            if receipt.get('confirmed') is not True or receipt.get('outcome') != 'placed':
                continue
            row = lineage.get(receipt.get('placed_thing_id'))
            if (not row or row.get('stage') != 'built' or (not include_missing and row['present'] is not True) or row.get('blocker') is not None
                    or row.get('definition') != placement.def_name or row.get('x') != placement.x or row.get('z') != placement.z
                    or row.get('stuff') != (receipt.get('stuff') or None) or row.get('rotation') != rotations[placement.rotation]):
                continue
            owned[row['current']] = dict(row, step=step.id, slot=str(i), goal=step.goal_id)
    return owned
