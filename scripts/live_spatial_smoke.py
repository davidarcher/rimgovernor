"""Live architect smoke test: real map and local vision model, no supplied placement cells.
Run against a disposable quicktest with the dashboard in Manual. Writes native
planning marks, verifies them, then removes only unchanged test-owned marks.
Does not claim colonists completed construction.
"""
import asyncio,json,time
from pathlib import Path
import httpx
from rimbot.runtime import Runtime
from rimbot.store import Store
from rimbot.rimapi import APIError
from rimbot.spatial import prepare_layout,cells

async def run():
    out=Path('.rimbot/spatial-tests')/time.strftime('%Y%m%d-%H%M%S');out.mkdir(parents=True)
    async with httpx.AsyncClient(trust_env=False) as h:
        live=(await h.get('http://127.0.0.1:8787/api/state')).json()
        if live['mode']!='manual' or live['busy']:raise RuntimeError('Use an idle test colony in Manual')
    rt=Runtime(Store(out/'test.sqlite'));started=time.monotonic();report={'passed':False}
    try:
        for _ in range(30):
            try:await rt.poll()
            except (APIError,httpx.HTTPError):pass
            if rt.connected:break
            await asyncio.sleep(2)
        if not rt.connected:raise RuntimeError(str(rt.status))
        fresh=await rt.manager_context({})
        rt.mode='automate';rt.cycle_generation=rt.generation
        rt.memory['projects']=[dict(project_id=id,kind=kind,owner=owner,outcome=outcome,status='approved',work_ids=[],constraints=[],definition_requirements={},quantity=None,success_signals=['Observed suitable site']) for id,kind,owner,outcome in [('shelter','construction','Infrastructure','One enclosed shelter near camp with usable interior space for three colonists to sleep.'),('food','growing','Survival','A small starter food field following suitable nearby soil; preserve access to shelter.'),('storage','storage','Infrastructure','A small accessible stockpile near or inside the planned shelter.')]]
        await prepare_layout(rt,fresh,rt.memory['projects'])
        layout=rt.memory['spatial_layout'];(out/'layout.json').write_text(json.dumps(layout,indent=2));(out/'map.png').write_bytes(rt.spatial_image)
        native=await rt.api.call('planning_state',{'map_id':rt.observation['map']['id']})
        for r in layout['regions']:
            if r.get('parent_id'):continue
            plan=next(p for p in native.plans if p.id==layout['native_plans'][r['id']])
            assert {(c.x,c.z) for c in plan.cells}==cells(r)
        assert layout['regions'],'No sites proposed'
        report.update(passed=True,elapsed=round(time.monotonic()-started,1),regions=len(layout['regions']),deferred=layout['deferred'],usage=rt.counters)
    except Exception as e:report['error']=str(e);raise
    finally:
        for r in rt.memory.get('spatial_layout',{}).get('regions',[]):
            pid=rt.memory['spatial_layout'].get('native_plans',{}).get(r['id'])
            if pid:await rt.api.call('planning_remove',{'map_id':rt.observation['map']['id'],'plan_id':pid,'expected_cells':[{'x':x,'z':z} for x,z in cells(r)]},write=True)
        (out/'report.json').write_text(json.dumps(report,indent=2));print(json.dumps(report),flush=True);print(out,flush=True)
        await rt.api.close();await rt.model.close()
        if rt.manager_model:await rt.manager_model.close()
        rt.store.close()

if __name__=='__main__':asyncio.run(run())
