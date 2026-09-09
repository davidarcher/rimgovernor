"""Deterministic intent compilation and native validation. No model dependency."""
import time
from .colony_plan import Buildings, RoomShell, Zone, NativeOperation, ClockAction, StandDown, Placement, Failure, TradeAction, CancelConstructionAction
from .receipts import reason
from .spatial import room_placements, GeometryConflict, validate_geometry, native_footprint
from .shell_site import ShellSiteRefusal, ZONE_ARGUMENTS, validate_shell_zones, validate_shell_access, validate_shell_connectivity


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
                    async def read_connectivity(name, args):
                        self.guard(rt, revision, token, direction)
                        result = await rt.game.query(name, **args)
                        self.guard(rt, revision, token, direction)
                        return result
                    try:
                        await validate_shell_connectivity(step.id, action, read_connectivity, rt.current_plan.spec)
                    except ShellSiteRefusal as error:
                        raise Blocked(error.code, str(error), evidence=error.evidence) from error
                operations = placements if placements is not None else action.targets if isinstance(action, CancelConstructionAction) else [action]
                for index, operation in enumerate(operations):
                    self.guard(rt, revision, token, direction)
                    key = str(index)
                    recorded = progress.issued.get(key)
                    if recorded and recorded.get('confirmed'):
                        continue
                    if recorded and not placements and not isinstance(action, (Zone, StandDown, CancelConstructionAction)):
                        raise Blocked('uncertain_write', 'Prior write has no confirmed receipt. Inspect its effects before replacing this step.')
                    if count >= max_operations:
                        return
                    progress.state = 'executing'
                    if placements is not None:
                        receipt = await self.place(rt, operation, progress, key, revision, token, direction)
                    elif isinstance(action, CancelConstructionAction):
                        from .construction_cancellation import execute_target
                        receipt = await execute_target(self, rt, action, operation, progress, key, revision, token, direction)
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
                            if step.source=='AUTOPILOT' and step.goal_id=='ActiveCombat':
                                status=(await rt.game.query('home/status',colonists=False,threats=True)).get('threats',{})
                                current={p['thingId'] for p in status.get('hostiles',[]) if p.get('downed') is False}
                                current.update(p['thingId'] for p in status.get('huntingPredators',[])
                                    if p.get('preyIsOurs') is True and p.get('predatorIsOurs') is False)
                                target=args.get('target') or rt.current_plan.control.get('combat',{}).get('target')
                                if target not in current:
                                    raise Blocked('threat_changed','The accepted threat is no longer confirmed; no combat order sent')
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
                                expected_plan_revision=revision, reconcile=False, expected_step_id=step.id)
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
                                receipt['load_token'] = token
                                receipt['issued_tick'] = rt.batch.summary.end_tick
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
                        'expected_facing': p.rotation,
                        'stuff': progress.issued[str(i)].get('stuff') or ''} for i,p in enumerate(placements)]
                    row = rt.projects.upsert({'title': step.title, 'detail': step.completion_criteria,
                        'source_step':step.id, 'targets': targets})
                    progress.project_id, progress.state = row.id, 'waiting'
                    await rt.projects.reconcile(rt.game, only_id=row.id)
                    self.guard(rt, revision, token, direction)
                    if row.state == 'complete':
                        progress.state = 'complete'
                elif isinstance(action, NativeOperation) and action.tool == 'home/install':
                    installation = next(iter(progress.issued.values()))
                    row = rt.projects.upsert({'title': step.title, 'detail': step.completion_criteria, 'source_step':step.id,
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
                from .hunting import HuntingRefused
                if isinstance(error,HuntingRefused):
                    progress.issued.pop(key,None)
                    failure=Failure(code='hunting_precondition',detail=str(error),retryable=False)
                elif isinstance(error, Blocked):
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
            from .resource_accounting import ResourceShortage, validate_execution_costs
            try:
                validate_execution_costs(rt.current_plan, progress, key, preview)
            except ResourceShortage as error:
                step=next(s for s in rt.current_plan.spec.steps if rt.current_plan.progress[s.id] is progress)
                raise Blocked('construction_resources',str(error),retryable=True,evidence=dict(error.evidence,
                    slot=key,load_token=token,direction=direction,signature=step.signature(),
                    tick=rt.batch.summary.end_tick)) from error
            try:
                occupied = native_footprint(preview, p)
                owner = next((s for s in rt.current_plan.spec.steps
                              if rt.current_plan.progress.get(s.id) is progress), None)
                if owner is not None:
                    validate_geometry(rt.current_plan.spec, {(owner.id, key): occupied})
            except GeometryConflict as error:
                raise Blocked('spatial_conflict', str(error), evidence=error.evidence) from error
            except ValueError as error:
                raise Blocked('incomplete_building_geometry', str(error)) from error
            reserved = {c for r in rt.current_plan.spec.reserved_walkways for c in r.cells()}
            if occupied & reserved:
                raise Blocked('reserved_walkway', 'Building footprint crosses a reserved walkway')
            shell = owner.action if owner is not None and isinstance(owner.action, RoomShell) else None
            if shell is not None:
                zones = await rt.game.query('home/list_zones', **ZONE_ARGUMENTS)
                try:
                    validate_shell_zones(owner.id, shell, zones)
                    async def read(name, args):
                        self.guard(rt, revision, token, direction)
                        result = await rt.game.query(name, **args)
                        self.guard(rt, revision, token, direction)
                        return result
                    await validate_shell_access(owner.id, shell, read)
                except ShellSiteRefusal as error:
                    raise Blocked(error.code, str(error), evidence=error.evidence) from error
            else:
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
        row = rt.projects.upsert({'title': action.label, 'targets':[{'kind':'zone','zone_id':str(zone['id']),
            'zone_patches': [patch.model_dump() for patch in action.patches],
            'zone_type': action.zone_type, 'crop': action.crop or None}]})
        progress.project_id = row.id
        return {'zone': action.label, 'cells': len(cells), 'crop': action.crop}
