from typing import Any, Literal
import json
from pydantic import BaseModel, ConfigDict, Field, field_validator


class Contract(BaseModel):
    model_config = ConfigDict(extra='forbid')


class Query(Contract):
    endpoint: str
    arguments: dict[str, Any] = Field(default_factory=dict, description='Only arguments from the endpoint contract. Filtering, fields, limit and offset belong beside arguments, not inside it.')
    path: str = ''
    where: dict[str, Any] = Field(default_factory=dict,description='Exact field/value equality only. No like, wildcard, or comparison operators. Use search for words in labels and names.')
    search: str = Field(default='', description='Case-insensitive words to find within row text, such as part of a label or definition name. Use this for discovery; where compares exact field values.')
    fields: list[str] = Field(default_factory=list)
    sort_by: str = ''
    near: dict[str, float] | None = None
    offset: int = Field(default=0, ge=0)
    limit: int = Field(default=30, ge=1, le=200)


class Check(Contract):
    query: Query
    field: str = Field(min_length=1,description='Exact field path in query result to verify. Use total for a filtered count, or items.0 followed by the observed field path. Do not check merely that a response object exists.')
    op: Literal['eq','ne','gte','lte','exists','absent']
    value: Any = Field(description='Expected post-action value; null only for exists/absent. For changing a priority, compare the actual priority to the requested number.')


class Action(Contract):
    title: str = Field(min_length=1, max_length=110)
    endpoint: str
    arguments: dict[str, Any] = Field(description='Complete native command arguments, including all required identifiers and positions. Describe the endpoint before proposing an unfamiliar command.')
    # Verify state, not an HTTP acknowledgment. No saved executable handles.
    done: Check | None = None
    requires: list[Check] = Field(default_factory=list)
    observation_basis: str | None = None


class Proposal(Contract):
    summary: str = Field(description='One or two short sentences. Put detailed work in actions and blockers, not this summary.')
    priority: Literal['urgent','high','normal','low'] = 'normal'
    actions: list[Action] = Field(default_factory=list)
    labor: list[str] = Field(default_factory=list, description='Other manager names whose help is needed. Do not repeat your own name. Request Workforce only for needed pawn labor configuration; placing an instant object does not require it.')
    resources: dict[str, float] = Field(default_factory=dict)
    blockers: list[str] = Field(default_factory=list)


class Decision(Contract):
    response: str = Field(max_length=12000,description='Explain the decision as needed. Start with a short player-facing summary; put supporting reasoning afterward.')
    accepted: list[str] = Field(default_factory=list, description='Proposal IDs; accept a complete proposal only.')
    deferred: dict[str, str] = Field(default_factory=dict, description='Every other proposal ID and its reason.')


ManagerName = Literal['Survival','Infrastructure','Security','Development','Workforce']


class Plans(Contract):
    @field_validator('response', mode='before')
    @classmethod
    def concise_display(cls,value):
        return value[:497]+'...' if isinstance(value,str) and len(value)>500 else value

    today: list[str] = Field(max_length=6)
    week: list[str] = Field(max_length=5)
    season: list[str] = Field(max_length=5)
    year: list[str] = Field(max_length=3)
    horizon: str = Field(max_length=250)
    response: str = Field(max_length=500)
    assignments: dict[ManagerName,str] = Field(default_factory=dict, description='Only managers that need to act now, each with a concise task. Delegate detailed inspection to them. Do not wake every manager for a narrow objective.')


class DailyPlan(Contract):
    @field_validator('response', mode='before')
    @classmethod
    def concise_display(cls, value):
        return value[:347]+'...' if isinstance(value,str) and len(value)>350 else value

    today: list[str] = Field(max_length=6)
    week: list[str] = Field(max_length=5)
    response: str = Field(max_length=350)
    assignments: dict[ManagerName,str] = Field(default_factory=dict, description='Managers needed for current work and their concrete tasks; review the whole colony overview for neglected needs.')


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
    root=data
    data = at(data, q.path)
    if q.path and data is None:
        paths=', '.join(k for k,v in root.items() if isinstance(v,list)) if isinstance(root,dict) else ''
        raise ValueError(f'Path {q.path!r} does not exist. Available list paths: '+(paths or 'inspect the response object'))
    if not isinstance(data, list):
        if q.where or q.search or q.near or q.sort_by:
            paths = ', '.join(k for k,v in data.items() if isinstance(v,list)) if isinstance(data,dict) else ''
            raise ValueError('Choose the list using path before filtering or sorting. Available list paths: '+(paths or 'inspect the response object'))
        return data
    for key,value in q.where.items():
        if isinstance(value,dict) and any(op in value for op in ('like','$like','$eq','$in','$gt','$lt')):
            raise ValueError(f'where.{key} uses unsupported operators. where compares exact values; use search for label/name words.')
    data = [r for r in data if all(at(r,k) == v for k,v in q.where.items())]
    if q.search.strip():
        words = q.search.casefold().split()
        data = [r for r in data if all(w in json.dumps(r,ensure_ascii=False).casefold() for w in words)]
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
        for key in q.fields:
            if data and all(isinstance(row,dict) and key.split('.')[0] not in row for row in data):
                available=sorted({k for row in data if isinstance(row,dict) for k in row})
                raise ValueError(f'Projection field {key!r} does not exist in these rows. Available fields: '+', '.join(available))
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
