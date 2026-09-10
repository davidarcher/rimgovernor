"""Short native adopted-room acceptance against an integrated controller source tree."""
import argparse
import asyncio
import json
from pathlib import Path
from session_checkpoint_acceptance import ready
from rimbot.bridge_runtime import BridgeRuntime
from rimbot.campaign_manifest import capture_manifest
from rimbot.config import Settings
from rimbot.player_commands import apply_command
from rimbot.session_checkpoint import prepare_resume,create_checkpoint
from rimbot.shelter_handoff import safe_rotation
from rimbot.store import Store

async def run(args):
    source=json.loads(args.thermal_report.read_text());assert source['outcome']=='passed'
    checkpoint=source['checkpoint']['manifest_path'];data,state=prepare_resume(checkpoint)
    args.output.mkdir(parents=True,exist_ok=False)
    store=Store(state/'bridge.sqlite')
    rt=BridgeRuntime(store,Path(data['root']),fresh=True,headless=True,resume=checkpoint,
        settings=Settings(model=args.model,timeout_seconds=90))
    report={'outcome':'failed','source_checkpoint':data,'cases':[]}
    bounds=source['plan']['colony_goals']['intent-edited-thermal-home']['evidence']['request']['bounds']
    def record(name,passed,**evidence):
        report['cases'].append(dict(name=name,passed=bool(passed),**evidence));print(name+': '+str(bool(passed)),flush=True)
        assert passed,name
    async def settle():
        rt.clock_events.extend(await rt.supervisor.poll());rt.receive_clock_events()
        async with asyncio.timeout(60):
            while rt.wake.is_set() or rt.deliberating or (rt.review_task and not rt.review_task.done()):await asyncio.sleep(.1)
    try:
        (args.output/'manifest.json').write_text(json.dumps(capture_manifest(Path(__file__).resolve().parents[1],
            Path(data['root']),Path(data['root'])/'config-headless',rt.router.routing.model_dump(mode='json')),indent=2))
        await ready(rt);await settle()
        rooms=await rt.game.query('home/list_rooms',x=bounds['x']+1,z=bounds['z']+1,cells=True)
        interior={(x,z) for x in range(bounds['x']+1,bounds['x']+bounds['width']-1)
            for z in range(bounds['z']+1,bounds['z']+bounds['height']-1)}
        room=next(r for r in rooms['rooms'] if r.get('cellsComplete') and {(c['x'],c['z']) for c in r['cells']}==interior)
        record('paired_native_thermal_room_preserved',room['openRoofCount']==0 and len(room['beds'])>=10
            and (room['temperature']>=16 if source['variant']=='cold' else room['temperature']<=28),room=room)
        buildings=await rt.game.query('home/list_buildings',aggregate=False,playerOnly=True)
        expected='Campfire' if source['variant']=='cold' else 'PassiveCooler'
        record('completed_fueled_thermal_furniture_loaded',any(b['defName']==expected and b.get('fuel',{}).get('hasFuel')
            and not b.get('isBlueprint') and not b.get('isFrame') for b in buildings['buildings']))
        await rt.steer('Adopt the existing edited shelter again after this load. Use AdoptRoom with intent_id edited-thermal-home, '
            'bounds '+json.dumps(bounds)+' and entrance north. Preserve its furniture and do not build another shell.')
        revision=rt.chat_revision
        async with asyncio.timeout(180):
            while rt.current_plan.control.get('interpreted_player_revision',0)<revision or rt.deliberating:await asyncio.sleep(.2)
        await rt.execute_manual_requests();await settle()
        adoption=rt.current_plan.colony_goals['intent-edited-thermal-home'].evidence['adoption']
        record('local_model_refreshes_exact_adoption',adoption['room_id']==room['id'] and adoption['loadToken']!=
            source['plan']['colony_goals']['intent-edited-thermal-home']['evidence']['adoption']['loadToken'],adoption=adoption)
        selected=None;previews=[]
        for x,z in sorted(interior,key=lambda p:(-p[1],p[0])):
            if x==bounds['x']+bounds['width']//2:continue
            preview=await rt.inspect_native('home/place_building',dict(defName='SleepingSpot',x=x,z=z,rotation='north',dryRun=True))
            previews.append(preview)
            rows=[r for r in preview.get('rotations',[]) if safe_rotation(r)]
            if preview.get('canPlace') and len(rows)==1 and {(c['x'],c['z']) for c in rows[0]['occupiedCells']}<=interior:
                selected=dict(def_name='SleepingSpot',x=x,z=z);break
        report['placement_previews']=previews;assert selected,'No legal non-aisle furniture edit'
        await settle()
        result=await apply_command(rt,dict(kind='PlaceBuildings',purpose='shelter',buildings=dict(kind='place_buildings',placements=[selected])),
            token=rt.context_token,revision=rt.chat_revision)
        await rt.execute_manual_requests()
        found=await rt.game.query('home/list_buildings',match='SleepingSpot',x=selected['x'],z=selected['z'],radius=1,aggregate=False,playerOnly=True)
        record('integrated_spatial_and_hands_native_edit_completed',rt.current_plan.progress[result['step']].state=='complete' and
            any(b['position']==dict(x=selected['x'],z=selected['z']) and b['status']=='built' for b in found['buildings']),placement=selected)
        before=rt.current_plan.model_dump();refused=False
        try:await apply_command(rt,dict(kind='AdoptRoom',intent_id='edited-thermal-home',bounds=dict(bounds,width=bounds['width']+1),entrance='north'),
            token=rt.context_token,revision=rt.chat_revision)
        except ValueError:refused=True
        record('invalid_geometry_preserves_adoption',refused and rt.current_plan.model_dump()==before)
        report['checkpoint']=await create_checkpoint(rt,rt.context_token);report['outcome']='passed'
    except Exception as error:
        report['error']=repr(error);report['error_evidence']=getattr(error,'evidence',None);raise
    finally:
        try:
            if rt.bridge:report['cleanup']=(await rt.bridge.core('games_stop',gameId=rt.bridge.game_id)).structuredContent
        except Exception as error:report['cleanup_error']=repr(error)
        finally:await rt.stop();store.close()
        (args.output/'result.json').write_text(json.dumps(report,indent=2))

if __name__=='__main__':
    parser=argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--thermal-report',type=Path,required=True);parser.add_argument('--output',type=Path,required=True)
    parser.add_argument('--model',default='qwen3.5-4b')
    asyncio.run(asyncio.wait_for(run(parser.parse_args()),360))
