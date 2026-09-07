"""Reload a fixed disposable save and benchmark the production manager loop.

The dashboard must be in Manual. Uses an isolated controller history, the dashboard's
model settings, and real game orders. Leaves the colony paused and Manual.
"""
import argparse,asyncio,hashlib,json,time,subprocess
from pathlib import Path
import httpx
from rimbot.runtime import Runtime
from rimbot.store import Store
from rimbot.config import Settings
from rimbot.benchmark import SetupMetrics

async def run(args):
    save=Path(args.save).resolve()
    if save.suffix!='.rws' or not save.is_file():raise ValueError('Supply an existing .rws in RimWorld Saves.')
    expected=Path.home()/"AppData/LocalLow/Ludeon Studios/RimWorld by Ludeon Studios/Saves"
    if save.parent!=expected.resolve():raise ValueError("Save must be in the native RimWorld Saves directory")
    import xml.etree.ElementTree as ET
    if ET.parse(save).getroot().tag!='savegame':raise ValueError('Invalid save')
    state=httpx.get('http://127.0.0.1:8787/api/state').json()
    if state['mode']!='manual' or state['busy']:raise ValueError('Dashboard must be idle and Manual.')
    folder=Path('.rimbot/setup-benchmarks')/time.strftime('%Y%m%d-%H%M%S');folder.mkdir(parents=True)
    settings=Settings.model_validate(state['settings'])
    rt=Runtime(Store(folder/'trace.sqlite'),settings)
    report={'passed':False,'save':save.name,'save_sha256':hashlib.sha256(save.read_bytes()).hexdigest(),
            'settings':settings.model_dump(),'objective':args.objective,'speed':args.speed,
            'git_revision':subprocess.check_output(['git','rev-parse','HEAD'],text=True).strip(),
            'target_defs':args.target_def,'timeout':args.timeout}
    diff=subprocess.check_output(['git','diff','HEAD'],text=True)
    native_diff=subprocess.check_output(['git','-C','integrations/RIMAPI','diff','HEAD'],text=True)
    (folder/'source.diff').write_text(diff);(folder/'native-source.diff').write_text(native_diff)
    report['source_dirty']=bool(subprocess.check_output(['git','status','--porcelain'],text=True).strip())
    metrics=None;started=None;identity=None
    try:
        before=await rt.api.request('GET','/api/v1/game/state')
        await rt.api.request('POST','/api/v1/game/load',body={'file_name':save.stem,'check_version':True,'skip_mod_mismatch':False})
        for _ in range(120):
            await asyncio.sleep(.5)
            game=await rt.api.request('GET','/api/v1/game/state')
            if game.get('program_state')=='Playing' and game.get('session_id')!=before.get('session_id'):break
        else:raise TimeoutError('Save did not load into a new session')
        await rt.api.request('POST','/api/v1/game/speed',params={'speed':0})
        await rt.poll();identity=rt.colony
        rt.memory=rt.empty_memory();rt.memory['direction']=[args.objective];rt.persist()
        mid=rt.observation['map']['id']
        initial=await rt.api.call('construction_state',{'map_id':mid})
        target=args.target_count or rt.observation['game']['colonist_count']
        metrics=SetupMetrics([b.model_dump() for b in initial.buildings],args.target_def,target)
        report['target_count']=target
        if sum(b.state=='built' and b.def_name in args.target_def for b in initial.buildings)>=target:raise ValueError('Save already satisfies benchmark target')
        await rt.start()
        started=time.time();rt.mode='automate'
        restored_speed=False
        await rt.api.request('POST','/api/v1/game/speed',params={'speed':args.speed})
        while time.time()-started<args.timeout:
            if (folder/'stop').exists():
                report['error']='Stopped for diagnosis';break
            if rt.colony!=identity:raise RuntimeError('Colony changed during benchmark')
            dashboard=httpx.get('http://127.0.0.1:8787/api/state').json()
            if dashboard['mode']!='manual' or dashboard['busy']:raise RuntimeError('Dashboard control changed during benchmark')
            if rt.memory.get('plans') and not getattr(rt,'initial_pause_session',None):
                game=await rt.api.call('get_game_state',{},fresh=True)
                if game.get('is_paused') or not restored_speed:
                    windows=await rt.api.call('get_ui_windows',{},fresh=True)
                    if any(w.get('force_pause') for w in windows):
                        raise RuntimeError('Test blocked by a pause-forcing native window: '+str(windows))
                    await rt.api.request('POST','/api/v1/game/speed',params={'speed':args.speed})
                    restored_speed=True
                    rt.note('test_control','Restored requested test speed after initial planning or native pause',speed=args.speed)
            pages=[];offset=0
            while True:
                work=await rt.api.call('get_map_construction_work',{'map_id':mid,'offset':offset,'limit':32},fresh=True)
                pages.extend(work['sites'])
                if work['next_offset'] is None:break
                offset=work['next_offset']
            work['sites']=pages
            buildings=await rt.api.call('construction_state',{'map_id':mid})
            success=metrics.sample(work,[b.model_dump() for b in buildings.buildings])
            if args.starter_base:
                from rimbot.starter_check import assess
                report['starter_base']=assess(rt.observation,[b.model_dump() for b in buildings.buildings],args.target_def,target)
                success=report['starter_base']['passed']
            with (folder/'observations.jsonl').open('a') as out:out.write(json.dumps({'at':time.time(),'work':work,'buildings':buildings.model_dump()})+'\n')
            print(f"{time.time()-started:.1f}s {rt.status.get('phase')} | idle {metrics.last_idle} | sites {len(pages)} | completed {len(metrics.completed_ids)}",flush=True)
            if success:report['passed']=True;break
            await asyncio.sleep(args.interval)
        if not report['passed']:report.setdefault('error','Target not completed before deadline')
    except BaseException as error:
        report['error']=f'{type(error).__name__}: {error}'
        raise
    finally:
        rt.mode='manual';await rt.cancel()
        try:
            if identity==rt.colony:await rt.api.request('POST','/api/v1/game/speed',params={'speed':0})
        finally:
            events=rt.store.history(rt.colony,100000,include_diagnostics=True)
            if started:
                events=[e for e in events if e['at']>=started]
                report.update(metrics.report(events,started),elapsed_seconds=round(time.time()-started,3))
            report['final_work']=rt.memory['work']
            report['final_projects']=rt.memory.get('projects',[])
            (folder/'report.json').write_text(json.dumps(report,indent=2))
            await rt.stop();rt.store.close()
            print('Report: '+str(folder/'report.json'),flush=True)
    return report['passed']

if __name__=='__main__':
    p=argparse.ArgumentParser(description=__doc__)
    p.add_argument('--execute',action='store_true',required=True)
    p.add_argument('--starter-base',action='store_true',help='Require roofed shelter, a stockpile and accessible food as well as completed sleeping objects')
    p.add_argument('--save',required=True)
    p.add_argument('--timeout',type=int,default=360)
    p.add_argument('--interval',type=float,default=3)
    p.add_argument('--speed',type=int,choices=(1,2,3),default=1)
    p.add_argument('--objective',default='Provide sleeping arrangements for everyone near the colony. Keep existing useful work progressing.')
    p.add_argument('--target-def',action='append',default=None)
    p.add_argument('--target-count',type=int)
    args=p.parse_args();args.target_def=args.target_def or ['Bed','SleepingSpot']
    if args.timeout<=0 or args.interval<=0:p.error('timeout and interval must be positive')
    raise SystemExit(0 if asyncio.run(run(args)) else 1)
