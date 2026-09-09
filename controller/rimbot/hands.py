"""Deterministic intent compilation and native validation. No model dependency."""
import time
from .colony_plan import Buildings, RoomShell, Zone, NativeOperation, ClockAction, StandDown, Placement, Failure, TradeAction
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


class GeometryConflict(ValueError):
    def __init__(self, step, other, point):
        self.evidence = {'step_id': step, 'conflicts_with': other,
                         'cell': {'x': point[0], 'z': point[1]}}
        super().__init__(f'{step} overlaps {other} at x={point[0]}, z={point[1]}; '
                         'adjust the conflicting footprint or reserved walkway')


def validate_geometry(spec):
    reserved = {c for r in spec.reserved_walkways for c in r.cells()}
    claimed = {}
    for step in spec.steps:
        action = step.action
        if isinstance(action, RoomShell):
            points = [(p.x,p.z) for p in room_placements(action)]
        elif isinstance(action, Buildings):
            points = [(p.x,p.z) for p in action.placements]
        elif isinstance(action, Zone):
            points = sorted({c for patch in action.patches for c in patch.cells()})
        else:
            continue
        for point in points:
            if point in reserved:
                raise GeometryConflict(step.id, 'reserved walkway', point)
            if point in claimed and claimed[point] != step.id:
                raise GeometryConflict(step.id, claimed[point], point)
            claimed[point] = step.id


class Blocked(Exception):
    def __init__(self, code, detail, *, retryable=False, evidence=None):
        self.failure = Failure(code=code, detail=detail, retryable=retryable, evidence=evidence or {})


class Hands:
    async def advance(self, rt, max_operations=12, only_ids=None):
        """Bound each controller pass to yield to UI, not to spend model calls."""
        revision, token, direction = rt.current_plan.revision, rt.context_token, rt.chat_revision
        count = 0
        for step in list(rt.current_plan.ready()):
            if only_ids is not None and (step.id not in only_ids or step.source != 'PLAYER'): continue
            progress = rt.current_plan.progress[step.id]
            try:
                self.guard(rt, revision, token, direction)
                action = step.action
                placements = room_placements(action) if isinstance(action, RoomShell) else action.placements if isinstance(action, Buildings) else None
                if isinstance(action, RoomShell):
                    # Check the entire remaining shell before this pass can write any piece.
                    # Per-piece validation still runs immediately before each write.
                    for index, placement in enumerate(placements):
                        self.guard(rt, revision, token, direction)
                        await self.place(rt, placement, progress, str(index), revision, token, direction, preview_only=True)
                    self.guard(rt, revision, token, direction)
                operations = placements if placements is not None else [action]
                for index, operation in enumerate(operations):
                    self.guard(rt, revision, token, direction)
                    key = str(index)
                    recorded = progress.issued.get(key)
                    if recorded and recorded.get('confirmed'):
                        continue
                    if recorded and not placements and not isinstance(action, (Zone, StandDown)):
                        raise Blocked('uncertain_write', 'Prior write has no confirmed receipt. Inspect its effects before replacing this step.')
                    if count >= max_operations:
                        return
                    progress.state = 'executing'
                    if placements is not None:
                        receipt = await self.place(rt, operation, progress, key, revision, token, direction)
                    elif isinstance(action, Zone):
                        receipt = await self.zone(rt, action, progress, key, revision, token, direction)
                    elif isinstance(action, StandDown):
                        progress.issued[key] = {'confirmed': False}
                        rt.persist()
                        receipt = await rt.stand_down(action.pawn_ids, expected_revision=direction,
                            expected_token=token, expected_plan_revision=revision)
                        if receipt['failed']:
                            raise Blocked('stand_down_incomplete', 'Some AI-owned drafts could not be released.',
                                retryable=True, evidence=receipt)
                    elif isinstance(action, TradeAction):
                        from .trading import execute_trade
                        progress.issued[key] = {'confirmed': False}
                        rt.persist()
                        async def read_trade(args):
                            self.guard(rt, revision, token, direction)
                            result = await rt.inspect_native('home/trade', args)
                            self.guard(rt, revision, token, direction)
                            return result
                        async def write_trade(args):
                            self.guard(rt, revision, token, direction)
                            result = await rt.native('home/trade', args, expected_revision=direction,
                                expected_token=token, expected_plan_revision=revision, reconcile=False)
                            return result['receipt']
                        receipt = await execute_trade(action, read_trade, write_trade)
                    else:
                        if isinstance(action, NativeOperation):
                            if action.tool == 'home/bills' and action.arguments.get('action') == 'add' and any(
                                    p.get('spending') != 'normal' or p.get('reserve',0)
                                    for p in rt.current_plan.control.get('resource_policy',{}).values()):
                                raise Blocked('resource_policy', 'Bill requires verified ingredient accounting under the current resource policy')
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
                            if action.completion in ('patient_tended', 'patient_in_bed') and result.get('receipt', {}).get('job', {}).get('verified') is not True:
                                raise Blocked('medical_order_unverified', 'Native state did not confirm the medical job.', evidence=result)
                            receipt = {'native_outcome': result.get('receipt', result).get('outcome', 'receipt'),
                                'meaning': 'Native command observed; this does not certify completion of pawn labor'}
                            if action.tool == 'home/install':
                                native = result.get('receipt', result)
                                receipt['inner_id'] = native['thingId']
                                receipt['rotation'] = (native.get('blueprint') or {}).get('rotation', native.get('rotation'))
                            if action.completion != 'native_receipt':
                                receipt['issued_at'] = time.time()
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
                    await rt.projects.reconcile(rt.game, only_id=row.id)
                    self.guard(rt, revision, token, direction)
                    if row.state == 'complete':
                        progress.state = 'complete'
                elif isinstance(action, NativeOperation) and action.tool == 'home/install':
                    installation = next(iter(progress.issued.values()))
                    row = rt.projects.upsert({'title': step.title, 'detail': step.completion_criteria,
                        'targets': [{'kind': 'installation', 'thing_id': installation['inner_id'],
                            'x': action.arguments['x'], 'z': action.arguments['z'], 'rotation': installation['rotation']}]})
                    progress.project_id, progress.state = row.id, 'waiting'
                    await rt.projects.reconcile(rt.game, only_id=row.id)
                    if row.state == 'complete':
                        progress.state = 'complete'
                elif isinstance(action, NativeOperation) and action.completion != 'native_receipt':
                    progress.state = 'waiting'
                else:
                    progress.state = 'complete'
                rt.note('execution', step.title+(': orders issued; awaiting construction' if placements and progress.state != 'complete' else
                    ': order issued; awaiting native completion' if progress.state == 'waiting' else ': native operation verified'))
                goal = rt.current_plan.colony_goals.get(step.goal_id) if step.goal_id else None
                if not getattr(rt,'manual_execution',None) and progress.state == 'complete' and (goal is None or all(
                        rt.current_plan.progress[s].state in ('complete', 'blocked', 'cancelled')
                        for s in goal.steps if s in rt.current_plan.progress)):
                    rt.signal('plan.method_finished', {'goal': step.goal_id, 'step': step.id})
                if not getattr(rt,'manual_execution',None) and all(rt.current_plan.progress[s.id].state in ('complete', 'cancelled') for s in rt.current_plan.spec.steps):
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
        explicit = getattr(rt,'manual_execution',None) == (token,direction,revision)
        if ((rt.mode != 'automate' and not explicit) or rt.context_token != token or rt.current_plan.revision != revision
                or rt.chat_revision != direction or rt.chat_revision > rt.handled_revision):
            raise InterruptedError('Plan or player direction changed')

    async def place(self, rt, p, progress, key, revision, token, direction, *, preview_only=False):
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
            from .resource_accounting import validate_execution_costs
            validate_execution_costs(rt.current_plan, progress, key, preview)
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
            if preview_only:
                return {'validated': True, 'stuff': stuff}
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
            if action.crop:
                args['plant'] = action.crop
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
        if action.crop and matches:
            self.guard(rt, revision, token, direction)
            await rt.native('home/zone_cells', dict(op='crop', zone=action.label, plant=action.crop, dryRun=False),
                expected_revision=direction, expected_token=token, expected_plan_revision=revision, reconcile=False)
        observed = await rt.game.query('home/list_zones', match=action.label, includeCells=True, maxCellsPerZone=10000)
        zone = next((z for z in observed['zones'] if z['label']==action.label), None)
        if zone is None or {(c['x'], c['z']) for c in zone.get('gridCells', [])} != set(cells):
            raise Blocked('zone_readback', 'Zone geometry did not match its committed intent')
        if action.crop and zone.get('plantDef') != action.crop:
            raise Blocked('crop_readback', 'Growing zone exists but its observed crop does not match the requested crop')
        row = rt.projects.upsert({'title': action.label, 'targets':[{'kind':'zone','zone_id':str(zone['id'])}]})
        progress.project_id = row.id
        return {'zone': action.label, 'cells': len(cells), 'crop': action.crop}
