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
        self.baseline_ids = None
        self.previous_pending = set()
        self.previous_pawns = {}
        self.hauled_items = {}
        self.first_pawn_progress = None
        self.first_completion = None

    def sample(self, plan, summary, anchor, buildings, zones, elapsed, *, rooms=None, pawns=None, items=None, mode=None):
        """Accept complete fresh native readbacks; reject missing/truncated evidence."""
        t = self.thresholds
        if buildings.get('truncated') or buildings.get('skipped', {}).get('byMaxDetailed', 0):
            raise ValueError('Building outcome observation truncated')
        if zones.get('truncated'):
            raise ValueError('Zone outcome observation truncated')
        near = lambda x, z: max(abs(x-anchor[0]), abs(z-anchor[1])) <= t.radius
        built = {b['thingId']: b for b in buildings['buildings'] if b['status'] == 'built'
                 and near(b['position']['x'], b['position']['z'])}
        excluded_beds={(b['position']['x'],b['position']['z']) for room in (rooms or {}).get('rooms',[])
                       for b in room.get('beds',[]) if b.get('medical') is True or b.get('forPrisoners') is True}
        sleeping = [b for b in built.values() if b['defName'] in ('Bed', 'SleepingSpot')
                    and b.get('medical') is not True and (b['position']['x'],b['position']['z']) not in excluded_beds]
        stockpile = False
        for zone in zones['zones']:
            # The native stockpile persists independently of current plan intent.
            if zone.get('type') != 'Zone_Stockpile':
                continue
            cells = {(c['x'], c['z']) for c in zone.get('gridCells', [])}
            if len(cells) != zone['gridCellCount']:
                raise ValueError('Zone outcome geometry incomplete')
            stockpile |= len(cells) >= t.stockpile_cells and all(near(*c) for c in cells)
        living = sum(not p.dead for p in summary.pawns)
        population = len(summary.pawns)
        if pawns is not None:
            population = len(pawns.get('pawns',[]))
            living = sum(p['dead'] is False for p in pawns.get('pawns',[])) if all(p.get('dead') is not None for p in pawns.get('pawns',[])) else None
        food = any(s.def_name == 'Pemmican' and s.owned_unforbidden_units > 0 for s in summary.supplies)
        ids = set(built)
        if self.baseline_ids is None:
            self.baseline_ids = ids.copy()
        targets = set()
        intended_origins = []
        for step in plan.spec.steps:
            action=step.action
            placements=room_placements(action) if isinstance(action,RoomShell) else action.placements if isinstance(action,Buildings) else []
            intended_origins.extend((p.def_name,p.x,p.z) for p in placements)
        targets.update(intended_origins)
        outside = sorted(identity for identity,b in built.items() if identity not in self.baseline_ids
                         and (b['defName'],b['position']['x'],b['position']['z']) not in targets)
        newly = sorted(ids-self.previous_ids) if self.previous_ids is not None else []
        if newly and self.first_completion is None:
            self.first_completion = elapsed
        pending = {b['thingId'] for b in buildings['buildings'] if b['status'] in ('blueprint','frame') and near(b['position']['x'],b['position']['z'])}
        progress_pawns = []
        work_jobs = {'DoBill','ConstructFinishFrame','ConstructDeliverResources','HaulToCell','Sow','Harvest'}
        for pawn in (pawns or {}).get('pawns', []):
            if pawn.get('job')=='HaulToCell' and pawn.get('carriedThingId'):
                self.hauled_items[pawn['carriedThingId']]={'pawn_id':pawn['thingId'],'elapsed_seconds':elapsed}
            previous = self.previous_pawns.get(pawn['thingId'])
            if (previous and pawn.get('job') in work_jobs and previous.get('job')==pawn.get('job')
                    and (previous.get('position')!=pawn.get('position') or previous.get('carriedThingId')!=pawn.get('carriedThingId'))):
                progress_pawns.append(pawn['thingId'])
        if progress_pawns and self.first_pawn_progress is None:
            self.first_pawn_progress = elapsed
        row = dict(elapsed_seconds=elapsed, **intent_metrics(plan),
                   nearby_completed_objects=len(built), completed_ids=sorted(ids),
                   nearby_sleeping_capacity=len(sleeping),
                   excess_sleeping_places=max(0, len(sleeping)-t.sleeping_places),
                   duplicate_intended_building_origins=len(intended_origins)-len(targets),
                   new_completed_outside_current_intent_ids=outside,
                   removed_completed_ids=sorted(self.previous_ids-ids) if self.previous_ids is not None else [],
                   newly_completed_ids=newly,
                   pending_ids=sorted(pending), disappeared_pending_ids=sorted(self.previous_pending-pending),
                   interrupted_step_ids=sorted(key for key,p in plan.progress.items() if p.issued and (p.state in ('blocked','cancelled') or mode=='manual' and p.state!='complete')),
                   mode=mode,
                   observed_progress_pawn_ids=sorted(progress_pawns),
                   functional=functional_capacity(buildings,zones,rooms,pawns,anchor,t,items=items,hauled_items=self.hauled_items),
                   stockpile=stockpile, starting_food_allowed=food, living_colonists=living,
                   usable=len(sleeping) >= t.sleeping_places and stockpile and food
                          and living == t.colonists and population == t.colonists)
        self.previous_ids = ids
        self.previous_pending = pending
        self.previous_pawns = {p['thingId']:p for p in (pawns or {}).get('pawns', [])}
        self.samples.append(row)
        return row

    def report(self):
        return dict(thresholds=asdict(self.thresholds), samples=self.samples,
                    first_observed_completion_seconds=self.first_completion,
                    first_pawn_progress_seconds=self.first_pawn_progress,
                    pawn_progress_scope='Sampled position/carry change while the same native construction/production/hauling/growing job is active; no actor attribution to AI orders',
                    latency_scope='First-observation times are sampling upper bounds; missing observations remain null',
                    scope='usable is the narrow foothold gate; functional components separately report native geometry, configuration and observed use; survival remains unverified',
                    attempt_scope='Retained persisted write slots; retries and pre-write refusals are not counted')


def _all_known(values):
    """Three-valued AND: an observed failure wins; missing evidence stays unknown."""
    return False if False in values else None if None in values else True


def functional_capacity(buildings, zones, rooms, pawns, anchor, thresholds, *, items=None, hauled_items=None):
    """Native geometry and current use; no pathfinding or food forecast inferred."""
    near = lambda p: max(abs(p['x']-anchor[0]), abs(p['z']-anchor[1])) <= thresholds.radius
    built = {(b['defName'], b['position']['x'], b['position']['z']): b['thingId']
             for b in buildings['buildings'] if b['status']=='built' and near(b['position'])}
    sheltered = set()
    room_unknown = rooms is None or rooms.get('truncated', False) or rooms.get('roomsOmitted',0)>rooms.get('outdoorRoomsOmitted',0)
    for room in (rooms or {}).get('rooms', []):
        valid = _all_known([room.get('properRoom'),
                           None if room.get('openRoofCount') is None else room['openRoofCount']==0,
                           None if room.get('psychologicallyOutdoors') is None else not room['psychologicallyOutdoors'],
                           None if room.get('touchesMapEdge') is None else not room['touchesMapEdge']])
        if valid is None or room.get('skipped') or room.get('bedCount') is None:
            room_unknown = True
        if valid is not True:
            continue
        for bed in room.get('beds', []):
            if bed.get('medical') is None or bed.get('forPrisoners') is None:
                room_unknown = True
                continue
            if bed['medical'] or bed['forPrisoners'] or bed['defName'] not in ('Bed','SleepingSpot'):
                continue
            p = bed['position']; key = (bed['defName'], p['x'], p['z'])
            if key in built:
                sheltered.add(key)
    sleeping = []
    used_bed_cells = set()
    for pawn in (pawns or {}).get('pawns', []):
        p = pawn.get('position') or {}
        if (pawn.get('job')=='LayDown' and pawn.get('dead') is False and pawn.get('isColonist') is True
                and pawn.get('isPrisoner') is False and any((p.get('x'),p.get('z'))==key[1:] for key in sheltered)):
            sleeping.append(pawn['thingId'])
            used_bed_cells.add((p['x'],p['z']))
    storage = []
    for zone in zones['zones']:
        if zone.get('type')!='Zone_Stockpile':
            continue
        cells = zone.get('gridCells', [])
        if not cells or not all(near(c) for c in cells):
            continue
        counts = [zone.get(k) for k in ('gridCellCount','listedCellCount','slotGroupCellCount','haulGridCellCount')]
        consistent = None if None in counts else len(set(counts))==1 and counts[0]==len(cells)
        allowed = (zone.get('filter') or zone.get('filterSummary') or {}).get('allowedDefCount')
        blocked=[zone.get('cellsImpassable'),zone.get('cellsNotStandable')]
        capacity=None if None in blocked else max(0,len(cells)-sum(blocked))
        valid = _all_known([None if capacity is None else capacity>=thresholds.stockpile_cells, consistent,
                            zone.get('contiguous'),None if allowed is None else allowed>0])
        storage.append(dict(id=zone['id'], cells=len(cells), grid_consistent=consistent,
                            allowed_def_count=allowed, usable_cell_lower_bound=capacity, configured=valid))
    deliveries=[]
    for thing in (items or {}).get('things',[]):
        for position in thing.get('positions',[]):
            identity=position.get('thingId')
            if position.get('spawned') is not True or identity not in (hauled_items or {}):
                continue
            for zone in zones['zones']:
                if not any(s['id']==zone['id'] and s['configured'] is True for s in storage):continue
                if any((c['x'],c['z'])==(position['x'],position['z']) for c in zone.get('gridCells',[])):
                    deliveries.append(dict(thing_id=identity,def_name=thing['defName'],zone_id=zone['id'],
                                           **hauled_items[identity]))
    configured = True if any(s['configured'] is True for s in storage) else None if any(s['configured'] is None for s in storage) else False
    return dict(roofed_sleeping_places=len(sheltered), roofed_count_complete=not room_unknown,
                sheltered_sleeping_pawn_ids=sorted(set(sleeping)),
                observed_sheltered_sleeping_capacity=len(used_bed_cells),
                sleeping_capacity_access_verified=True if len(used_bed_cells)>=thresholds.sleeping_places else None,
                storage=storage,
                storage_configured=configured,
                # Current LayDown proves use at the observed bed; it cannot prove
                # every vacant bed or a storage zone is reachable by every pawn.
                storage_access_verified=True if deliveries else None, observed_storage_deliveries=deliveries,
                item_positions_complete=None if items is None else not items.get('truncated',False) and not any(t.get('positionsNotListed',0) for t in items.get('things',[])),
                feasible_food_work_verified=None,
                usable_capacity_verified=_all_known([
                    True if len(sheltered)>=thresholds.sleeping_places else None if room_unknown else False,
                    configured, True if deliveries else None,
                    True if len(used_bed_cells)>=thresholds.sleeping_places else None]),
                scope='Tested single-slot sleeping/storage capacity: access witnessed for listed sleeping pawns and delivered items only; other paths, item suitability and sustained food work remain unverified')


def event_metrics(events, *, began_at=None, role_metrics=None):
    """Summarize retained exact diagnostics without counting readbacks as writes."""
    import hashlib
    import json
    from collections import Counter
    from statistics import median
    calls = [e for e in events if e['kind'] in ('planner_tool','campaign_native')]
    groups = {}
    replay = []
    for kind in ('planner_tool','campaign_native'):
        rows = [e for e in calls if e['kind']==kind]
        fingerprints = Counter()
        unidentified = 0
        for row in rows:
            args = row.get('arguments')
            if not isinstance(args, dict) or args.get('truncated') is True:
                unidentified += 1
                continue
            encoded = json.dumps([row.get('tool'),args],sort_keys=True,separators=(',',':'))
            fingerprints[hashlib.sha256(encoded.encode()).hexdigest()] += 1
        durations = sorted(e['elapsed_seconds'] for e in rows if isinstance(e.get('elapsed_seconds'), (int,float)))
        groups[kind] = dict(attempts=len(rows), rejected=sum(e.get('outcome')=='rejected' for e in rows),
            cancelled=sum(e.get('outcome')=='cancelled' for e in rows),
            failed=sum(e.get('outcome')=='failed' for e in rows),
            repeated_identical_attempts=sum(n-1 for n in fingerprints.values()), fingerprint_unknown=unidentified,
            latency_seconds=dict(count=len(durations), total=sum(durations),
                median=median(durations) if durations else None, maximum=max(durations) if durations else None))
    useful = [e for e in calls if e['kind']=='campaign_native' and e.get('useful_order_receipt') is True]
    first = min((e['at'] for e in useful), default=None)
    interventions = [e for e in events if e['kind'] in ('human','clock_event','campaign_intervention')]
    for e in events:
        if e['kind'] in ('planner_tool','campaign_native','execution_blocked','plan_decision','human','clock_event','campaign_intervention','campaign_budget'):
            replay.append({k:e[k] for k in ('id','at','kind','tool','outcome','elapsed_seconds','text','budget') if k in e})
    return dict(calls=groups,
        first_useful_order_seconds=None if first is None or began_at is None else max(0,first-began_at),
        first_useful_order_scope='First returned non-clock/non-UI gameplay write receipt; downstream usefulness requires observed outcomes',
        model_roles=role_metrics if role_metrics is not None else None,
        context_compactions=sum(e['kind']=='campaign_budget' and e.get('budget',{}).get('compacted') is True for e in events),
        context_budget_samples=[e['budget'] for e in events if e['kind']=='campaign_budget'],
        interventions=[{k:e[k] for k in ('id','at','kind','text','native_event') if k in e} for e in interventions],
        replay=replay, scope='Retained strategist tool diagnostics and runtime native dispatches; background reads and rejected pre-dispatch writes excluded; token usage is provider-reported and may omit failed calls')
