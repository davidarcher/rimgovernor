from types import SimpleNamespace
from unittest.mock import AsyncMock
import pytest
from rimbot.projects import ProjectBook
from rimbot.bridge_runtime import BridgeRuntime
from rimbot.store import Store
from rimbot.receipts import verdict_line, _outcome

@pytest.mark.asyncio
async def test_projects_follow_blueprint_frame_finished_and_removed():
    book=ProjectBook()
    p=book.upsert({'title':'Sleep indoors','targets':[{'kind':'building','def_name':'Bed','x':12,'z':15,'stuff':'WoodLog'}]})
    async def query(*args,**kwargs):return {'buildings':rows,'skipped':{}}
    game=SimpleNamespace(query=query)
    for status,identity in [('blueprint','Blueprint_Bed1'),('frame','Frame2'),('built','Bed3')]:
        rows=[{'thingId':identity,'defName':'Bed' if status=='built' else status,'buildDefName':'Bed','position':{'x':12,'z':15},'stuff':'WoodLog','status':status}]
        await book.reconcile(game)
        assert p.state==('complete' if status=='built' else 'pending')
    assert p.matched_ids==['Bed3']
    rows=[]
    await book.reconcile(game)
    assert p.state=='planned' and not p.matched_ids
    rows=[{'thingId':'Bed4','defName':'Bed','position':{'x':12,'z':15},'stuff':'Steel','status':'built'}]
    await book.reconcile(game)
    assert p.state!='complete'

@pytest.mark.asyncio
async def test_instant_zone_verified_and_cancelled_project_not_revived():
    book=ProjectBook();spec={'title':'Supplies','targets':[{'kind':'zone','zone_id':'7'}]}
    p=book.upsert(spec)
    assert book.upsert(dict(spec,title='Another name')).id==p.id
    await book.reconcile(SimpleNamespace(query=AsyncMock(return_value={'zones':[{'id':7,'gridCellCount':1}]})))
    assert p.state=='complete'
    book.cancel(p.id)
    with pytest.raises(ValueError,match='cancelled'):book.upsert(spec)
    game=SimpleNamespace(query=AsyncMock())
    await book.reconcile(game)
    game.query.assert_not_awaited()

@pytest.mark.asyncio
async def test_saved_identity_restores_memory_but_fresh_game_does_not(tmp_path):
    store=Store(tmp_path/'state.sqlite')
    identity={'colonyId':'saved','mapId':1,'loadToken':'first','tick':5}
    def runtime():
        rt=BridgeRuntime(store,tmp_path,model_factory=lambda _:SimpleNamespace())
        rt.game=SimpleNamespace(query=AsyncMock(side_effect=lambda *a,**k:dict(identity)))
        return rt
    rt=runtime();await rt.sync_identity();await rt.steer('Build a clinic')
    rt.projects.upsert({'title':'Clinic'});rt.plan={'long':'Healthy colony','short':'Clinic'};rt.persist()
    restarted=runtime();await restarted.sync_identity()
    assert restarted.chat[-2]['text']=='Build a clinic'
    assert restarted.chat[-2]['delivery']=='interrupted'
    assert restarted.chat[-1]['interrupted_event_ids']==[restarted.chat[-2]['id']]
    assert restarted.projects.rows[0].title=='Clinic'
    assert restarted.mode=='manual'
    old=restarted.context_token
    identity['loadToken']='reloaded'
    with pytest.raises(ValueError,match='stale'):await restarted.ensure_context(old)
    assert restarted.projects.rows[0].title=='Clinic'
    identity['colonyId']='fresh'
    await restarted.sync_identity()
    assert not restarted.chat and not restarted.projects.rows
    store.close()

def test_duplicate_receipt_never_claims_placed():
    receipt={'success':True,'dryRun':False,'outcome':'already_present'}
    assert _outcome(receipt)=='already_present'
    assert 'NOTHING was placed' in verdict_line(receipt)
