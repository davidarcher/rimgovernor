from types import SimpleNamespace
from unittest.mock import AsyncMock
import pytest
from rimbot.resource_budget import balances, allocate, observe_budget, quote, BudgetConflict
from rimbot.native_models import ConstructionRequest, ConstructionResult


def test_competing_projects_cannot_both_spend_the_same_steel():
    budget=balances({'Steel':350},{},{'Steel':50})
    accepted,rejected=allocate(budget,[('defense',{'Steel':250}),('workshop',{'Steel':180})])
    assert accepted==['defense'] and rejected=={'workshop':{'Steel':130}}
    assert budget['Steel'].spendable==300  # Allocator does not mutate the observation.


def test_native_remaining_deliveries_are_the_only_site_commitment():
    # Delivery moves 80 loose steel into the frame; obligation drops by the same amount.
    before=balances({'Steel':300},{'Steel':180},{})
    delivered=balances({'Steel':220},{'Steel':100},{})
    assert before['Steel'].spendable==delivered['Steel'].spendable==120
    # Completed construction has no remaining delivery obligation; no stale software hold.
    assert balances({'Steel':120},{},{})['Steel'].spendable==120
    assert balances({'Steel':300},{},{})['Steel'].spendable==300  # Cancelled native blueprint.


def test_unknown_material_is_not_free_and_empty_orders_are_immediate():
    assert allocate({},[('spot',{})])==(['spot'],{})
    assert allocate({},[('wall',{'UnknownMaterial':5})])==([] ,{'wall':{'UnknownMaterial':5}})
    with pytest.raises(ValueError):allocate({},[('bad',{'Steel':-1})])
    with pytest.raises(ValueError):balances({'Steel':1},{},{'Steel':-1})
    b=balances({'Steel':50},{'Steel':100},{})['Steel']
    assert b.spendable==0 and b.deficit==50


def request():
    return ConstructionRequest(map_id=7,buildings=[{'def_name':'Wall','stuff_def_name':'TimberMod',
        'position':{'x':10,'z':10},'rotation':0}])


async def test_quote_uses_native_definitions_and_does_not_charge_existing_sites():
    req=request()
    result=ConstructionResult(accepted=True,items=[{'placement':req.buildings[0], 'state':'ready','thing_id':None,'reason':''}])
    definition=SimpleNamespace(def_name='Wall',costs=[SimpleNamespace(def_name='FastenerMod',count=2)],stuff_count=5)
    api=SimpleNamespace(native=SimpleNamespace(inspect=AsyncMock(return_value=result)),
                        call=AsyncMock(return_value=SimpleNamespace(items=[definition],next_offset=None)))
    rt=SimpleNamespace(api=api)
    assert await quote(rt,req)=={'FastenerMod':2,'TimberMod':5}
    result.items[0].state='blueprint';result.items[0].thing_id=25
    api.call.reset_mock()
    assert await quote(rt,req)=={}
    api.call.assert_not_called()


async def test_budget_reads_all_pages_and_full_supply_groups(colony):
    from conftest import dto_fixture
    rt,_=colony
    rt.memory['colony_focus']={'x':10,'z':10}
    def fill(name,value):return dto_fixture({'$ref':'#/components/schemas/'+name},value)
    supply=fill('MapResourceOverview',{'map_id':7,'supplies':[
        {'def_name':'Steel','allowed_quantity':300,'forbidden_quantity':900}]})
    pages=[fill('ConstructionWorkOverview',{'map_id':7,'offset':0,'total':2,'next_offset':1,
        'sites':[{'thing_id':1,'materials':[{'def_name':'Steel','needed':100}]}]}),
        fill('ConstructionWorkOverview',{'map_id':7,'offset':1,'total':2,'next_offset':None,
        'sites':[{'thing_id':2,'materials':[{'def_name':'Steel','needed':80}]}]})]
    async def call(name,args,**kw):
        if name=='get_map_resource_overview':return supply
        return pages[args['offset']]
    rt.api.call=call
    budget=await observe_budget(rt)
    assert budget.materials['Steel'].spendable==120 and budget.site_count==2
    assert rt.memory['resource_budget']['materials']['Steel']['committed']==180
    pages[1]['sites'][0]['thing_id']=1
    with pytest.raises(BudgetConflict,match='pagination'):await observe_budget(rt)


async def test_execution_serializes_admission_and_never_sends_over_budget_order(colony,monkeypatch):
    import asyncio
    from rimbot.contracts import Action
    from rimbot.resource_budget import ConstructionBudget
    from test_native_contracts import manifest
    rt,_=colony;rt.mode='automate';rt.cycle_generation=rt.generation
    rt.catalog.install_contracts(manifest())
    rt.action_complete=AsyncMock(return_value=False)
    committed=0;sent=[]
    async def price(runtime,req):return {'Steel':250 if req.buildings[0].position.x==10 else 180}
    async def survey(runtime):return ConstructionBudget(map_id=7,observed_tick=1000,site_count=len(sent),materials=balances({'Steel':300},{'Steel':committed},{}))
    async def place(req,revision):
        nonlocal committed
        await asyncio.sleep(0)  # Let another caller contend while the write is in flight.
        committed+=250;sent.append(req)
        return ConstructionResult(accepted=True,items=[{'placement':req.buildings[0],'state':'blueprint','thing_id':10,'reason':''}])
    monkeypatch.setattr('rimbot.resource_budget.quote',price)
    monkeypatch.setattr('rimbot.resource_budget.observe_budget',survey)
    rt.api.native.place=place
    a=request();b=request();b.buildings[0].position.x=20
    results=await asyncio.gather(*(rt.execute(Action(title='Build',endpoint='construction_place',arguments=r.model_dump()),'Executor:construction') for r in (a,b)),return_exceptions=True)
    assert results[0] is None and isinstance(results[1],BudgetConflict)
    assert len(sent)==1 and len(rt.memory['work'])==1
    assert rt.memory['work'][0]['status']=='issued'
