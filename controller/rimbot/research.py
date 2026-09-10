"""Needs-driven research preparation; shared Hands remains the only order writer."""
from .colony_plan import ColonyGoal
from .strategic_state import fingerprint

MAX_QUEUE = 8
MAX_VISITS = 128


def prerequisite_queue(projects, finished, targets):
    """Topological native prerequisites; reject incomplete graphs rather than guessing."""
    done, visiting, result = set(finished), set(), []
    visits = 0

    def visit(name):
        nonlocal visits
        if name in done:
            return
        visits += 1
        if visits > MAX_VISITS:
            raise ValueError('Research prerequisite graph exceeds the inspection bound')
        if name in visiting:
            raise ValueError('Cyclic research prerequisites: ' + name)
        row = projects.get(name)
        if row is None or row.get('hidden') is not False or row.get('knowledgeCategory'):
            raise ValueError('Unavailable ordinary research prerequisite: ' + name)
        if not isinstance(row.get('prerequisites'), list) or not isinstance(row.get('hiddenPrerequisites'), list):
            raise ValueError('Incomplete native prerequisites: ' + name)
        visiting.add(name)
        for dependency in sorted(set(row['prerequisites'] + row['hiddenPrerequisites'])):
            visit(dependency)
        visiting.remove(name)
        done.add(name)
        result.append(name)

    for target in targets:
        visit(target)
    return result[:MAX_QUEUE]


def needs(plan, facts, nodes):
    """Only admitted, unsatisfied goals and their observed unavailable capabilities."""
    active = {name for name, _ in nodes if name != 'EnsureResearch'}
    active.update(name for name, goal in plan.colony_goals.items()
                  if not goal.cancelled and goal.status != 'complete' and (name.startswith('intent-') or name.startswith('MaintainResource-')))
    active = {name for name in active if name not in plan.colony_goals or (
        not plan.colony_goals[name].cancelled and plan.colony_goals[name].source != 'LLM_ADVISOR')}
    result = set()
    for goal_id in sorted(active):
        goal = plan.colony_goals.get(goal_id)
        if goal and (goal.cancelled or goal.source == 'LLM_ADVISOR'):
            continue
        if goal:
            for definition in goal.evidence.get('required_capabilities', []):
                if facts.get('definitions', {}).get(definition, {}).get('available') is False:
                    result.add((goal_id, 'ThingDef:' + definition))
            for row in goal.evidence.get('production_deficits', []):
                if row.get('researchBlocked') is True:
                    result.add((goal_id, 'RecipeDef:' + row['recipe']))
    for step in plan.spec.steps:
        if step.goal_id not in active or plan.progress[step.id].state in ('cancelled', 'complete'):
            continue
        action = step.action
        definitions = ([p.def_name for p in action.placements] if action.kind == 'place_buildings' else
                       [action.wall_def, action.door_def] if action.kind == 'build_room_shell' else [])
        for definition in definitions:
            if facts.get('definitions', {}).get(definition, {}).get('available') is False:
                result.add((step.goal_id, 'ThingDef:' + definition))
    return sorted(result, key=lambda item: (plan.colony_goals[item[0]].priority_class if item[0] in plan.colony_goals else 4, item))


def eligible_researchers(people, overrides):
    return sorted(p['thingId'] for p in people
        if not any(p.get(k) for k in ('dead', 'downed', 'drafted', 'mentalState'))
        and overrides.get(p['thingId'], {}).get('Research') != 0
        and (p.get('work') or {}).get('applies') is True
        and any(w.get('name') == 'Research' and w.get('disabled') is False
                and w.get('priority', 0) > 0 for w in p['work'].get('types', [])))


def usable_laboratories(project, benches):
    required = project.get('requiredResearchBuilding')
    facilities = project.get('requiredResearchFacilities')
    if not isinstance(facilities, list):
        return []
    return [bench for bench in benches.get('benches') or []
            if bench.get('powered') is True and (required is None or bench.get('defName') == required)
            and set(facilities) <= {f.get('defName') for f in bench.get('facilities') or [] if f.get('active') is True}]


async def refresh(rt, facts, people, nodes):
    plan = rt.current_plan
    requested = needs(plan, facts, nodes)
    goal = plan.colony_goals.get('EnsureResearch')
    if not requested and goal is None:
        return []
    token, direction = rt.context_token, rt.chat_revision
    goal = plan.colony_goals.setdefault('EnsureResearch', ColonyGoal(priority_class=3))
    if goal.cancelled:
        return []
    state = plan.control.setdefault('research', {})
    if not requested and goal.status == 'complete' and not state.get('needs'):
        return []
    # Retain previous requirements long enough to observe the unlocked capability.
    prior = state.get('needs', [])
    checks = (requested + [tuple(row) for row in prior if tuple(row) not in requested])[:MAX_QUEUE]
    evidence = {'needs': requested, 'capabilities': {}, 'queue': [], 'blocker': ''}
    goal.evidence['research'] = evidence
    state['needs'] = requested
    try:
        snapshot = await rt.game.invoke('home/research', {'locked': True, 'finished': True, 'dryRun': True, 'watch': False})
        if snapshot.get('success') is not True or not isinstance(snapshot.get('finished'), list):
            raise ValueError('Native research state unavailable')
        projects = {p['defName']: p for p in snapshot.get('available', []) + snapshot.get('locked', [])}
        targets = []
        for owner, capability in checks:
            value = await rt.game.invoke('home/research', {'capability': capability, 'dryRun': True, 'watch': False})
            value = value.get('capability') or {}
            evidence['capabilities'][capability] = value
            if value.get('known') is not True:
                raise ValueError('Native capability unavailable: ' + capability)
            if (owner, capability) in requested and value.get('researchReady') is not True:
                if not isinstance(value.get('prerequisites'), list):
                    raise ValueError('Capability prerequisites unavailable: ' + capability)
                targets.extend(value['prerequisites'])
        queue = prerequisite_queue(projects, snapshot['finished'], sorted(set(targets)))
        evidence.update(queue=queue, finished=snapshot['finished'], benches=snapshot.get('researchBenches'))
        current = snapshot.get('current')
        evidence['current'] = current
        owned = state.get('owned')
        if state.get('direction') != plan.control.get('player_direction', 0) or state.get('token') != token:
            state.pop('owned', None)
            owned = None
        if owned and owned in snapshot['finished']:
            evidence['completed_project'] = owned
            state.pop('owned', None)
            owned = None
        if not queue:
            if requested and any(v.get('researchReady') is not True or v.get('availableNow') is not True for v in evidence['capabilities'].values()):
                raise ValueError('Research completion has not unlocked the required capability')
            goal.status, goal.reason = 'complete', ''
            await rt.ensure_context(token)
            if rt.chat_revision != direction:
                raise InterruptedError('Player direction changed during research preparation')
            return []
        if current and current.get('defName') != owned:
            raise ValueError('Player research is preserved: ' + current.get('defName', 'unknown'))
        if owned and not current:
            raise ValueError('Owned research was cleared before completion; player selection is preserved')
        if len(requested) > MAX_QUEUE:
            evidence['deferred_needs'] = len(requested) - MAX_QUEUE
        if current:
            progress = current.get('progress')
            if progress is None:
                raise ValueError('Native research progress unavailable')
            if progress != state.get('progress'):
                state.update(progress=progress, last_progress_tick=facts['tick'])
            elif facts['tick'] - state.get('last_progress_tick', facts['tick']) >= rt.controller.policy.blocked_after_ticks:
                raise ValueError('Research made no progress; inspect bench access, power and researcher workload')
        benches = snapshot.get('researchBenches') or {}
        project = current or projects.get(queue[0], {})
        evidence['laboratory_requirements'] = {
            'building': project.get('requiredResearchBuilding'),
            'facilities': project.get('requiredResearchFacilities'),
            'native_reasons': project.get('lockReasons', [])}
        if not usable_laboratories(project, benches):
            raise ValueError('Laboratory capacity unavailable: provide an eligible powered bench or a bench that needs no power')
        if not eligible_researchers(people, plan.control.get('work_overrides', {})):
            raise ValueError('No eligible assigned researcher; player work priorities are preserved')
        if not current and project.get('canStartNow') is not True:
            raise ValueError('Research infrastructure or native requirements missing: ' + str(project.get('lockReasons', queue[0])))
        evidence['next'] = None if current else queue[0]
        if current and current.get('progress') != goal.evidence.get('research_progress'):
            goal.last_progress_tick = facts['tick']
            goal.evidence['research_progress'] = current.get('progress')
        goal.status, goal.reason = 'active', ''
    except ValueError as error:
        goal.status, goal.reason = 'blocked', str(error)
        evidence['blocker'] = str(error)
    await rt.ensure_context(token)
    if rt.chat_revision != direction:
        raise InterruptedError('Player direction changed during research preparation')
    return [('EnsureResearch', 3)]


async def method(rt):
    from .colony_skills import native, SkillBlocked
    plan = rt.current_plan
    goal = plan.colony_goals['EnsureResearch']
    evidence = goal.evidence.get('research', {})
    if evidence.get('blocker'):
        raise SkillBlocked(evidence['blocker'])
    project = evidence.get('next')
    if not project:
        return None
    name = 'research-' + fingerprint(project)[:12]
    if goal.method_seen(name):
        raise SkillBlocked('Research selection already attempted; inspect its retained outcome')
    plan.control['research']['prepared'] = dict(project=project, token=rt.context_token,
        direction=plan.control.get('player_direction', 0), needs=evidence.get('needs', []))
    return name, [native('home/research', set=project, expectedCurrent='', watch=False,
        **{key: rt.identity[key] for key in ('colonyId', 'loadToken', 'mapId')})]


def selected(rt, project):
    state = rt.current_plan.control.setdefault('research', {})
    state.update(owned=project, token=rt.context_token,
                 direction=rt.current_plan.control.get('player_direction', 0))
    state.pop('progress', None)


async def validate_dispatch(rt, arguments):
    plan = rt.current_plan
    prepared = plan.control.get('research', {}).get('prepared', {})
    goal = plan.colony_goals.get('EnsureResearch')
    if (not goal or goal.cancelled or goal.status != 'active'
            or prepared.get('project') != arguments.get('set')
            or prepared.get('token') != rt.context_token
            or prepared.get('direction') != plan.control.get('player_direction', 0)):
        raise ValueError('Research preparation invalidated; no selection sent')
    owners = [plan.colony_goals.get(owner) for owner, _ in prepared.get('needs', [])]
    if not owners or not any(owner and not owner.cancelled and owner.status != 'complete' for owner in owners):
        raise ValueError('Research bottleneck is obsolete; no selection sent')
    snapshot = await rt.game.invoke('home/research', {'dryRun': True, 'watch': False})
    if snapshot.get('success') is not True or snapshot.get('current') is not None:
        raise ValueError('Player research changed; no selection sent')
