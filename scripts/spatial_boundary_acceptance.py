"""Paused native definition, map-edge, zone and interrupted-write acceptance."""
import argparse
import asyncio
import json
import time
from pathlib import Path

from deterministic_foothold import NoInference
from session_checkpoint_acceptance import ready
from spatial_site_acceptance import complete_buildings,complete_zones
from rimgovernor.bridge_runtime import BridgeRuntime
from rimgovernor.campaign_manifest import capture_manifest
from rimgovernor.colony_plan import PlanStep
from rimgovernor.construction_preflight import preflight_construction
from rimgovernor.hands import Hands
from rimgovernor.headless import isolated_root,prepare
from rimgovernor.player_commands import apply_command
from rimgovernor.shell_site import ShellSiteRefusal,ZONE_ARGUMENTS
from rimgovernor.spatial import projected_obstruction
from rimgovernor.colony_plan import Placement
from rimgovernor.store import Store


async def run(args):
    args.output.mkdir(parents=True,exist_ok=False)
    root=isolated_root(args.source_root,args.output/'bridge');configuration=prepare(root)
    store=Store(args.output/'state.sqlite')
    rt=BridgeRuntime(store,root,fresh=True,headless=True,model_factory=lambda _:NoInference())
    report=dict(outcome='failed',cases=[])
    def save(): (args.output/'result.json').write_text(json.dumps(report,indent=2))
    def record(name,passed,**evidence):
        report['cases'].append(dict(name=name,passed=bool(passed),**evidence));save()
        print(name+': '+str(bool(passed)),flush=True);assert passed,name
    async def settle():
        async with asyncio.timeout(60):
            while rt.wake.is_set() or rt.deliberating or (rt.review_task and not rt.review_task.done()):await asyncio.sleep(.1)
    async def census():
        return complete_buildings(await rt.game.query('home/list_buildings',aggregate=False,playerOnly=True))
    try:
        report['manifest']=capture_manifest(Path(__file__).resolve().parents[1],root,configuration,rt.router.routing.model_dump(mode='json'))
        await ready(rt);await settle()
        initial=await rt.game.query('home/status',colonists=False,threats=False)
        facts=await rt.game.query('home/colony_facts',planning=True)
        layout=await rt.controller.skills.layout(facts);bounds=layout['room']
        x,z=bounds['x']+2,bounds['z']+2
        before=await census();plan_before=rt.current_plan.model_dump()
        custom=await rt.inspect_native('home/place_building',dict(defName=args.custom_def,x=x,z=z,stuff='WoodLog',rotation='north',dryRun=True))
        placement=Placement(def_name=args.custom_def,x=x,z=z,materials=['WoodLog'])
        record('custom_native_definition_projection',custom.get('canPlace') is True and projected_obstruction(custom,placement)=={(x,z)},native=custom)
        spec=rt.current_plan.spec.model_copy(deep=True)
        spec.steps.append(PlanStep(id='custom-pocket',title='Custom projected pocket',completion_criteria='Native buildings',
            action=dict(kind='place_buildings',placements=[dict(def_name=args.custom_def,x=a,z=b,materials=['WoodLog'])
                for a,b in ((x-1,z),(x+1,z),(x,z-1),(x,z+1))])))
        refusal=None
        try:await preflight_construction(spec,rt.current_plan,rt.game)
        except ShellSiteRefusal as error:refusal=dict(code=error.code,evidence=error.evidence)
        record('custom_definition_cannot_seal_untracked_pocket',refusal is not None and refusal['code']=='projected_pawn_access',refusal=refusal)
        edge=rt.current_plan.spec.model_copy(deep=True)
        edge.steps.append(PlanStep(id='edge',title='Outward map-edge entrance',completion_criteria='Native shell',
            action=dict(kind='build_room_shell',bounds=dict(x=0,z=0,width=5,height=5),wall_def='Wall',door_def='Door',materials=['WoodLog'],entrance='west')))
        edge_error=None
        try:await preflight_construction(edge,rt.current_plan,rt.game)
        except ValueError as error:edge_error=dict(detail=str(error),evidence=getattr(error,'evidence',None))
        record('map_edge_entrance_refused',edge_error is not None,evidence=edge_error)
        floor=await rt.inspect_native('home/place_building',dict(defName='WoodPlankFloor',x=x,z=z,rotation='north',dryRun=True))
        record('native_floor_preview',floor.get('canPlace') is True and projected_obstruction(floor,Placement(def_name='WoodPlankFloor',x=x,z=z))==set(),native=floor)
        catalog=await rt.game.invoke('rimworld/list_architect_designators',dict(categoryId='Zone'))
        previews=[]
        for classname in ('Designator_AreaBuildRoof','Designator_AreaHomeExpand','Designator_AreaNoRoof'):
            rows=[r for r in catalog['designators'] if r.get('className')=='RimWorld.'+classname]
            assert len(rows)==1,rows
            request=dict(designatorId=rows[0]['id'],x=x,z=z,width=2,height=2,dryRun=True,keepSelected=False)
            preview=await rt.inspect_native('rimworld/apply_architect_designator',request)
            previews.append(dict(classname=classname,request=request,native=preview))
            assert preview.get('dryRun') is True and preview.get('appliedCellCount')==0,preview
        record('native_roof_and_home_area_previews',True,previews=previews)
        zone_args=dict(op='create',zoneType='stockpile',label='B06 large zone',
            x=max(0,x-16),z=max(0,z-16),width=32,height=32,watch=False)
        large=await rt.inspect_native('home/zone_cells',dict(zone_args,dryRun=True))
        accepted={(c['x'],c['z']) for c in large.get('cells',[]) if c.get('accepted') is True}
        record('large_native_zone_preview',large.get('success') is True and len(large.get('cells',[]))==1024
            and len(accepted)==large.get('cellsAccepted') and len(accepted)>0,native=large)
        zone_receipt=await rt.game.invoke('home/zone_cells',dict(zone_args,dryRun=False),allow_write=True)
        zone_census=complete_zones(await rt.game.query('home/list_zones',**ZONE_ARGUMENTS))
        actual=next(row for row in zone_census['zones'] if row['label']=='B06 large zone')
        record('large_native_zone_exact_grid',{(c['x'],c['z']) for c in actual['gridCells']}==accepted,
            receipt=zone_receipt,census=zone_census)
        await rt.game.invoke('home/zone_cells',dict(op='delete',zone=str(actual['id']),watch=False,dryRun=False),allow_write=True)
        record('all_previews_preserve_orders_and_plan',await census()==before and rt.current_plan.model_dump()==plan_before)
        # Lose the first response after its real native write, exactly as a transport
        # interruption can. Hands must observe that object before issuing the rest.
        command=await apply_command(rt,dict(kind='PlaceBuildings',purpose='shelter',buildings=dict(kind='place_buildings',placements=[
            dict(def_name='SleepingSpot',x=x,z=z),dict(def_name='SleepingSpot',x=x+1,z=z)])),token=rt.context_token,revision=rt.chat_revision)
        await settle();identity=command['step'];rt.manual_requests.clear()
        real_native=rt.native;writes=[]
        async def lose_once(name,arguments,**kwargs):
            result=await real_native(name,arguments,**kwargs);writes.append(dict(tool=name,args=arguments,receipt=result))
            if len(writes)==1:raise InterruptedError('Acceptance transport interruption after native write')
            return result
        rt.native=lose_once
        rt.manual_execution=(rt.context_token,rt.chat_revision,rt.current_plan.revision)
        started=time.monotonic();await Hands().advance(rt,max_operations=1,only_ids={identity})
        first_seconds=time.monotonic()-started
        progress=rt.current_plan.progress[identity]
        record('lost_receipt_retains_uncertain_intent',len(writes)==1 and not progress.issued['0']['confirmed'],issued=progress.issued.copy(),seconds=first_seconds)
        started=time.monotonic();await Hands().advance(rt,only_ids={identity})
        second_seconds=time.monotonic()-started
        rt.native=real_native;rt.manual_execution=None
        observed=await census()
        spots=[b for b in observed if b.get('defName')=='SleepingSpot' and (b['position']['x'],b['position']['z']) in {(x,z),(x+1,z)}]
        record('interrupted_batch_observes_before_retry_without_duplicates',len(writes)==2 and len(spots)==2
            and progress.state=='complete' and all(r['confirmed'] for r in progress.issued.values()),writes=writes,seconds=second_seconds,observed=spots)
        zones=complete_zones(await rt.game.query('home/list_zones',**ZONE_ARGUMENTS))
        final=await rt.game.query('home/status',colonists=False,threats=False)
        record('paused_zero_inference',initial['time']['ticksGame']==final['time']['ticksGame'] and final['time']['paused']
            and NoInference.attempts==0 and rt.counters['model_calls']==0,zones=zones)
        report['outcome']='passed'
    except Exception as error:
        report.update(error=repr(error),error_evidence=getattr(error,'evidence',None));raise
    finally:
        try:await rt.stop()
        finally:store.close();save()


if __name__=='__main__':
    parser=argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--source-root',type=Path,required=True)
    parser.add_argument('--output',type=Path,required=True)
    parser.add_argument('--custom-def',default='B06AuditWall')
    asyncio.run(asyncio.wait_for(run(parser.parse_args()),900))
