from rimbot.config import Settings
from rimbot.contracts import Action
import pytest

async def test_all_default_roles_share_4b(colony):
    rt,_=colony
    assert Settings().model=='qwen3.5-4b'
    assert rt.manager_model is None
    for role in ('Survival','Infrastructure','Strategy: plan','Daily planning: plan','Administrator: reconcile','Executor:construction'):
        assert rt.model_for_role(role).settings.model=='qwen3.5-4b'

@pytest.mark.parametrize('kind',['growing','stockpile'])
async def test_zone_creation_verified_without_labor(colony,kind):
    rt,_=colony
    args={'map_id':7,'point_a':{'x':10,'z':10},'point_b':{'x':11,'z':11}}
    if kind=='growing':args['plant_def']='Plant_Rice'
    action=Action(title='Create zone',endpoint='post_map_zone_'+kind,arguments=args)
    receipt={'zone':{'id':42}} if kind=='growing' else {'zone_id':42,'success':True}
    calls=[]
    async def call(endpoint,arguments,**kwargs):
        assert kwargs.get('fresh') is True
        calls.append(endpoint)
        if endpoint=='get_map_zones':return {'zones':[{'id':42,'cells_count':4}]}
        assert arguments=={'map_id':7,'zone_id':42}
        return {'zone':{'id':42},'plant_def_name':'Plant_Rice','plant_count':0}
    rt.api.call=call
    assert not await rt.action_complete(action)
    assert await rt.action_complete(action,receipt)
    rt.memory['work']=[{'id':'zone','status':'issued','action':action.model_dump(),'native_result':receipt}]
    await rt.reconcile()
    assert rt.memory['work'][0]['status']=='complete'
    assert calls

@pytest.mark.parametrize('actual',[{'zones':[]},{'zones':[{'id':43,'cells_count':4}]},{'zones':[{'id':42,'cells_count':2}]}])
async def test_missing_or_partial_zone_is_not_complete(colony,actual):
    rt,_=colony
    async def call(*args,**kwargs):return actual
    rt.api.call=call
    action=Action(title='Stockpile',endpoint='post_map_zone_stockpile',arguments={'map_id':7,'point_a':{'x':0,'z':0},'point_b':{'x':1,'z':1}})
    assert not await rt.action_complete(action,{'zone_id':42,'success':True})
    assert not await rt.action_complete(action,{'zone_id':42,'success':False})

async def test_wrong_crop_does_not_verify_growing_zone(colony):
    rt,_=colony
    async def call(endpoint,*args,**kwargs):
        if endpoint=='get_map_zones':return {'zones':[{'id':42,'cells_count':1}]}
        return {'zone':{'id':42},'plant_def_name':'Plant_Daylily'}
    rt.api.call=call
    action=Action(title='Food zone',endpoint='post_map_zone_growing',arguments={'map_id':7,'point_a':{'x':1,'z':1},'point_b':{'x':1,'z':1},'plant_def':'Plant_Rice'})
    assert not await rt.action_complete(action,{'zone':{'id':42}})
    assert not await rt.action_complete(action,{'zone':None})
