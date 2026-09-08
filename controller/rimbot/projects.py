"""Durable intentions with native evidence, independent of model prose."""
import re
import uuid
from typing import Literal
from pydantic import BaseModel, ConfigDict, Field

class Target(BaseModel):
    model_config = ConfigDict(extra='forbid')
    kind: Literal['building', 'zone']
    def_name: str = ''
    x: int = 0
    z: int = 0
    zone_id: str = ''
    stuff: str = ''

class ProjectSpec(BaseModel):
    model_config = ConfigDict(extra='forbid')
    title: str = Field(min_length=1, max_length=160)
    detail: str = Field(default='', max_length=1000)
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
        target_key = lambda targets: sorted(t.model_dump_json() for t in targets)
        existing = next((row for row in self.rows if key(row.title) == key(spec.title) or
            (spec.targets and target_key(row.targets) == target_key(spec.targets))), None)
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

    async def reconcile(self, game, *, only_id=None):
        # Queries are memoized for this pass, not persisted across changes in game state.
        cache = {}
        for row in self.rows:
            if (only_id is not None and row.id != only_id) or row.state == 'cancelled' or not row.targets:
                continue
            found, pending, missing, ids = 0, 0, 0, []
            try:
                for target in row.targets:
                    key = target.model_dump_json()
                    if key not in cache:
                        if target.kind == 'zone':
                            result = await game.query('home/list_zones')
                            cache[key] = ('zone', result)
                        else:
                            result = await game.query('home/list_buildings', match=target.def_name,
                                x=target.x, z=target.z, radius=1, aggregate=False, playerOnly=True)
                            cache[key] = ('building', result)
                    kind, result = cache[key]
                    if kind == 'zone':
                        matches = [z for z in result['zones'] if str(z['id']) == target.zone_id and z['gridCellCount'] > 0]
                        found += bool(matches)
                        missing += not matches
                        ids.extend(str(z['id']) for z in matches)
                    else:
                        if result.get('skipped', {}).get('byMaxDetailed', 0):
                            raise ValueError('Building observation truncated')
                        matches = [b for b in result['buildings'] if b['position']['x'] == target.x
                            and b['position']['z'] == target.z and b['defName'] == target.def_name and b['status'] == 'built'
                            and (not target.stuff or b['stuff'] == target.stuff)]
                        queued = [b for b in result['buildings'] if b['position']['x'] == target.x
                            and b['position']['z'] == target.z and b['status'] in ('blueprint','frame')
                            and b.get('buildDefName') == target.def_name and (not target.stuff or b['stuff'] == target.stuff)]
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
