"""Observe actual second-seed bed use after ordinary construction and a Manual sleep timetable."""
import argparse,asyncio,json,time,traceback
from pathlib import Path
from rimbot.bridge_runtime import BridgeRuntime
from rimbot.campaign_manifest import capture_manifest
from rimbot.headless import isolated_root,prepare
from rimbot.store import Store
from deterministic_foothold import NoInference
from session_checkpoint_acceptance import ready
from lifecycle_measurement import bed_use_sample

async def run(args):
    root=isolated_root(args.source_root,args.output/'bridge');config=prepare(root)
    store=Store(args.output/'state.sqlite');rt=BridgeRuntime(store,root,fresh=True,headless=True,model_factory=lambda _:NoInference())
    report={'outcome':'failed','scope':'Actual native bed use; ordinary Manual sleep schedule after verified safe construction, not sustained survival','samples':[]}
    NoInference.attempts=0
    started=time.monotonic();manual=False
    try:
        report['manifest']=capture_manifest(Path(__file__).resolve().parents[1],root,config,{'model':'no inference'})
        await ready(rt)
        roster=await rt.game.query('home/list_pawns',colonistsOnly=True,health=True)
        starting={p['thingId'] for p in roster['pawns']};report['starting']=sorted(starting)
        await rt.set_mode('automate')
        used=set()
        while time.monotonic()-started<args.seconds:
            await asyncio.sleep(.5 if manual else 5)
            if manual:
                await rt.bridge.call('rimworld/set_time_speed',speed='Paused',ultraSpeedBoost=False)
            async with rt.lock:
                sample=await bed_use_sample(rt);report['samples'].append(sample)
                used|={p['id'] for p in sample['pawns'] if p.get('in_bed') is True and p.get('bed')}
                facts=await rt.game.query('home/colony_facts',planning=False)
                roster=await rt.game.query('home/list_pawns',colonistsOnly=True,health=True,schedule=True)
                status=await rt.game.query('home/status',colonists=False,threats=True)
            if any(p.get('dead') for p in roster['pawns']):raise AssertionError('A starting colonist died')
            if starting<=used:
                report['outcome']='passed';break
            safe=(status['threats'].get('hostileCount')==0 and status['threats'].get('huntingPredatorCount')==0
                and all(not p.get('mentalState') and p.get('downed') is False and p.get('drafted') is False
                    and (p.get('health') or {}).get('needsTend') is False
                    and (p.get('health') or {}).get('bleeding') is False for p in roster['pawns']))
            if not manual and facts.get('indoorSleepingCapacity',0)>=len(starting) and safe:
                await rt.set_mode('manual')
                report['manual_entry']={'facts':facts,'roster':roster,'tick':facts['tick']}
                report['schedules']=[]
                for pawn in roster['pawns']:
                    request=dict(pawn=pawn['thingId'],schedule='S'*24,dryRun=True)
                    preview=await rt.inspect_native('home/pawn_config',request)
                    assert preview.get('success') is True,preview
                    receipt=await rt.game.invoke('home/pawn_config',dict(request,dryRun=False),allow_write=True)
                    assert receipt.get('success') is True,receipt
                    report['schedules'].append({'request':request,'preview':preview,'receipt':receipt})
                manual=True
            if manual:
                status=await rt.game.query('home/status',colonists=True,threats=True)
                assert not status['threats'].get('hostileCount') and not status['threats'].get('huntingPredatorCount'),status['threats']
                assert safe,'Native safety preconditions changed during Manual bed observation'
                await rt.bridge.call('rimworld/set_time_speed',speed='Superfast',ultraSpeedBoost=False)
            else:
                assert rt.mode=='automate','Routine preparation left Automate'
            report['used']=sorted(used)
            (args.output/'progress.json').write_text(json.dumps({'tick':facts['tick'],'manual':manual,'used':sorted(used),'capacity':facts.get('indoorSleepingCapacity'),'safe':safe}))
        else:report['outcome']='timeout'
        report['used']=sorted(used)
        assert NoInference.attempts==0 and rt.counters['model_calls']==0
    except Exception as error:report.update(error=str(error),traceback=traceback.format_exc())
    finally:
        try:
            if rt.connected:
                await rt.bridge.call('rimworld/set_time_speed',speed='Paused',ultraSpeedBoost=False)
                await rt.bridge.core('games_stop',gameId=rt.bridge.game_id)
        except Exception as error:report['cleanup_error']=str(error)
        await rt.stop();store.close()
        report['elapsed_seconds']=time.monotonic()-started
        (args.output/'result.json').write_text(json.dumps(report,indent=2))
    print(json.dumps({k:report.get(k) for k in ('outcome','error','used')}),flush=True)

if __name__=='__main__':
    parser=argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--source-root',type=Path,required=True);parser.add_argument('--output',type=Path,required=True)
    parser.add_argument('--seconds',type=int,default=1200)
    asyncio.run(run(parser.parse_args()))
