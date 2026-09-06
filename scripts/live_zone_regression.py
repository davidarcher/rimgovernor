"""Disposable live regression: leaves one wall blueprint; creates no zones.
Run only against a loaded test colony with the dashboard in Manual.
"""
import asyncio
from pathlib import Path
import httpx
from rimbot.config import Settings
from rimbot.runtime import Runtime
from rimbot.store import Store
from rimbot.native_models import ConstructionRequest

async def main():
    async with httpx.AsyncClient(timeout=30) as http:
        state=(await http.get('http://127.0.0.1:8787/api/state')).json()
        assert state['mode']=='manual' and not state['busy'], 'Use Manual and wait for the review to stop'
        rt=Runtime(Store(Path('.rimbot/zone-regression.sqlite')),Settings(**state['settings']))
        try:
            await rt.poll()
            mid=rt.observation['map']['id']
            things=await rt.api.call('get_map_things',{'map_id':mid},fresh=True)
            origin=next(t['position'] for t in things if t['def_name']=='WoodLog')
            defs=await rt.api.native.call('construction_definitions',{'search':'Wall','offset':0,'limit':32})
            wall=next(d for d in defs.items if d.def_name=='Wall')
            stuff=next(m.def_name for m in wall.allowed_materials if m.def_name=='WoodLog')
            request=None
            for dx in range(2,10):
                p={'x':origin['x']+dx,'z':origin['z']}
                candidate=ConstructionRequest(map_id=mid,buildings=[{'def_name':'Wall','stuff_def_name':stuff,'position':p,'rotation':0}])
                check=await rt.api.native.inspect(candidate)
                if check.accepted and check.items[0].state=='ready':request=candidate;break
            assert request is not None, 'No suitable test wall site'
            result=await rt.api.native.place(request)
            assert result.accepted
            before=await rt.api.call('get_map_zones',{'map_id':mid},fresh=True)
            p=request.buildings[0].position.model_dump()
            for kind in ('growing','stockpile'):
                body={'map_id':mid,'point_a':p,'point_b':p}
                if kind=='growing':body['plant_def']='Plant_Rice'
                response=await http.post(rt.settings.rimapi_url+'/api/v1/map/zone/'+kind,json=body)
                payload=response.json()
                assert 'blocked by' in str(payload) and 'Blueprint_Wall' in str(payload), payload
                after=await rt.api.call('get_map_zones',{'map_id':mid},fresh=True)
                assert before==after, 'Rejected request changed zones'
                print(kind, 'rejected wall blueprint; zones unchanged:',payload)
        finally:
            await rt.api.http.aclose()
            await rt.model.http.aclose()

if __name__=='__main__':asyncio.run(main())
