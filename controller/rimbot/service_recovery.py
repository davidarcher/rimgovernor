"""Bounded repair and fuel methods in the shared plan; completion is native state."""
from .colony_plan import Failure
from .strategic_state import fingerprint


def pending(state):
    if state.get('success') is not True or not isinstance(state.get('buildings'), list):
        return None
    work = []
    for row in state['buildings']:
        if row.get('forbidden') or row.get('burning'):
            continue
        if row.get('broken') is True:
            work.append((row, 'breakdown'))
        if row.get('hitPoints', 0) < row.get('maxHitPoints', 0):
            work.append((row, 'repair'))
        if row.get('fuel') is not None and row['fuel'] < (row.get('fuelTarget') or 0) * .25:
            work.append((row, 'refuel'))
    return sorted(work, key=lambda pair: (pair[1] != 'refuel', pair[0]['thingId']))


def prerequisites(facts):
    state = facts.get('recovery', {})
    return fingerprint(dict(resources=facts.get('resources'), buildings=state.get('buildings'),
                            roofHazard=state.get('roofHazard'), restrictions=state.get('restrictions'),
                            areas=state.get('areas')))


def outcome(action, receipt, state, pawns):
    if state.get('success') is not True:
        return 'waiting'
    row = next((r for r in state.get('buildings', []) if r['thingId'] == action.arguments['thingId']), None)
    method = action.arguments['method']
    if row and (method == 'repair' and row['hitPoints'] == row['maxHitPoints']
                or method == 'breakdown' and row.get('broken') is False
                or method == 'refuel' and row.get('fuel', 0) > (receipt.get('fuel') or 0)):
        return 'complete'
    pawn = next((p for p in pawns if p.get('thingId') == action.arguments['pawn']), None)
    if pawn and pawn.get('job') == receipt.get('job'):
        return 'waiting'
    return Failure(code='recovery_unverified', detail='Exact service has not recovered; interrupted or missing work needs observation before replacement.',
                   evidence={'building': row, 'pawn': pawn})


async def refresh(rt):
    plan = rt.current_plan
    waiting = [s for s in plan.spec.steps if s.action.kind == 'native_operation'
               and s.action.tool == 'home/recover_service' and plan.progress[s.id].state == 'waiting']
    if not waiting:
        return
    token, direction = rt.context_token, rt.chat_revision
    state = await rt.game.query('home/recovery_state')
    await rt.sync_identity()
    if rt.context_token != token or rt.chat_revision != direction or rt.current_plan is not plan:
        return
    for step in waiting:
        progress = plan.progress[step.id]
        issued = progress.issued.get('0', {})
        if issued.get('load_token') != token or issued.get('player_direction') != plan.control.get('player_direction', 0):
            result = Failure(code='recovery_context_changed', detail='Recovery order belongs to an earlier load or player direction.')
        elif state.get('tick', -1) <= issued.get('issued_tick', -1):
            continue
        else:
            result = outcome(step.action, issued.get('recovery_receipt', {}), state,
                             rt.batch.native.get('pawns', {}).get('pawns', []))
        if result == 'waiting':
            continue
        progress.state = 'blocked' if isinstance(result, Failure) else 'complete'
        progress.failure = result if isinstance(result, Failure) else None
        rt.signal('plan.step_' + progress.state, {'step': step.id})


async def compile_method(rt, goal, facts, people):
    from .colony_skills import SkillBlocked, native
    state = facts.get('recovery', {})
    goal.evidence['service_prerequisite'] = prerequisites(facts)
    work = pending(state)
    if work is None:
        raise SkillBlocked('Fresh native service state unavailable')
    people = sorted((p for p in people if not any(p.get(k) for k in ('dead', 'downed', 'drafted', 'mentalState'))),
                    key=lambda p: p['thingId'])
    if state.get('roofHazard'):
        if not state.get('areas'):
            raise SkillBlocked('Roof-sensitive hazard requires a reachable wholly roofed allowed work area; no safe refuge observed')
        safe = {a['id'] for a in state['areas']}
        for restriction in state.get('restrictions', [])[:8]:
            if restriction.get('area') in safe:
                continue
            for area in state['areas'][:2]:
                args = dict(pawn=restriction['pawn'], areaId=area['id'])
                key = 'roof-' + fingerprint(dict(args, window=facts['tick']//600))[:16]
                if goal.method_seen(key):
                    continue
                preview = await rt.inspect_native('home/recovery_area', dict(args, dryRun=True))
                if preview.get('accepted') is True:
                    return key, [native('home/recovery_area', **args)]
    pairs = len(work) * len(people)
    cursor = goal.evidence.get('preview_cursor', 0)
    refusals = []
    for offset in range(min(8, pairs)):
        index = (cursor + offset) % pairs
        row, method = work[index % len(work)]
        pawn = people[index // len(work)]
        goal.evidence['preview_cursor'] = (index + 1) % pairs
        args = dict(thingId=row['thingId'], pawn=pawn['thingId'], method=method)
        key = method + '-' + fingerprint(dict(args, hp=row['hitPoints'], fuel=row.get('fuel')))[:16]
        if goal.method_seen(key):
            continue
        preview = await rt.inspect_native('home/recover_service', dict(args, dryRun=True))
        if preview.get('success') is True and preview.get('accepted') is True:
            goal.evidence['recovery'] = preview
            return key, [dict(native('home/recover_service', **args), completion='service_recovered')]
        refusals.append(preview)
    if work:
        goal.evidence['refusals'] = refusals
        raise SkillBlocked('Recovery requires reachable permitted supplies and an available native worker')
    plan = getattr(rt, 'current_plan', None)
    if plan and 'infrastructure' in plan.control.get('disaster_recovery', {}).get('deficits', []):
        raise SkillBlocked('Tracked infrastructure is missing or protected; accepted rebuilding or player direction is required')
    return None
