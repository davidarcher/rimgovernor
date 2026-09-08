import json
from unittest.mock import AsyncMock
import pytest
from rimbot.scout import investigate


def call(name, arguments):
    return {'id':'call','type':'function','function':{'name':name,'arguments':json.dumps(arguments)}}


def report(evidence=['e1']):
    return call('report',dict(answer='Wood is short for this blueprint.', confidence='high',
        evidence=evidence, missing_facts=[], recommendations=['Check nearby allowed wood.']))


class Router:
    def __init__(self, calls):self.calls=iter(calls);self.messages=[]
    async def complete(self, role, messages, tools, progress):
        self.messages.append(json.loads(json.dumps(messages)))
        assert str(role)=='analyst'
        assert {t['function']['name'] for t in tools}=={'describe','inspect','report'}
        return {'role':'assistant','tool_calls':[next(self.calls)]},{}


SCHEMA = dict(type='object',properties={'match':{'type':'string'}},additionalProperties=False)
PROJECTION = dict(load_token='test-load',tick=100,resources={})


@pytest.mark.asyncio
async def test_scout_keeps_raw_evidence_out_of_strategist_report():
    router=Router([call('inspect',dict(name='home/list_buildings',arguments={'match':'Bed'})), report()])
    raw={'resourceDeficit':[{'defName':'WoodLog','stillNeeded':45}], 'private_detail':'native detail'}
    read=AsyncMock(return_value=raw)
    result,audit=await investigate(router,'Why is the bed blocked?',PROJECTION,AsyncMock(return_value=SCHEMA),read,AsyncMock())
    assert result['investigation']['reads']==1 and result['report']['evidence']==['e1']
    assert 'private_detail' not in json.dumps(result)
    assert audit[0]['payload']==raw and audit[0]['arguments']=={'match':'Bed'}
    assert 'private_detail' in json.dumps(router.messages[-1])


@pytest.mark.asyncio
async def test_scout_rejects_writes_and_invented_citations():
    router=Router([call('inspect',dict(name='home/order',arguments={'action':'attack'})),
        report(['e99']), report(['e0'])])
    read=AsyncMock();describe=AsyncMock()
    result,_=await investigate(router,'Check security',PROJECTION,describe,read,AsyncMock())
    read.assert_not_awaited();describe.assert_not_awaited()
    assert result['report']['evidence']==['e0']
    assert 'must cite observed' in json.dumps(router.messages[-1])


@pytest.mark.asyncio
async def test_oversized_result_is_not_empty_evidence_and_reads_are_bounded():
    query=call('inspect',dict(name='home/list_buildings',arguments={}))
    router=Router([query,query,report(['e0'])]);read=AsyncMock(return_value={'large':'x'*9000})
    result,audit=await investigate(router,'Find blocker',PROJECTION,AsyncMock(return_value=SCHEMA),read,AsyncMock(),max_reads=1)
    assert read.await_count==1 and audit==[]
    assert 'not an empty result' in json.dumps(router.messages[-1])
    assert 'budget exhausted' in json.dumps(router.messages[-1])


@pytest.mark.asyncio
async def test_stale_load_aborts_investigation_instead_of_retaining_advice():
    router=Router([call('inspect',dict(name='home/list_pawns',arguments={}))])
    with pytest.raises(InterruptedError):
        await investigate(router,'Check pawn',PROJECTION,AsyncMock(return_value=SCHEMA),
            AsyncMock(side_effect=InterruptedError('New load')),AsyncMock())


@pytest.mark.asyncio
async def test_unknown_native_filter_never_reaches_bridge():
    router=Router([call('inspect',dict(name='home/list_buildings',arguments={'made_up_filter':True})),report(['e0'])])
    read=AsyncMock()
    await investigate(router,'Check bed',PROJECTION,AsyncMock(return_value=SCHEMA),read,AsyncMock())
    read.assert_not_awaited()
