"""Observed upkeep deficits retained in the shared ColonyPlan.

Receipts never clear a deficit. Unknown observations retain it, and bounded native
jobs yield to player work, medical needs and combat without changing schedules.
"""
from dataclasses import dataclass

from .native_forecasts import finite
from .strategic_state import fingerprint


@dataclass(frozen=True)
class UpkeepContract:
    goal: str
    field: str
    priority: int
    enter: float = 0
    recover: float = 0


CONTRACTS = (
    UpkeepContract('MaintainFireSafety', 'fires', 1),
    UpkeepContract('SecureSupplies', 'vulnerable', 3),
    UpkeepContract('MaintainEssentialRepairs', 'damaged', 3),
    UpkeepContract('MaintainCleanFacilities', 'filth', 3),
    UpkeepContract('MaintainSleeping', 'sleeping', 3),
    UpkeepContract('MaintainMedicalReserves', 'medicine', 3),
)
GOALS = {c.goal for c in CONTRACTS}


def retry_signature(state, people, facts, control):
    """Eligibility changes reopen immediately; continuous simulation noise does not."""
    targets = [{k: row.get(k) for k in ('id', 'kind', 'previousBed', 'roofed', 'inStorage',
        'forbidden', 'burning', 'home', 'cleanable', 'safeWorkers')} | {
            'available': sorted(b['id'] for b in row.get('available', []))}
        for row in state['targets'] or []]
    workers = [{k: p.get(k) for k in ('thingId', 'dead', 'downed', 'drafted', 'mentalState', 'work')}
        | {'care': {k: (p.get('health') or {}).get(k) for k in ('needsTend', 'bleeding')}} for p in people]
    return fingerprint(dict(targets=targets, workers=workers, known=state['known'], unsafe=state.get('unsafe'),
        definitions={k: v.get('available') for k, v in facts.get('definitions', {}).items()},
        overrides=control.get('work_overrides', {}), direction=control.get('player_direction')))


def evidence(facts):
    """Reject partial/failed sections; empty successful censuses can prove recovery."""
    raw = facts.get('upkeep') or {}
    current = raw.get('version') == 1 and raw.get('tick') == facts.get('tick')
    errors = raw.get('errors') or {}

    def section(name):
        rows = raw.get(name)
        return rows if current and name not in errors and isinstance(rows, list) else None

    items, structures, fires, filth = (section(k) for k in ('items', 'structures', 'fires', 'filth'))
    vulnerable = None
    if items is not None and all(type(r.get('roofed')) is bool and type(r.get('inStorage')) is bool
            and finite(r.get('deteriorationRate')) is not None and isinstance(r.get('id'), str)
            and finite(r.get('baseDeteriorationRate', r.get('deteriorationRate'))) is not None
            and type(r.get('forbidden')) is bool for r in items):
        vulnerable = [r for r in items if r.get('baseDeteriorationRate', r['deteriorationRate']) > 0 and (not r['roofed'] or not r['inStorage'])
                      and not r['forbidden']]
        # Actual food deadlines outrank durable materials; stable IDs break ties.
        vulnerable.sort(key=lambda r: (not r.get('medicine', False),
            r.get('rotTicks') if finite(r.get('rotTicks')) is not None else float('inf'), r['id']))
    damaged = None
    if structures is not None and all(finite(r.get('hitPoints')) is not None
            and finite(r.get('maxHitPoints')) is not None and type(r.get('home')) is bool
            and isinstance(r.get('id'), str) for r in structures):
        damaged = sorted((r for r in structures if r['home'] and r['maxHitPoints'] > 0
                          and r['hitPoints'] < r['maxHitPoints']),
                         key=lambda r: (r['hitPoints'] / r['maxHitPoints'], r['id']))
    if fires is not None:
        if any(type(r.get('home')) is not bool or not isinstance(r.get('id'), str) for r in fires):
            fires = None
        else:
            fires = sorted((r for r in fires if r['home']), key=lambda r: r['id'])
    if filth is not None:
        if any(not isinstance(r.get('id'), str) or type(r.get('home')) is not bool for r in filth):
            filth = None
        else:
            filth = sorted((r for r in filth if r['home']), key=lambda r: (r.get('room') not in ('Kitchen', 'Hospital', 'Laboratory'), r['id']))
    return dict(vulnerable=vulnerable, damaged=damaged, fires=fires, filth=filth,
                sleeping=facts.get('sleepingUpkeep') if current else None,
                medicine=facts.get('medicalReserve') if current else None)


def upkeep_nodes(facts, control):
    from .sleeping_upkeep import sleeping_evidence
    facts['sleepingUpkeep'] = sleeping_evidence(facts, control)
    from .medical_reserves import reserve_evidence
    facts['medicalReserve'] = reserve_evidence(facts, control)
    observed = evidence(facts)
    states = control.setdefault('upkeep', {})
    nodes = []
    for contract in CONTRACTS:
        rows = observed[contract.field]
        state = states.setdefault(contract.goal, {})
        known = rows is not None
        # Unknown facts keep existing risk and prevent claiming completion.
        active = state.get('active', False)
        if known:
            active = len(rows) > (contract.recover if active else contract.enter)
        state.update(active=active, known=known, count=len(rows) if known else None,
                     entry=contract.enter, recovery=contract.recover,
                     observed_tick=facts.get('tick'), targets=rows)
        state['unsafe'] = contract.goal == 'MaintainFireSafety' and known and (
            len(rows) > 3 or any(finite(r.get('size')) is None or r['size'] > 1 for r in rows))
        if active or not known:
            # Missing a read is a visible blocker, not an invented emergency.
            nodes.append((contract.goal, contract.priority if known or active else 4))
    return nodes


def progress_metric(goal_id, rows):
    if rows is None:
        return None
    if goal_id == 'MaintainSleeping':
        return len(rows)
    field = {'SecureSupplies': 'count', 'MaintainMedicalReserves': 'count', 'MaintainCleanFacilities': 'thickness', 'MaintainFireSafety': 'size'}.get(goal_id)
    values = ([r.get('maxHitPoints', 0) - r.get('hitPoints', 0) for r in rows]
              if goal_id == 'MaintainEssentialRepairs' else [r.get(field) for r in rows])
    return sum(values) if all(finite(v) is not None for v in values) else None


def reconcile_upkeep(rt, facts):
    from .colony_plan import Failure

    raw = facts.get('upkeep') or {}
    if raw.get('version') != 1 or raw.get('tick') != facts.get('tick'):
        return
    for step in rt.current_plan.spec.steps:
        if getattr(step.action, 'completion', None) != 'upkeep_target':
            continue
        progress = rt.current_plan.progress[step.id]
        if progress.state != 'waiting':
            continue
        receipt = progress.issued.get('0', {})
        if not receipt.get('confirmed'):
            continue
        if (receipt.get('load_token') != rt.context_token
                or receipt.get('player_direction') != rt.current_plan.control.get('player_direction', 0)):
            progress.state = 'blocked'
            progress.failure = Failure(code='upkeep_ownership_changed', detail='Upkeep order ownership changed; observe before recovery')
            continue
        if facts.get('tick', -1) < receipt.get('issued_tick', 0):
            continue
        action = step.action.arguments['action']
        section = {'haul': 'items', 'repair': 'structures', 'clean': 'filth', 'firefight': 'fires'}[action]
        rows = raw.get(section)
        if not isinstance(rows, list) or section in (raw.get('errors') or {}):
            continue
        target = next((r for r in rows if r.get('id') == step.action.arguments['target']), None)
        complete = False
        if action in ('clean', 'firefight'):
            complete = target is None
        elif target is not None and action == 'repair':
            complete = finite(target.get('hitPoints')) is not None and target['hitPoints'] == target.get('maxHitPoints')
        elif target is not None:
            goal = rt.current_plan.colony_goals.get(step.goal_id)
            original = (goal.evidence.get('upkeep_orders', {}).get(step.id) or {}) if goal else {}
            count = finite(original.get('count'))
            complete = (target.get('roofed') is True and target.get('inStorage') is True
                        and count is not None and finite(target.get('count')) is not None and target['count'] >= count)
        if complete:
            progress.state = 'complete'
            progress.issued['0']['postcondition'] = dict(tick=facts['tick'], target=target)
        elif target is None and action in ('haul', 'repair'):
            progress.state = 'blocked'
            progress.failure = Failure(code='upkeep_target_missing', detail='Tracked upkeep target disappeared; loss or stack merge is not verified completion')


async def upkeep_method(rt, goal_id, facts, people):
    from .colony_skills import SkillBlocked, native
    from .bridge import BridgeError

    if goal_id == 'MaintainSleeping':
        from .sleeping_upkeep import sleeping_method
        return await sleeping_method(rt, facts)
    if goal_id == 'MaintainMedicalReserves':
        from .medical_reserves import reserve_method
        return await reserve_method(rt, facts)

    goal = rt.current_plan.colony_goals[goal_id]
    goal.evidence.pop('waiting_for_storage_roof', None)
    state = rt.current_plan.control['upkeep'][goal_id]
    if not state['known']:
        raise SkillBlocked('Upkeep evidence unavailable: ' + goal_id)
    rows = state['targets']
    if not rows:
        return None
    action, work = {
        'SecureSupplies': ('haul', 'Hauling'),
        'MaintainEssentialRepairs': ('repair', 'Construction'),
        'MaintainCleanFacilities': ('clean', 'Cleaning'),
        'MaintainFireSafety': ('firefight', 'Firefighter'),
    }[goal_id]
    if goal_id == 'MaintainFireSafety' and (len(rows) > 3 or any(
            finite(r.get('size')) is None or r['size'] > 1 for r in rows)):
        raise SkillBlocked('Fire exceeds bounded safe intervention; retain emergency hold')
    candidates = []
    overrides = rt.current_plan.control.get('work_overrides', {})
    forced = {p['id'] for p in (facts.get('upkeep') or {}).get('people') or [] if p.get('playerForcedJob') is True}
    for p in people:
        types = (p.get('work') or {}).get('types', [])
        enabled = any(w.get('name') == work and w.get('disabled') is False and w.get('priority', 0) > 0 for w in types)
        if (enabled and p['thingId'] not in forced and not any(p.get(k) for k in ('dead', 'downed', 'drafted', 'mentalState'))
                and overrides.get(p['thingId'], {}).get(work) != 0
                and (p.get('health') or {}).get('needsTend') is False
                and (p.get('health') or {}).get('bleeding') is False):
            candidates.append(p)
    candidates.sort(key=lambda p: p['thingId'])
    failures = []
    goal.evidence.pop('waiting_for_native_cleaning', None)
    goal.evidence.pop('waiting_for_native_fire', None)
    if action == 'firefight':
        # The installed firefighting WorkGiver is not directly orderable.
        # Let enabled ordinary workers respond; never bypass that native rule.
        if candidates and all(any(p['thingId'] in r.get('safeWorkers', []) for p in candidates) for r in rows):
            goal.evidence['waiting_for_native_fire'] = True
            return None
        raise SkillBlocked('No enabled available firefighter with safe access; retain emergency hold')
    if action == 'clean' and candidates and all(r.get('cleanable') is False for r in rows):
        goal.evidence['waiting_for_native_cleaning'] = True
        return None
    for target in rows[:8]:
        if action == 'clean' and target.get('cleanable') is False:
            continue
        # One job per exact target and observed condition. An accepted job is
        # watched until the target changes; it is never replayed every review.
        method = action + '-' + fingerprint({'target': target['id']})[:16]
        if goal.method_seen(method):
            return None
        for pawn in candidates[:8]:
            if action == 'firefight' and pawn['thingId'] not in target.get('safeWorkers', []):
                continue
            request = dict(action=action, pawn=pawn['thingId'], target=target['id'], draft=False, watch=False)
            if action == 'haul':
                request['requireSafeStorage'] = True
            try:
                preview = await rt.inspect_native('home/order', dict(request, dryRun=True))
            except BridgeError as error:
                preview = error.result.structuredContent or {}
                if preview.get('errorKind') not in ('job_refused', 'work_disabled', 'no_storage', 'unreachable_storage', 'not_reachable'):
                    raise
                failures.append(dict(target=target['id'], pawn=pawn['thingId'], error=str(error), preview=preview))
                continue
            if preview.get('success') is not True:
                failures.append(dict(target=target['id'], pawn=pawn['thingId'], error=preview.get('error'), preview=preview))
                continue
            if action == 'haul':
                checks = (preview.get('diagnostics') or {}).get('checks') or {}
                cell = checks.get('storageCell')
                if checks.get('betterStorageFound') is not True or not isinstance(cell, dict):
                    failures.append(dict(target=target['id'], error='No verified better storage destination'))
                    continue
                # The native preview reports the destination. Protection is
                # checked independently instead of equating hauling with safety.
                protected = (facts.get('upkeep') or {}).get('storageCells')
                if not isinstance(protected, list) or not any(c.get('x') == cell.get('x')
                        and c.get('z') == cell.get('z') and c.get('roofed') is True for c in protected):
                    failures.append(dict(target=target['id'], error='Native haul destination is not verified covered storage'))
                    continue
            goal.evidence['upkeep_order'] = dict(target=target, pawn=pawn['thingId'],
                context=rt.context_token, direction=rt.chat_revision, preview=preview)
            return method, [dict(native('home/order', **request), completion='upkeep_target')]
    goal.evidence['upkeep_refusals'] = failures
    storage_missing = any((f.get('preview') or {}).get('errorKind') == 'no_storage'
        or f.get('error') == 'Native haul destination is not verified covered storage' for f in failures)
    if action == 'haul' and candidates and storage_missing:
        from .upkeep_storage import covered_storage, supply_storeroom
        if storage := await covered_storage(rt, facts, rows[:8]):
            return storage
        return await supply_storeroom(rt, facts)
    raise SkillBlocked('No available enabled worker and safe native ' + action + ' job; preserve player settings')
