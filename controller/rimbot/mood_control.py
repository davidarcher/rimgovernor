"""Bounded mood relief through shared goals and ordinary native need jobs."""
from .native_forecasts import finite
from .colony_plan import Failure
from .bridge import BridgeError


def assess(people, forecasts, previous):
    """Native thresholds reflect traits/ideology; no prediction of future mood."""
    targets = {p['id']: p for p in forecasts or []}
    result = {}
    for p in people:
        identity = p['thingId']
        needs = p.get('needs') or {}
        mood, threshold = finite(needs.get('mood')), finite(needs.get('breakThresholdMinor'))
        old = previous.get(identity, {})
        mental = p.get('mentalState') or needs.get('mentalState')
        known = mood is not None and threshold is not None
        target = finite(targets.get(identity, {}).get('moodTarget'))
        active = bool(mental) or (mood <= threshold + (0.05 if old.get('active') else 0) if known else old.get('active', False))
        if known and target is not None and target < mood and target <= threshold:
            active = True  # Current thought pressure warrants review, not a predicted break time.
        causes = []
        for need in ('food', 'rest', 'joy'):
            level = finite(needs.get(need))
            retained = any(c['need'] == need for c in old.get('causes', []))
            if level is not None and level < (0.5 if retained else 0.3):
                causes.append(dict(need=need, level=level, target=0.5,
                    expectedNeedBenefit=0.5-level, expectedMoodBenefit=None,
                    laborPawns=1, resources='native permitted food' if need == 'food' else 'existing eligible facilities'))
            elif level is None and retained:
                causes.append(dict(next(c for c in old['causes'] if c['need'] == need), level=None))
        causes.sort(key=lambda c: (c['level'] is None, c['level'] if c['level'] is not None else 1, c['need']))
        active = active or (old.get('active', False) and bool(causes))
        result[identity] = dict(active=active, known=known, mood=mood, threshold=threshold,
            pressure=None if target is None or mood is None else target-mood,
            mentalState=mental, causes=causes, thoughts=p.get('thoughts'),
            otherNeeds=needs.get('all'),
            traits=(p.get('bio') or {}).get('traits'),
            schedule=(p.get('schedule') or {}).get('current'),
            playerForced=p.get('jobPlayerForced'), drafted=p.get('drafted'), downed=p.get('downed'),
            priority=1 if mental else 2,
            dead=p.get('dead'), job=p.get('job'))
    # A missing pawn/read cannot silently certify recovery after a load or transit.
    for identity, old in previous.items():
        if identity not in result and old.get('active'):
            result[identity] = dict(old, known=False, missing=True)
    return result


def priority_nodes(states):
    ranked = sorted(((pawn, state) for pawn, state in states.items() if state['active']),
        key=lambda item: (item[1]['priority'],
            item[1]['mood']-item[1]['threshold'] if item[1]['known'] else -1, item[0]))
    return [('EnsureMood-'+pawn, state['priority']) for pawn, state in ranked]


async def method(rt, identity, facts, people):
    from .colony_skills import SkillBlocked, native
    pawn_id = identity.removeprefix('EnsureMood-')
    state = facts['mood'][pawn_id]
    goal = rt.current_plan.colony_goals[identity]
    goal.evidence['mood'] = state
    if state.get('mentalState'):
        raise SkillBlocked('Active mental break: preserve native behavior and player ownership; inspect safety before advancing.')
    if not state['known'] or state.get('missing') or state.get('dead'):
        raise SkillBlocked('Mood or pawn identity unavailable; recovery is unverified.')
    pawn = next(p for p in people if p['thingId'] == pawn_id)
    if pawn.get('drafted') is not False or pawn.get('downed') is not False or pawn.get('jobPlayerForced') is not False:
        raise SkillBlocked('Pawn unavailable or player-forced work is protected.')
    if not state['causes']:
        raise SkillBlocked('No measured correctable food/rest/recreation deficit; environmental/social thoughts require eligible facilities or player direction. Future mood benefit is unknown.')
    failures = []
    for cause in state['causes']:
        if cause['level'] is None:
            failures.append(cause['need']+': current need is unknown')
            continue
        name = cause['need']
        if goal.method_seen(name):
            continue
        args = dict(pawn=pawn_id, need=name, expectedJob=pawn.get('jobLoadId'),
                    expectedSchedule=(pawn.get('schedule') or {}).get('current'))
        if type(args['expectedJob']) is not int or not args['expectedSchedule']:
            failures.append(name+': native job/schedule identity unavailable')
            continue
        try:
            preview = await rt.inspect_native('home/relieve_need', dict(args, dryRun=True))
        except BridgeError as error:
            # This call is admission-only. Dispatch errors remain owned by Hands.
            preview = error.result.structuredContent or {}
            if (preview.get('success') is not False
                    or preview.get('tool') != 'home/relieve_need'
                    or error.tool not in ('home/relieve_need', 'games_call_tool')):
                raise
            goal.evidence.setdefault('need_preview_refusals', {})[name] = preview
        if preview.get('success') is not True:
            failures.append(name+': '+str(preview.get('error')))
            continue
        return name, [dict(native('home/relieve_need', **args), completion='need_recovered')]
    raise SkillBlocked('; '.join(failures) or 'Bounded need methods exhausted; inspect current needs and native blockers.')


def outcome(action, pawns):
    pawn = next((p for p in pawns if p.get('thingId') == action.arguments['pawn']), None)
    if pawn is None:
        return 'waiting'
    if (pawn.get('dead') or pawn.get('downed') or pawn.get('mentalState')
            or pawn.get('drafted') or pawn.get('jobPlayerForced')):
        return Failure(code='need_recovery_interrupted', detail='Pawn unavailable, in a mental break or under player orders; need recovery is unverified.')
    level = finite((pawn.get('needs') or {}).get(action.arguments['need']))
    return 'complete' if level is not None and level >= 0.5 else 'waiting'
