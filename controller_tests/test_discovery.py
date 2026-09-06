import pytest
from rimbot.discovery import DefinitionIndex
from rimbot.catalog import Catalog


def test_full_text_discovery_ranks_native_object_and_returns_query():
    catalog=Catalog();catalog.available.update({'construction_definitions','construction_place'})
    index=DefinitionIndex({'things_defs':[
        {'def_name':'Bedroll','label':'bedroll','description':'Portable sleeping furniture.','is_building':True,'work_to_build':600},
        {'def_name':'SleepingSpot','label':'sleeping spot','description':'Designates ground where people sleep.','is_building':True,'work_to_build':0,'cost_stuff_count':0},
        {'def_name':'AnimalSleepingSpot','label':'animal sleeping spot','description':'Animals sleep here.','is_building':True},
        {'def_name':'Gun_Custom','label':'custom rifle','description':'Long ranged weapon.','is_weapon':True},
        {'def_name':'Bullet_Custom','label':'rifle bullet','description':'rifle rifle rifle'},
    ]})
    try:
        hit=index.search('free instant sleeping spot',catalog,['construction_place'])[0]
        assert hit.def_name=='SleepingSpot' and hit.facts['work_to_build']==0
        assert hit.read.endpoint=='construction_definitions' and hit.read.arguments['search']=='SleepingSpot'
        assert hit.actions==['construction_place']
        assert index.search('rifle',catalog,[])[0].def_name=='Gun_Custom'
        assert not index.search('sleeping spot',catalog,[])[0].actions
        assert index.search('" OR * -- sleeping',catalog,[])
    finally:index.close()


async def test_discovery_reuses_index_and_invalidates_on_game_change(colony):
    rt,_=colony;rt.api.invalidate(definitions=True);calls=[]
    async def typed(*args):
        calls.append(1)
        return {'plant_defs':[{'def_name':'ModdedCrop','label':'modded potato','description':'Food tuber'}]}
    rt.api.typed_data=typed
    first=await rt.api.search('potato',[])
    second=await rt.api.search('tuber',[])
    assert first.definitions[0].def_name==second.definitions[0].def_name=='ModdedCrop'
    assert len(calls)==1
    assert first.definitions[0].read.path=='plant_defs'
    rt.api.invalidate(definitions=True)
    await rt.api.search('potato',[])
    assert len(calls)==2
