"""Admission ordering for existing development goals; never creates game orders."""
from .native_forecasts import finite


def release_admission(plan, identity, admitted, reason):
    """Let the next candidate use a slot when compilation cannot create work."""
    rows = plan.control.get('development', {}).get('goals', {})
    row = rows.get(identity)
    if not row or not row['selected']:
        return
    row.update(selected=False, reason=reason)
    plan.colony_goals[identity].evidence['development'] = dict(row)
    admitted.discard(identity)
    waiting = [key for key, candidate in rows.items()
               if candidate['reason'] == 'Development capacity committed to earlier projects']
    if waiting:
        next_id = min(waiting, key=lambda key: (-rows[key]['score'], key))
        rows[next_id].update(selected=True, reason='')
        plan.colony_goals[next_id].evidence['development'] = dict(rows[next_id])
        admitted.add(next_id)


def committed_projects(plan):
    """Count accepted player work and unresolved development, including uncertain writes."""
    projects = set()
    for step in plan.spec.steps:
        progress = plan.progress[step.id]
        goal = plan.colony_goals.get(step.goal_id)
        if step.source == 'LLM_ADVISOR' or (goal and goal.priority_class < 3 and step.source != 'PLAYER'):
            continue
        unresolved = progress.state != 'complete' and bool(progress.issued)
        if progress.state in ('pending', 'executing', 'waiting') or unresolved:
            projects.add(step.goal_id or step.id)
    research = plan.colony_goals.get('EnsureResearch')
    current = (research.evidence.get('research', {}).get('current') or {}) if research else {}
    if (research and not research.cancelled and research.status != 'complete' and current.get('defName')
            and current['defName'] == plan.control.get('research', {}).get('owned')):
        projects.add('EnsureResearch')
    return projects


def deficit(identity, goal, facts, policy):
    """Comparable deficit fractions, not predicted utility or completion times."""
    if identity == 'MaintainWaste':
        from .waste_management import pending_items
        pending = pending_items(goal.evidence.get('observation', {}))
        return None if pending is None else int(bool(pending))
    if identity == 'EnsureResearch':
        queue = goal.evidence.get('research', {}).get('queue')
        return (1 if queue else 0) if isinstance(queue, list) else None
    if identity == 'EnsureFoodStorage':
        return 0 if facts.get('foodStorage') is True else 1
    if identity == 'MaintainEquipment':
        gear = facts.get('gearUpkeep')
        if not isinstance(gear, dict) or gear.get('success') is not True:
            return None
        pawns = gear.get('pawns')
        if not isinstance(pawns, list) or not pawns or any(type(p.get('deficit')) is not bool
                or not isinstance(p.get('candidates'), list) for p in pawns):
            return None
        return sum(p['deficit'] or bool(p['candidates']) for p in pawns) / len(pawns)
    if identity == 'EnsureBasicDefense':
        target, stock = min(2, facts.get('colonists', 0)), finite(facts.get('armed'))
    elif identity == 'MaintainWood':
        target, stock = policy.wood_target, finite(facts.get('resources', {}).get('WoodLog'))
    elif identity.startswith('MaintainResource-'):
        target, stock = finite(goal.target.get('quantity')), finite(goal.evidence.get('stock'))
    else:
        return None
    if stock is None or target is None or target <= 0:
        return None
    return min(1, max(0, (target-stock)/target))


def arbitrate(plan, facts, people, nodes, policy, *, context, direction):
    """Rank fresh admissions while retaining accepted identities and native work.

    Age advances in game ticks, so paused reviews cannot manufacture priority.
    Context/direction changes and tick rewinds discard selection hysteresis.
    """
    tick = facts['tick']
    previous = plan.control.get('development', {})
    if (previous.get('context') != context or previous.get('direction') != direction
            or tick < previous.get('tick', tick)):
        previous = {}
    workers = sum(p.get('dead') is False and p.get('downed') is False
                  and p.get('drafted') is False and not p.get('mentalState')
                  and (p.get('work') or {}).get('applies') is True for p in people)
    capacity = min(policy.max_development_projects, workers)
    committed = committed_projects(plan)
    free = max(0, capacity-len(committed))
    emergency = any(priority < 2 for _, priority in nodes)
    rows = {}
    candidates = []
    for identity, priority in nodes:
        if priority < 3:
            continue
        goal = plan.colony_goals[identity]
        old = previous.get('goals', {}).get(identity, {})
        since = old.get('waiting_since', tick)
        fraction = deficit(identity, goal, facts, policy)
        # Aging eventually dominates the bounded deficit and selection bonuses.
        age = max(0, tick-since)/2500
        score = round(100*(fraction or 0) + age
                      + (100 if goal.source == 'PLAYER' else 0)
                      + (20 if old.get('selected') else 0), 3)
        reason = ''
        if goal.cancelled:
            reason = 'Player cancelled this goal'
        elif goal.source == 'LLM_ADVISOR':
            reason = 'Adviser suggestions cannot admit autonomous work'
        elif emergency:
            reason = 'Emergency precedence'
        elif goal.status != 'active':
            reason = goal.reason or 'Goal is '+goal.status
        elif identity in committed:
            reason = 'Existing commitment retained; awaiting observed progress'
        elif workers == 0:
            reason = 'No observed available workers'
        elif fraction is None:
            reason = 'Observed deficit unavailable'
        row = dict(score=score, deficit_fraction=fraction, waiting_since=since,
                   selected=False, reason=reason, committed=identity in committed)
        rows[identity] = row
        if not reason:
            candidates.append(identity)
    candidates.sort(key=lambda identity: (-rows[identity]['score'], identity))
    selected = set(candidates[:free])
    for identity in candidates:
        rows[identity]['selected'] = identity in selected
        if identity not in selected:
            rows[identity]['reason'] = 'Development capacity committed to earlier projects'
    for identity, row in rows.items():
        if row['committed']:
            row['waiting_since'] = tick
        plan.colony_goals[identity].evidence['development'] = dict(row)
    plan.control['development'] = dict(context=context, direction=direction, tick=tick,
        available_workers=workers, capacity=capacity, committed=sorted(committed),
        goals=rows, labor=facts.get('forecasts', {}).get('labor'),
        completion_days=None)
    ordered = [(identity, priority) for identity, priority in nodes if priority < 3]
    ordered += sorted(((identity, priority) for identity, priority in nodes if priority >= 3),
                      key=lambda node: (-rows[node[0]]['score'], node[0]))
    # Existing work still gets watchdog/reconciliation handling in the controller.
    return ordered, selected | committed
