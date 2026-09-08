"""Campaign evidence counters; native receipts and pawn outcomes stay distinct."""
from dataclasses import asdict, dataclass

from .colony_plan import Buildings, RoomShell, Zone
from .hands import room_placements


@dataclass(frozen=True)
class Thresholds:
    colonists: int = 8
    sleeping_places: int = 8
    stockpile_cells: int = 9
    radius: int = 30


def intent_metrics(plan):
    """Count retained write-intent slots, not retries or unrecorded refusals."""
    totals = dict(intended_building_objects=0, intended_zone_cells=0,
                  attempted_building_objects=0, attempted_zone_cells=0,
                  accepted_building_effects=0, accepted_zone_cells=0)
    for step in plan.spec.steps:
        action, progress = step.action, plan.progress[step.id]
        placements = (room_placements(action) if isinstance(action, RoomShell)
                      else action.placements if isinstance(action, Buildings) else None)
        if placements is not None:
            totals['intended_building_objects'] += len(placements)
            for index in range(len(placements)):
                receipt = progress.issued.get(str(index))
                if receipt is not None:
                    totals['attempted_building_objects'] += 1
                    totals['accepted_building_effects'] += receipt.get('confirmed') is True
        elif isinstance(action, Zone):
            cells = len({c for patch in action.patches for c in patch.cells()})
            totals['intended_zone_cells'] += cells
            receipt = progress.issued.get('0')
            if receipt is not None:
                totals['attempted_zone_cells'] += cells
                if receipt.get('confirmed') is True:
                    totals['accepted_zone_cells'] += receipt.get('cells', 0)
    return totals


class CampaignEvidence:
    def __init__(self, thresholds=Thresholds()):
        self.thresholds = thresholds
        self.previous_ids = None
        self.samples = []

    def sample(self, plan, summary, anchor, buildings, zones, elapsed):
        """Accept complete fresh native readbacks; reject missing/truncated evidence."""
        t = self.thresholds
        if buildings.get('truncated') or buildings.get('skipped', {}).get('byMaxDetailed', 0):
            raise ValueError('Building outcome observation truncated')
        if zones.get('truncated'):
            raise ValueError('Zone outcome observation truncated')
        near = lambda x, z: max(abs(x-anchor[0]), abs(z-anchor[1])) <= t.radius
        built = {b['thingId']: b for b in buildings['buildings'] if b['status'] == 'built'
                 and near(b['position']['x'], b['position']['z'])}
        sleeping = [b for b in built.values() if b['defName'] in ('Bed', 'SleepingSpot')]
        stockpile = False
        labels = {s.action.label for s in plan.spec.steps
                  if isinstance(s.action, Zone) and s.action.zone_type == 'stockpile'}
        for zone in zones['zones']:
            # Match current native geometry to a committed stockpile identity. A
            # completed plan step alone cannot establish that the zone still exists.
            if zone.get('label') not in labels or zone.get('type') != 'Zone_Stockpile':
                continue
            cells = {(c['x'], c['z']) for c in zone.get('gridCells', [])}
            if len(cells) != zone['gridCellCount']:
                raise ValueError('Zone outcome geometry incomplete')
            stockpile |= len(cells) >= t.stockpile_cells and all(near(*c) for c in cells)
        living = sum(not p.dead for p in summary.pawns)
        food = any(s.def_name == 'Pemmican' and s.owned_unforbidden_units > 0 for s in summary.supplies)
        ids = set(built)
        row = dict(elapsed_seconds=elapsed, **intent_metrics(plan),
                   nearby_completed_objects=len(built), completed_ids=sorted(ids),
                   nearby_sleeping_capacity=len(sleeping),
                   excess_sleeping_places=max(0, len(sleeping)-t.sleeping_places),
                   removed_completed_ids=sorted(self.previous_ids-ids) if self.previous_ids is not None else [],
                   newly_completed_ids=sorted(ids-self.previous_ids) if self.previous_ids is not None else [],
                   stockpile=stockpile, starting_food_allowed=food, living_colonists=living,
                   usable=len(sleeping) >= t.sleeping_places and stockpile and food
                          and living == t.colonists and len(summary.pawns) == t.colonists)
        self.previous_ids = ids
        self.samples.append(row)
        return row

    def report(self):
        return dict(thresholds=asdict(self.thresholds), samples=self.samples,
                    scope='Observed sleeping places and stockpile geometry; shelter, access, filters and survival unverified',
                    attempt_scope='Retained persisted write slots; retries and pre-write refusals are not counted')
