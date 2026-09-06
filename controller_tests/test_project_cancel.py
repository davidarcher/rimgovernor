import asyncio
import httpx
from rimbot.server import create_app
from rimbot.semantic import retain_project
from rimbot.semantic_models import WorkObjective

def project(rt):
    return retain_project(rt.memory,'Infrastructure',WorkObjective(kind='construction',outcome='Unwanted room',success_signals=['Room exists']))

async def test_cancel_project_endpoint_preserves_native_orders_and_shared_work(colony):
    rt,game=colony
    p=project(rt);p['work_ids']=['own','shared','done']
    rt.memory['projects'].append({'project_id':'other','status':'approved','work_ids':['shared']})
    rt.memory['work']=[{'id':'own','status':'issued'},{'id':'shared','status':'issued'},{'id':'done','status':'complete'}]
    app=create_app(rt);app.state.rt=rt
    async with httpx.AsyncClient(transport=httpx.ASGITransport(app=app),base_url='http://testserver') as client:
        r=await client.delete('/api/projects/'+p['project_id'],headers={'x-rimbot':'1'})
        assert r.status_code==200
        assert (await client.delete('/api/projects/missing',headers={'x-rimbot':'1'})).status_code==404
    assert p['status']=='retired' and p['cancelled_by_player']
    assert [w['status'] for w in rt.memory['work']]==['dismissed','issued','complete']
    assert not game.writes

async def test_cancellation_waits_for_sent_order_then_retires_project(colony):
    rt,_=colony;p=project(rt)
    finished=asyncio.Event()
    async def sent():
        await finished.wait()
        p['status']='awaiting_work'
        rt.executing=False
    rt.executing=True;rt.task=asyncio.create_task(sent())
    cancellation=asyncio.create_task(rt.cancel_project(p['project_id']))
    await asyncio.sleep(0)
    assert not cancellation.done()
    finished.set();await cancellation
    assert p['status']=='retired'

async def test_admin_scrubs_existing_projects_when_departments_fail(colony):
    from rimbot.semantic import semantic_review
    from rimbot.semantic_models import ObjectiveProposal,ObjectiveDecision
    from rimbot.model import ModelError
    rt,game=colony;p=project(rt)
    rt.mode='automate';rt.cycle_generation=rt.generation
    async def ask(role,context,contract,thinking):
        if contract is ObjectiveProposal:raise ModelError('Department unavailable')
        assert contract is ObjectiveDecision and context['proposals']=={}
        assert context['projects'][0]['reviews_without_order_change']==0
        return ObjectiveDecision(response='Removed obsolete room',retire_projects={p['project_id']:'Player no longer needs this room'})
    rt.planner.ask=ask
    await semantic_review(rt,{},['Infrastructure'])
    assert p['status']=='retired' and not game.writes
