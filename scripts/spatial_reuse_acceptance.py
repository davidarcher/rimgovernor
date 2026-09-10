"""Paired restart, retired-room protection and ordinary live edits of an accepted room."""
import argparse
import asyncio
import json
from pathlib import Path

from deterministic_foothold import NoInference
from session_checkpoint_acceptance import ready
from rimbot.bridge import runtime_file_read
from rimbot.bridge_runtime import BridgeRuntime
from rimbot.campaign_manifest import capture_manifest
from rimbot.colony_plan import Decision,PlanSpec
from rimbot.colony_skills import SkillBlocked
from rimbot.player_commands import apply_command
from rimbot.room_adoption import validate_adoption
from rimbot.session_checkpoint import prepare_resume,create_checkpoint
from rimbot.shelter_handoff import player_shelter,verified_room,sleeping_handoff
from rimbot.colony_policy import criteria,ColonyPolicy
from rimbot.spatial_program import stage_layout
from rimbot.shell_site import ShellSiteRefusal
from rimbot.store import Store


async def run(args):
    args.output.mkdir(parents=True,exist_ok=False)
    data,state=prepare_resume(args.checkpoint)
    store=Store(state/'bridge.sqlite')
    rt=BridgeRuntime(store,Path(data['root']),fresh=True,headless=True,resume=args.checkpoint,
        model_factory=lambda _:NoInference())
    report=dict(outcome='failed',cases=[],source_checkpoint=data)
    def save():
        (args.output/'result.json').write_text(json.dumps(report,indent=2))
    def record(name,passed,**evidence):
        report['cases'].append(dict(name=name,passed=bool(passed),**evidence));save()
        print(name+': '+str(bool(passed)),flush=True);assert passed,name
    async def settle():
        rt.clock_events.extend(await rt.supervisor.poll());rt.receive_clock_events()
        async with asyncio.timeout(60):
            while rt.wake.is_set() or rt.deliberating or (rt.review_task and not rt.review_task.done()):await asyncio.sleep(.1)
    async def command(payload):
        await settle()
        result=await apply_command(rt,payload,token=rt.context_token,revision=rt.chat_revision)
        await rt.execute_manual_requests()
        return result
    try:
        report['manifest']=capture_manifest(Path(__file__).resolve().parents[1],rt.root,rt.root/'config-headless',
            rt.router.routing.model_dump(mode='json'))
        await ready(rt);await settle()
        identity,goal,shell=player_shelter(rt.current_plan)
        stale=False
        try:await validate_adoption(rt,goal)
        except SkillBlocked:stale=True
        record('paired_load_requires_fresh_adoption',stale and rt.mode=='manual')
        await command(goal.evidence['request'])
        room,interior=await verified_room(rt,shell)
        record('paired_native_room_and_furnishings_preserved',len(interior)==40 and room['openRoofCount']==0 and len(room['beds'])>=2,room=room)
        facts=await rt.game.query('home/colony_facts',planning=True)
        stage_layout(rt.current_plan,facts,criteria(facts,ColonyPolicy()),[])
        record('native_shortfall_keeps_shelter_phase',rt.current_plan.control['spatial_program']['stage']=='habitable_shelter',
            program=rt.current_plan.control['spatial_program'])
        selected=await sleeping_handoff(rt,facts,identity,shell)
        assert selected,'Expected observed indoor sleeping shortfall'
        await command(dict(kind='PlaceBuildings',purpose='shelter',buildings=selected[1][0]))
        facts=await rt.game.query('home/colony_facts',planning=True)
        stage_layout(rt.current_plan,facts,criteria(facts,ColonyPolicy()),[])
        record('observed_native_capacity_releases_service_phase',facts['indoorSleepingCapacity']>=facts['colonists']
            and rt.current_plan.control['spatial_program']['stage'] in ('food_services','capacity'),
            program=rt.current_plan.control['spatial_program'],facts=facts)
        completed=[s.id for s in rt.current_plan.spec.steps if s.action.kind=='place_buildings'
            and rt.current_plan.progress[s.id].state=='complete']
        assert len(completed)>=2,completed
        spec=rt.current_plan.spec.model_dump();spec['steps']=[s for s in spec['steps'] if s['id'] not in completed]
        await rt.commit_strategy(Decision(expected_revision=rt.current_plan.revision,disposition='revise',
            assessment='Retire verified construction',rationale='Native construction remains observed',reply='Retire verified construction',
            plan=PlanSpec.model_validate(spec)),actor='strategist',expected_token=rt.context_token,expected_revision=rt.chat_revision)
        record('completed_room_orders_archived',all(rt.store.retired_action(rt.colony,s) for s in completed),identities=completed)
        door=shell['entrance_cell'];before=rt.current_plan.model_dump();refusal=None
        try:
            await command(dict(kind='PlaceBuildings',purpose='shelter',buildings=dict(kind='place_buildings',placements=[
                dict(def_name='Wall',x=door['x'],z=door['z']-1,materials=['WoodLog'])])))
        except ShellSiteRefusal as error:refusal=dict(code=error.code,evidence=error.evidence)
        record('retired_native_room_cannot_be_sealed',refusal is not None and refusal['code']=='projected_pawn_access'
            and rt.current_plan.model_dump()==before,refusal=refusal)
        catalogs={}
        for category in ('Floors','Zone','Orders'):
            catalogs[category]=await rt.game.invoke('rimworld/list_architect_designators',dict(categoryId=category))
        report['native_catalogs']=catalogs;save()
        chosen=None
        for x,z in sorted(interior,reverse=True):
            neighbors={(x-1,z),(x+1,z),(x,z-1),(x,z+1)}
            if len(neighbors&interior)>2:continue
            preview=await rt.inspect_native('home/place_building',dict(defName='Wall',x=x,z=z,stuff='WoodLog',rotation='north',dryRun=True))
            if preview.get('canPlace') is True:
                chosen=dict(def_name='Wall',x=x,z=z,materials=['WoodLog']);break
        assert chosen,'No legal ordinary corner edit'
        edit=await command(dict(kind='PlaceBuildings',purpose='shelter',buildings=dict(kind='place_buildings',placements=[chosen])))
        for _ in range(30):
            if rt.current_plan.progress[edit['step']].state=='complete':break
            await settle();await rt.supervisor.change('Superfast',max_ticks=600)
            async with asyncio.timeout(30):
                while True:
                    clock=(await runtime_file_read(rt.bridge.call,'home/supervised_play',op='status')).structuredContent
                    if not clock['active']:break
                    await asyncio.sleep(.2)
            assert clock['stopReason'] in ('tick_budget','requested_pause') and clock['pauseVerified'],clock
            await settle()
        record('ordinary_player_corner_edit_completed',rt.current_plan.progress[edit['step']].state=='complete',placement=chosen)
        invalidated=False
        try:await verified_room(rt,shell)
        except SkillBlocked:invalidated=True
        record('live_native_interior_edit_invalidates_adoption',invalidated)
        report['checkpoint']=await create_checkpoint(rt,rt.context_token)
        record('zero_inference',NoInference.attempts==0 and rt.counters['model_calls']==0)
        report['outcome']='passed'
    except Exception as error:
        report.update(error=repr(error),error_evidence=getattr(error,'evidence',None));raise
    finally:
        report['plan']=rt.current_plan.model_dump(mode='json')
        try:await rt.stop()
        finally:store.close();save()


if __name__=='__main__':
    parser=argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--checkpoint',type=Path,required=True)
    parser.add_argument('--output',type=Path,required=True)
    asyncio.run(asyncio.wait_for(run(parser.parse_args()),900))
