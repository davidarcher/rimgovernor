"""Player semantic requests join the same durable goals and action executor."""
from typing import Annotated, Literal
from pydantic import Field, TypeAdapter
from .colony_plan import Contract, ColonyGoal, CommitSteps, Decision, PlanSpec, PlanStep, RoomShell, Zone
from .colony_skills import native
from .config import ModelRole
from .strategic_state import fingerprint


class SetResearch(Contract):
    kind: Literal['SetResearch']
    project: str = Field(min_length=1)


class CreateGoal(Contract):
    kind: Literal['CreateGoal']
    goal: Literal['EnsureFoodSupply', 'EnsureInitialShelter', 'EnsureFoodStorage', 'EnsureCooking',
                  'EnsureTemperatureSafety', 'EnsureBasicPower', 'EnsureBasicDefense', 'MaintainWood']
    food_days: float | None = Field(default=None, ge=1, le=120)


class ModifyResourcePolicy(Contract):
    kind: Literal['ModifyResourcePolicy']
    resource: str = Field(min_length=1)
    reserve: int = Field(default=0, ge=0)
    spending: Literal['normal', 'defense_only', 'stop'] = 'normal'


class CancelGoal(Contract):
    kind: Literal['CancelGoal']
    goal: str = Field(min_length=1)


class BuildRoom(Contract):
    kind: Literal['BuildRoom']
    intent_id: str = Field(pattern=r'^[a-zA-Z0-9_-]{1,40}$')
    room: RoomShell
    purpose: Literal['shelter', 'defense', 'production', 'storage', 'comfort'] = 'shelter'


class CreateZone(Contract):
    kind: Literal['CreateZone']
    intent_id: str = Field(pattern=r'^[a-zA-Z0-9_-]{1,40}$')
    zone: Zone


class SetWorkPriority(Contract):
    kind: Literal['SetWorkPriority']
    pawn: str = Field(min_length=1)
    work_type: str = Field(min_length=1)
    priority: int = Field(ge=0, le=4)


class CreateBill(Contract):
    kind: Literal['CreateBill']
    bench: str = Field(min_length=1)
    recipe: str = Field(min_length=1)
    target_count: int = Field(ge=1, le=10000)


class DraftPawn(Contract):
    kind: Literal['DraftPawn']
    pawn: str = Field(min_length=1)
    drafted: bool


class MovePawn(Contract):
    kind: Literal['MovePawn']
    pawn: str = Field(min_length=1)
    x: int = Field(ge=0)
    z: int = Field(ge=0)


Command = Annotated[SetResearch | CreateGoal | ModifyResourcePolicy | CancelGoal | BuildRoom |
                    CreateZone | SetWorkPriority | CreateBill | DraftPawn | MovePawn, Field(discriminator='kind')]
COMMAND = TypeAdapter(Command)


async def apply_command(rt, payload, *, token, revision):
    request = COMMAND.validate_python(payload)
    await rt.ensure_context(token)
    if rt.chat_revision != revision: raise ValueError('Player direction changed; request discarded')
    plan = rt.current_plan
    if isinstance(request, CreateGoal):
        if request.food_days is not None and request.goal != 'EnsureFoodSupply':
            raise ValueError('food_days applies only to EnsureFoodSupply')
        goal = plan.colony_goals.setdefault(request.goal, ColonyGoal(priority_class=2))
        plan.control.setdefault('suppressed_goals',{}).pop(request.goal,None)
        goal.source, goal.cancelled, goal.status = 'PLAYER', False, 'active'
        goal.reason = ''
        if request.food_days is not None:
            goal.target['food_days'] = request.food_days
            policy = plan.control.setdefault('policy', {})
            policy['food_target_days'] = request.food_days
            policy['food_min_days'] = min(request.food_days*.7, request.food_days-0.1)
        result = {'goal': request.goal, 'source': 'PLAYER', 'status': goal.status, 'target': goal.target}
    elif isinstance(request, ModifyResourcePolicy):
        observed = await rt.game.query('home/colony_facts', planning=True)
        known = set(observed.get('resources', {})) | {resource for definition in observed.get('definitions', {}).values()
                                                    for resource in definition.get('costs', {})}
        if request.resource not in known:
            raise ValueError('Use an observed resource definition; this policy key is unknown: '+request.resource)
        await rt.ensure_context(token)
        if rt.chat_revision != revision: raise ValueError('Player direction changed; policy not updated')
        plan.control.setdefault('resource_policy', {})[request.resource] = request.model_dump(exclude={'kind','resource'})
        result = {'resource': request.resource, 'policy': plan.control['resource_policy'][request.resource]}
    elif isinstance(request, CancelGoal):
        goal = plan.colony_goals.get(request.goal)
        if goal is None: raise ValueError('Unknown goal; inspect controller state for exact IDs')
        goal.cancelled, goal.status, goal.reason = True, 'blocked', 'Cancelled by player'
        if goal.target.get('satisfies'):
            plan.control.setdefault('suppressed_goals',{})[goal.target['satisfies']] = request.goal
        for step in goal.steps:
            if step in plan.progress and plan.progress[step].state != 'complete': plan.cancel(step)
        result = {'cancelled': request.goal, 'existing_native_orders': 'Retained; cancellation stops new controller orders and does not erase already issued game orders'}
    else:
        purpose = getattr(request, 'purpose', 'production')
        if isinstance(request, SetResearch): action = native('home/research', set=request.project, watch=False)
        elif isinstance(request, BuildRoom): action = request.room.model_dump()
        elif isinstance(request, CreateZone): action = request.zone.model_dump()
        elif isinstance(request, SetWorkPriority):
            roster = await rt.game.query('home/list_pawns',colonistsOnly=True,work=True)
            pawn = next((p for p in roster.get('pawns',[]) if p.get('thingId')==request.pawn),None)
            work = next((w for w in (pawn or {}).get('work',{}).get('types',[]) if w.get('name')==request.work_type),None)
            if not work or work.get('disabled') is not False:
                raise ValueError('Work type must be observed and available for this colonist')
            action = native('home/pawn_config', pawn=request.pawn, work=f'{request.work_type}={request.priority}', watch=False)
        elif isinstance(request, CreateBill):
            action = native('home/bills', action='add', bench=request.bench, recipe=request.recipe,
                repeatMode='TargetCount', targetCount=request.target_count,
                unpauseWhenYouHave=max(0, request.target_count//2), pauseWhenSatisfied='on', watch=False)
        elif isinstance(request, DraftPawn):
            action = native('home/order', action='draft' if request.drafted else 'undraft', pawn=request.pawn, watch=False)
            purpose = 'defense'
        elif isinstance(request, MovePawn):
            action = native('home/order', action='goto', pawn=request.pawn, x=request.x, z=request.z, watch=False)
            action['completion'] = 'pawn_at_position'
            purpose = 'defense'
        else: raise ValueError('Unsupported command')
        intent = getattr(request, 'intent_id', f'command-{revision}-{fingerprint(payload)[:10]}')
        identity = 'player-'+intent
        prior = next((s for s in plan.spec.steps if s.id == identity), None)
        if prior is None:
            last = plan.control.get('player_intents', {}).get(intent, {}).get('step')
            prior = next((s for s in plan.spec.steps if s.id == last), None)
        if prior:
            if prior.action.model_dump() == PlanStep(id=identity, title=request.kind, action=action,
                    completion_criteria='Native desired state observed').action.model_dump():
                return {'existing_step': prior.id, 'state': plan.progress[prior.id].state}
            if plan.progress[prior.id].issued:
                raise ValueError('This intent has issued game orders. Inspect and cancel/change construction explicitly before relocating it.')
            # Keep previous identities as durable cancelled intent; a follow-up uses
            # a new version while preserving the same conversational intent record.
            identity += '-'+str(revision)
        goal_id = 'intent-'+intent if isinstance(request,(BuildRoom,CreateZone)) else None
        step = PlanStep(id=identity, title=request.kind, action=action, priority=75, goal_id=goal_id,
            source='PLAYER', purpose=purpose, completion_criteria='Native desired state observed')
        reason = 'Explicit player command: '+request.kind
        if prior:
            if any(d.step == prior.id for s in plan.spec.steps for d in s.after):
                raise ValueError('Dependent work references this intent; revise its dependencies before replacing it')
            spec = plan.spec.model_dump()
            spec['steps'] = [s.model_dump() for s in plan.spec.steps if s.id != prior.id] + [step.model_dump()]
            decision = Decision(expected_revision=plan.revision, disposition='revise', assessment=reason,
                rationale=reason, reply=reason, plan=PlanSpec.model_validate(spec))
        else:
            decision = CommitSteps(expected_revision=plan.revision, reason=reason, steps=[step]).decision(plan)
        await rt.commit_strategy(decision,
            actor=ModelRole.STRATEGIST, expected_token=token, expected_revision=revision)
        if prior:
            plan.progress[prior.id].state = 'cancelled'
            plan.cancelled_ids.append(prior.id)
            plan.cancelled_actions.append(prior.signature())
        plan.control.setdefault('player_intents', {})[intent] = {'step': identity, 'request': request.model_dump()}
        if goal_id:
            satisfies = ('EnsureInitialShelter' if isinstance(request,BuildRoom) and purpose=='shelter' else
                         'EnsureFoodStorage' if isinstance(request,BuildRoom) and purpose=='storage' else
                         'EnsureFoodSupply' if isinstance(request,CreateZone) and request.zone.zone_type=='growing' else '')
            goal = plan.colony_goals.setdefault(goal_id,ColonyGoal(source='PLAYER',priority_class=2))
            goal.status, goal.cancelled, goal.reason = 'active', False, ''
            goal.steps = [identity]
            goal.target = {'satisfies':satisfies,'intent_id':intent}
            goal.evidence['request'] = request.model_dump()
        if isinstance(request, SetWorkPriority):
            plan.control.setdefault('work_overrides', {}).setdefault(request.pawn, {})[request.work_type] = request.priority
        result = {'step': identity, 'source': 'PLAYER', 'state': plan.progress[identity].state,
                  'execution': 'Queued for Hands; native labor is verified separately'}
        if rt.mode == 'manual': rt.manual_requests.append((identity,token,revision))
    rt.note('player_command_accepted', request.kind, request=payload, result=result)
    rt.persist()
    return result
