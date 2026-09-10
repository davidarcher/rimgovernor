"""Player semantic requests join the same durable goals and action executor."""
from typing import Annotated, Literal
from pydantic import Field, TypeAdapter, model_validator
from .colony_plan import Contract, ColonyGoal, CommitSteps, Decision, PlanSpec, PlanStep, RoomShell, RoomBounds, Buildings, Zone, Cell
from .colony_skills import native
from .config import ModelRole
from .strategic_state import fingerprint

class ResearchRefused(ValueError):
    """Native research admission failed; preserve its factual explanation."""


class SetResearch(Contract):
    kind: Literal['SetResearch']
    project: str = Field(min_length=1)


class CreateGoal(Contract):
    kind: Literal['CreateGoal']
    goal: Literal['EnsureFoodSupply', 'EnsureInitialShelter', 'EnsureFoodStorage', 'EnsureCooking',
                  'EnsureTemperatureSafety', 'EnsureBasicPower', 'EnsureBasicDefense', 'MaintainWood', 'MaintainResource']
    food_days: float | None = Field(default=None, ge=1, le=120,
        description='Food stock runway target in days, valid only for EnsureFoodSupply. Food storage is a separate stockpile goal.')

    resource: str | None = Field(default=None, description='Exact native resource definition or label, required for MaintainResource.')
    quantity: int | None = Field(default=None, ge=1, le=100000, description='Maintained stock target, required for MaintainResource.')

    @model_validator(mode='after')
    def valid_target(self):
        if self.food_days is not None and self.goal!='EnsureFoodSupply':
            raise ValueError('food_days applies only to EnsureFoodSupply, not storage or another goal')
        if (self.goal == 'MaintainResource') != (self.resource is not None and self.quantity is not None):
            raise ValueError('MaintainResource requires both resource and quantity; other goals do not take these fields')
        if self.goal != 'MaintainResource' and (self.resource is not None or self.quantity is not None):
            raise ValueError('Resource targets apply only to MaintainResource')
        return self


class ModifyResourcePolicy(Contract):
    kind: Literal['ModifyResourcePolicy']
    resource: str = Field(min_length=1)
    spending: Literal['normal', 'defense_only', 'stop'] = Field(
        description='normal allows routine spending; defense_only permits only defensive work; stop prohibits all spending, including defense.')


class SetResourceReserve(Contract):
    kind: Literal['SetResourceReserve']
    resource: str = Field(min_length=1)
    reserve: int = Field(ge=0, description='Explicitly requested reserve quantity. Zero removes the reserve; spending restrictions remain unchanged.')


class CancelGoal(Contract):
    kind: Literal['CancelGoal']
    goal: str = Field(min_length=1)


class CancelConstruction(Contract):
    kind: Literal['CancelConstruction']
    intent_id: str = Field(min_length=1, description='Exact tracked player construction intent or step ID. Removes its current pending blueprints/frames and stops future placement; preserves completed buildings.')


class BuildRoom(Contract):
    kind: Literal['BuildRoom']
    intent_id: str = Field(pattern=r'^[a-zA-Z0-9_-]{1,40}$')
    room: RoomShell
    purpose: Literal['shelter', 'defense', 'production', 'storage', 'comfort'] = 'shelter'


class RelocateConstruction(Contract):
    kind: Literal['RelocateConstruction']
    intent_id: str = Field(min_length=1, description='Exact tracked player construction intent or step ID to move. Explicit removal of its old pending orders is required.')
    replacement: Annotated[RoomShell | Buildings, Field(discriminator='kind')]


class AdoptRoom(Contract):
    kind: Literal['AdoptRoom']
    intent_id: str = Field(pattern=r'^[a-zA-Z0-9_-]{1,40}$')
    bounds: RoomBounds
    entrance: Literal['north','east','south','west']
    interior_cells: list[Cell] | None = Field(default=None, min_length=1, max_length=3844,
        description='Exact complete observed native interior for a nonrectangular room; omit for the rectangular interior.')
    entrance_cell: Cell | None = Field(default=None,
        description='Exact observed completed boundary door; required with nonrectangular interior cells.')

    @model_validator(mode='after')
    def geometry(self):
        if (self.interior_cells is None) != (self.entrance_cell is None):
            raise ValueError('Nonrectangular adoption requires both exact interior cells and an entrance cell')
        if self.interior_cells is not None:
            from .shell_site import connected_cells
            points={(p.x,p.z) for p in self.interior_cells}
            if len(points)!=len(self.interior_cells):raise ValueError('Duplicate adopted room cell')
            b=self.bounds
            if any(not (b.x<x<b.x+b.width-1 and b.z<z<b.z+b.height-1) for x,z in points):
                raise ValueError('Adopted interior cells must lie inside the inspected bounds')
            x,z=self.entrance_cell.x,self.entrance_cell.z
            dx,dz={'north':(0,1),'south':(0,-1),'east':(1,0),'west':(-1,0)}[self.entrance]
            inside=(x-dx,z-dz)
            if (x,z) in points or (x+dx,z+dz) in points or connected_cells(inside,points)!=points:
                raise ValueError('Adopted geometry needs a connected interior and a boundary entrance facing outside')
        return self


class CreateZone(Contract):
    kind: Literal['CreateZone']
    intent_id: str = Field(pattern=r'^[a-zA-Z0-9_-]{1,40}$')
    zone: Zone


class PlaceBuildings(Contract):
    kind: Literal['PlaceBuildings']
    buildings: Buildings
    purpose: Literal['shelter','defense','production','storage','comfort'] = 'production'


class EditZone(Contract):
    kind: Literal['EditZone']
    zone_id: int = Field(ge=0, description='Exact existing zone ID from current native inspection.')
    operation: Literal['add', 'remove', 'delete', 'crop', 'filter']
    cells: list[Cell] = Field(default_factory=list, max_length=1024)
    crop: str | None = None
    preset: Literal['everything', 'nothing', 'food', 'perishables', 'nonperishables', 'outdoorSafe'] | None = None
    allow: list[str] = Field(default_factory=list, max_length=64)
    disallow: list[str] = Field(default_factory=list, max_length=64)
    priority: Literal['Low', 'Normal', 'Preferred', 'Important', 'Critical'] | None = None

    @model_validator(mode='after')
    def operation_fields(self):
        if bool(self.cells) != (self.operation in ('add', 'remove')):
            raise ValueError('Only add/remove require explicit inspected cells')
        if bool(self.crop) != (self.operation == 'crop'):
            raise ValueError('Only crop requires a sowable native crop definition')
        filters = self.preset is not None or self.allow or self.disallow or self.priority is not None
        if bool(filters) != (self.operation == 'filter'):
            raise ValueError('Only filter requires storage settings')
        if any(not value.strip() or any(c in value for c in ',;\n\r') for value in self.allow+self.disallow):
            raise ValueError('Each filter entry must be one observed native definition name')
        return self


class SetWorkPriority(Contract):
    kind: Literal['SetWorkPriority']
    pawn: str = Field(min_length=1, description='Observed colonist ID or an exact, unambiguous colonist name.')
    work_type: str = Field(min_length=1)
    priority: int = Field(ge=0, le=4)


class CreateBill(Contract):
    kind: Literal['CreateBill']
    bench: str = Field(min_length=1)
    recipe: str = Field(min_length=1)
    target_count: int = Field(ge=1, le=10000)
    ingredients: list[str] | None = Field(default=None, min_length=1, max_length=64,
        description='Optional complete ingredient whitelist of observed native ThingDef names. Omit to preserve recipe defaults.')

    @model_validator(mode='after')
    def exact_ingredients(self):
        if self.ingredients is not None and any(not value.strip() or any(c in value for c in ',;\n\r') for value in self.ingredients):
            raise ValueError('Each ingredient must be one native definition name')
        return self


class SetBuildingTemperature(Contract):
    kind: Literal['SetBuildingTemperature']
    thing: str = Field(min_length=1, description='Exact observed native building ThingID; never a building group or guessed label.')
    celsius: float = Field(ge=-273.15, le=1000, description='Explicit requested temperature setpoint in Celsius; observed room temperature is a separate outcome.')


class DraftPawn(Contract):
    kind: Literal['DraftPawn']
    pawn: str = Field(min_length=1)
    drafted: bool


class MovePawn(Contract):
    kind: Literal['MovePawn']
    pawn: str = Field(min_length=1)
    x: int = Field(ge=0)
    z: int = Field(ge=0)


Command = Annotated[SetResearch | CreateGoal | ModifyResourcePolicy | SetResourceReserve | CancelGoal | CancelConstruction | RelocateConstruction | AdoptRoom | BuildRoom |
                    PlaceBuildings | CreateZone | EditZone | SetWorkPriority | CreateBill | SetBuildingTemperature | DraftPawn | MovePawn, Field(discriminator='kind')]
COMMAND = TypeAdapter(Command)
COMMAND_TYPES = (SetResearch,CreateGoal,ModifyResourcePolicy,SetResourceReserve,CancelGoal,CancelConstruction,RelocateConstruction,AdoptRoom,BuildRoom,PlaceBuildings,
                 CreateZone,EditZone,SetWorkPriority,CreateBill,SetBuildingTemperature,DraftPawn,MovePawn)
COMMAND_NAMES = {kind.__name__ for kind in COMMAND_TYPES}


def resource_options(resources):
    """Prefer exact native display labels; ambiguous labels retain definition IDs."""
    labels=[str(label).strip().casefold() for label in resources.values()]
    return {label if label and labels.count(str(label).strip().casefold())==1 else identity:identity
            for identity,label in resources.items()}


def resolve_resource(value, resources):
    if value in resources: return value
    matches=[identity for label,identity in resource_options(resources).items()
             if label.strip().casefold()==value.strip().casefold()]
    if len(matches)!=1: raise ValueError('Use an exact observed resource name; the resource is unknown or ambiguous: '+value)
    return matches[0]


def semantic_tools(resources=None):
    from .consultation import structured_tool
    descriptions = {
        'SetResearch':'Select a research project requested by the player.',
        'CreateGoal':'Set a persistent colony target. MaintainResource with resource and quantity means keep acquiring or producing that stock, for example maintain 50 steel. The deterministic controller chooses downstream actions.',
        'ModifyResourcePolicy':'Change a resource spending restriction while preserving its existing reserve.',
        'SetResourceReserve':'Protect an explicitly requested numeric stock floor from spending, preserving the spending restriction. This does not acquire stock. Use CreateGoal/MaintainResource to replenish or maintain a stock target. Do not use for spending-only instructions.',
        'CancelGoal':'Stop future controller orders for a named goal. KEEP all existing game blueprints and frames. Use this when the player says to keep, retain or leave existing orders in place.',
        'CancelConstruction':'REMOVE existing pending blueprints and partly built frames for a tracked player intent. Use ONLY when the player explicitly requests removing those game orders. NEVER use when told to keep blueprints, frames or existing orders; use CancelGoal instead. Completed buildings remain.',
        'BuildRoom':'Request a room shell with walls and an entrance using inspected geometry.',
        'RelocateConstruction':'Explicitly move a tracked unfinished construction intent. Validates the replacement before removing exact old pending orders. Requires explicit permission to remove old orders; completed buildings cannot be relocated this way. Use inspected replacement geometry.',
        'AdoptRoom':'Use one existing enclosed, fully roofed native room as the preferred colony shelter. Supply inspected bounds and entrance side; for a nonrectangular room also supply its exact complete interior_cells and completed entrance_cell. Adds no building orders; furnishing rechecks the exact native room and preserves a continuous aisle.',
        'PlaceBuildings':'Place a semantic batch of furniture or buildings using observed definitions and positions.',
        'CreateZone':'Create a growing zone or stockpile specifically requested by the player.',
        'EditZone':'Explicitly edit an existing observed zone: expand, remove cells, delete, change crop or storage filter. Deletion removes the zone designation; it does not destroy stored items. Use exact native filter definitions from inspection.',
        'SetWorkPriority':'Change persistent work assignments, independently of the current pawn job. Priority 0 disables a work type even when the pawn is currently doing another job.',
        'CreateBill':'Create a production bill with a target count and, when requested, a complete ingredient whitelist using observed native recipe definitions.',
        'SetBuildingTemperature':'Set the temperature control of one exact observed building, such as a cooler or heater. This sets the control; it does not certify actual cooling or heating.',
        'DraftPawn':'Draft or undraft a pawn for direct combat control. This does not change work assignments.',
        'MovePawn':'Order a pawn to a specific inspected position.',
    }
    result=[]
    for kind in COMMAND_TYPES:
        schema=kind.model_json_schema()
        schema['properties'].pop('kind')
        schema['required']=[key for key in schema.get('required',[]) if key!='kind']
        if kind in (ModifyResourcePolicy,SetResourceReserve) and resources:
            schema['properties']['resource']['enum']=sorted(resource_options(resources) if isinstance(resources,dict) else resources)
            schema['properties']['resource']['description']=(
                'Choose exactly the native resource named by the player. A base resource name does not '
                'include other resources with extra qualifiers in their labels. Change multiple resources '
                'only when each is requested. Use the exact displayed name from the enum.')
        result.append(structured_tool(kind.__name__,descriptions[kind.__name__],schema))
    return result


def command_schema():
    # Provider function contracts need a root object with properties. Nest the
    # domain union and keep its referenced definitions at the document root.
    request = COMMAND.json_schema()
    definitions = request.pop('$defs',{})
    return {'type':'object','properties':{'request':request},'required':['request'],
            'additionalProperties':False,'$defs':definitions}


def resolve_goal_id(query, goals):
    if query in goals: return query
    def normalized(value): return ''.join(c for c in value.casefold() if c.isalnum())
    matches = []
    for identity, value in goals.items():
        row = value.model_dump() if hasattr(value,'model_dump') else value
        aliases = [identity,identity.removeprefix('intent-'),row.get('label',''),
                   row.get('target',{}).get('intent_id','')]
        if normalized(query) in {normalized(a) for a in aliases if a}: matches.append(identity)
    if len(matches)!=1: raise ValueError('Goal name is unknown or ambiguous; inspect controller state for an exact ID')
    return matches[0]


def resolve_colonist(query, pawns):
    matches=[p for p in pawns if p.get('thingId')==query]
    if not matches:
        matches=[p for p in pawns if isinstance(p.get('name'),str)
                 and p['name'].strip().casefold()==query.strip().casefold()]
    if len(matches)!=1 or not matches[0].get('thingId'):
        raise ValueError('Colonist name is unknown or ambiguous; use an observed exact colonist ID')
    return matches[0]


def command_confirmation(name, result):
    if name=='CreateGoal':
        days=result.get('target',{}).get('food_days')
        return f'Food target set to {days:g} days.' if days is not None else 'Persistent colony goal accepted: '+result['goal']+'.'
    if name in ('ModifyResourcePolicy','SetResourceReserve'):
        label='Components' if result['resource']=='ComponentIndustrial' else result['resource']
        policy=result['policy']
        rule={'normal':'normal spending','defense_only':'defense spending only','stop':'all spending stopped, including defense'}[policy['spending']]
        return f"{label}: {rule} for controller orders and production inputs. Reserve: {policy['reserve']}. Native enforcement is queued through Hands."
    if name=='CancelGoal':
        return 'Cancelled '+result['cancelled'].removeprefix('intent-').replace('-',' ')+'. Existing game orders remain in place.'
    if name=='CancelConstruction':
        return 'Construction cancellation accepted. Exact pending targets are tracked in the colony plan; completed buildings are preserved.'
    if name=='RelocateConstruction':
        return 'Construction relocation accepted. The validated replacement waits for exact old-order cancellation; pawn construction is tracked separately.'
    if name=='AdoptRoom':
        return 'Existing roofed room selected as the colony shelter. Native furnishings and temperature remain separately verified.'
    titles={'SetResearch':'Research change','SetWorkPriority':'Work assignment change','DraftPawn':'Draft change',
            'MovePawn':'Movement order','BuildRoom':'Room shell','PlaceBuildings':'Building batch',
            'CreateZone':'Zone','EditZone':'Zone edit','CreateBill':'Production bill','SetBuildingTemperature':'Temperature setpoint'}
    return titles.get(name,name)+' accepted. Execution is tracked in the colony plan.'


async def apply_command(rt, payload, *, token, revision):
    request = COMMAND.validate_python(payload)
    await rt.ensure_context(token)
    if rt.chat_revision != revision: raise ValueError('Player direction changed; request discarded')
    plan = rt.current_plan
    if isinstance(request, CreateGoal):
        goal_id = request.goal
        if request.goal == 'MaintainResource':
            observed = await rt.game.query('home/colony_facts', planning=True)
            request.resource = resolve_resource(request.resource, observed.get('policyResources', {}))
            await rt.ensure_context(token)
            if rt.chat_revision != revision: raise ValueError('Player direction changed; target not updated')
            goal_id += '-' + request.resource
        if request.goal == 'MaintainResource' and goal_id in plan.colony_goals:
            prior = plan.colony_goals[goal_id]
            prior.reopen_methods()
            prior.attempts += 1
            for step_id in prior.steps:
                if step_id in plan.progress and plan.progress[step_id].state == 'blocked': plan.cancel(step_id)
        goal = plan.colony_goals.setdefault(goal_id, ColonyGoal(priority_class=2))
        if request.goal == 'MaintainResource': goal.target = {'resource': request.resource, 'quantity': request.quantity}
        plan.control.setdefault('suppressed_goals',{}).pop(request.goal,None)
        goal.source, goal.cancelled, goal.status = 'PLAYER', False, 'active'
        goal.reason = ''
        if request.food_days is not None:
            goal.target['food_days'] = request.food_days
            policy = plan.control.setdefault('policy', {})
            policy['food_target_days'] = request.food_days
            policy['food_min_days'] = min(request.food_days*.7, request.food_days-0.1)
        result = {'goal': goal_id, 'source': 'PLAYER', 'status': goal.status, 'target': goal.target}
    elif isinstance(request, (ModifyResourcePolicy,SetResourceReserve)):
        observed = await rt.game.query('home/colony_facts', planning=True)
        known = set(observed.get('resources', {})) | set(observed.get('policyResources', {})) | {resource for definition in observed.get('definitions', {}).values()
                                                    for resource in definition.get('costs', {})}
        request.resource=resolve_resource(request.resource,
            {identity:observed.get('policyResources',{}).get(identity,identity) for identity in known})
        if request.resource not in known:
            raise ValueError('Use an observed resource definition; this policy key is unknown: '+request.resource)
        await rt.ensure_context(token)
        if rt.chat_revision != revision: raise ValueError('Player direction changed; policy not updated')
        previous_policy = dict(plan.control['resource_policy'][request.resource]) if request.resource in plan.control.get('resource_policy', {}) else None
        policy = plan.control.setdefault('resource_policy', {}).setdefault(request.resource, {'reserve':0,'spending':'normal'})
        policy.update(request.model_dump(exclude={'kind','resource'}))
        from .production_policy import policy_arguments
        from .colony_plan import NativeOperation
        action = NativeOperation(tool='home/production_policy', arguments=policy_arguments(rt))
        identity = 'resource-policy-' + fingerprint({'arguments': action.arguments, 'revision': revision})[:24]
        try:
            if not any(s.id == identity for s in plan.spec.steps):
                step = PlanStep(id=identity, title='Apply resource production policy', action=action, source='PLAYER', priority=100,
                                completion_criteria='Native production budgets match persistent player policy')
                decision = CommitSteps(expected_revision=plan.revision, reason='Player production budget', steps=[step]).decision(plan)
                await rt.commit_strategy(decision, actor=ModelRole.STRATEGIST, expected_token=token, expected_revision=revision)
        except Exception:
            if previous_policy is None: plan.control['resource_policy'].pop(request.resource, None)
            else: plan.control['resource_policy'][request.resource] = previous_policy
            if not plan.control['resource_policy']: plan.control.pop('resource_policy')
            raise
        if rt.mode == 'manual': rt.manual_requests.append((identity, token, revision))
        result = {'resource': request.resource, 'policy': plan.control['resource_policy'][request.resource], 'step': identity}
    elif isinstance(request, AdoptRoom):
        from .room_adoption import adopt
        result = await adopt(rt, request, token=token, revision=revision)
    elif isinstance(request, RelocateConstruction):
        from .construction_relocation import relocate
        result = await relocate(rt, request, token=token, revision=revision)
    elif isinstance(request, CancelConstruction):
        from .construction_cancellation import capture_targets, validate_player_authorization, requests_relocation
        from .colony_plan import CancelConstructionAction
        if requests_relocation(rt,revision):
            raise ValueError('Player requested relocation. Use RelocateConstruction so the replacement is validated before any removal.')
        validate_player_authorization(rt, revision)
        intent = plan.control.get('player_intents', {}).get(request.intent_id, {})
        source_id = intent.get('step', request.intent_id)
        source = next((s for s in plan.spec.steps if s.id == source_id), None)
        if source is None:
            raise ValueError('Unknown construction intent; inspect the plan for an exact intent or step ID')
        targets = await capture_targets(rt.game, plan, source.id)
        await rt.ensure_context(token)
        if rt.chat_revision != revision:
            raise ValueError('Player direction changed; existing construction preserved')
        action = CancelConstructionAction(source_step=source.id, targets=targets,
            **{k:rt.identity[k] for k in ('colonyId','loadToken','mapId')})
        identity = 'cancel-construction-'+fingerprint(action.model_dump())[:24]
        existing = next((s for s in plan.spec.steps if s.id == identity), None)
        if existing:
            return {'step':identity, 'state':plan.progress[identity].state, 'targets':len(targets)}
        step = PlanStep(id=identity, title='Cancel construction: '+request.intent_id[:100],
            action=action, source='PLAYER', priority=100, completion_criteria='Exact captured pending construction targets removed or observed absent')
        decision = CommitSteps(expected_revision=plan.revision, reason='Explicit player construction cancellation', steps=[step]).decision(plan)
        await rt.commit_strategy(decision, actor=ModelRole.STRATEGIST, expected_token=token, expected_revision=revision)
        if rt.mode == 'manual':
            rt.manual_requests.append((identity,token,revision))
        result = {'step':identity, 'state':plan.progress[identity].state, 'targets':len(targets),
            'execution':'Queued for Hands; completed buildings preserved'}
    elif isinstance(request, CancelGoal):
        request.goal = resolve_goal_id(request.goal,plan.colony_goals)
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
        if isinstance(request, SetResearch):
            try:
                preview=await rt.inspect_native('home/research',{'set':request.project,'dryRun':True,'watch':False})
            except ValueError as error:
                raise ResearchRefused('Research request blocked: '+str(error)) from error
            write=preview.get('write') or {}
            resolved=(write.get('resolved') or {}).get('defName')
            if write.get('refused') is not False or not resolved:
                raise ResearchRefused('Research request blocked: '+(write.get('reason') or 'Project could not be resolved and validated'))
            request.project=resolved
            action = native('home/research', set=request.project, watch=False)
        elif isinstance(request, BuildRoom): action = request.room.model_dump()
        elif isinstance(request, PlaceBuildings): action = request.buildings.model_dump()
        elif isinstance(request, CreateZone): action = request.zone.model_dump()
        elif isinstance(request, EditZone):
            arguments = dict(op=request.operation, zone=str(request.zone_id), watch=False)
            if request.cells:
                arguments['cells'] = ';'.join(f'{cell.x},{cell.z}' for cell in request.cells)
            for field, native_field in (('crop','plant'),('preset','preset'),('priority','priority')):
                if (value := getattr(request, field)) is not None: arguments[native_field] = value
            for field in ('allow', 'disallow'):
                if value := getattr(request, field): arguments[field] = ','.join(value)
            preview = await rt.inspect_native('home/zone_cells', dict(arguments, dryRun=True))
            if preview.get('success') is not True or any(cell.get('accepted') is not True for cell in preview.get('cells', [])):
                raise ValueError('Native zone edit refused: '+str(preview.get('error') or preview.get('reason') or preview.get('cells')))
            action = native('home/zone_cells', **arguments)
        elif isinstance(request, SetWorkPriority):
            roster = await rt.game.query('home/list_pawns',colonistsOnly=True,work=True)
            pawn = resolve_colonist(request.pawn,roster.get('pawns',[]))
            request.pawn=pawn['thingId']
            work = next((w for w in (pawn or {}).get('work',{}).get('types',[]) if w.get('name')==request.work_type),None)
            if not work or work.get('disabled') is not False:
                raise ValueError('Work type must be observed and available for this colonist')
            action = native('home/pawn_config', pawn=request.pawn, work=f'{request.work_type}={request.priority}', watch=False)
        elif isinstance(request, CreateBill):
            action = native('home/bills', action='add', bench=request.bench, recipe=request.recipe,
                repeatMode='TargetCount', targetCount=request.target_count,
                unpauseWhenYouHave=max(0, request.target_count//2), pauseWhenSatisfied='on', watch=False)
            if request.ingredients is not None:
                action['arguments']['only'] = ','.join(request.ingredients)
                preview = await rt.inspect_native('home/bills', dict(action['arguments'], dryRun=True))
                if preview.get('success') is not True or preview.get('write', {}).get('refused') is not False:
                    raise ValueError('Native bill ingredient whitelist refused: '+str(
                        (preview.get('write') or {}).get('reason') or preview.get('error') or preview.get('reason')
                        or 'Ingredient eligibility was not confirmed'))
        elif isinstance(request, SetBuildingTemperature):
            observed = await rt.game.query('home/list_buildings', aggregate=False, playerOnly=True)
            matches = [building for building in observed.get('buildings', []) if building.get('thingId') == request.thing]
            if (observed.get('success') is not True or observed.get('skipped', {}).get('byMaxDetailed')
                    or len(matches) != 1 or matches[0].get('isBlueprint') or matches[0].get('isFrame')):
                raise ValueError('Temperature control needs one exact observed completed player building')
            preview = await rt.inspect_native('home/building_config',
                {'thing':request.thing, 'temperature':request.celsius, 'dryRun':True, 'watch':False})
            fields = [field for field in preview.get('fields', []) if field.get('field') == 'temperature']
            if (preview.get('success') is not True or preview.get('refused') != []
                    or len(fields) != 1 or fields[0].get('refused') is not False):
                raise ValueError('Native building temperature control refused this setpoint')
            action = native('home/building_config', thing=request.thing, temperature=request.celsius, watch=False)
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
            archived_id = last or identity
            archived = (plan._archive_read(archived_id) if plan._archive_read is not None else None)
            if archived:
                archived_action = archived['step']['action']
                proposed = PlanStep(id=identity, title=request.kind, action=action,
                                    completion_criteria='Native desired state observed').action.model_dump()
                if archived_action == proposed:
                    return {'existing_step': archived_id, 'state': 'complete', 'archived': True}
                raise ValueError('This intent is completed and archived. Use a new explicit intent for new work; its receipts are preserved.')
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
                         'EnsureFoodSupply' if isinstance(request,CreateZone) and request.zone.zone_type=='growing' else
                         'EnsureFoodStorage' if isinstance(request,CreateZone) and request.zone.preset in ('food','perishables') else '')
            goal = plan.colony_goals.setdefault(goal_id,ColonyGoal(source='PLAYER',priority_class=2))
            goal.status, goal.cancelled, goal.reason = 'active', False, ''
            goal.steps = [identity]
            goal.target = {'satisfies':satisfies,'intent_id':intent}
            goal.evidence['request'] = request.model_dump()
        if isinstance(request,(DraftPawn,MovePawn)):
            plan.control.setdefault('player_draft_overrides',{})[request.pawn]=getattr(request,'drafted',True)
        if isinstance(request, SetWorkPriority):
            plan.control.setdefault('work_overrides', {}).setdefault(request.pawn, {})[request.work_type] = request.priority
        result = {'step': identity, 'source': 'PLAYER', 'state': plan.progress[identity].state,
                  'execution': 'Queued for Hands; native labor is verified separately'}
        if rt.mode == 'manual': rt.manual_requests.append((identity,token,revision))
    rt.note('player_command_accepted', request.kind, request=payload, result=result)
    rt.persist()
    return result
