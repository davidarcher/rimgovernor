"""Capture explicit construction removals, then execute only those native identities."""
from .colony_plan import Buildings, RoomShell, ConstructionTarget, CancelConstructionAction


async def capture_targets(game, plan, source_step):
    from .hands import room_placements
    source = next((s for s in plan.spec.steps if s.id == source_step), None)
    if source is None or source.source != 'PLAYER' or not isinstance(source.action, (Buildings, RoomShell)):
        raise ValueError('Cancellation needs a tracked player construction step')
    placements = room_placements(source.action) if isinstance(source.action, RoomShell) else source.action.placements
    progress = plan.progress[source.id]
    targets = []
    for index, placement in enumerate(placements):
        issued = progress.issued.get(str(index))
        if not issued:
            continue
        if issued.get('confirmed') is not True:
            raise ValueError('Construction placement has an uncertain receipt; reconcile it before cancellation')
        found = await game.query('home/list_buildings', x=placement.x, z=placement.z,
            radius=1, aggregate=False, playerOnly=True)
        if found.get('skipped', {}).get('byMaxDetailed') or not isinstance(found.get('buildings'), list):
            raise ValueError('Construction observation is incomplete')
        pending = [b for b in found['buildings'] if b.get('position', {}).get('x') == placement.x
            and b.get('position', {}).get('z') == placement.z and (b.get('isBlueprint') or b.get('isFrame'))]
        if not pending:
            continue  # Completed buildings and already removed orders are preserved.
        if len(pending) != 1:
            raise ValueError('Construction cell has ambiguous pending orders')
        target = pending[0]
        if (target.get('buildDefName') != placement.def_name or not target.get('thingId')
                or (target.get('stuff') or '') != (issued.get('stuff') or '')):
            raise ValueError('Construction changed since issuance; existing orders were preserved')
        targets.append(ConstructionTarget(thing=target['thingId'], expectedDef=placement.def_name,
            expectedStuff=target.get('stuff') or '', x=placement.x, z=placement.z))
    return targets


def arguments(action, target, *, dry_run):
    return dict(target.model_dump(), colonyId=action.colonyId, loadToken=action.loadToken,
        mapId=action.mapId, dryRun=dry_run)


async def validate_cancellations(spec, current, game, identity):
    old = {s.id: s for s in current.spec.steps}
    for step in spec.steps:
        action = step.action
        if not isinstance(action, CancelConstructionAction) or old.get(step.id) == step:
            continue
        if step.source != 'PLAYER' or any(getattr(action, k) != identity[k] for k in ('colonyId','loadToken','mapId')):
            raise ValueError('Construction cancellation belongs to another native context')
        captured = await capture_targets(game, current, action.source_step)
        if action.targets != captured:
            raise ValueError('Construction targets changed; existing orders were preserved')
        for target in action.targets:
            preview = await game.invoke('home/cancel_construction', arguments(action, target, dry_run=True))
            if preview.get('success') is not True or preview.get('applied') is not False:
                raise ValueError('Native construction cancellation preview refused')


def retire_sources(plan, previous_ids):
    """Persist source suppression in the same transaction as accepted cancellation."""
    for step in plan.spec.steps:
        if step.id in previous_ids or not isinstance(step.action, CancelConstructionAction):
            continue
        source = next((s for s in plan.spec.steps if s.id == step.action.source_step), None)
        if source is None:
            continue
        if plan.progress[source.id].state not in ('complete','cancelled'):
            plan.cancel(source.id)
        goal = plan.colony_goals.get(source.goal_id)
        if goal:
            goal.cancelled, goal.status, goal.reason = True, 'blocked', 'Construction cancelled by player'
            if goal.target.get('satisfies'):
                plan.control.setdefault('suppressed_goals',{})[goal.target['satisfies']] = source.goal_id


async def execute_target(hands, rt, action, target, progress, key, revision, token, direction):
    from .hands import Blocked
    if any(getattr(action, k) != rt.identity[k] for k in ('colonyId','loadToken','mapId')):
        raise Blocked('cancellation_context_changed', 'Observe the construction in this load and request cancellation again')
    found = await rt.game.query('home/list_buildings', x=target.x, z=target.z,
        radius=1, aggregate=False, playerOnly=True)
    hands.guard(rt, revision, token, direction)
    if found.get('skipped', {}).get('byMaxDetailed') or not isinstance(found.get('buildings'), list):
        raise Blocked('incomplete_observation', 'Cannot verify the exact cancellation target')
    present = next((b for b in found['buildings'] if b.get('thingId') == target.thing), None)
    if present is None:
        return {'thing_id':target.thing, 'observed_absent':True,
            'meaning':'Exact pending order absent; replacements and completed buildings preserved'}
    if progress.issued.get(key):
        raise Blocked('uncertain_write', 'Cancellation target still exists after an uncertain write; no repeat sent')
    preview = await rt.inspect_native('home/cancel_construction', arguments(action, target, dry_run=True))
    if preview.get('success') is not True or preview.get('applied') is not False:
        raise Blocked('native_refused', 'Native cancellation preview refused', evidence=preview)
    hands.guard(rt, revision, token, direction)
    progress.issued[key] = {'confirmed':False, 'thing_id':target.thing}
    rt.persist()
    result = await rt.native('home/cancel_construction', arguments(action, target, dry_run=False),
        expected_revision=direction, expected_token=token, expected_plan_revision=revision, reconcile=False)
    receipt = result.get('receipt', result)
    if receipt.get('success') is not True or receipt.get('removed') is not True:
        raise Blocked('cancellation_unverified', 'Native receipt did not verify removal', evidence=receipt)
    return {'thing_id':target.thing, 'removed':True, 'native_target':receipt.get('target')}
