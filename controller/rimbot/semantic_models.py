"""Controller-owned objectives; native commands remain in the generated game API."""
from typing import Literal
from pydantic import Field
from .contracts import Contract

ProjectKind=Literal['construction','growing','production','storage','work_assignment','supply_access','care','security','research']

class WorkObjective(Contract):
    project_id: str = Field(default='',description='Existing project ID to continue; blank creates a new objective.')
    kind: ProjectKind
    outcome: str = Field(min_length=1,max_length=250,description='Desired player-visible result, not endpoints, cells or a sequence of API calls.')
    quantity: int | None = Field(default=None,ge=1,description='Desired capacity or quantity if meaningful; null when not applicable.')
    definition_requirements: dict[str,bool|float|str] = Field(default_factory=dict,description='Construction only: required native building-definition properties supplied by guidance or observation, e.g. bed_humanlike=true. Empty for other systems. Unknown properties cannot be assumed.')
    constraints: list[str] = Field(default_factory=list,max_length=6)
    success_signals: list[str] = Field(min_length=1,max_length=4,description='Observable evidence of the outcome. These are criteria for review, not claims of completion.')

class ObjectiveProposal(Contract):
    summary: str = Field(max_length=350)
    priority: Literal['urgent','high','normal','low']='normal'
    objectives: list[WorkObjective] = Field(default_factory=list,max_length=5)
    blockers: list[str] = Field(default_factory=list,max_length=6)

# Capability groups correspond to native player systems, not room recipes.
EXECUTION_DOMAINS={
    'construction':('construction_','forbidden','order_designate'),
    'growing':('zone_growing','order_designate','forbidden'),
    'production':('bills',),
    'storage':('zone_stockpile',),
    'work_assignment':('work_settings','priority','time_assignment'),
    'supply_access':('forbidden',),
    'care':('medical',),
    'security':('pawn_job','pawn_edit_status','jobs_make_equip'),
    'research':('research',),
}
