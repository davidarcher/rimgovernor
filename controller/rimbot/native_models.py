"""Generated from Contracts/construction.openapi.json. Do not edit."""
from typing import Literal
from pydantic import BaseModel, ConfigDict, Field

class NativeObject(BaseModel):
    model_config = ConfigDict(extra="forbid", strict=True)

class DefinitionQuery(NativeObject):
    search: str
    offset: int = Field(ge=0, le=2147483647)
    limit: int = Field(ge=1, le=32)

class Cost(NativeObject):
    def_name: str
    count: int

class Material(NativeObject):
    def_name: str
    label: str

class BuildingDefinition(NativeObject):
    def_name: str
    label: str
    size_x: int
    size_z: int
    rotatable: bool
    work_to_build: float
    stuff_count: int
    costs: list[Cost]
    allowed_materials: list[Material]
    description: str
    is_bed: bool
    bed_humanlike: bool

class PageBuildingDefinition(NativeObject):
    items: list[BuildingDefinition]
    total: int
    next_offset: int | None

class Cell(NativeObject):
    x: int
    z: int

class RoomQuery(NativeObject):
    map_id: int
    offset: int = Field(ge=0, le=2147483647)
    limit: int = Field(ge=1, le=32)
    near: Cell
    cell_limit: int = Field(ge=1, le=256)

class PositionDto(NativeObject):
    x: int
    y: int
    z: int

class RoomDto(NativeObject):
    visible_cell: PositionDto | None
    fogged_cells_count: int
    visible_cells: list[PositionDto]
    cells_truncated: bool
    pawns_reaching_visible_cell: list[int]
    id: int
    role_label: str
    temperature: float
    cells_count: int
    touches_map_edge: bool
    is_prison_cell: bool
    is_doorway: bool
    open_roof_count: int
    contained_beds_ids: list[int]

class PageRoomDto(NativeObject):
    items: list[RoomDto]
    total: int
    next_offset: int | None

class Placement(NativeObject):
    def_name: str
    stuff_def_name: str
    position: Cell
    rotation: int = Field(ge=0, le=3)

class ConstructionRequest(NativeObject):
    map_id: int
    buildings: list[Placement] = Field(min_length=1, max_length=128)

class PlacementResult(NativeObject):
    placement: Placement
    state: Literal['ready', 'blueprint', 'frame', 'built', 'rejected']
    thing_id: int | None
    reason: str

class ConstructionResult(NativeObject):
    accepted: bool
    items: list[PlacementResult]

class MapQuery(NativeObject):
    map_id: int

class ConstructionThing(NativeObject):
    thing_id: int
    def_name: str
    position: Cell
    rotation: int
    state: str

class ConstructionState(NativeObject):
    revision: str
    buildings: list[ConstructionThing]

class AreaQuery(NativeObject):
    map_id: int
    center: Cell
    radius: int = Field(ge=0, le=32)

class AreaCell(NativeObject):
    position: Cell
    terrain_def: str
    fertility: float
    roofed: bool
    walkable: bool
    thing_ids: list[int]
    zone_id: int | None
    zone_type: str
    zone_label: str
    plan_id: str
    plantable: bool
    encloses: bool
    is_door: bool

class AreaResult(NativeObject):
    center: Cell
    radius: int
    cells: list[AreaCell]

class AllowAllRequest(NativeObject):
    map_id: int

class AllowAllResult(NativeObject):
    map_id: int
    changed_count: int
    excluded_jelly_count: int
    remaining_eligible_count: int

class MapPlan(NativeObject):
    id: str
    label: str
    color_def: str
    cells: list[Cell]

class PlanColor(NativeObject):
    def_name: str
    label: str
    html_color: str

class PlanningState(NativeObject):
    plans: list[MapPlan]
    colors: list[PlanColor]

class PlanRequest(NativeObject):
    map_id: int
    label: str
    color_def: str
    cells: list[Cell] = Field(min_length=1, max_length=4096)

class Footprint(NativeObject):
    cells: list[Cell]
    encloses: bool
    is_door: bool
    is_bed: bool

class Footprints(NativeObject):
    items: list[Footprint]

class GrowingCellsRequest(NativeObject):
    map_id: int
    plant_def: str
    cells: list[Cell] = Field(min_length=1, max_length=4096)

class GrowingCellsResult(NativeObject):
    zone_id: int
    plant_def: str
    cells: list[Cell]

class RemovePlanRequest(NativeObject):
    map_id: int
    plan_id: str
    expected_cells: list[Cell]

class RemovePlanResult(NativeObject):
    removed: bool

class ContractError(NativeObject):
    code: str
    message: str

REQUEST_TYPES = {
    'construction_definitions': DefinitionQuery,
    'construction_rooms': RoomQuery,
    'construction_inspect': ConstructionRequest,
    'construction_place': ConstructionRequest,
    'construction_state': MapQuery,
    'construction_area': AreaQuery,
    'orders_unforbid_all': AllowAllRequest,
    'orders_forbidden_overview': AllowAllRequest,
    'planning_state': MapQuery,
    'planning_create': PlanRequest,
    'construction_footprints': ConstructionRequest,
    'zone_growing_cells': GrowingCellsRequest,
    'planning_remove': RemovePlanRequest,
}

RESPONSE_TYPES = {
    'construction_definitions': PageBuildingDefinition,
    'construction_rooms': PageRoomDto,
    'construction_inspect': ConstructionResult,
    'construction_place': ConstructionResult,
    'construction_state': ConstructionState,
    'construction_area': AreaResult,
    'orders_unforbid_all': AllowAllResult,
    'orders_forbidden_overview': AllowAllResult,
    'planning_state': PlanningState,
    'planning_create': MapPlan,
    'construction_footprints': Footprints,
    'zone_growing_cells': GrowingCellsResult,
    'planning_remove': RemovePlanResult,
}
