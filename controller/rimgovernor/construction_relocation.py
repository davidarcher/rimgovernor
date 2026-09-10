"""Validate a replacement and exact removal together before changing native orders."""
from .colony_plan import CancelConstructionAction, CommitSteps, PlanStep, RoomShell
from .construction_cancellation import capture_targets, validate_player_authorization
from .strategic_state import fingerprint


async def relocate(rt, request, *, token, revision):
    plan = rt.current_plan
    intent = plan.control.get('player_intents', {}).get(request.intent_id, {})
    source_id = intent.get('step', request.intent_id)
    source = next((step for step in plan.spec.steps if step.id == source_id), None)
    if source is None or source.source != 'PLAYER':
        raise ValueError('Relocation requires an exact tracked player construction intent')
    if type(source.action) is not type(request.replacement):
        raise ValueError('Relocation must preserve the construction kind; request a separate project for different work')
    if source.action == request.replacement:
        raise ValueError('Replacement geometry is unchanged')
    if any(dep.step == source.id for step in plan.spec.steps for dep in step.after):
        raise ValueError('Dependent work references this construction; resolve it before relocation')
    progress = plan.progress[source.id]
    if isinstance(source.action, RoomShell) and not progress.issued and progress.state in ('pending','blocked'):
        from .player_commands import apply_command
        intent_key=next((key for key,value in plan.control.get('player_intents',{}).items()
                         if value.get('step')==source.id),request.intent_id)
        return await apply_command(rt,{'kind':'BuildRoom','intent_id':intent_key,
            'room':request.replacement.model_dump(),'purpose':source.purpose},token=token,revision=revision)
    validate_player_authorization(rt, revision)
    targets = await capture_targets(rt.game, plan, source.id)
    # Completed or independently removed slots cannot be interpreted as consent
    # to duplicate construction elsewhere. Use a separate explicit project.
    if not targets or len(targets) != len(progress.issued):
        raise ValueError('Relocation requires every issued placement to remain pending; completed or missing construction is preserved')
    await rt.ensure_context(token)
    if rt.chat_revision != revision:
        raise ValueError('Player direction changed; existing construction preserved')
    cancellation = CancelConstructionAction(source_step=source.id, targets=targets,
        **{key: rt.identity[key] for key in ('colonyId', 'loadToken', 'mapId')})
    suffix = fingerprint(dict(source=source.id, replacement=request.replacement.model_dump(),
                              cancellation=cancellation.model_dump(), revision=revision))[:20]
    removal_id, replacement_id = 'relocate-remove-'+suffix, 'relocate-build-'+suffix
    removal = PlanStep(id=removal_id, title='Remove old construction for relocation',
        source='PLAYER', purpose=source.purpose, priority=100, action=cancellation,
        completion_criteria='All exact old pending orders removed or observed absent')
    replacement = PlanStep(id=replacement_id, title='Relocated '+source.title,
        source='PLAYER', purpose=source.purpose, priority=75, goal_id=source.goal_id,
        action=request.replacement, after=[{'step':removal_id, 'when':'complete'}],
        completion_criteria='Replacement native construction completed')
    await rt.commit_strategy(CommitSteps(expected_revision=plan.revision,
        reason='Explicit relocation with validated replacement and exact old-order removal',
        steps=[removal, replacement]).decision(plan), actor='strategist',
        expected_token=token, expected_revision=revision)
    intent_key = next((key for key, value in plan.control.get('player_intents', {}).items()
                       if value.get('step') == source.id), request.intent_id)
    prior_request = dict(intent.get('request', {}))
    if isinstance(request.replacement, RoomShell):
        prior_request.update(kind='BuildRoom', intent_id=intent_key,
            room=request.replacement.model_dump(), purpose=source.purpose)
    else:
        prior_request.update(kind='PlaceBuildings', buildings=request.replacement.model_dump(), purpose=source.purpose)
    plan.control.setdefault('player_intents', {})[intent_key] = {'step':replacement_id, 'request':prior_request}
    goal = plan.colony_goals.get(source.goal_id)
    if goal:
        goal.cancelled, goal.status, goal.reason = False, 'active', ''
        goal.steps = [removal_id, replacement_id]
        goal.evidence['request'] = prior_request
        satisfies = goal.target.get('satisfies')
        if satisfies:
            plan.control.setdefault('suppressed_goals', {}).pop(satisfies, None)
    if rt.mode == 'manual':
        rt.manual_requests.extend((identity, token, revision) for identity in (removal_id, replacement_id))
    return {'step':replacement_id, 'cancellation_step':removal_id, 'targets':len(targets),
            'state':plan.progress[replacement_id].state}
