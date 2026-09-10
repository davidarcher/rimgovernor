"""Durable intentions with native evidence, independent of model prose."""
import re
import uuid
import math
from copy import deepcopy
from typing import Literal
from pydantic import BaseModel, ConfigDict, Field
from .colony_plan import Rectangle, Buildings, RoomShell, Zone

class Target(BaseModel):
    model_config = ConfigDict(extra='forbid')
    kind: Literal['building', 'zone', 'installation']
    thing_id: str = ''
    rotation: int = Field(default=0, ge=0, le=3)
    expected_facing: Literal['north', 'east', 'south', 'west'] | None = None
    def_name: str = ''
    x: int = 0
    z: int = 0
    zone_id: str = ''
    stuff: str = ''
    zone_patches: list[Rectangle] = Field(default_factory=list, max_length=32)
    zone_type: Literal['stockpile', 'growing'] | None = None
    crop: str | None = None
    zone_settings: dict | None = None


def facing_matches(target, building):
    if target.expected_facing is None:
        return True  # Legacy construction targets did not record a facing contract.
    if 'rotationInt' in building:
        rotation = building['rotationInt']
        if type(rotation) is not int or rotation not in range(4):
            raise ValueError('Invariant native building facing unavailable')
        return ('north', 'east', 'south', 'west')[rotation] == target.expected_facing
    facing = building.get('rotation')
    if not isinstance(facing, str) or facing.casefold() not in ('north', 'east', 'south', 'west'):
        # Native ToStringHuman is localized. Unknown labels cannot prove rotation.
        raise ValueError('Native building facing unavailable or unrecognized')
    return facing.casefold() == target.expected_facing


def zone_matches(target, result):
    """Distinguish an observed edit from unavailable native zone postconditions."""
    if (result.get('success') is not True or not isinstance(result.get('zones'), list)
            or result.get('zoneCount') != len(result['zones'])
            or result.get('zoneCountOnMap') != len(result['zones'])
            or (result.get('totals') or {}).get('gridSweepFailed') is not False):
        raise ValueError('Complete zone census unavailable')
    matches = [z for z in result['zones'] if str(z['id']) == target.zone_id]
    if not matches:
        return False
    if len(matches) != 1:
        raise ValueError('Zone identity is ambiguous')
    zone = matches[0]
    geometry = []
    for field, count, omitted in (('cells', 'listedCellCount', 'cellsNotListed'),
                                   ('gridCells', 'gridCellCount', 'gridCellsNotListed')):
        cells = zone.get(field)
        if not isinstance(cells, list) or any(not isinstance(c, dict) or
                any(type(c.get(k)) is not int for k in ('x', 'z')) for c in cells):
            raise ValueError('Zone cell coordinates unavailable')
        points = {(c['x'], c['z']) for c in cells}
        if len(points) != len(cells) or zone.get(count) != len(points) or zone.get(omitted) != 0:
            raise ValueError('Zone geometry truncated or inconsistent')
        geometry.append(points)
    if zone.get('consistent') is not True or geometry[0] != geometry[1]:
        raise ValueError('Zone list/grid agreement unavailable')
    expected = {cell for patch in target.zone_patches for cell in patch.cells()}
    if geometry[0] != expected:
        return False
    observed_type = ('growing' if zone.get('type') == 'Zone_Growing' or 'plantDefExplicitlySet' in zone
                     else 'stockpile' if zone.get('type') == 'Zone_Stockpile' or 'filterSummary' in zone else None)
    if observed_type is None:
        raise ValueError('Zone type unavailable')
    if target.zone_type is not None and target.zone_type != observed_type:
        return False
    if target.crop:
        if not isinstance(zone.get('plantDef'), str) or not zone['plantDef']:
            raise ValueError('Explicit zone crop unavailable')
        if zone['plantDef'] != target.crop:
            return False
    if target.zone_settings is not None:
        if zone_settings(zone) != target.zone_settings:
            return False
    return True


def zone_settings(zone):
    """An exact native settings contract; display samples cannot certify a filter."""
    if zone.get('type') == 'Zone_Growing':
        if any(type(zone.get(k)) is not bool for k in ('allowSow', 'allowCut')):
            raise ValueError('Native sow/cut settings unavailable')
        return {k: zone[k] for k in ('allowSow', 'allowCut')}
    if zone.get('type') != 'Zone_Stockpile':
        raise ValueError('Native zone kind unavailable')
    priority = zone.get('priority')
    contract = (zone.get('filter') or {}).get('contract')
    if priority not in ('Low', 'Normal', 'Preferred', 'Important', 'Critical') or not isinstance(contract, dict):
        raise ValueError('Exact native stockpile settings unavailable')
    if type(contract.get('version')) is not int or contract['version'] != 1:
        raise ValueError('Exact native stockpile filter unavailable')
    for field in ('allowedDefs', 'disallowedSpecial'):
        names = contract.get(field)
        if not isinstance(names, list) or any(not isinstance(n, str) or not n for n in names) or len(set(names)) != len(names):
            raise ValueError('Complete native filter definitions unavailable')
    for field, maximum in (('hitPoints', 1), ('quality', 6), ('mentalBreakChance', 1)):
        bounds = contract.get(field)
        if (not isinstance(bounds, list) or len(bounds) != 2 or
                any(type(n) not in (int, float) or not math.isfinite(n) for n in bounds)
                or not 0 <= bounds[0] <= bounds[1] <= maximum
                or (field == 'quality' and any(type(n) is not int for n in bounds))):
            raise ValueError('Native filter range unavailable')
    contract = deepcopy(contract)
    for field in ('allowedDefs', 'disallowedSpecial'):
        contract[field].sort()
    return dict(priority=priority, filter=contract)

class ProjectSpec(BaseModel):
    model_config = ConfigDict(extra='forbid')
    title: str = Field(min_length=1, max_length=160)
    detail: str = Field(default='', max_length=1000)
    source_step: str = ''
    targets: list[Target] = Field(default_factory=list, max_length=256)

class Project(ProjectSpec):
    id: str
    state: Literal['planned', 'pending', 'complete', 'blocked', 'cancelled'] = 'planned'
    evidence: str = ''
    matched_ids: list[str] = Field(default_factory=list)

class ProjectBook:
    def __init__(self, rows=()):
        self.rows = [Project.model_validate(row) for row in rows]

    def dump(self):
        return [row.model_dump() for row in self.rows]

    def upsert(self, spec):
        spec = ProjectSpec.model_validate(spec)
        key = lambda title: re.sub(r'\W+', ' ', title.casefold()).strip()
        if not key(spec.title):
            raise ValueError('Give the project a meaningful title')
        for target in spec.targets:
            if target.kind == 'building' and not target.def_name:
                raise ValueError('Building targets need an observed def_name and origin cell')
            if target.kind == 'zone' and not target.zone_id:
                raise ValueError('Zone targets need an observed zone_id')
            if target.kind == 'installation' and not target.thing_id:
                raise ValueError('Installation targets need the exact inner building ID')
        target_key = lambda targets: sorted(t.model_dump_json() for t in targets)
        existing = next((row for row in self.rows if (
            row.source_step == spec.source_step if spec.source_step else
            not row.source_step and (key(row.title) == key(spec.title) or
                (spec.targets and target_key(row.targets) == target_key(spec.targets))))), None)
        if existing and existing.state == 'cancelled':
            raise ValueError('Player cancelled this project; do not recreate it')
        if existing:
            existing.detail = spec.detail
            # Refine targets rather than appending duplicate tracked work.
            if existing.targets != spec.targets:
                existing.targets = spec.targets
                existing.state, existing.evidence = 'planned', 'Targets updated; awaiting observation'
            return existing
        row = Project(id=uuid.uuid4().hex[:12], **spec.model_dump())
        self.rows.append(row)
        return row

    def cancel(self, identity):
        row = next((p for p in self.rows if p.id == identity), None)
        if row is None:
            raise ValueError('Unknown project')
        row.state, row.evidence = 'cancelled', 'Cancelled by player; existing game orders are unchanged'
        return row

    def ground_legacy_targets(self, plan):
        """Recover expectations from the exact durable action, never current map state."""
        from .spatial import room_placements
        for row in self.rows:
            if row.state == 'cancelled':
                continue
            owners = [s for s in plan.spec.steps if plan.progress[s.id].project_id == row.id]
            if len(owners) != 1 or (row.source_step and row.source_step != owners[0].id):
                continue
            step = owners[0]
            if isinstance(step.action, Zone) and len(row.targets) == 1 and row.targets[0].kind == 'zone':
                target = row.targets[0]
                if not target.zone_patches:
                    target.zone_patches = [p.model_copy(deep=True) for p in step.action.patches]
                    target.zone_type, target.crop = step.action.zone_type, step.action.crop or None
                    row.source_step = step.id
            elif isinstance(step.action, (Buildings, RoomShell)):
                placements = room_placements(step.action) if isinstance(step.action, RoomShell) else step.action.placements
                expected = {(p.def_name, p.x, p.z): p for p in placements}
                if len(expected) != len(row.targets) or len({(t.def_name, t.x, t.z) for t in row.targets}) != len(row.targets) or any(t.kind != 'building' or
                        (t.def_name, t.x, t.z) not in expected for t in row.targets):
                    continue
                for target in row.targets:
                    if target.expected_facing is None:
                        target.expected_facing = expected[target.def_name, target.x, target.z].rotation
                row.source_step = step.id

    async def reconcile(self, game, *, only_id=None, plan=None):
        if plan is not None:
            self.ground_legacy_targets(plan)
        # Queries are memoized for this pass, not persisted across changes in game state.
        cache = {}
        for row in self.rows:
            if (only_id is not None and row.id != only_id) or row.state == 'cancelled' or not row.targets:
                continue
            found, pending, missing, ids = 0, 0, 0, []
            try:
                for target in row.targets:
                    key = ('zone', bool(target.zone_patches), target.zone_settings is not None) if target.kind == 'zone' else target.model_dump_json()
                    if key not in cache:
                        if target.kind == 'installation':
                            result = await game.invoke('home/install', {'thingId': target.thing_id, 'dryRun': True})
                            cache[key] = ('installation', result)
                        elif target.kind == 'zone':
                            args = dict(includeCells=True, includeContents=False, maxCellsPerZone=10000) if target.zone_patches else {}
                            if target.zone_settings is not None:
                                args['filter'] = True
                            result = await game.query('home/list_zones', **args)
                            cache[key] = ('zone', result)
                        else:
                            result = await game.query('home/list_buildings', match=target.def_name,
                                x=target.x, z=target.z, radius=1, aggregate=False, playerOnly=True)
                            cache[key] = ('building', result)
                    kind, result = cache[key]
                    if kind == 'installation':
                        if result.get('thingId') != target.thing_id:
                            raise ValueError('Installation identity was not confirmed')
                        position = result.get('position') or {}
                        blueprint = result.get('blueprint') or {}
                        complete = result.get('state') == 'installed' and position.get('x') == target.x and position.get('z') == target.z and result.get('rotation') == target.rotation
                        queued = result.get('state') == 'queued' and blueprint.get('x') == target.x and blueprint.get('z') == target.z and blueprint.get('rotation') == target.rotation
                        found += int(complete)
                        pending += int(queued)
                        missing += int(not complete and not queued)
                        if complete:
                            ids.append(target.thing_id)
                    elif kind == 'zone':
                        matches = ([target.zone_id] if zone_matches(target, result) else []) if target.zone_patches else [
                            str(z['id']) for z in result['zones'] if str(z['id']) == target.zone_id and z['gridCellCount'] > 0]
                        found += bool(matches)
                        missing += not matches
                        ids.extend(matches)
                    else:
                        if result.get('skipped', {}).get('byMaxDetailed', 0):
                            raise ValueError('Building observation truncated')
                        matches = [b for b in result['buildings'] if b['position']['x'] == target.x
                            and b['position']['z'] == target.z and b['defName'] == target.def_name and b['status'] == 'built'
                            and (not target.stuff or b['stuff'] == target.stuff) and facing_matches(target, b)]
                        queued = [b for b in result['buildings'] if b['position']['x'] == target.x
                            and b['position']['z'] == target.z and b['status'] in ('blueprint','frame')
                            and b.get('buildDefName') == target.def_name and (not target.stuff or b['stuff'] == target.stuff)
                            and facing_matches(target, b)]
                        found += bool(matches)
                        pending += int(bool(queued) and not matches)
                        missing += int(not matches and not queued)
                        ids.extend(b['thingId'] for b in matches)
                row.matched_ids = ids
                if found == len(row.targets):
                    row.state, row.evidence = 'complete', f'{found} targets observed in the game'
                elif pending:
                    row.state, row.evidence = 'pending', f'{found} complete; {pending} awaiting pawn work; {missing} missing'
                else:
                    row.state, row.evidence = 'planned', f'{found} complete; {missing} missing or removed'
            except Exception as error:
                row.state, row.evidence = 'blocked', 'Cannot verify: '+str(error)[:300]
