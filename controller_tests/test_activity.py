import httpx
from rimbot.server import create_app

async def test_outcomes_survive_a_burst_of_tool_events(colony):
    rt,_=colony
    result=rt.note('work_outcome','Bed completed',role='Executor:construction')
    rt.note('spatial_plan','Kitchen reserved',role='Architect')
    for i in range(320):rt.note('tool_result','Inspection',role='Infrastructure',tool='query')
    rt.store.event('another-colony','work_outcome',text='Wrong colony')
    app=create_app(rt);app.state.rt=rt
    async with httpx.AsyncClient(transport=httpx.ASGITransport(app=app),base_url='http://testserver') as client:
        response=await client.get('/api/manager-activity')
    events=response.json()['events']
    assert result['id'] in {e['id'] for e in events}
    assert any(e['kind']=='spatial_plan' for e in events)
    assert not any(e.get('text')=='Wrong colony' for e in events)
    assert len(events)==len({e['id'] for e in events})
    assert [e['id'] for e in events]==sorted((e['id'] for e in events),reverse=True)
