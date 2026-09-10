"""Generated from data/bridge_observation.schema.json. Do not edit."""
from typing import Literal
from pydantic import BaseModel, ConfigDict, Field

class NativeObject(BaseModel):
    model_config = ConfigDict(extra="forbid", strict=True)

class BridgeCell(NativeObject):
    x: int
    z: int

class BridgePawn(NativeObject):
    thing_id: str
    name: str
    position: BridgeCell
    job: str | None
    drafted: bool
    downed: bool
    dead: bool
    mood: float | None
    food: float | None
    rest: float | None
    armed: bool | None
    primary_weapon: str | None
    needs_tend: bool | None
    bleeding: bool | None

class BridgeSupply(NativeObject):
    def_name: str
    label: str
    owned_units: int
    owned_unforbidden_units: int
    forbidden_units_all_owners: int
    stockpiled_units_all_owners: int
    fogged_units_all_owners: int
    trader_units: int

class BridgeObservation(NativeObject):
    backend: Literal['rimbridge']
    start_tick: int
    end_tick: int
    same_tick: bool
    paused: bool
    map_name: str | None
    pawns: list[BridgePawn]
    supplies: list[BridgeSupply]
    visible_rooms: int
    fogged_rooms_omitted: int
    zone_count: int
    warnings: list[str]
    alert_labels: list[str]
    hostile_count: int
    hunting_predator_count: int
