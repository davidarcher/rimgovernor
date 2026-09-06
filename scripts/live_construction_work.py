"""Disposable live construction observation test; leaves a test blueprint and restores forbid state; reload your fixture afterward."""
import asyncio,json,time
from pathlib import Path
import httpx
from rimbot.catalog import Catalog
from rimbot.rimapi import RimAPI
from rimbot.http_models import ConstructionWorkOverview

async def main():
    state=httpx.get('http://127.0.0.1:8787/api/state').json()
    assert state['mode']=='manual' and state['connected']
    api=RimAPI('http://127.0.0.1:8765',Catalog());changed=None;placement=None
    try:
        await api.discover()
        assert (await api.call('get_game_state',{},fresh=True))['is_paused']
        mid=state['observation']['map']['id']
        definitions=await api.call('construction_definitions',{'search':'Bed','offset':0,'limit':32})
        bed=next(d for d in definitions.items if d.def_name=='Bed')
        things=await api.call('get_map_things',{'map_id':mid},fresh=True)
        center=state['observation']['pawns'][0]['colonist']['position']
        stack=min((t for t in things if t['def_name']=='WoodLog'),key=lambda t:abs(t['position']['x']-center['x'])+abs(t['position']['z']-center['z']))
        for dx in range(-8,9):
            for dz in range(-8,9):
                candidate={'map_id':mid,'buildings':[{'def_name':'Bed','stuff_def_name':stack['def_name'],'position':{'x':stack['position']['x']+dx,'z':stack['position']['z']+dz},'rotation':0}]}
                test=await api.call('construction_inspect',candidate)
                if test.accepted and test.items[0].state=='ready':placement=candidate;break
            if placement:break
        assert placement,'No valid test site near a material stack'
        result=await api.call('construction_place',placement)
        assert result.accepted
        target=result.items[0].thing_id
        raw=await api.call('get_map_construction_work',{'map_id':mid,'limit':32},fresh=True)
        before=ConstructionWorkOverview.model_validate(raw)
        row=next(s for s in before.sites if s.thing_id==target)
        material=next(m for m in row.materials if m.def_name==stack['def_name'])
        assert material.needed>0 and row.work_done==0
        changed={'map_id':mid,'thing_ids':[stack['thing_id']],'forbidden':stack['is_forbidden']}
        await api.call('post_things_set_forbidden',{**changed,'forbidden':not stack['is_forbidden']},write=True)
        after=ConstructionWorkOverview.model_validate(await api.call('get_map_construction_work',{'map_id':mid,'limit':32},fresh=True))
        new=next(m for s in after.sites if s.thing_id==target for m in s.materials if m.def_name==stack['def_name'])
        delta=stack['stack_count'] if stack['is_forbidden'] else -stack['stack_count']
        assert new.allowed_quantity-material.allowed_quantity==delta
        assert new.forbidden_quantity-material.forbidden_quantity==-delta
        assert new.needed==material.needed,'Allowing supplies must not pretend delivery happened'
        assert after.observed_tick==before.observed_tick
        Path('.rimbot/work-live.json').write_text(json.dumps({'passed':True,'before':before.model_dump(),'after':after.model_dump()},indent=2))
        print('Passed native material, forbid, paused-state and DTO checks.')
    finally:
        if changed:await api.call('post_things_set_forbidden',changed,write=True)
        # Caller reloads the fixed fixture after this test. Avoid deleting via a guessed command.
        await api.close()

if __name__=='__main__':asyncio.run(main())
