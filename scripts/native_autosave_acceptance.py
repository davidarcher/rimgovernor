"""Cross a real autosave boundary with ordinary simulation, then reload its save."""
from rimgovernor.bridge import gabs_executable
import argparse
import asyncio
import hashlib
import json
import time
import traceback
import xml.etree.ElementTree as ET
from pathlib import Path
from rimgovernor.bridge import bridge_session
from rimgovernor.campaign_manifest import capture_manifest
from rimgovernor.clock_control import PlayClock
from rimgovernor.headless import isolated_root,prepare
from rimgovernor.bridge_game import BridgeGame
from rimgovernor.bridge_runtime import BridgeRuntime
from rimgovernor.bridge_observation import observe
from rimgovernor.store import Store


async def run(args):
    root=isolated_root(args.source_root,args.output/'bridge');config=prepare(root)
    preferences=root/'headless-profile/Config/Prefs.xml'
    tree=ET.parse(preferences);pause=tree.getroot().find('pauseOnLoad')
    if pause is None:pause=ET.SubElement(tree.getroot(),'pauseOnLoad')
    pause.text='True';tree.write(preferences,encoding='utf8',xml_declaration=True)
    report={'outcome':'failed','events':[],'samples':[],'model_calls':0,'save_edits':[],
        'preference_edits':{'pauseOnLoad':True}}
    def save():
        (args.output/'result.json').write_text(json.dumps(report,indent=2))
    saves=root/'headless-profile/Saves'
    prior={p.name for p in saves.glob('*.rws')}
    report['manifest']=capture_manifest(Path(__file__).resolve().parents[1],root,config,{'mode':'no inference'})
    store = Store(args.output/'controller.sqlite') if args.mixed else None
    rt = None
    try:
        async with bridge_session(gabs_executable(root),config) as bridge:
            try:
                await bridge.core('games_start',gameId=bridge.game_id)
                await bridge.connect()
                await bridge.call('rimworld/load_game_ready',saveName='RimGovernor-tribal8-baseline',
                    readiness='visual',timeoutMs=90000)
                clock=PlayClock(bridge);await clock.change('Paused')
                if args.mixed:
                    from mixed_checkpoint_fixture import prepare_mixed
                    # Fresh native drop pods can still contain the ordinary starting
                    # stock. Let them land before reserving construction resources.
                    report['arrival_window']=await clock.change('Superfast',max_ticks=600)
                    async with asyncio.timeout(30):
                        while clock.state.get('active'):
                            await asyncio.sleep(.2)
                            await clock.poll()
                    assert clock.state['stopReason']=='tick_budget',clock.state
                    rt=BridgeRuntime(store,root,headless=True)
                    rt.bridge,rt.game=bridge,BridgeGame(bridge)
                    await rt.sync_identity()
                    rt.batch=await observe(rt.game)
                    report['arrival_facts']=(await bridge.call('home/colony_facts',planning=True)).structuredContent
                    report['mixed']=await prepare_mixed(rt,compact=args.compact_construction)
                    clock=rt.supervisor
                    def snapshot():
                        plan=rt.current_plan
                        return {'steps':plan.spec.model_dump(mode='json'),
                            'progress':{key:value.model_dump(mode='json') for key,value in plan.progress.items()},
                            'costs':plan.control.get('costs'), 'actions':rt.counters['actions']}
                    before=json.loads(json.dumps(snapshot()))
                    report['mixed_before']=json.loads(json.dumps(before))
                    report['mixed_boundaries']=[]
                report['start']=(await bridge.call('home/status',colonists=False,threats=False)).structuredContent
                report['identity']=(await bridge.call('home/colony_identity')).structuredContent
                start=await clock.change('Superfast',max_ticks=args.ticks)
                report['window']=start
                deadline=time.monotonic()+args.seconds
                while time.monotonic()<deadline:
                    await asyncio.sleep(1)
                    events=await clock.poll()
                    report['events'].extend(events)
                    if rt:
                        rt.clock_events.extend(events)
                        rt.receive_clock_events()
                        assert snapshot()==before,'Autosave processing changed pending work or replayed an order'
                        for event in events:
                            if event['kind'] in ('long_event','force_pause_cleared'):
                                report['mixed_boundaries'].append({'event':event,'clock':dict(clock.state),
                                    'controller':json.loads(json.dumps(snapshot()))})
                    if len(report['samples'])==0 or time.monotonic()-report['samples'][-1]['monotonic']>=5:
                        status=(await bridge.call('home/status',colonists=False,threats=False)).structuredContent
                        sample={'monotonic':time.monotonic(),'tick':status['time']['ticksGame'],
                            'active':clock.state['active'],'stop':clock.state.get('stopReason')}
                        report['samples'].append(sample);save();print(json.dumps(sample),flush=True)
                    if not clock.state['active']:break
                else:raise AssertionError('Autosave test exceeded its wall-clock bound')
                report['end']=clock.state
                assert clock.state['stopReason']=='tick_budget' and clock.state['pauseVerified'],clock.state
                final=(await bridge.call('home/status',colonists=False,threats=False)).structuredContent
                assert final['time']['paused'] and final['time']['ticksGame']==start['tickDeadline']
                assert clock.state['tickDeadline']==start['tickDeadline']
                longs=[e for e in report['events'] if e['kind']=='long_event']
                cleared=[e for e in report['events'] if e['kind']=='force_pause_cleared'
                    and e.get('event',{}).get('forcePauseKind')=='long_event']
                assert longs and cleared,'No observed long-event recovery'
                report['autosaves']=[]
                for path in saves.glob('*.rws'):
                    if path.name in prior:continue
                    xml=ET.parse(path).getroot()
                    tick=int(xml.findtext('.//tickManager/ticksGame'))
                    assert start['startTick']<tick<start['tickDeadline']
                    report['autosaves'].append({'name':path.name,'tick':tick,
                        'sha256':hashlib.sha256(path.read_bytes()).hexdigest()})
                assert report['autosaves'],'No native autosave was produced'
                selected=report['autosaves'][-1]
                assert any(abs(e['tick']-selected['tick'])<=1 for e in longs),'Save tick does not match the long event'
                await bridge.call('rimworld/load_game_ready',saveName=Path(selected['name']).stem,
                    readiness='visual',timeoutMs=90000)
                await clock.change('Paused')
                loaded=(await bridge.call('home/status',colonists=False,threats=False)).structuredContent
                identity=(await bridge.call('home/colony_identity')).structuredContent
                report.update(loaded=loaded,loaded_identity=identity)
                assert loaded['time']['paused'] and loaded['time']['ticksGame'] in (selected['tick'],selected['tick']+1),loaded['time']
                assert identity['colonyId']==report['identity']['colonyId'] and identity['loadToken']!=report['identity']['loadToken'],identity
                assert identity['mapId']==report['identity']['mapId'],identity
                assert hashlib.sha256((saves/selected['name']).read_bytes()).hexdigest()==selected['sha256']
                if rt:
                    old_token,old_revision=rt.context_token,rt.chat_revision
                    await rt.sync_identity()
                    assert rt.mode=='manual' and snapshot()==before
                    try:
                        await rt.native('home/order',{'action':'draft','pawn':'obsolete','dryRun':False},
                            expected_token=old_token,expected_revision=old_revision)
                    except (ValueError,InterruptedError) as error:
                        report['stale_write_refusal']=str(error)
                    else:raise AssertionError('Autosave reload admitted obsolete work')
                    assert snapshot()==before
                    report['mixed_after_load']=snapshot()
                report.update(outcome='passed',loaded=loaded,loaded_identity=identity)
            finally:
                if rt:await rt.router.close()
                report['cleanup']=(await bridge.core('games_stop',gameId=bridge.game_id)).model_dump(mode='json')
    except Exception as error:
        report['error']=repr(error)
        report['traceback']=traceback.format_exc()
    finally:
        if store:store.close()
        save()
    print(json.dumps({'outcome':report['outcome'],'error':report.get('error')}),flush=True)
    return report['outcome']=='passed'


if __name__=='__main__':
    parser=argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--source-root',type=Path,required=True)
    parser.add_argument('--output',type=Path,required=True)
    parser.add_argument('--ticks',type=int,default=61000)
    parser.add_argument('--seconds',type=int,default=600)
    parser.add_argument('--mixed',action='store_true',help='Retain partially issued construction, material reservations and pending zone/work across autosave and reload')
    parser.add_argument('--compact-construction',action='store_true',help='Use two ordinary wall placements for the partial-construction fixture on scarce-stock seeds')
    args=parser.parse_args()
    raise SystemExit(0 if asyncio.run(run(args)) else 1)
