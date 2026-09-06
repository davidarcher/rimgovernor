"""Controller-owned objectives; native commands remain in the generated game API."""
from typing import Literal
from pydantic import Field
from .contracts import Contract, Decision

ProjectKind=Literal['construction','growing','production','storage','work_assignment','supply_access','care','security','research']

class WorkObjective(Contract):
    project_id: str = Field(default='',description='Existing project ID to continue; blank creates a new objective.')
    kind: ProjectKind = Field(description="Command system needed: construction places ALL furniture/buildings including beds and recreation; growing creates crop zones; storage creates/configures stockpile zones; supply_access ONLY changes forbidden flags and cannot create stockpiles; production configures bills; work_assignment changes priorities/schedules ONLY; care treats patients, never builds beds; research selects technology, never recreation.")
    outcome: str = Field(min_length=1,max_length=250,description='Desired player-visible result, not endpoints, cells or a sequence of API calls.')
    quantity: int | None = Field(default=None,ge=1,description='Desired capacity or quantity if meaningful; null when not applicable.')
    crop_def: str = Field(default='',description='Growing only: one observed native crop definition. Split different crops into separate objectives.')
    target_cells: int | None = Field(default=None,ge=1,description='Growing only: desired TOTAL colony-wide zone cells for this crop, including existing zones, not extra cells. Choose based on nutrition demand, reserves, season and labor.')
    definition_requirements: dict[str,bool|float|str] = Field(default_factory=dict,description='Construction only: required native building-definition properties supplied by guidance or observation, e.g. bed_humanlike=true. Empty for other systems. Unknown properties cannot be assumed.')
    constraints: list[str] = Field(default_factory=list,max_length=6)
    success_signals: list[str] = Field(min_length=1,max_length=4,description='Observable evidence the result is usable, including operating needs such as feed for housed animals. Construction completion or an alert disappearing alone is insufficient. These are review criteria, not claims of completion.')

class ObjectiveProposal(Contract):
    summary: str = Field(max_length=350)
    priority: Literal['urgent','high','normal','low']='normal'
    objectives: list[WorkObjective] = Field(default_factory=list,max_length=5)
    blockers: list[str] = Field(default_factory=list,max_length=6)

# Capability groups correspond to native player systems, not room recipes.
EXECUTION_DOMAINS={
    'construction':('construction_','forbidden','orders_unforbid_all','order_designate'),
    'growing':('zone_growing','order_designate','forbidden','orders_unforbid_all'),
    'production':('bills',),
    'storage':('zone_stockpile',),
    'work_assignment':('work_settings','priority','time_assignment'),
    'supply_access':('forbidden','orders_unforbid_all',),
    'care':('medical',),
    'security':('pawn_job','pawn_edit_status','jobs_make_equip'),
    'research':('research',),
}

class ObjectiveDecision(Decision):
    updates: dict[str, WorkObjective] = Field(default_factory=dict, description='Accepted candidate ID to corrected objective. Set project_id to an existing project to continue/revise it instead of creating a duplicate. Correct wrong kind, infeasible assumptions or scope here.')
    keep_projects: list[str] = Field(default_factory=list, description='Existing active project IDs to keep. Omitted existing projects remain active. Keeping alone does not queue new orders.')
    retire_projects: dict[str,str] = Field(default_factory=dict, description='Existing project ID to reason: duplicate, obsolete, infeasible or achieved. Retires tracking only; does not cancel game orders.')
