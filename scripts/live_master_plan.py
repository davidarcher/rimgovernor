"""Read-only game test: real architect, persisted master plan, incremental reuse.

Requires the dashboard idle in Manual and a loaded colony. Writes only isolated
controller artifacts under .rimbot; no game buildings or planning marks are issued.
"""
import asyncio,json,time
from pathlib import Path
from unittest.mock import Mock
import httpx
from rimbot.runtime import Runtime
from rimbot.store import Store
from rimbot.config import Settings
from rimbot.base_plan import prepare_master_plan,reserve_site,SiteRequest,extent
from rimbot.spatial import cells

async def main():
 state=httpx.get('http://127.0.0.1:8787/api/state').json()
 if state['mode']!='manual' or state['busy']:raise ValueError('Dashboard must be idle and Manual')
 folder=Path('.rimbot/master-plan-tests')/time.strftime('%Y%m%d-%H%M%S');folder.mkdir(parents=True)
 store=Store(folder/'trace.sqlite');rt=Runtime(store,Settings.model_validate(state['settings']))
 try:
  await rt.poll();rt.memory=rt.empty_memory();rt.cycle_generation=rt.generation
  project={'project_id':'shelter-test','owner':'Infrastructure','kind':'construction','outcome':'Shelter for the current tribal colony','status':'approved','work_ids':[]}
  rt.memory['projects']=[project];context=await rt.manager_context({});context=await rt.observe_resources(context)
  await prepare_master_plan(rt,context,[project]);plan=rt.memory['spatial_layout']
  (folder/'plan.json').write_text(json.dumps(plan,indent=2),encoding='utf-8');(folder/'plan.png').write_bytes(rt.spatial_image)
  rt.model_for_role=Mock(side_effect=AssertionError('Routine demand invoked architect'))
  zone=next(z for z in plan['zones'] if z['purpose']=='residential' and z['max_size']['width']>=8 and z['max_size']['height']>=7)
  first=await reserve_site(rt,project,SiteRequest(zone_id=zone['id'],label='Test shelter',purpose='room',width=6,height=5))
  larger=await reserve_site(rt,project,SiteRequest(zone_id=zone['id'],label='Test shelter',purpose='room',width=8,height=7,reuse_region_id=first['region']['id']))
  assert cells(first['region'])<=cells(larger['region'])<=cells(extent(zone))
  await prepare_master_plan(rt,context,[project]);rt.model_for_role.assert_not_called()
  report={'passed':True,'colony':rt.colony,'zones':len(plan['zones']),'phases':len(plan['build_phases']),'routine_architect_calls':0,'game_orders':0}
  (folder/'report.json').write_text(json.dumps(report,indent=2),encoding='utf-8');print(json.dumps(report));print(str(folder))
 finally:await rt.stop();store.close()
if __name__=='__main__':asyncio.run(main())
