"""Read-only, repeatable native resource survey and cache regression on a paused colony."""
import asyncio,json,time
from pathlib import Path
import httpx
from rimbot.catalog import Catalog
from rimbot.rimapi import RimAPI
from rimbot.http_models import MapResourceOverview
from rimbot.resources import resource_brief

async def main():
    state=httpx.get('http://127.0.0.1:8787/api/state').json()
    assert state['mode']=='manual'
    api=RimAPI('http://127.0.0.1:8765',Catalog())
    try:
        await api.discover()
        game=await api.call('get_game_state',{},fresh=True)
        assert game['is_paused'],'Pause the test colony for stable comparisons.'
        center=state['memory']['colony_focus']
        args={'map_id':state['observation']['map']['id'],'center_x':center['x'],'center_z':center['z'],'nearby_radius':40}
        samples=[];latencies=[]
        for refresh in (True,False):
            start=time.perf_counter()
            raw=await api.call('get_map_resource_overview',{**args,'refresh_terrain':refresh},fresh=True)
            samples.append(MapResourceOverview.model_validate(raw))
            latencies.append(round((time.perf_counter()-start)*1000,2))
        cold,warm=samples
        assert not cold.terrain_cache_hit and warm.terrain_cache_hit
        assert cold.terrain_observed_tick==warm.terrain_observed_tick
        assert cold.model_dump(exclude={'terrain_cache_hit'})==warm.model_dump(exclude={'terrain_cache_hit'})
        assert sum(t.visible_cells for t in warm.terrain)==warm.visible_cells
        for t in warm.terrain:assert 0<=t.largest_nearby_patch<=t.nearby_cells<=t.visible_cells
        for s in warm.supplies:
            assert s.allowed_quantity+s.forbidden_quantity==s.quantity
            assert 0<=s.nearby_allowed_quantity<=s.allowed_quantity
            assert 0<=s.nearby_forbidden_quantity<=s.forbidden_quantity
        for p in warm.plants:
            assert p.harvestable_count<=p.wild_count+p.sown_count
            assert 0<=p.nearby_harvestable_yield<=p.harvestable_yield
        for a in warm.animals:assert 0<=a.nearby_wild_meat_estimate<=a.wild_meat_estimate
        for m in warm.minerals:assert 0<=m.nearby_base_yield_estimate<=m.base_yield_estimate
        for invalid in ({**args,'nearby_radius':0},{**args,'center_x':-1}):
            response=httpx.get('http://127.0.0.1:8765/api/v1/map/resource-overview',params=invalid)
            assert response.status_code==400,response.text
            assert response.json()['code']=='invalid_request'
        brief=resource_brief(warm)
        report={'passed':True,'latency_ms_cold_warm':latencies,'full_chars':len(warm.model_dump_json()),'brief_chars':len(json.dumps(brief,separators=(',',':'))),'overview':warm.model_dump()}
        Path('.rimbot/resources-live.json').write_text(json.dumps(report,indent=2))
        print({k:v for k,v in report.items() if k!='overview'})
    finally:await api.close()

if __name__=='__main__':asyncio.run(main())
