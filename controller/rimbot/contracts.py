from typing import Any, Literal
from pydantic import BaseModel, ConfigDict, Field


class Contract(BaseModel):
    model_config = ConfigDict(extra='forbid')


class Query(Contract):
    endpoint: str
    arguments: dict[str, Any] = Field(default_factory=dict, description='Only arguments from the endpoint contract. Filtering, fields, limit and offset belong beside arguments, not inside it.')
    path: str = ''
    where: dict[str, Any] = Field(default_factory=dict)
    fields: list[str] = Field(default_factory=list)
    sort_by: str = ''
    near: dict[str, float] | None = None
    offset: int = Field(default=0, ge=0)
    limit: int = Field(default=30, ge=1, le=200)


class Check(Contract):
    query: Query
    field: str = ''
    op: Literal['eq','ne','gte','lte','exists','absent'] = 'exists'
    value: Any = None


class Action(Contract):
    title: str = Field(min_length=1, max_length=110)
    endpoint: str
    arguments: dict[str, Any] = Field(default_factory=dict)
    # Verify state, not an HTTP acknowledgment. No saved executable handles.
    done: Check
    requires: list[Check] = Field(default_factory=list)


class Proposal(Contract):
    summary: str = Field(description='One or two short sentences. Put detailed work in actions and blockers, not this summary.')
    priority: Literal['urgent','high','normal','low'] = 'normal'
    actions: list[Action] = Field(default_factory=list)
    labor: list[str] = Field(default_factory=list)
    resources: dict[str, float] = Field(default_factory=dict)
    blockers: list[str] = Field(default_factory=list)


class Decision(Contract):
    response: str = Field(max_length=650)
    accepted: list[str] = Field(default_factory=list, description='Proposal IDs; accept a complete proposal only.')
    deferred: dict[str, str] = Field(default_factory=dict, description='Every other proposal ID and its reason.')


class Plans(Contract):
    today: list[str] = Field(max_length=6)
    week: list[str] = Field(max_length=5)
    season: list[str] = Field(max_length=5)
    year: list[str] = Field(max_length=3)
    horizon: str = Field(max_length=250)
    response: str = Field(max_length=500)


class DailyPlan(Contract):
    today: list[str] = Field(max_length=6)
    week: list[str] = Field(max_length=5)
    response: str = Field(max_length=350)


def at(value, path):
    for key in path.split('.') if path else []:
        if isinstance(value, dict):
            value = value.get(key)
        elif isinstance(value, list) and key.isdigit():
            value = value[int(key)] if int(key) < len(value) else None
        else:
            return None
    return value


def select(data, q: Query):
    data = at(data, q.path)
    if not isinstance(data, list):
        if q.where or q.near or q.sort_by:
            raise ValueError('Choose the list using path before filtering or sorting.')
        return data
    data = [r for r in data if all(at(r,k) == v for k,v in q.where.items())]
    if q.near:
        def distance(r):
            p = r.get('position') or {}
            return sum((p.get(k, 1e9) - v)**2 for k,v in q.near.items())
        data.sort(key=distance)
    elif q.sort_by:
        data.sort(key=lambda r:(at(r,q.sort_by) is None, at(r,q.sort_by)))
    total = len(data)
    data = data[q.offset:q.offset+q.limit]
    if q.fields:
        data = [{key:at(row,key) for key in q.fields} for row in data]
    return {'items':data, 'total':total, 'offset':q.offset, 'next_offset':q.offset+len(data) if q.offset+len(data)<total else None}


def satisfies(result, check: Check):
    value = at(result, check.field)
    if check.op == 'exists':
        return value is not None and value != [] and value != 0
    if check.op == 'absent':
        return value is None or value == [] or value == 0
    if check.op == 'eq':
        return value == check.value
    if check.op == 'ne':
        return value != check.value
    if not isinstance(value, (int,float)) or not isinstance(check.value, (int,float)):
        return False
    return value >= check.value if check.op == 'gte' else value <= check.value
