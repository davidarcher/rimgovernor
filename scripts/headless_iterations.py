"""Fresh eight-tribal, real-model trials with a configurable consecutive-pass gate."""
import argparse
import asyncio
import json
import subprocess
import sys
import time
from collections import Counter
from pathlib import Path
from rimbot.bridge_runtime import BridgeRuntime
from rimbot.config import Settings
from rimbot.store import Store
from rimbot.campaign_metrics import CampaignEvidence, event_metrics
from rimbot.bridge_game import is_write
from rimbot.receipts import _outcome
from rimbot.bridge_observation import observe
from rimbot.campaign_manifest import capture_manifest


def consecutive_passes(results, *, require_manifest=False):
    streak = best = 0
    previous = None
    previous_iteration = None
    counts = Counter(result['iteration'] for result in results)
    for result in sorted(results, key=lambda r: r['iteration']):
        iteration = result['iteration']
        manifest = result.get('manifest_fingerprint')
        identity = (result.get('revision'), result.get('model'), result.get('direction'), manifest)
        if previous != identity or previous_iteration is None or iteration != previous_iteration + 1:
            streak = 0
        if (counts[iteration] == 1 and result.get('outcome') == 'usable_foothold'
                and not result.get('interrupted') and not result.get('cleanup_error')
                and (not require_manifest or bool(manifest))):
            streak += 1
            best = max(best, streak)
        else:
            streak = 0
        previous = identity
        previous_iteration = iteration
    return best


class FastTrial(BridgeRuntime):
    async def native(self, name, arguments, **kwargs):
        began=time.monotonic(); outcome='cancelled'; useful=False
        try:
            result=await super().native(name,arguments,**kwargs)
            outcome='returned'
            useful=(is_write(name,arguments) and name not in {
                'rimworld/set_time_speed','rimworld/open_letter','rimworld/dismiss_letter',
                'rimworld/click_screen_target','rimworld/click_ui_target','rimworld/scroll_ui_target',
                'rimworld/open_main_tab','rimworld/close_main_tab'}
                and (name!='home/place_building' or _outcome(result.get('receipt', {}))=='placed'))
            return result
        except Exception:
            outcome='failed'
            raise
        finally:
            self.note('campaign_native',name,tool=name,arguments=arguments,outcome=outcome,
                      elapsed_seconds=round(time.monotonic()-began,4),useful_order_receipt=useful)

    async def model_progress(self, values):
        if 'request_budget' in values:
            self.note('campaign_budget','Model context budget',budget=values['request_budget'])
        await super().model_progress(values)

async def sample_metrics(rt,evidence,anchor,elapsed,*,capture=None):
    started=time.monotonic()
    await rt.sync_identity()
    token=rt.context_token
    batch=await observe(rt.game)
    buildings=await rt.game.query('home/list_buildings',aggregate=False,playerOnly=True)
    zones=await rt.game.query('home/list_zones',includeCells=True,maxCellsPerZone=10000,filter=True)
    errors={}
    try:rooms=await rt.game.query('home/list_rooms')
    except Exception as error:rooms=None;errors['rooms']=str(error)
    try:pawns=await rt.game.query('home/list_pawns',colonistsOnly=True)
    except Exception as error:pawns=None;errors['pawns']=str(error)
    try:
        items=await rt.game.query('home/list_things',ownership='ours',includeHeld=True,
            x=round(anchor[0]),z=round(anchor[1]),radius=evidence.thresholds.radius+1,maxPositionsPerDef=32)
    except Exception as error:items=None;errors['items']=str(error)
    await rt.sync_identity()
    if token!=rt.context_token:raise ValueError('Campaign observation crossed a load/map change')
    result=evidence.sample(rt.current_plan,batch.summary,anchor,buildings,zones,elapsed,rooms=rooms,pawns=pawns,items=items,mode=rt.mode)
    if capture is not None:capture.update(buildings=buildings,zones=zones,rooms=rooms,pawns=pawns,items=items)
    result['observation_seconds']=round(time.monotonic()-started,4)
    result['optional_observation_errors']=errors
    return result


async def worker(args):
    folder=args.output/f'iteration-{args.worker:02}';folder.mkdir(parents=True,exist_ok=True)
    if (folder/'result.json').exists():raise RuntimeError('Choose a new output directory; existing trial evidence will not be overwritten')
    store=Store(folder/'state.sqlite')
    root=args.source_root
    if args.isolated:
        from rimbot.headless import isolated_root
        root=isolated_root(root,folder/'bridge')
    rt=FastTrial(store,root,fresh=True,headless=not args.rendered,settings=Settings(model=args.model))
    report={'iteration':args.worker,'model':args.model,'headless':not args.rendered,'speed':'Paused deliberation; bounded execution',
            'revision':subprocess.check_output(['git','rev-parse','HEAD'],text=True).strip(),
            'direction':args.direction}
    evidence=CampaignEvidence()
    report['metrics']=evidence.report()
    (folder/'thresholds.json').write_text(json.dumps(report['metrics']['thresholds'],indent=2),encoding='utf-8')
    start=time.monotonic()
    try:
        from rimbot.headless import prepare, prepare_rendered
        configuration = prepare_rendered(root) if args.rendered else prepare(root)
        source = Path(__file__).resolve().parents[1]
        loaded_runtime = Path(sys.modules[BridgeRuntime.__module__].__file__).resolve()
        if loaded_runtime != source/'controller/rimbot/bridge_runtime.py':
            raise ValueError('Campaign runtime was imported from another checkout; set PYTHONPATH to this source/controller')
        manifest = capture_manifest(source, root, configuration,
                                    rt.router.routing.model_dump(mode='json'),
                                    profile=root/('profile' if args.rendered else 'headless-profile'))
        report['revision'] = manifest['inputs']['source']['revision']
        report['manifest_fingerprint'] = manifest['fingerprint']
        (folder/'manifest.json').write_text(json.dumps(manifest,indent=2),encoding='utf-8')
        await rt.start()
        while not rt.connected:
            if rt.task.done():raise RuntimeError('Native startup failed')
            if time.monotonic()-start>150:raise TimeoutError('Native startup timeout')
            await asyncio.sleep(1)
        anchor=(sum(p.position.x for p in rt.batch.summary.pawns)/8,
                sum(p.position.z for p in rt.batch.summary.pawns)/8)
        if args.direction:
            await rt.steer(args.direction)
        try:report['foothold']=await sample_metrics(rt,evidence,anchor,0)
        except Exception as error:
            report['foothold']={'usable':False,'observation_error':str(error)}
            report.setdefault('outcome_observation_errors',[]).append({'elapsed_seconds':0,'error':str(error)})
        report['began_at']=time.time()
        await rt.set_mode('automate')
        began=time.monotonic();deadline=began+args.seconds;extended=False;last=0;acknowledged=False;last_sample=-15
        while time.monotonic()<deadline:
            await asyncio.sleep(2)
            elapsed=round(time.monotonic()-began,1)
            report.update(counters=rt.counters.copy(),elapsed_seconds=elapsed)
            if elapsed-last_sample>=15:
                last_sample=elapsed
                try:
                    report['foothold']=await sample_metrics(rt,evidence,anchor,elapsed)
                except Exception as error:
                    report['foothold']={'usable':False,'observation_error':str(error)}
                    report.setdefault('outcome_observation_errors',[]).append({'elapsed_seconds':elapsed,'error':str(error)})
            if rt.counters['actions'] and not extended and not args.fixed_window:
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
                    rt.note('campaign_intervention','Acknowledged fixture Ancient danger hold',letter_id=rows[0].get('id'))
                    await rt.set_mode('automate')
                    continue
            if rt.mode!='automate' or not rt.connected:
                report['stopped_reason']=rt.phase;break
        report['outcome']='usable_foothold' if report.get('foothold',{}).get('usable') else 'not_usable'
        report['plan']=rt.current_plan.model_dump()
        report['observation']=rt.batch.summary.model_dump()
    except Exception as error:
        report.update(outcome='error',error=str(error))
    finally:
        if rt.task:
            try:await asyncio.wait_for(rt.stop(),45)
            except Exception as error:report['cleanup_error']=str(error)
        report['metrics']=evidence.report()
        report['interrupted']=bool(report.get('stopped_reason') or report.get('error') or report.get('cleanup_error'))
        report['events']=store.history(rt.colony,limit=10000,include_diagnostics=True)
        report['telemetry']=event_metrics(report['events'],began_at=report.get('began_at'),role_metrics=rt.router.metrics)
        report['telemetry']['event_history_may_be_truncated']=len(report['events'])>=10000
        report['telemetry']['model_context_tokens']=rt.settings.model_context_tokens
        report['telemetry']['reserved_output_tokens']=rt.settings.max_output_tokens
        store.close()
        (folder/'result.json').write_text(json.dumps(report,indent=2),encoding='utf-8')
        print(json.dumps({'iteration':args.worker,'outcome':report['outcome'],'foothold':report.get('foothold'),
                          'error':report.get('error')}),flush=True)


if __name__=='__main__':
    parser=argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--worker',type=int)
    parser.add_argument('--iterations',type=int,default=20)
    parser.add_argument('--consecutive',type=int,default=1,help='Required consecutive foothold passes with identical revision, model and direction')
    parser.add_argument('--parallel',type=int,default=1,help='Concurrent isolated game/model workers; begin with 2')
    parser.add_argument('--isolated',action='store_true',help=argparse.SUPPRESS)
    parser.add_argument('--seconds',type=int,default=120)
    parser.add_argument('--fixed-window',action='store_true',help='Do not extend the observation window after a late first action')
    parser.add_argument('--rendered',action='store_true',help='Show the isolated game window with normal rendering')
    parser.add_argument('--model',default='qwen3.5-9b')
    parser.add_argument('--source-root',type=Path,default=Path('.rimbot/bridge'))
    parser.add_argument('--direction',default='',help='Frozen player objective, recorded before automation begins')
    parser.add_argument('--output',type=Path,default=Path('.rimbot/headless-campaign'))
    args=parser.parse_args()
    if args.worker:asyncio.run(worker(args))
    else:
        if not 1<=args.iterations<=20:parser.error('Choose 1 to 20 iterations')
        if not 1<=args.consecutive<=args.iterations:parser.error('Consecutive passes must be between 1 and the iteration count')
        if not 1<=args.parallel<=8:parser.error('Choose 1 to 8 parallel workers')
        args.output.mkdir(parents=True,exist_ok=True)
        results=[]
        def trial(number):
            subprocess.run([sys.executable,__file__,'--worker',str(number),'--seconds',str(args.seconds),
                '--model',args.model,'--output',str(args.output),'--source-root',str(args.source_root),
                '--direction',args.direction,'--isolated']+(['--fixed-window'] if args.fixed_window else [])
                +(['--rendered'] if args.rendered else []),check=True)
            result=json.loads((args.output/f'iteration-{number:02}'/'result.json').read_text())
            return {k:result.get(k) for k in ('iteration','outcome','revision','model','direction','manifest_fingerprint','counters','foothold','interrupted','error','cleanup_error')}
        # Small batches leave room for fixes between batches. A successful worker
        # prevents another batch; already-running siblings finish and retain evidence.
        from concurrent.futures import ThreadPoolExecutor, as_completed
        with ThreadPoolExecutor(max_workers=args.parallel) as pool:
            for first in range(1,args.iterations+1,args.parallel):
                futures=[pool.submit(trial,n) for n in range(first,min(first+args.parallel,args.iterations+1))]
                for future in as_completed(futures):
                    results.append(future.result());results.sort(key=lambda r:r['iteration'])
                    (args.output/'summary.json').write_text(json.dumps(results,indent=2),encoding='utf-8')
                if consecutive_passes(results,require_manifest=args.consecutive>1)>=args.consecutive:break
