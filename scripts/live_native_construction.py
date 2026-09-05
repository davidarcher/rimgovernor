"""Live typed-contract regression on a disposable colony. Leaves a bed/spot.

--execute is required. Verifies strict rejection, legal placement, idempotency,
instant objects and optional real-model draft/arbitration with native readback.
"""
import argparse
import asyncio
import json
import time
from pathlib import Path
import httpx
from rimbot.config import Settings
from rimbot.runtime import Runtime
from rimbot.store import Store
from rimbot.contracts import Proposal
from rimbot.native_models import ConstructionRequest,Placement,Cell,DefinitionQuery,RoomQuery

async def run(use_model):
    async with httpx.AsyncClient() as http:
        state=(await http.get('http://127.0.0.1:8787/api/state')).json()
    if state['mode']!='manual' or state['busy']:raise RuntimeError('Dashboard must be idle in Manual.')
    folder=Path('.rimbot/native-construction-tests')/time.strftime('%Y%m%d-%H%M%S');folder.mkdir(parents=True)
    rt=Runtime(Store(folder/'trace.sqlite'),Settings(**state['settings']));report={'passed':False};started=time.monotonic()
    try:
        await rt.poll();rt.cycle_generation=rt.generation;rt.mode='automate'
        mid=rt.observation['map']['id'];base=rt.observation['pawns'][0]['colonist']['position']
        bed_defs=await rt.api.native.call('construction_definitions',DefinitionQuery(search='bed',offset=0,limit=32))
        bed=next(d for d in bed_defs.items if d.label=='bed')
        spot_defs=await rt.api.native.call('construction_definitions',DefinitionQuery(search='sleeping spot',offset=0,limit=32))
        spot=next(d for d in spot_defs.items if d.label=='sleeping spot')
        loose=await rt.api.call('get_map_things',{'map_id':mid})
        available={t['def_name'] for t in loose}
        material=next(m for m in bed.allowed_materials if m.def_name in available)
        rooms=await rt.api.native.call('construction_rooms',RoomQuery(map_id=mid,offset=0,limit=4))
        assert all(r.visible_cell is not None for r in rooms.items)
        report['rooms']=rooms.model_dump()
        def request(definition,x,z):
            return ConstructionRequest(map_id=mid,buildings=[Placement(def_name=definition.def_name,stuff_def_name=material.def_name if definition.allowed_materials else '',position=Cell(x=x,z=z),rotation=0)])
        async def find_site(definition):
            for distance in range(2,16):
                for dx,dz in [(distance,0),(0,distance),(-distance,0),(0,-distance)]:
                    q=request(definition,base['x']+dx,base['z']+dz)
                    inspection=await rt.api.native.inspect(q)
                    if inspection.accepted and all(i.state=='ready' for i in inspection.items):return q
            raise RuntimeError('No legal nearby fixture site.')
        q=await find_site(bed)
        bad=q.model_dump();bad['unexpected']=True
        response=await rt.api.http.post('/api/v2/construction/place',json=bad)
        assert response.status_code==400 and response.json()['code']=='contract_violation'
        overlap=ConstructionRequest(map_id=mid,buildings=q.buildings*2)
        rejected=await rt.api.native.place(overlap)
        assert not rejected.accepted and (await rt.api.native.inspect(q)).items[0].state=='ready'
        outside=request(bed,-1,-1)
        assert not (await rt.api.native.place(outside)).accepted
        first=await rt.api.native.place(q);second=await rt.api.native.place(q)
        report['bed']=first.model_dump()
        report['bed_repeat']=second.model_dump()
        assert first.accepted and first.items[0].state=='blueprint' and first.items[0].thing_id==second.items[0].thing_id, str(report['bed'])+' / '+str(report['bed_repeat'])
        spot_request=await find_site(spot);instant=await rt.api.native.place(spot_request)
        assert instant.accepted and instant.items[0].state=='built'
        at=await rt.api.call('get_map_things_at',{'map_id':mid,'position':{'x':spot_request.buildings[0].position.x,'z':spot_request.buildings[0].position.z}})
        assert any(t['thing_id']==instant.items[0].thing_id and t['def_name']==spot.def_name for t in at)
        report['instant']=instant.model_dump();report['protocol_passed']=True
        if use_model:
            target=await find_site(bed)
            context={'player_direction':'Bounded test: draft exactly one bed at the supplied inspected site using the supplied observed material. Do not address other colony needs. Use the native construction tools. Only draft an order; the integration executes after arbitration.',
                'map_id':mid,'site':target.buildings[0].position.model_dump(),'definition':bed.model_dump(),'material':material.model_dump()}
            proposals,decision=await asyncio.wait_for(rt.coordinate(context,['Infrastructure']),180)
            report.update(proposals=proposals,decision=decision.model_dump())
            actions=[a for role in decision.accepted for a in Proposal.model_validate(proposals[role]).actions]
            assert len(actions)==1 and actions[0].endpoint=='construction_place' and actions[0].done is None
            actual=ConstructionRequest.model_validate(actions[0].arguments)
            assert actual==target
            await rt.api.request('POST','/api/v1/game/speed',params={'speed':1})
            await rt.execute(actions[0],'Infrastructure')
            observed=await rt.api.native.inspect(target)
            assert observed.accepted and observed.items[0].state in ('blueprint','frame','built') and rt.counters['actions']==1
            report['model_readback']=observed.model_dump()
        report['passed']=True
    except BaseException as e:
        report['error']=str(e);raise
    finally:
        if rt.observation.get('game'):
            game=await rt.api.call('get_game_state',{},fresh=True)
            if game.get('session_id')==rt.observation['game']['session_id']:await rt.api.request('POST','/api/v1/game/speed',params={'speed':0})
        report.update(seconds=round(time.monotonic()-started,2),counters=rt.counters)
        (folder/'report.json').write_text(json.dumps(report,indent=2),encoding='utf-8')
        print(f"Passed: {report['passed']}. Evidence: {folder}/report.json",flush=True)
        await rt.stop();rt.store.close()

if __name__=='__main__':
    p=argparse.ArgumentParser(description=__doc__);p.add_argument('--execute',action='store_true',required=True);p.add_argument('--model',action='store_true');args=p.parse_args()
    asyncio.run(run(args.model))
