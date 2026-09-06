from conftest import dto_fixture
from rimbot.http_models import MapResourceOverview
from rimbot.resources import resource_brief


def test_resource_brief_preserves_availability_and_reports_omissions():
    raw=dto_fixture({'$ref':'#/components/schemas/MapResourceOverview'}, {
        'supplies':[{'def_name':f'Item{i}','quantity':100,'allowed_quantity':30,
                     'forbidden_quantity':70,'nearby_allowed_quantity':10,
                     'nearby_forbidden_quantity':20} for i in range(15)],
        'fishing':[{'totally_frozen':None}]})
    brief=resource_brief(MapResourceOverview.model_validate(raw))
    assert brief['supplies']['total_groups']==15
    assert brief['supplies']['omitted_groups']==3
    assert len(brief['supplies']['items'])==12
    assert brief['supplies']['items'][0]['nearby_allowed_quantity']==10
    assert brief['supplies']['items'][0]['forbidden_quantity']==70
    assert brief['fishing']['items'][0]['totally_frozen'] is None


async def test_resource_observation_uses_observed_focus_and_native_contract(colony):
    rt,game=colony
    rt.memory['colony_focus']={'x':101,'z':103}
    context=await rt.observe_resources({'direction':'Grow a colony'})
    assert context['direction']=='Grow a colony'
    assert context['resource_overview']['map_id']==game.map_id
    assert context['resource_overview']['supplies']['omitted_groups']==0


async def test_missing_resource_endpoint_is_unknown_not_empty_resources(colony):
    rt,_=colony
    rt.catalog.available.discard('get_map_resource_overview')
    context=await rt.observe_resources({})
    assert context['resource_overview']['available'] is False
    assert 'supplies' not in context['resource_overview']
