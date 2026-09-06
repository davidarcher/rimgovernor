# Generated from controller/contracts/discovery.openapi.json. Do not edit.
from __future__ import annotations
from typing import Literal, Union
from pydantic import BaseModel, ConfigDict, Field, JsonValue, RootModel, TypeAdapter

class WireModel(BaseModel):
    model_config = ConfigDict(strict=True, extra='forbid', protected_namespaces=())


class DiscoveryQuery(WireModel):
    search: str = Field()

class DefinitionRead(WireModel):
    endpoint: str = Field()
    arguments: dict[str, JsonValue] = Field()
    path: str = Field()
    where: dict[str, JsonValue] = Field()

class DefinitionMatch(WireModel):
    group: str = Field()
    def_name: str = Field()
    label: str = Field()
    description: str = Field()
    facts: dict[str, JsonValue] = Field()
    read: DefinitionRead = Field()
    actions: list[str] = Field()

class EndpointMatch(WireModel):
    name: str = Field()
    description: str = Field()
    write: bool = Field()

class DiscoveryResult(WireModel):
    endpoints: list[EndpointMatch] = Field()
    definitions: list[DefinitionMatch] = Field()
    indexed_definitions: int = Field()
    notes: list[str] = Field()

DiscoveryQuery.model_rebuild()
DefinitionRead.model_rebuild()
DefinitionMatch.model_rebuild()
EndpointMatch.model_rebuild()
DiscoveryResult.model_rebuild()

QUERY_TYPES = {
}

BODY_TYPES = {
}

RESPONSE_TYPES = {
}

class HttpOperations:
    async def _call(self, operation_id, *, query=None, body=None):
        raise NotImplementedError
