"""Maintained waste containment through the shared goals, native WorkGivers and Hands."""
from .colony_plan import Failure
from .strategic_state import fingerprint


def arguments(plan):
    goal = plan.colony_goals.get('MaintainWaste')
    return {key: ','.join(sorted(goal.target.get(key, []))) if goal else '' for key in ('unwanted', 'bury')}


def pending_items(state):
    if state.get('success') is not True or not isinstance(state.get('items'), list):
        return None
    return [row for row in state['items'] if row.get('eligible') is True and row.get('state') == 'exposed']


def outcome(action, receipt, state, pawns):
    if state.get('success') is not True:
        return 'waiting'
    item = next((r for r in state.get('items', []) if r.get('thingId') == action.arguments['thingId']), None)
    burial = action.arguments['thingId'] in action.arguments.get('bury', '').split(',')
    if item and item.get('state') in (('buried',) if burial else ('relocated', 'buried')):
        return 'complete'
    pawn = next((p for p in pawns if p.get('thingId') == action.arguments['pawn']), None)
    if pawn and (pawn.get('carriedThingId') == action.arguments['thingId'] or pawn.get('job') == receipt.get('job')):
        return 'waiting'
    return Failure(code='waste_unverified', detail='Exact waste is not observed in separated storage or a grave. '
        'Absence, merged stacks and interrupted hauling do not prove disposal; inspect before issuing replacement work.',
        evidence={'item': item, 'pawn': pawn})


async def refresh(rt):
    plan = rt.current_plan
    goal = plan.colony_goals.get('MaintainWaste')
    waiting = [s for s in plan.spec.steps if s.action.kind == 'native_operation'
               and s.action.tool == 'home/manage_waste' and plan.progress[s.id].state == 'waiting']
    if (goal is None or goal.cancelled) and not waiting:
        return
    token, direction = rt.context_token, rt.chat_revision
    state = await rt.game.query('home/waste_state', **arguments(plan))
    await rt.sync_identity()
    if rt.context_token != token or rt.chat_revision != direction or rt.current_plan is not plan:
        return
    plan.control['waste'] = state
    for step in waiting:
        progress = plan.progress[step.id]
        issued = progress.issued.get('0', {})
        # Changed direction/load cannot turn a prior order into new controller authority.
        if issued.get('load_token') != token or issued.get('player_direction') != plan.control.get('player_direction', 0):
            result = Failure(code='waste_context_changed', detail='Waste order belongs to an earlier load or player direction; observe retained native work before replacing it.')
        elif state.get('tick', -1) <= issued.get('issued_tick', -1):
            continue
        else:
            result = outcome(step.action, issued.get('waste_receipt', {}), state,
                             rt.batch.native.get('pawns', {}).get('pawns', []))
        if result == 'waiting':
            continue
        progress.state = 'blocked' if isinstance(result, Failure) else 'complete'
        progress.failure = result if isinstance(result, Failure) else None
        rt.signal('plan.step_' + progress.state, {'step': step.id, 'meaning': 'Containment is not destruction'})


async def compile_method(rt, goal):
    from .colony_skills import SkillBlocked, native
    state = rt.current_plan.control.get('waste', {})
    items = pending_items(state)
    if items is None:
        raise SkillBlocked('Native waste state unavailable; no containment or disposal claim is possible')
    people = rt.batch.native.get('pawns', {}).get('pawns', [])
    refusals = []
    for row in sorted(items, key=lambda r: (r.get('kind') != 'corpse', r['thingId']))[:8]:
        for pawn in sorted(people, key=lambda p: p['thingId']):
            if pawn.get('dead') or pawn.get('downed') or pawn.get('drafted') or pawn.get('mentalState'):
                continue
            args = dict(arguments(rt.current_plan), thingId=row['thingId'], pawn=pawn['thingId'])
            method = 'contain-' + fingerprint(args)[:16]
            if goal.method_seen(method):
                continue
            preview = await rt.inspect_native('home/manage_waste', dict(args, dryRun=True))
            if preview.get('success') is True and preview.get('accepted') is True:
                goal.evidence['containment'] = preview
                return method, [dict(native('home/manage_waste', **args), completion='waste_contained')]
            refusals.append(preview)
            if len(refusals) >= 8:
                break
        if len(refusals) >= 8:
            break
    if items:
        goal.evidence['refusals'] = refusals
        raise SkillBlocked('No eligible waste haul within current native filters, destination separation, labor and player policy; no zones or possessions changed')
    return None
