"""Fresh eight-tribal, real-model trials; stop at a starter foothold or 20 runs."""
import argparse
import asyncio
import json
import subprocess
import sys
import time
from pathlib import Path
from rimbot.bridge_runtime import BridgeRuntime
from rimbot.config import Settings
from rimbot.store import Store


class FastTrial(BridgeRuntime):
    async def review(self):
        await super().review()
        if self.mode=='automate' and self.supervisor and not self.supervisor.hold:
            await self.supervisor.change('Superfast')


def foothold(rt, anchor):
    sleeping=set(); stockpile=False
    near=lambda x,z: max(abs(x-anchor[0]),abs(z-anchor[1]))<=30
    for step in rt.current_plan.spec.steps:
        if rt.current_plan.progress[step.id].state!='complete':continue
        action=step.action
        if action.kind=='place_buildings':
            sleeping.update((p.x,p.z) for p in action.placements
                if p.def_name in ('Bed','SleepingSpot') and near(p.x,p.z))
        if action.kind=='create_zone' and action.zone_type=='stockpile':
            cells={c for patch in action.patches for c in patch.cells()}
            stockpile |= len(cells)>=9 and all(near(x,z) for x,z in cells)
    pawns=rt.batch.summary.pawns
    food=any(s.def_name=='Pemmican' and s.owned_unforbidden_units>0 for s in rt.batch.summary.supplies)
    return {'nearby_sleeping_capacity':len(sleeping),'stockpile':stockpile,'starting_food_allowed':food,
        'living_colonists':sum(not p.dead for p in pawns),
        'usable':len(sleeping)>=8 and stockpile and food and len(pawns)==8 and not any(p.dead for p in pawns)}


async def worker(args):
    folder=args.output/f'iteration-{args.worker:02}';folder.mkdir(parents=True,exist_ok=True)
    if (folder/'result.json').exists():raise RuntimeError('Choose a new output directory; existing trial evidence will not be overwritten')
    store=Store(folder/'state.sqlite')
    rt=FastTrial(store,Path('.rimbot/bridge'),fresh=True,headless=True,settings=Settings(model=args.model))
    report={'iteration':args.worker,'model':args.model,'headless':True,'speed':'Superfast after review',
            'revision':subprocess.check_output(['git','rev-parse','HEAD'],text=True).strip()}
    start=time.monotonic()
    try:
        await rt.start()
        while not rt.connected:
            if rt.task.done():raise RuntimeError('Native startup failed')
            if time.monotonic()-start>150:raise TimeoutError('Native startup timeout')
            await asyncio.sleep(1)
        anchor=(sum(p.position.x for p in rt.batch.summary.pawns)/8,
                sum(p.position.z for p in rt.batch.summary.pawns)/8)
        await rt.set_mode('automate')
        began=time.monotonic();deadline=began+args.seconds;extended=False;last=0;acknowledged=False
        while time.monotonic()<deadline:
            await asyncio.sleep(2)
            elapsed=round(time.monotonic()-began,1)
            report.update(counters=rt.counters.copy(),elapsed_seconds=elapsed,foothold=foothold(rt,anchor))
            if rt.counters['actions'] and not extended:
                deadline=max(deadline,time.monotonic()+120);extended=True
            if elapsed-last>=15:
                print(json.dumps({'iteration':args.worker,'seconds':elapsed,**rt.counters}),flush=True);last=elapsed
            if report['foothold']['usable']:break
            if rt.mode!='automate' and not acknowledged and rt.connected:
                letters=await rt.game.invoke('rimworld/list_letters',{})
                rows=letters.get('letters',[])
                if (not letters.get('truncated') and len(rows)==1 and rows[0].get('label')=='Ancient danger'
                        and rt.batch.summary.hostile_count==0 and not any(p.dead or p.downed or p.bleeding for p in rt.batch.summary.pawns)):
                    acknowledged=True;report['fixture_warning_acknowledged']=rows[0].get('id')
                    await rt.set_mode('automate')
                    continue
            if rt.mode!='automate' or not rt.connected:
                report['stopped_reason']=rt.phase;break
        report['outcome']='usable_foothold' if report['foothold']['usable'] else 'not_usable'
        report['plan']=rt.current_plan.model_dump()
        report['observation']=rt.batch.summary.model_dump()
    except Exception as error:
        report.update(outcome='error',error=str(error))
    finally:
        if rt.task:
            try:await asyncio.wait_for(rt.stop(),45)
            except Exception as error:report['cleanup_error']=str(error)
        report['events']=store.history(rt.colony,limit=10000,include_diagnostics=True)
        store.close()
        (folder/'result.json').write_text(json.dumps(report,indent=2),encoding='utf-8')
        print(json.dumps({'iteration':args.worker,'outcome':report['outcome'],'foothold':report.get('foothold'),
                          'error':report.get('error')}),flush=True)


if __name__=='__main__':
    parser=argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--worker',type=int)
    parser.add_argument('--iterations',type=int,default=20)
    parser.add_argument('--seconds',type=int,default=120)
    parser.add_argument('--model',default='qwen3.5-9b')
    parser.add_argument('--output',type=Path,default=Path('.rimbot/headless-campaign'))
    args=parser.parse_args()
    if args.worker:asyncio.run(worker(args))
    else:
        if not 1<=args.iterations<=20:parser.error('Choose 1 to 20 iterations')
        args.output.mkdir(parents=True,exist_ok=True)
        results=[]
        for number in range(1,args.iterations+1):
            subprocess.run([sys.executable,__file__,'--worker',str(number),'--seconds',str(args.seconds),
                '--model',args.model,'--output',str(args.output)],check=True)
            result=json.loads((args.output/f'iteration-{number:02}'/'result.json').read_text())
            results.append({k:result.get(k) for k in ('iteration','outcome','revision','counters','foothold','error')})
            (args.output/'summary.json').write_text(json.dumps(results,indent=2),encoding='utf-8')
            if result['outcome']=='usable_foothold':break
