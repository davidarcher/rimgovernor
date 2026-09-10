"""Deterministic intent compilation and native validation. No model dependency."""
import time
from .colony_plan import Buildings, RoomShell, Zone, NativeOperation, ClockAction, StandDown, Placement, Failure, TradeAction, CancelConstructionAction
from .receipts import reason
from .spatial import room_placements, GeometryConflict, validate_geometry, native_footprint
from .shell_site import ShellSiteRefusal, ZONE_ARGUMENTS, validate_shell_zones, validate_shell_access
from .projects import zone_settings
from .native_contracts import NativeNotDispatched


class Blocked(Exception):
    def __init__(self, code, detail, *, retryable=False, evidence=None):
        self.failure = Failure(code=code, detail=detail, retryable=retryable, evidence=evidence or {})


class Hands:
    async def advance(self, rt, max_operations=12, only_ids=None):
        """Bound each controller pass to yield to UI, not to spend model calls."""
        revision, token, direction = rt.current_plan.revision, rt.context_token, rt.chat_revision
        count = 0
        for step in list(rt.current_plan.ready()):
            intent = None
            if only_ids is not None and (step.id not in only_ids or step.source != 'PLAYER'): continue
            progress = rt.current_plan.progress[step.id]
            try:
                self.guard(rt, revision, token, direction)
                action = step.action
                placements = room_placements(action) if isinstance(action, RoomShell) else action.placements if isinstance(action, Buildings) else None
                spatial_checked=False
                async def before_placement_write():
                    nonlocal spatial_checked
                    if spatial_checked:return
                    from types import SimpleNamespace
                    from .construction_preflight import preflight_construction
                    async def spatial_read(name, args, **kwargs):
                        self.guard(rt, revision, token, direction)
                        result = (await rt.inspect_native(name, args) if name in ('home/place_building', 'home/placement_previews')
                                  else await rt.game.query(name, **args))
                        self.guard(rt, revision, token, direction)
                        return result
                    try:
                        await preflight_construction(rt.current_plan.spec, rt.current_plan,
                            SimpleNamespace(invoke=spatial_read, bridge=getattr(rt.game, 'bridge', None)), refresh=True)
                    except ValueError as error:
                        raise Blocked(getattr(error, 'code', 'spatial_preflight'), str(error),
                            evidence=getattr(error, 'evidence', {})) from error
                    spatial_checked=True
                if isinstance(action, RoomShell):
                    # Check the entire remaining shell before this pass can write any piece.
                    # Per-piece validation still runs immediately before each write.
                    await self.preflight_shell(rt, placements, progress, revision, token, direction)
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
                        receipt = await self.place(rt, operation, progress, key, revision, token, direction,
                                                   before_write=before_placement_write)
                    elif isinstance(action, CancelConstructionAction):
                        from .construction_cancellation import execute_target
                        receipt = await execute_target(self, rt, action, operation, progress, key, revision, token, direction)
                    elif isinstance(action, Zone):
                        receipt = await self.zone(rt, action, progress, key, revision, token, direction)
                    elif isinstance(action, StandDown):
                        progress.issued[key] = {'confirmed': False}
                        rt.persist()
                        receipt = await rt.stand_down(action.pawn_ids, expected_revision=direction,
                            expected_token=token, expected_plan_revision=revision,
                            **({'require_clear_threats': True} if step.goal_id == 'CriticalMedical' else {}))
                        if receipt['failed']:
                            raise Blocked('stand_down_incomplete', 'Some AI-owned drafts could not be released.',
                                retryable=True, evidence=receipt)
                    elif isinstance(action, TradeAction):
                        from .trading import execute_trade
                        floors, stopped = {}, []
                        if action.policy is not None:
                            from .trade_policy import economic_reserves
                            buildings = await rt.inspect_native('home/list_buildings', {'aggregate': False, 'playerOnly': True})
                            self.guard(rt, revision, token, direction)
                            floors, stopped = economic_reserves(rt.current_plan, buildings, action.policy)
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
                                expected_token=token, expected_plan_revision=revision, reconcile=False, expected_step_id=step.id)
                            return result['receipt']
                        receipt = await execute_trade(action, read_trade, write_trade, floors=floors, stopped=stopped)
                    else:
                        if isinstance(action, NativeOperation):
                            args = dict(action.arguments)
                            if action.tool == 'home/upkeep_home':
                                from .home_coverage import guard as home_guard
                                await home_guard(rt, step)
                                self.guard(rt, revision, token, direction)
                            if action.tool == 'home/upkeep_wall':
                                from .wall_upgrade import arguments as wall_arguments
                                args = await wall_arguments(rt, step)
                                self.guard(rt, revision, token, direction)
                            if step.goal_id and step.goal_id.startswith('Population-'):
                                from .population import guard, SkillBlocked
                                try:
                                    await guard(rt, step.goal_id)
                                except SkillBlocked as error:
                                    raise Blocked('population_admission', str(error)) from error
                                self.guard(rt, revision, token, direction)
                            if action.tool == 'home/production_policy':
                                from .production_policy import policy_arguments
                                args = policy_arguments(rt)
                            if step.source=='AUTOPILOT' and step.goal_id=='ActiveCombat':
                                args['requireCombatHealth'] = True
                                from .combat_health import combat_health_hold
                                health = await rt.game.query('home/list_pawns', colonistsOnly=True, health=True)
                                hold = combat_health_hold(health)
                                if hold:
                                    raise Blocked('combat_health_hold', hold)
                                status=(await rt.game.query('home/status',colonists=False,threats=True)).get('threats',{})
                                current={p['thingId'] for p in status.get('hostiles',[]) if p.get('downed') is False}
                                current.update(p['thingId'] for p in status.get('huntingPredators',[])
                                    if p.get('preyIsOurs') is True and p.get('predatorIsOurs') is False)
                                target=(args.get('target') if args.get('action')=='attack' else
                                    rt.current_plan.control.get('combat',{}).get('target'))
                                if target not in current:
                                    raise Blocked('threat_changed','The accepted threat is no longer confirmed; no combat order sent')
                            schema = await rt.game.describe(action.tool)
                            if 'dryRun' in schema.get('properties', {}):
                                preview = await rt.inspect_native(action.tool, dict(args, dryRun=True))
                                if preview.get('success') is False or (action.tool in ('home/upkeep_home', 'home/upkeep_wall', 'home/manage_waste', 'home/recover_service', 'home/recovery_area') and preview.get('accepted') is not True):
                                    raise Blocked('native_refused', reason(preview), evidence=preview)
                                if action.completion == 'surgery_health':
                                    from .surgery import effect_from_preview
                                    if step.source != 'PLAYER' or effect_from_preview(preview) != action.medical_effect:
                                        raise Blocked('surgery_direction_required', 'Surgery requires an unchanged explicitly directed patient operation')
                                args['dryRun'] = False
                            self.guard(rt, revision, token, direction)
                            player_direction = rt.current_plan.control.get('player_direction', 0)
                            intent = {'confirmed': False}
                            progress.issued[key] = intent
                            if action.completion in ('pawn_gear','pawn_equipped'):
                                progress.issued[key].update(issued_at=time.time(), load_token=token,
                                    player_direction=player_direction, issued_tick=rt.batch.summary.end_tick)
                            if action.completion == 'need_recovered':
                                progress.issued[key].update(issued_at=time.time(), load_token=token,
                                    player_direction=player_direction)
                            if step.goal_id and step.goal_id.startswith('Population-'):
                                progress.issued[key].update(load_token=token, issued_at=time.time())
                            if action.tool in ('home/caravan', 'home/fulfill_quest'):
                                progress.issued[key]['issued_tick'] = rt.batch.summary.end_tick
                            rt.persist()
                            result = await rt.native(action.tool, args, expected_revision=direction, expected_token=token,
                                expected_plan_revision=revision, reconcile=False, expected_step_id=step.id)
                            if action.tool == 'home/upkeep_home' and (result.get('receipt', result).get('accepted') is not True
                                    or result.get('receipt', result).get('covered') is not True):
                                raise Blocked('home_coverage_unverified', 'Native Home coverage was not observed.', evidence=result)
                            if step.goal_id and step.goal_id.startswith('Population-'):
                                outcome = result.get('receipt', result)
                                if outcome.get('success') is not True or (action.tool == 'home/order'
                                        and (outcome.get('job') or {}).get('verified') is not True):
                                    raise Blocked('population_order_unverified', reason(outcome), evidence=outcome)
                            if action.completion in ('patient_tended', 'patient_in_bed') and result.get('receipt', {}).get('job', {}).get('verified') is not True:
                                raise Blocked('medical_order_unverified', 'Native state did not confirm the medical job.', evidence=result)
                            if action.completion == 'upkeep_target' and result.get('receipt', {}).get('job', {}).get('verified') is not True:
                                raise Blocked('upkeep_order_unverified', 'Native state did not confirm the upkeep job.', evidence=result)
                            if action.tool == 'home/upkeep_wall' and result.get('receipt', result).get('accepted') is not True:
                                raise Blocked('wall_removal_unverified', 'Native wall demolition was not confirmed.', evidence=result)
                            receipt = {'native_outcome': result.get('receipt', result).get('outcome', 'receipt'),
                                'meaning': 'Native command observed; this does not certify completion of pawn labor'}
                            if action.tool == 'home/recover_service':
                                receipt['recovery_receipt'] = result.get('receipt', result)
                            if action.tool == 'home/manage_waste':
                                receipt['waste_receipt'] = result.get('receipt', result)
                            if action.tool == 'home/husbandry_config':
                                outcome = result.get('receipt', result)
                                self.guard(rt, revision, token, direction)
                                if outcome.get('success') is not True or not outcome.get('after'):
                                    raise Blocked('husbandry_unconfirmed', 'Animal setting write is unconfirmed; observe before retrying', evidence=result)
                                goal = rt.current_plan.colony_goals[step.goal_id]
                                goal.evidence['settings'][args['animal']] = outcome['after']
                                receipt['animal_settings'] = outcome
                            if action.completion == 'surgery_health':
                                receipt['bill_id'] = result.get('receipt', result).get('billId')
                                if not receipt['bill_id']:
                                    raise Blocked('surgery_uncertain', 'Operation bill identity was not confirmed; observe before retrying', evidence=result)
                            if action.tool == 'home/order':
                                receipt['order_generation'] = result.get('receipt', result).get('orderGeneration')
                                receipt['haul_tracking_id'] = result.get('receipt', result).get('haulTrackingId')
                            if action.tool == 'home/upkeep_wall':
                                outcome = result.get('receipt', result)
                                receipt['wall_removal_id'] = outcome.get('removalId')
                                receipt['wall_target'] = outcome.get('target')
                                if not receipt['wall_removal_id'] or receipt['wall_target'] != args['target']:
                                    raise Blocked('wall_removal_unverified', 'Native demolition identity was not confirmed.', evidence=result)
                            if action.tool == 'home/install':
                                native = result.get('receipt', result)
                                receipt['inner_id'] = native['thingId']
                                receipt['rotation'] = (native.get('blueprint') or {}).get('rotation', native.get('rotation'))
                            if action.completion != 'native_receipt':
                                receipt['issued_at'] = time.time()
                                receipt['load_token'] = token
                                receipt['issued_tick'] = rt.batch.summary.end_tick
                                receipt['player_direction'] = player_direction
                                outcome = result.get('receipt', result)
                                receipt['order_generation'] = outcome.get('orderGeneration')
                                receipt['patient_order_generation'] = outcome.get('targetOrderGeneration')
                                if action.tool in ('home/caravan', 'home/fulfill_quest'):
                                    receipt['issued_tick'] = result.get('receipt', result).get('observation', {}).get('ticksGame', receipt['issued_tick'])
                        else:
                            self.guard(rt, revision, token, direction)
                            intent = {'confirmed': False}
                            progress.issued[key] = intent
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
            except NativeNotDispatched:
                if intent is not None and progress.issued.get(key) is intent:
                    progress.issued.pop(key)
                    progress.state = 'pending'
                    rt.persist()
                return
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
                    if (step.source=='AUTOPILOT' and step.goal_id=='AllowStartingSupplies'
                            and isinstance(action,NativeOperation) and action.tool=='rimworld/apply_architect_designator'
                            and payload.get('dryRun') is True and payload.get('acceptedCellCount')==0
                            and payload.get('appliedCellCount')==0
                            and payload.get('designator',{}).get('className')=='RimWorld.Designator_Unforbid'):
                        failure=Failure(code='starting_supplies_unavailable',detail='Starter stock allow preview no longer applies.',
                            retryable=True,evidence={'native':payload,'load_token':token,'signature':step.signature(),
                                'player_direction':rt.current_plan.control.get('player_direction',0),
                                'tick':rt.batch.summary.end_tick})
                progress.state, progress.failure = 'blocked', failure
                rt.note('execution_blocked', step.title+': '+failure.detail, failure=failure.model_dump())
                rt.persist()
                rt.signal('plan.step_failed', {'step': step.id, 'failure': failure.model_dump()})

    @staticmethod
    def guard(rt, revision, token, direction, *, reviewing=False):
        explicit = getattr(rt,'manual_execution',None) == (token,direction,revision)
        if ((rt.mode != 'automate' and not explicit) or rt.context_token != token or rt.current_plan.revision != revision
                or rt.chat_revision != direction or (not reviewing and rt.chat_revision > rt.handled_revision)):
            raise InterruptedError('Plan or player direction changed')

    async def preflight_shell(self, rt, placements, progress, revision, token, direction, *, coalesce=True):
        """Share independent reads only until this read-only shell pass ends."""
        from .placement_previews import PlacementPreviews
        observed = {}
        async def read(name, args):
            self.guard(rt, revision, token, direction)
            key = (name, tuple(sorted(args.items())))
            if key not in observed:
                observed[key] = await rt.game.query(name, **args)
            self.guard(rt, revision, token, direction)
            return observed[key]
        previews = PlacementPreviews(rt.game, placements, inspect=rt.inspect_native) if coalesce else None
        results = []
        for index, placement in enumerate(placements):
            self.guard(rt, revision, token, direction)
            results.append(await self.place(rt, placement, progress, str(index), revision, token, direction,
                preview_only=True, site_read=read if coalesce else None, previews=previews))
            self.guard(rt, revision, token, direction)
        return results

    async def place(self, rt, p, progress, key, revision, token, direction, *, preview_only=False, writer_locked=False, before_write=None, site_read=None, previews=None):
        if writer_locked and not preview_only:raise ValueError('Locked construction preflight cannot write')
        if not preview_only and (site_read is not None or previews is not None):
            raise ValueError('Shared construction observations cannot authorize writes')
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
            preview = (await previews.get(p, stuff) if previews is not None else
                await rt.game.invoke('home/place_building',args,allow_write=False) if writer_locked
                else await rt.inspect_native('home/place_building', args))
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
                    player_direction=rt.current_plan.control.get('player_direction',0),tick=rt.batch.summary.end_tick)) from error
            try:
                occupied = native_footprint(preview, p)
                owner = next((s for s in rt.current_plan.spec.steps
                              if rt.current_plan.progress.get(s.id) is progress), None)
                if owner is not None:
                    validate_geometry(rt.current_plan.spec, {(owner.id, key): occupied}, current=rt.current_plan)
            except GeometryConflict as error:
                raise Blocked('spatial_conflict', str(error), evidence=error.evidence) from error
            except ValueError as error:
                raise Blocked('incomplete_building_geometry', str(error)) from error
            reserved = {c for r in rt.current_plan.spec.reserved_walkways for c in r.cells()}
            if occupied & reserved:
                raise Blocked('reserved_walkway', 'Building footprint crosses a reserved walkway')
            shell = owner.action if owner is not None and isinstance(owner.action, RoomShell) else None
            if shell is not None:
                zones = (await site_read('home/list_zones', ZONE_ARGUMENTS) if site_read else
                         await rt.game.query('home/list_zones', **ZONE_ARGUMENTS))
                try:
                    validate_shell_zones(owner.id, shell, zones)
                    async def read(name, args):
                        self.guard(rt, revision, token, direction,reviewing=writer_locked)
                        result = await site_read(name, args) if site_read else await rt.game.query(name, **args)
                        self.guard(rt, revision, token, direction,reviewing=writer_locked)
                        return result
                    await validate_shell_access(owner.id, shell, read)
                except ShellSiteRefusal as error:
                    raise Blocked(error.code, str(error), evidence=error.evidence) from error
            # Zones can legally coexist with furniture; native placement owns
            # compatibility. Zone edits retain their separate exact-cell guards.
            self.guard(rt, revision, token, direction,reviewing=writer_locked)
            if preview_only:
                return {'validated': True, 'stuff': stuff}
            if before_write is not None:
                await before_write()
                self.guard(rt, revision, token, direction)
            progress.issued[key] = {'confirmed': False}
            rt.persist()
            result = await rt.native('home/place_building', dict(args, dryRun=False), expected_revision=direction,
                expected_token=token, expected_plan_revision=revision, reconcile=False,
                expected_step_id=owner.id if owner else None)
            receipt = result['receipt']
            if receipt.get('outcome') not in ('placed', 'already_present'):
                raise Blocked('placement_refused', reason(receipt), evidence=receipt)
            return {'stuff': stuff, 'outcome': receipt['outcome'],
                    'placed_thing_id': (receipt.get('placed') or {}).get('thingId')}
        step=next(s for s in rt.current_plan.spec.steps if rt.current_plan.progress[s.id] is progress)
        evidence={k:last.get(k) for k in ('materials','rotations','researchFinished','buildableByPlayer')} if last else {}
        evidence.update(slot=key,load_token=token,direction=direction,signature=step.signature(),
            player_direction=rt.current_plan.control.get('player_direction',0),
            tick=getattr(getattr(rt.batch,'summary',None),'end_tick',None),prewrite=True)
        raise Blocked('construction_unavailable', 'No acceptable material/placement is currently buildable', retryable=True,
            evidence=evidence)

    async def zone(self, rt, action, progress, key, revision, token, direction):
        cells = sorted({c for patch in action.patches for c in patch.cells()})
        zones = await rt.game.query('home/list_zones', match=action.label, includeCells=True, maxCellsPerZone=10000, filter=True)
        matches = [z for z in zones['zones'] if z['label']==action.label]
        expected_settings = dict(allowSow=True, allowCut=True) if action.zone_type == 'growing' else None
        if matches:
            if len(matches)!=1 or {(c['x'],c['z']) for c in matches[0].get('gridCells', [])} != set(cells):
                raise Blocked('zone_conflict', 'Existing zone label has different or incomplete geometry; no duplicate created')
            if action.zone_type == 'stockpile':
                preview_args = dict(op='filter', zone=str(matches[0]['id']), priority=action.priority, dryRun=True)
                if action.preset:
                    preview_args['preset'] = action.preset
                if action.allow:
                    preview_args['allow'] = ','.join(action.allow)
                preview = await rt.inspect_native('home/zone_cells', preview_args)
                expected_settings = zone_settings(dict(type='Zone_Stockpile', priority=action.priority, filter=preview.get('after')))
            if zone_settings(matches[0]) != expected_settings or (action.crop and matches[0].get('plantDef') != action.crop):
                raise Blocked('zone_conflict', 'Existing zone settings differ from the requested intent; explicit editing is required')
        else:
            args = dict(op='create', zoneType=action.zone_type, label=action.label,
                cells=';'.join(f'{x},{z}' for x,z in cells), priority=action.priority)
            if action.preset:
                args['preset'] = action.preset
            if action.allow:
                args['allow'] = ','.join(action.allow)
            if action.covered_empty:
                args['requireCoveredEmpty'] = True
            if action.crop:
                args['plant'] = action.crop
            preview = await rt.inspect_native('home/zone_cells', dict(args, dryRun=True))
            if preview.get('cellsAccepted') != len(cells):
                raise Blocked('zone_cells_refused', 'Not all proposed zone cells are legal', evidence=preview)
            if action.zone_type == 'stockpile':
                expected_settings = zone_settings(dict(type='Zone_Stockpile', priority=action.priority, filter=preview.get('filter')))
            self.guard(rt, revision, token, direction)
            progress.issued[key] = {'confirmed': False}
            rt.persist()
            owner=next((s for s in rt.current_plan.spec.steps if rt.current_plan.progress.get(s.id) is progress),None)
            result = await rt.native('home/zone_cells', dict(args, dryRun=False), expected_revision=direction,
                expected_token=token, expected_plan_revision=revision, reconcile=False,
                expected_step_id=owner.id if owner else None)
            if result['receipt'].get('cellsAccepted') != len(cells):
                raise Blocked('zone_partial', 'Zone was only partially created; inspect before revising', evidence=result['receipt'])
        observed = await rt.game.query('home/list_zones', match=action.label, includeCells=True, maxCellsPerZone=10000, filter=True)
        zone = next((z for z in observed['zones'] if z['label']==action.label), None)
        if zone is None or {(c['x'], c['z']) for c in zone.get('gridCells', [])} != set(cells):
            raise Blocked('zone_readback', 'Zone geometry did not match its committed intent')
        if action.crop and zone.get('plantDef') != action.crop:
            raise Blocked('crop_readback', 'Growing zone exists but its observed crop does not match the requested crop')
        if zone_settings(zone) != expected_settings:
            raise Blocked('zone_settings_readback', 'Zone settings do not match the native preview; inspect before retrying')
        step = next(s for s in rt.current_plan.spec.steps if rt.current_plan.progress[s.id] is progress)
        row = rt.projects.upsert({'title': action.label, 'source_step': step.id, 'targets':[{'kind':'zone','zone_id':str(zone['id']),
            'zone_patches': [patch.model_dump() for patch in action.patches],
            'zone_type': action.zone_type, 'crop': action.crop or None, 'zone_settings': expected_settings}]})
        progress.project_id = row.id
        return {'zone': action.label, 'zone_id': str(zone['id']), 'cells': len(cells), 'crop': action.crop}
