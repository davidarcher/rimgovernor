"""Strategic commitments and deterministic progress are distinct durable state."""
import hashlib
import json
from copy import deepcopy
from typing import Annotated, Literal
from pydantic import BaseModel, ConfigDict, Field, PrivateAttr, model_validator, model_serializer
from .config import ModelRole


class Contract(BaseModel):
    model_config = ConfigDict(extra='forbid')


class Cell(Contract):
    x: int = Field(ge=0)
    z: int = Field(ge=0)


class Rectangle(Cell):
    width: int = Field(ge=1, le=64)
    height: int = Field(ge=1, le=64)

    def cells(self):
        return [(x, z) for z in range(self.z, self.z+self.height) for x in range(self.x, self.x+self.width)]


class Placement(Cell):
    def_name: str = Field(min_length=1)
    rotation: Literal['north', 'east', 'south', 'west'] = 'north'
    materials: list[str] = Field(default_factory=list, max_length=8, description='Observed acceptable stuff defs in preference order; native eligibility and available quantities choose one.')


class Buildings(Contract):
    kind: Literal['place_buildings'] = 'place_buildings'
    placements: list[Placement] = Field(min_length=1, max_length=256)


class RoomBounds(Rectangle):
    width: int = Field(ge=4, le=64)
    height: int = Field(ge=4, le=64)


class RoomShell(Contract):
    kind: Literal['build_room_shell'] = 'build_room_shell'
    bounds: RoomBounds
    wall_def: str = Field(min_length=1)
    door_def: str = Field(min_length=1)
    materials: list[str] = Field(min_length=1, max_length=8)
    entrance: Literal['north', 'east', 'south', 'west']

    @model_validator(mode='after')
    def interior(self):
        if self.wall_def == self.door_def:
            raise ValueError('A room shell needs distinct wall and entrance definitions; a perimeter of doors is not a wall shell. Inspect the construction catalog index.')
        if self.bounds.width < 4 or self.bounds.height < 4:
            raise ValueError('A room shell needs an interior; use a building batch for a wall segment')
        return self


def zone_schema(schema):
    # Grammar compilers may ignore object constraints beside anyOf. Each
    # alternative must describe the complete object, including its action tag.
    alternatives = []
    for zone_type in ('stockpile', 'growing'):
        branch = deepcopy(schema)
        branch['properties']['zone_type'] = {'type': 'string', 'const': zone_type}
        branch['required'] = list(dict.fromkeys([*branch.get('required', []), 'kind']))
        if zone_type == 'growing':
            branch['properties']['crop']['minLength'] = 1
            branch['required'].append('crop')
        alternatives.append(branch)
    schema['anyOf'] = alternatives


class Zone(Contract):
    model_config = ConfigDict(extra='forbid', json_schema_extra=zone_schema)
    kind: Literal['create_zone'] = 'create_zone'
    zone_type: Literal['stockpile', 'growing']
    label: str = Field(min_length=1, max_length=80)
    patches: list[Rectangle] = Field(min_length=1, max_length=32)
    crop: str = Field(default='', description='Required and nonempty for growing zones: an observed sowable native definition. Stockpiles do not require a crop.')
    preset: str | None = None
    allow: list[str] = Field(default_factory=list, max_length=64,
        description='Observed native item definitions to allow after the stockpile preset.')
    covered_empty: bool = Field(default=False,
        description='Stockpile creation requires currently roofed, empty, unzoned cells at native dispatch.')
    priority: Literal['Low', 'Normal', 'Preferred', 'Important', 'Critical'] = 'Normal'

    @model_validator(mode='after')
    def crop_required(self):
        if self.zone_type == 'growing' and not self.crop:
            raise ValueError('A growing zone needs an observed sowable crop definition')
        if self.zone_type == 'growing' and (self.allow or self.covered_empty):
            raise ValueError('Storage filters and covered cells require a stockpile')
        return self


class ConstructionTarget(Cell):
    thing: str = Field(min_length=1)
    expectedDef: str = Field(min_length=1)
    expectedStuff: str = ''


class CancelConstructionAction(Contract):
    kind: Literal['cancel_construction'] = 'cancel_construction'
    source_step: str = Field(min_length=1)
    colonyId: str = Field(min_length=1)
    loadToken: str = Field(min_length=1)
    mapId: int
    targets: list[ConstructionTarget] = Field(default_factory=list, max_length=256)

    @model_validator(mode='after')
    def unique_targets(self):
        if len({t.thing for t in self.targets}) != len(self.targets):
            raise ValueError('Construction cancellation targets must be unique')
        return self


class CaravanTarget(Contract):
    pawn_ids: list[str] = Field(min_length=1)
    destination: int = Field(ge=0)
    caravan_id: str | None = None
    cargo: dict[str, int] = Field(default_factory=dict)
    carried_cargo: dict[str, int] = Field(default_factory=dict)
    storage_cargo: dict[str, int] = Field(default_factory=dict)
    stored_baseline: dict[str, int] = Field(default_factory=dict)

    @model_validator(mode='after')
    def valid_manifest(self):
        if len(set(self.pawn_ids)) != len(self.pawn_ids) or any(not p.startswith('Thing_') for p in self.pawn_ids):
            raise ValueError('Caravan members require unique native pawn IDs')
        if any(type(count) is not int or count <= 0 for count in [*self.cargo.values(), *self.carried_cargo.values(), *self.storage_cargo.values(), *self.stored_baseline.values()]):
            raise ValueError('Caravan cargo counts must be positive integers')
        return self


class NativeOperation(Contract):
    kind: Literal['native_operation'] = 'native_operation'
    tool: Literal['home/upkeep_bed', 'home/recovery_area', 'home/recover_service', 'home/husbandry_config', 'home/relieve_need', 'home/medical_operations', 'home/caravan_gift', 'home/fulfill_quest', 'home/caravan', 'home/accept_quest', 'home/manage_waste', 'home/gear_upkeep', 'home/population', 'home/acquire_resource', 'home/production_policy', 'home/confirm_colony_names', 'home/pawn_config', 'home/building_config', 'home/bills', 'home/order',
        'home/zone_cells', 'home/trade', 'home/research', 'rimworld/apply_architect_designator',
        'rimworld/open_letter', 'rimworld/dismiss_letter', 'rimworld/click_screen_target',
        'home/install', 'home/dialog_text', 'rimworld/click_ui_target', 'rimworld/scroll_ui_target',
        'rimworld/open_main_tab', 'rimworld/close_main_tab']
    arguments: dict
    # Honest fallback for native operations lacking a higher-level compiler.
    completion: Literal['upkeep_target', 'service_recovered', 'need_recovered', 'pawn_gear', 'waste_contained', 'native_receipt', 'patient_tended', 'patient_in_bed', 'pawn_equipped', 'pawn_at_position',
                        'surgery_health', 'caravan_departed', 'caravan_arrived', 'caravan_returned', 'quest_completed'] = 'native_receipt'
    medical_effect: dict | None = None
    caravan_target: CaravanTarget | None = None

    @model_serializer(mode='wrap')
    def serialize(self, handler):
        value = handler(self)
        if self.caravan_target is None:
            value.pop('caravan_target', None)  # Preserve existing native-action fingerprints.
        if self.medical_effect is None:
            value.pop('medical_effect', None)
        return value

    @model_validator(mode='after')
    def medical_completion(self):
        if self.tool == 'home/medical_operations' or self.completion == 'surgery_health' or self.medical_effect is not None:
            if (self.tool != 'home/medical_operations' or self.completion != 'surgery_health'
                    or not self.medical_effect or not self.arguments.get('recipe')
                    or not str(self.arguments.get('patient', '')).startswith('Thing_')
                    or 'expectedHealth' not in self.arguments or not self.arguments.get('expectedCare')
                    or not self.arguments.get('colonyId') or not self.arguments.get('loadToken')
                    or type(self.arguments.get('mapId')) is not int):
                raise ValueError('Surgery requires a discovered patient, recipe, health effect and care policy')
            if (set(self.medical_effect) != {'recipe', 'part', 'addsHediff', 'removesHediff'}
                    or self.medical_effect['recipe'] != self.arguments['recipe']
                    or type(self.medical_effect['part']) is not int
                    or self.medical_effect['part'] != self.arguments.get('part')
                    or not (self.medical_effect['addsHediff'] or self.medical_effect['removesHediff'])):
                raise ValueError('Surgical effect must match the exact requested recipe and body part')
        if self.tool == 'home/recover_service' or self.completion == 'service_recovered':
            if (self.tool != 'home/recover_service' or self.completion != 'service_recovered'
                    or self.arguments.get('method') not in ('repair', 'refuel', 'breakdown')
                    or any(not str(self.arguments.get(k, '')).startswith('Thing_') for k in ('thingId', 'pawn'))):
                raise ValueError('Recovery requires exact building/pawn IDs, method and service completion')
        if self.completion == 'upkeep_target':
            if (self.tool != 'home/order' or self.arguments.get('action') not in ('haul', 'repair', 'clean')
                    or any(not str(self.arguments.get(k, '')).startswith('Thing_') for k in ('pawn', 'target'))):
                raise ValueError('Upkeep postconditions require exact native pawn and target identities')
        if self.tool == 'home/manage_waste' or self.completion == 'waste_contained':
            if (self.tool != 'home/manage_waste' or self.completion != 'waste_contained'
                    or any(not str(self.arguments.get(k, '')).startswith('Thing_') for k in ('thingId', 'pawn'))):
                raise ValueError('Waste hauling requires exact native item/pawn IDs and containment completion')
        if self.tool == 'home/gear_upkeep' or self.completion == 'pawn_gear':
            if (self.tool != 'home/gear_upkeep' or self.completion != 'pawn_gear'
                    or any(not str(self.arguments.get(k, '')).startswith('Thing_') for k in ('pawn', 'target'))
                    or not self.arguments.get('expectedLoadout')):
                raise ValueError('Apparel upkeep requires exact pawn, item, loadout signature and worn postcondition')
        if self.completion == 'need_recovered':
            if (self.tool != 'home/relieve_need' or self.arguments.get('need') not in ('food', 'rest', 'joy')
                    or not str(self.arguments.get('pawn', '')).startswith('Thing_')):
                raise ValueError('Need recovery requires an exact pawn and native need relief action')
        if self.tool == 'home/relieve_need' and self.completion != 'need_recovered':
            raise ValueError('Need relief requires observed need recovery, not an order receipt')
        if (self.tool == 'home/fulfill_quest') != (self.completion == 'quest_completed'):
            raise ValueError('Quest fulfillment requires native terminal quest completion')
        if self.completion == 'quest_completed' and not self.arguments.get('questId'):
            raise ValueError('Quest completion requires an exact native quest identity')
        if self.tool == 'home/caravan':
            expected = {'form': 'caravan_departed', 'move': 'caravan_arrived', 'visit': 'caravan_arrived', 'return': 'caravan_returned', 'stop': 'native_receipt'}
            if self.completion != expected.get(self.arguments.get('action')) or self.caravan_target is None:
                raise ValueError('Caravan orders require a matching observed outcome target')
            if self.arguments['action'] == 'form':
                if set(self.arguments.get('pawnIds', '').split(',')) != set(self.caravan_target.pawn_ids):
                    raise ValueError('Caravan manifest does not match selected pawn identities')
            elif self.arguments.get('caravanId') != self.caravan_target.caravan_id or not self.caravan_target.caravan_id:
                raise ValueError('Caravan route requires the exact observed caravan identity')
            if self.arguments['action'] not in ('return', 'stop') and self.arguments.get('destination') != self.caravan_target.destination:
                raise ValueError('Caravan outcome destination must match the native order')
        elif self.caravan_target is not None or self.completion.startswith('caravan_'):
            raise ValueError('Caravan outcome targets require a caravan operation')
        if self.completion in ('pawn_equipped', 'pawn_at_position'):
            required = 'equip' if self.completion == 'pawn_equipped' else 'goto'
            if self.tool != 'home/order' or self.arguments.get('action') != required or not str(self.arguments.get('pawn', '')).startswith('Thing_'):
                raise ValueError('Pawn postcondition requires an exact observed pawn and matching native order')
            if required == 'equip' and not str(self.arguments.get('target', '')).startswith('Thing_'):
                raise ValueError('Equipment postcondition requires an exact observed weapon')
            if required == 'goto' and any(not isinstance(self.arguments.get(k), int) for k in ('x','z')):
                raise ValueError('Movement postcondition requires destination coordinates')
        if self.completion in ('patient_tended', 'patient_in_bed'):
            required = 'tend' if self.completion == 'patient_tended' else 'rescue'
            if (self.tool != 'home/order' or self.arguments.get('action') != required
                    or not isinstance(self.arguments.get('target'), str)
                    or not self.arguments['target'].startswith('Thing_')
                    or not isinstance(self.arguments.get('pawn'), str)
                    or not self.arguments['pawn'].startswith('Thing_')):
                raise ValueError(f'{self.completion} requires a native {required} order with exact observed pawn and patient Thing IDs')
        return self


class TradeLine(Contract):
    item: str = Field(min_length=1, max_length=160, description='Observed unique item def or exact label, never a row index from an older session.')
    count: int = Field(description='Positive buys; negative sells.')

    @model_validator(mode='after')
    def nonempty(self):
        if not self.count or self.item.startswith('#'):
            raise ValueError('Use a nonzero count and an observed item name, not a session row index')
        return self


class TradeTarget(Contract):
    item: str = Field(min_length=1, max_length=160)
    stock: int = Field(ge=0, le=100000)
    max_buy: int = Field(default=0, ge=0, le=100000)
    max_sell: int = Field(default=0, ge=0, le=100000)
    max_buy_price: float = Field(default=0, ge=0, allow_inf_nan=False)
    min_sell_price: float = Field(default=0, ge=0, allow_inf_nan=False)


class TradePolicy(Contract):
    targets: list[TradeTarget] = Field(min_length=1, max_length=30)
    silver_reserve: int = Field(ge=0)

    @model_validator(mode='after')
    def unique_targets(self):
        names = [t.item.casefold() for t in self.targets]
        if len(set(names)) != len(names) or any(n.startswith('#') for n in names):
            raise ValueError('Use unique native definitions for economic targets')
        return self


class TradeAction(Contract):
    kind: Literal['trade'] = 'trade'
    trader_id: str = Field(min_length=1)
    negotiator: str = Field(min_length=1)
    lines: list[TradeLine] = Field(default_factory=list,max_length=30)
    policy: TradePolicy | None = None
    max_silver_spend: int = Field(ge=0,description='Maximum net silver the colony may pay for this deal.')

    @model_validator(mode='after')
    def unique_lines(self):
        if bool(self.lines) == (self.policy is not None):
            raise ValueError('Provide either exact lines or an economic policy')
        if len({line.item.casefold() for line in self.lines}) != len(self.lines):
            raise ValueError('Combine duplicate trade lines')
        return self


class ClockAction(Contract):
    kind: Literal['clock'] = 'clock'
    speed: Literal['Paused', 'Normal', 'Fast', 'Superfast']
    mode: Literal['colony', 'combat'] = 'colony'
    ignored_hostiles: str = ''
    ignored_downed: str = ''


class StandDown(Contract):
    kind: Literal['stand_down'] = 'stand_down'
    pawn_ids: list[str] = Field(min_length=1, max_length=100,
        description='Exact observed pawn IDs to release from AI-owned drafting. Player-owned drafts are untouched.')


Action = Annotated[Buildings | RoomShell | Zone | NativeOperation | ClockAction | StandDown | TradeAction | CancelConstructionAction, Field(discriminator='kind')]


class Dependency(Contract):
    step: str
    when: Literal['issued', 'complete'] = 'complete'


class PlanStep(Contract):
    id: str = Field(min_length=1, max_length=64, pattern=r'^[a-zA-Z0-9_-]+$')
    title: str = Field(min_length=1, max_length=160)
    priority: int = Field(default=50, ge=0, le=100)
    goal_id: str | None = None
    source: Literal['PLAYER', 'AUTOPILOT', 'LLM_ADVISOR'] = 'PLAYER'
    purpose: Literal['shelter', 'defense', 'production', 'storage', 'comfort'] = 'production'
    after: list[Dependency] = Field(default_factory=list, max_length=30)
    action: Action
    completion_criteria: str = Field(min_length=1, max_length=500)
    reconsider_when: list[str] = Field(default_factory=list, max_length=8)

    def signature(self):
        return hashlib.sha256(self.action.model_dump_json().encode()).hexdigest()


class PlanSpec(Contract):
    long_term: str = Field(default='', max_length=2000)
    right_now: str = Field(default='', max_length=1200)
    goals: list[str] = Field(default_factory=list, max_length=16)
    constraints: list[str] = Field(default_factory=list, max_length=16)
    assumptions: list[str] = Field(default_factory=list, max_length=16)
    risks: list[str] = Field(default_factory=list, max_length=16)
    reserved_walkways: list[Rectangle] = Field(default_factory=list, max_length=40)
    steps: list[PlanStep] = Field(default_factory=list, max_length=80)

    @model_validator(mode='after')
    def graph(self):
        ids = {s.id for s in self.steps}
        if len(ids) != len(self.steps):
            raise ValueError('Plan step IDs must be unique')
        done = set()
        remaining = {s.id: {d.step for d in s.after} for s in self.steps}
        if any(not deps <= ids for deps in remaining.values()):
            raise ValueError('Dependency refers to a missing step')
        while remaining:
            ready = {key for key, deps in remaining.items() if deps <= done}
            if not ready:
                raise ValueError('Plan dependencies contain a cycle')
            done |= ready
            remaining = {k: v for k, v in remaining.items() if k not in ready}
        return self


class Decision(Contract):
    expected_revision: int = Field(ge=0, description='Current committed plan revision, not the next revision. Read inspect_plan if uncertain.')
    disposition: Literal['continue', 'revise', 'defer'] = Field(description='revise creates or replaces a plan (including the first plan); continue/defer preserve it and require plan=null.')
    assessment: str = Field(min_length=1, max_length=1500)
    rationale: str = Field(min_length=1, max_length=1500)
    reply: str = Field(min_length=1, max_length=1800)
    plan: PlanSpec | None = None
    used_consultations: list[str] = Field(default_factory=list, max_length=16)
    retry_steps: list[str] = Field(default_factory=list, max_length=16, description='Explicitly retry blocked steps only when their failure is marked retryable and new evidence supports it.')


def repeatable_player_setting(step):
    return (step.source == 'PLAYER' and isinstance(step.action, NativeOperation)
            and (step.action.tool in ('home/husbandry_config', 'home/production_policy', 'home/research', 'home/pawn_config', 'home/building_config')
                 or step.action.tool == 'home/order' and step.action.arguments.get('action') in ('draft', 'undraft', 'goto')))


def repeatable_completed_operation(step):
    return repeatable_player_setting(step) or (step.source == 'AUTOPILOT' and step.goal_id
        and isinstance(step.action, NativeOperation) and step.action.completion == 'upkeep_target')


class CommitSteps(Contract):
    expected_revision: int = Field(ge=0)
    reason: str = Field(min_length=1,max_length=1200)
    steps: list[PlanStep] = Field(min_length=1,max_length=8)

    def decision(self, current):
        existing={step.id for step in current.spec.steps}
        if any(step.id in existing for step in self.steps):
            raise ValueError('Append new step IDs only; existing work is preserved. Use commit_plan to revise it.')
        renewed = {step.signature() for step in self.steps if repeatable_completed_operation(step)}
        retired = {step.id for step in current.spec.steps if repeatable_completed_operation(step)
                   and step.signature() in renewed and current.progress[step.id].state == 'complete'}
        pinned = set(current.control.get('combat', {}).get('steps', []))
        # Player rooms/zones still support maintained goals and native edit
        # reconciliation. Their completion is not permission to forget them.
        pinned.update(identity for goal in current.colony_goals.values()
                      if goal.source == 'PLAYER' and not goal.cancelled
                      for identity in goal.steps)
        if len(current.spec.steps)+len(self.steps)>72:
            retired |= {step.id for step in current.spec.steps if step.id not in pinned
                       and current.progress[step.id].state == 'complete'
                       and (step.source == 'PLAYER' or isinstance(step.action, NativeOperation))}
        retired -= pinned
        rows = []
        for step in [*current.spec.steps,*self.steps]:
            if step.id in retired: continue
            row = step.model_dump()
            row['after'] = [d for d in row['after'] if d['step'] not in retired]
            rows.append(row)
        spec=PlanSpec.model_validate(dict(current.spec.model_dump(),steps=rows))
        return Decision(expected_revision=self.expected_revision,disposition='revise',
            assessment=self.reason,rationale=self.reason,reply=self.reason,plan=spec)


class Failure(Contract):
    code: str
    detail: str
    retryable: bool = False
    evidence: dict = Field(default_factory=dict)


class StepProgress(Contract):
    state: Literal['pending', 'executing', 'waiting', 'complete', 'blocked', 'cancelled'] = 'pending'
    issued: dict[str, dict] = Field(default_factory=dict)
    failure: Failure | None = None
    project_id: str | None = None
    recovery_history: list[dict] = Field(default_factory=list)


class ColonyGoal(Contract):
    priority_class: int = Field(ge=0, le=4)
    status: Literal['active', 'suspended', 'complete', 'blocked'] = 'active'
    method: str = ''
    reason: str = ''
    started_tick: int = 0
    last_progress_tick: int = 0
    attempts: int = 0
    steps: list[str] = Field(default_factory=list)
    evidence: dict = Field(default_factory=dict)
    source: Literal['PLAYER', 'AUTOPILOT', 'LLM_ADVISOR'] = 'AUTOPILOT'
    target: dict = Field(default_factory=dict)
    cancelled: bool = False
    method_epoch: int = Field(default=0, ge=0)
    archived_methods: int = Field(default=0, ge=0)
    _method_contains: object = PrivateAttr(default=None)

    def method_seen(self, name):
        if name in self.evidence.get('methods',{}):return True
        if self.archived_methods and self._method_contains is None:
            raise ValueError('Goal method archive is unavailable; cannot safely repeat work')
        return bool(self.archived_methods and self._method_contains(name))

    def reopen_methods(self):
        self.method_epoch += 1
        self.archived_methods = 0
        self.evidence['methods'] = {}


def repeatable_treatment(step):
    return (step.source == 'AUTOPILOT' and step.goal_id == 'CriticalMedical'
        and isinstance(step.action, NativeOperation) and step.action.tool == 'home/order'
        and step.action.completion == 'patient_tended' and step.action.arguments.get('action') == 'tend')


class ColonyPlan(Contract):
    revision: int = 0
    chosen_tick: int = 0
    rationale: str = ''
    spec: PlanSpec = Field(default_factory=PlanSpec)
    progress: dict[str, StepProgress] = Field(default_factory=dict)
    cancelled_ids: list[str] = Field(default_factory=list)
    cancelled_actions: list[str] = Field(default_factory=list)
    history: list[dict] = Field(default_factory=list)
    colony_goals: dict[str, ColonyGoal] = Field(default_factory=dict)
    control: dict = Field(default_factory=dict)
    _archive_contains: object = PrivateAttr(default=None)
    _archive_read: object = PrivateAttr(default=None)

    def commit(self, decision: Decision, *, actor: ModelRole, tick: int):
        if actor != ModelRole.STRATEGIST:
            raise PermissionError('Only the strategist can commit strategic intent')
        if decision.expected_revision != self.revision:
            raise ValueError(f'Expected revision {decision.expected_revision}, but current plan revision is {self.revision}; reconsider against the current plan')
        if decision.disposition != 'revise':
            if decision.plan is not None:
                raise ValueError('Continue/defer cannot replace the plan')
            return False
        if decision.plan is None:
            raise ValueError('A revision needs a structured plan')
        if decision.plan == self.spec:
            return False
        old = {s.id: s for s in self.spec.steps}
        if self.control.get('archived_action_count') and self._archive_contains is None:
            raise ValueError('Retired action archive is unavailable; cannot safely admit new identities')
        for step in decision.plan.steps:
            if (step.id in self.control.get('retired_steps',{})
                    or self._archive_contains is not None and self._archive_contains(step.id)):
                raise ValueError('Retired action identity cannot be reused: '+step.id)
            if step.id in self.cancelled_ids or step.signature() in self.cancelled_actions:
                # Retained cancelled work is history, not a request to issue it again.
                if old.get(step.id)==step and self.progress[step.id].state=='cancelled':
                    continue
                raise ValueError('Player cancelled step '+step.id)
            if any(prior.id != step.id and prior.signature() == step.signature()
                   and not (repeatable_treatment(step) and repeatable_treatment(prior)
                       and (old.get(step.id) == step or (self.progress[prior.id].state == 'complete'
                           and self.progress[prior.id].issued.get('0', {}).get('confirmed') is True)))
                   and not (repeatable_completed_operation(step) and repeatable_completed_operation(prior)
                       and self.progress[prior.id].state == 'complete'
                       and prior.id not in {s.id for s in decision.plan.steps}) for prior in old.values()):
                raise ValueError('Reuse the existing step ID for identical intent')
            if step.id in old and step.signature() != old[step.id].signature():
                raise ValueError('Changed execution intent needs a new step ID; keep prior receipts intact')
        self.history.append(dict(revision=self.revision, chosen_tick=self.chosen_tick,
            rationale=self.rationale, spec=self.spec.model_dump()))
        self.history = self.history[-12:]
        retained = {s.id for s in decision.plan.steps}
        for identity, step in old.items():
            if identity not in retained and self.progress[identity].state=='complete':
                self.control.setdefault('retired_steps',{})[identity] = step.model_dump()
        self.spec = decision.plan
        self.revision += 1
        self.chosen_tick, self.rationale = tick, decision.rationale
        for step in self.spec.steps:
            self.progress.setdefault(step.id, StepProgress())
        return True

    def cancel(self, step_id):
        if step_id not in self.progress:
            raise ValueError('Unknown plan step')
        self.progress[step_id].state = 'cancelled'
        if step_id not in self.cancelled_ids:
            self.cancelled_ids.append(step_id)
        step = next(s for s in self.spec.steps if s.id == step_id)
        if step.signature() not in self.cancelled_actions:
            self.cancelled_actions.append(step.signature())
        self.revision += 1

    def ready(self):
        for step in sorted(self.spec.steps, key=lambda s: -s.priority):
            if step.goal_id and (goal := self.colony_goals.get(step.goal_id)) and goal.status != 'active':
                continue
            state = self.progress[step.id]
            if state.state not in ('pending', 'executing'):
                continue
            if all(self.progress[d.step].state in (('waiting', 'complete') if d.when == 'issued' else ('complete',)) for d in step.after):
                yield step
