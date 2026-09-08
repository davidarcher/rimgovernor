"""One-shot advisers. Their only capability is returning a schema-checked report."""
import json
import uuid
from typing import Literal
from pydantic import Field
from .colony_plan import Contract, RoomShell, Zone, Rectangle
from .config import ModelRole


class SpatialDesign(Contract):
    rooms: list[RoomShell] = Field(default_factory=list, max_length=12)
    zones: list[Zone] = Field(default_factory=list, max_length=12)
    walkways: list[Rectangle] = Field(default_factory=list, max_length=30)
    caveats: list[str] = Field(default_factory=list, max_length=8)


class Advice(Contract):
    answer: str = Field(min_length=1, max_length=1400)
    confidence: Literal['low', 'medium', 'high']
    evidence: list[str] = Field(default_factory=list, max_length=16)
    missing_facts: list[str] = Field(default_factory=list, max_length=10)
    recommendations: list[str] = Field(default_factory=list, max_length=10)
    design: SpatialDesign | None = None


def structured_tool(name, description, schema):
    def inline(value):
        if isinstance(value, list):
            return [inline(v) for v in value]
        if not isinstance(value, dict):
            return value
        if '$ref' in value:
            target = schema
            for key in value['$ref'].removeprefix('#/').split('/'):
                target = target[key]
            return inline({**target, **{k:v for k,v in value.items() if k != '$ref'}})
        return {k:inline(v) for k,v in value.items() if k != '$defs'}
    return {'type':'function', 'function':{'name':name, 'description':description, 'parameters':inline(schema)}}


class Consultations:
    def __init__(self, router):
        # Deliberately no runtime, game client, plan repository, or recursive tool.
        self.router = router

    async def ask(self, role, question, projection, progress, image=None):
        role = ModelRole(role)
        if role == ModelRole.STRATEGIST:
            raise ValueError('A strategist consultation cannot recursively invoke the strategist')
        if not 1 <= len(question) <= 1200:
            raise ValueError('Ask one concise, specific question')
        if image and role != ModelRole.ARCHITECT:
            raise ValueError('Only the optional architect accepts image context')
        instructions = ('Answer the narrow question from the supplied evidence. You are an adviser, not a manager. '
            'No orders or commitments are possible. Identify missing facts rather than inventing game rules. '
            'Return report once, with concise findings and cited observation IDs. No chain-of-thought. '
            'The strategist may accept or reject your recommendations. Do not assume hidden state.')
        if role == ModelRole.ARCHITECT:
            instructions += ' Propose spatial geometry with doors, usable interiors and walkways. Coordinates need native validation. An image alone cannot prove terrain or build eligibility.'
        elif role == ModelRole.CRITIC:
            instructions += ' Challenge assumptions and identify consequential risks in the proposed decision.'
        content = json.dumps({'question': question, 'evidence': projection}, ensure_ascii=False)
        if image:
            content = [{'type':'text','text':content}, {'type':'image_url','image_url':{'url':image}}]
        response, _ = await self.router.complete(role, [{'role':'system','content':instructions},
            {'role':'user','content':content}], [structured_tool('report','Return advice only.',Advice.model_json_schema())], progress)
        calls = response.get('tool_calls', [])
        if len(calls) != 1 or calls[0]['function']['name'] != 'report':
            raise ValueError('Adviser did not return one structured report; no action or plan was changed')
        report = Advice.model_validate_json(calls[0]['function']['arguments'])
        if report.design is not None and role != ModelRole.ARCHITECT:
            raise ValueError('Spatial design is reserved for the architect capability')
        return {'id': uuid.uuid4().hex[:12], 'role': role.value, 'question': question,
            'load_token': projection['load_token'], 'tick': projection['tick'], 'report':report.model_dump(), 'used':False}
