"""Strategic commitments and deterministic progress are distinct durable state."""
import hashlib
import json
from typing import Annotated, Literal
from pydantic import BaseModel, ConfigDict, Field, model_validator
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


class RoomShell(Contract):
    kind: Literal['build_room_shell'] = 'build_room_shell'
    bounds: Rectangle
    wall_def: str = Field(min_length=1)
    door_def: str = Field(min_length=1)
    materials: list[str] = Field(min_length=1, max_length=8)
    entrance: Literal['north', 'east', 'south', 'west']

    @model_validator(mode='after')
    def interior(self):
        if self.bounds.width < 4 or self.bounds.height < 4:
            raise ValueError('A room shell needs an interior; use a building batch for a wall segment')
        return self


class Zone(Contract):
    kind: Literal['create_zone'] = 'create_zone'
    zone_type: Literal['stockpile', 'growing']
    label: str = Field(min_length=1, max_length=80)
    patches: list[Rectangle] = Field(min_length=1, max_length=32)
    crop: str = ''
    preset: str | None = None
    priority: Literal['Low', 'Normal', 'Preferred', 'Important', 'Critical'] = 'Normal'

    @model_validator(mode='after')
    def crop_required(self):
        if self.zone_type == 'growing' and not self.crop:
            raise ValueError('A growing zone needs an observed sowable crop definition')
        return self


class NativeOperation(Contract):
    kind: Literal['native_operation'] = 'native_operation'
    tool: Literal['home/pawn_config', 'home/building_config', 'home/bills', 'home/order',
        'home/zone_cells', 'home/trade', 'rimworld/apply_architect_designator']
    arguments: dict
    # Honest fallback for native operations lacking a higher-level compiler.
    completion: Literal['native_receipt'] = 'native_receipt'


class ClockAction(Contract):
    kind: Literal['clock'] = 'clock'
    speed: Literal['Paused', 'Normal', 'Fast', 'Superfast']
    mode: Literal['colony', 'combat'] = 'colony'
    ignored_hostiles: str = ''
    ignored_downed: str = ''


Action = Annotated[Buildings | RoomShell | Zone | NativeOperation | ClockAction, Field(discriminator='kind')]


class Dependency(Contract):
    step: str
    when: Literal['issued', 'complete'] = 'complete'


class PlanStep(Contract):
    id: str = Field(min_length=1, max_length=64, pattern=r'^[a-zA-Z0-9_-]+$')
    title: str = Field(min_length=1, max_length=160)
    priority: int = Field(default=50, ge=0, le=100)
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
    expected_revision: int = Field(ge=0)
    disposition: Literal['continue', 'revise', 'defer']
    assessment: str = Field(min_length=1, max_length=1500)
    rationale: str = Field(min_length=1, max_length=1500)
    reply: str = Field(min_length=1, max_length=1800)
    plan: PlanSpec | None = None
    used_consultations: list[str] = Field(default_factory=list, max_length=16)
    retry_steps: list[str] = Field(default_factory=list, max_length=16, description='Explicitly retry blocked steps only when their failure is marked retryable and new evidence supports it.')


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


class ColonyPlan(Contract):
    revision: int = 0
    chosen_tick: int = 0
    rationale: str = ''
    spec: PlanSpec = Field(default_factory=PlanSpec)
    progress: dict[str, StepProgress] = Field(default_factory=dict)
    cancelled_ids: list[str] = Field(default_factory=list)
    cancelled_actions: list[str] = Field(default_factory=list)
    history: list[dict] = Field(default_factory=list)

    def commit(self, decision: Decision, *, actor: ModelRole, tick: int):
        if actor != ModelRole.STRATEGIST:
            raise PermissionError('Only the strategist can commit strategic intent')
        if decision.expected_revision != self.revision:
            raise ValueError('Plan revision changed; inspect the current plan')
        if decision.disposition != 'revise':
            if decision.plan is not None:
                raise ValueError('Continue/defer cannot replace the plan')
            return False
        if decision.plan is None:
            raise ValueError('A revision needs a structured plan')
        if decision.plan == self.spec:
            return False
        old = {s.id: s for s in self.spec.steps}
        for step in decision.plan.steps:
            if step.id in self.cancelled_ids or step.signature() in self.cancelled_actions:
                raise ValueError('Player cancelled step '+step.id)
            if any(prior.id != step.id and prior.signature() == step.signature() for prior in old.values()):
                raise ValueError('Reuse the existing step ID for identical intent')
            if step.id in old and step.signature() != old[step.id].signature():
                raise ValueError('Changed execution intent needs a new step ID; keep prior receipts intact')
        self.history.append(dict(revision=self.revision, chosen_tick=self.chosen_tick,
            rationale=self.rationale, spec=self.spec.model_dump()))
        self.history = self.history[-12:]
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
            state = self.progress[step.id]
            if state.state not in ('pending', 'executing'):
                continue
            if all(self.progress[d.step].state in (('waiting', 'complete') if d.when == 'issued' else ('complete',)) for d in step.after):
                yield step
