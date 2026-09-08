"""Deterministic intent compilation and native validation. No model dependency."""
from .colony_plan import Buildings, RoomShell, Zone, NativeOperation, ClockAction, Placement, Failure
from .receipts import reason


def room_placements(room: RoomShell):
    b = room.bounds
    x2, z2 = b.x+b.width-1, b.z+b.height-1
    door = {'north': (b.x+b.width//2, z2), 'south': (b.x+b.width//2, b.z),
        'east': (x2, b.z+b.height//2), 'west': (b.x, b.z+b.height//2)}[room.entrance]
    perimeter = [(x, z) for x, z in b.cells() if x in (b.x, x2) or z in (b.z, z2)]
    # Put the door first; no room shell can compile into four corners only.
    perimeter.sort(key=lambda c: c != door)
    return [Placement(x=x, z=z, def_name=room.door_def if (x,z)==door else room.wall_def,
        rotation=room.entrance if (x,z)==door else 'north', materials=room.materials) for x,z in perimeter]


def validate_geometry(spec):
    reserved = {c for r in spec.reserved_walkways for c in r.cells()}
    claimed = {}
    rooms = [(s.id, set(s.action.bounds.cells())) for s in spec.steps if isinstance(s.action, RoomShell)]
    for step in spec.steps:
        action = step.action
        if isinstance(action, RoomShell):
            points = [(p.x,p.z) for p in room_placements(action)]
        elif isinstance(action, Buildings):
            points = [(p.x,p.z) for p in action.placements]
        elif isinstance(action, Zone):
            points = sorted({c for patch in action.patches for c in patch.cells()})
            if any(set(points) & cells for _, cells in rooms):
                raise ValueError('A zone overlaps a committed room footprint')
        else:
            continue
        for point in points:
            if point in reserved:
                raise ValueError(f'{step.id} occupies a reserved walkway at {point}')
            if point in claimed and claimed[point] != step.id:
                raise ValueError(f'{step.id} overlaps {claimed[point]} at {point}')
            claimed[point] = step.id


class Blocked(Exception):
    def __init__(self, code, detail, *, retryable=False, evidence=None):
        self.failure = Failure(code=code, detail=detail, retryable=retryable, evidence=evidence or {})


class Hands:
    async def advance(self, rt, max_operations=12):
        """Bound each controller pass to yield to UI, not to spend model calls."""
        revision, token, direction = rt.current_plan.revision, rt.context_token, rt.chat_revision
        count = 0
        for step in list(rt.current_plan.ready()):
            progress = rt.current_plan.progress[step.id]
            try:
                self.guard(rt, revision, token, direction)
                action = step.action
                placements = room_placements(action) if isinstance(action, RoomShell) else action.placements if isinstance(action, Buildings) else None
                operations = placements if placements is not None else [action]
                for index, operation in enumerate(operations):
                    self.guard(rt, revision, token, direction)
                    key = str(index)
                    recorded = progress.issued.get(key)
                    if recorded and recorded.get('confirmed'):
                        continue
                    if recorded and not placements and not isinstance(action, Zone):
                        raise Blocked('uncertain_write', 'Prior write has no confirmed receipt. Inspect its effects before replacing this step.')
                    if count >= max_operations:
                        return
                    progress.state = 'executing'
                    if placements is not None:
                        receipt = await self.place(rt, operation, progress, key, revision, token, direction)
                    elif isinstance(action, Zone):
                        receipt = await self.zone(rt, action, progress, key, revision, token, direction)
                    else:
                        if isinstance(action, NativeOperation):
                            args = dict(action.arguments)
                            schema = await rt.game.describe(action.tool)
                            if 'dryRun' in schema.get('properties', {}):
                                preview = await rt.inspect_native(action.tool, dict(args, dryRun=True))
                                if preview.get('success') is False:
                                    raise Blocked('native_refused', reason(preview), evidence=preview)
                                args['dryRun'] = False
                            self.guard(rt, revision, token, direction)
                            progress.issued[key] = {'confirmed': False}
                            rt.persist()
                            result = await rt.native(action.tool, args, expected_revision=direction, expected_token=token,
                                expected_plan_revision=revision, reconcile=False)
                            receipt = {'native_outcome': result.get('receipt', result).get('outcome', 'receipt'),
                                'meaning': 'Native command observed; this does not certify completion of pawn labor'}
                        else:
                            self.guard(rt, revision, token, direction)
                            progress.issued[key] = {'confirmed': False}
                            rt.persist()
                            receipt = await rt.control_clock(action.speed, mode=action.mode,
                                ignored_hostiles=action.ignored_hostiles, ignored_downed=action.ignored_downed,
                                expected_revision=direction, expected_token=token, expected_plan_revision=revision)
                            receipt = {'active': receipt.get('active'), 'stopReason': receipt.get('stopReason')}
                    progress.issued[key] = dict(receipt, confirmed=True)
                    progress.failure = None
                    rt.persist()
                    count += 1
                if placements is not None:
                    targets = [{'kind': 'building', 'def_name': p.def_name, 'x': p.x, 'z': p.z,
                        'stuff': progress.issued[str(i)].get('stuff') or ''} for i,p in enumerate(placements)]
                    row = rt.projects.upsert({'title': step.title, 'detail': step.completion_criteria, 'targets': targets})
                    progress.project_id, progress.state = row.id, 'waiting'
                else:
                    progress.state = 'complete'
                rt.note('execution', step.title+(': orders issued; awaiting construction' if placements else ': native operation verified'))
                if all(rt.current_plan.progress[s.id].state in ('complete', 'cancelled') for s in rt.current_plan.spec.steps):
                    rt.signal('plan.completed', {'revision': revision})
                rt.persist()
            except InterruptedError:
                return
            except Exception as error:
                try:
                    self.guard(rt, revision, token, direction)
                except InterruptedError:
                    return
                if isinstance(error, Blocked):
                    failure = error.failure
                else:
                    payload = getattr(getattr(error, 'result', None), 'structuredContent', None) or {}
                    failure = Failure(code='native_failure', detail=str(error)[:1000], evidence=payload)
                progress.state, progress.failure = 'blocked', failure
                rt.note('execution_blocked', step.title+': '+failure.detail, failure=failure.model_dump())
                rt.persist()
                rt.signal('plan.step_failed', {'step': step.id, 'failure': failure.model_dump()})

    @staticmethod
    def guard(rt, revision, token, direction):
        if (rt.mode != 'automate' or rt.context_token != token or rt.current_plan.revision != revision
                or rt.chat_revision != direction or rt.chat_revision > rt.handled_revision):
            raise InterruptedError('Plan or player direction changed')

    async def place(self, rt, p, progress, key, revision, token, direction):
        found = await rt.game.query('home/list_buildings', match=p.def_name, x=p.x, z=p.z, radius=1, aggregate=False, playerOnly=True)
        if found.get('skipped', {}).get('byMaxDetailed'):
            raise Blocked('incomplete_observation', 'Construction query was truncated')
        matches = [b for b in found['buildings'] if b['position']['x']==p.x and b['position']['z']==p.z
            and (b['defName']==p.def_name or b.get('buildDefName')==p.def_name)
            and (not p.materials or b.get('stuff') in p.materials)]
        if matches:
            return {'thing_id': matches[0]['thingId'], 'stuff': matches[0].get('stuff'), 'observed': matches[0]['status']}
        last = None
        for stuff in p.materials or [None]:
            args = dict(defName=p.def_name, x=p.x, z=p.z, rotation=p.rotation, dryRun=True)
            if stuff:
                args['stuff'] = stuff
            preview = await rt.inspect_native('home/place_building', args)
            last = preview
            if not preview.get('canPlace'):
                continue
            if preview.get('madeFromStuff') and not stuff:
                raise Blocked('material_choice_required', 'Specify acceptable observed materials; no implicit native default material')
            if preview.get('materials', {}).get('canBuildNow') is not True:
                continue
            occupied = {(c['x'], c['z']) for r in preview['rotations'] for c in r.get('occupiedCells', [])}
            reserved = {c for r in rt.current_plan.spec.reserved_walkways for c in r.cells()}
            if occupied & reserved:
                raise Blocked('reserved_walkway', 'Building footprint crosses a reserved walkway')
            zones = await rt.game.query('home/list_zones', x=p.x, z=p.z, radius=8, includeCells=True, maxCellsPerZone=10000)
            for zone in zones['zones']:
                cells = zone.get('gridCells')
                if cells is None or len(cells) != zone['gridCellCount']:
                    raise Blocked('incomplete_zone_geometry', 'Cannot prove that construction avoids existing zones')
                if occupied & {(c['x'], c['z']) for c in cells}:
                    raise Blocked('existing_zone', 'Building footprint overlaps an existing zone', evidence={'zone': zone['label']})
            self.guard(rt, revision, token, direction)
            progress.issued[key] = {'confirmed': False}
            rt.persist()
            result = await rt.native('home/place_building', dict(args, dryRun=False), expected_revision=direction,
                expected_token=token, expected_plan_revision=revision, reconcile=False)
            receipt = result['receipt']
            if receipt.get('outcome') not in ('placed', 'already_present'):
                raise Blocked('placement_refused', reason(receipt), evidence=receipt)
            return {'stuff': stuff, 'outcome': receipt['outcome']}
        raise Blocked('construction_unavailable', 'No acceptable material/placement is currently buildable', retryable=True,
            evidence={k:last.get(k) for k in ('materials','rotations','researchFinished','buildableByPlayer')} if last else {})

    async def zone(self, rt, action, progress, key, revision, token, direction):
        cells = sorted({c for patch in action.patches for c in patch.cells()})
        zones = await rt.game.query('home/list_zones', match=action.label, includeCells=True, maxCellsPerZone=10000)
        matches = [z for z in zones['zones'] if z['label']==action.label]
        if matches:
            if len(matches)!=1 or {(c['x'],c['z']) for c in matches[0].get('gridCells', [])} != set(cells):
                raise Blocked('zone_conflict', 'Existing zone label has different or incomplete geometry; no duplicate created')
        else:
            args = dict(op='create', zoneType=action.zone_type, label=action.label,
                cells=';'.join(f'{x},{z}' for x,z in cells), priority=action.priority)
            if action.preset:
                args['preset'] = action.preset
            preview = await rt.inspect_native('home/zone_cells', dict(args, dryRun=True))
            if preview.get('cellsAccepted') != len(cells):
                raise Blocked('zone_cells_refused', 'Not all proposed zone cells are legal', evidence=preview)
            self.guard(rt, revision, token, direction)
            progress.issued[key] = {'confirmed': False}
            rt.persist()
            result = await rt.native('home/zone_cells', dict(args, dryRun=False), expected_revision=direction,
                expected_token=token, expected_plan_revision=revision, reconcile=False)
            if result['receipt'].get('cellsAccepted') != len(cells):
                raise Blocked('zone_partial', 'Zone was only partially created; inspect before revising', evidence=result['receipt'])
        if action.crop:
            self.guard(rt, revision, token, direction)
            await rt.native('home/zone_cells', dict(op='crop', zone=action.label, plant=action.crop, dryRun=False),
                expected_revision=direction, expected_token=token, expected_plan_revision=revision, reconcile=False)
        observed = await rt.game.query('home/list_zones', match=action.label, includeCells=True, maxCellsPerZone=10000)
        zone = next((z for z in observed['zones'] if z['label']==action.label), None)
        if zone is None or {(c['x'], c['z']) for c in zone.get('gridCells', [])} != set(cells):
            raise Blocked('zone_readback', 'Zone geometry did not match its committed intent')
        row = rt.projects.upsert({'title': action.label, 'targets':[{'kind':'zone','zone_id':str(zone['id'])}]})
        progress.project_id = row.id
        return {'zone': action.label, 'cells': len(cells), 'crop': action.crop}
