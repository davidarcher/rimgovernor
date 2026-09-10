"""Recover a costed native construction after ordinary forbidding makes stock unavailable."""
from rimbot.native_scenario import advance_game
import argparse
import asyncio
import json
import traceback
from pathlib import Path
from session_checkpoint_acceptance import ready
from rimbot.bridge_runtime import BridgeRuntime
from rimbot.campaign_manifest import capture_manifest
from rimbot.colony_plan import ColonyGoal,CommitSteps
from rimbot.construction_recovery import recover_construction
from rimbot.headless import isolated_root,prepare
from rimbot.store import Store


async def exercise_recovery(rt,report=None,*,obstruction=False,material_identity=False):
    if report is None:report={}
    obstruction_material = 'Steel' if material_identity else 'WoodLog'
    previous_task=rt.execution_task
    try:
        rt.execution_task=asyncio.current_task()  # The probe drives shared Hands explicitly.
        forbid=await rt.controller.skills.designator('Designator_Forbid')
        allow=await rt.controller.skills.designator('Designator_Unforbid')
        stock=await rt.game.query('home/list_things',match='WoodLog',includeHeld=False,maxPositionsPerDef=200)
        wood=next(r for r in stock['things'] if r['defName']=='WoodLog')
        positions=wood['positions'];assert positions
        async def designate(identity):
            for p in positions:
                position=p.get('position',p)
                await rt.game.invoke('rimworld/apply_architect_designator',dict(designatorId=identity,
                    x=position['x'],z=position['z'],keepSelected=False),allow_write=True)
        await designate(allow)
        if obstruction and material_identity:
            steel=await rt.game.query('home/list_things',match='Steel',includeHeld=False,maxPositionsPerDef=200)
            for row in steel['things']:
                if row['defName']!='Steel':continue
                for item in row['positions']:
                    cell=item.get('position',item)
                    await rt.game.invoke('rimworld/apply_architect_designator',dict(designatorId=allow,
                        x=cell['x'],z=cell['z'],keepSelected=False),allow_write=True)
        facts=await rt.game.query('home/colony_facts',planning=True)
        cells=sorted((c for c in facts['cells'] if c['walkable'] and not c['occupied'] and not c.get('zone')),
            key=lambda c:(c['x']-facts['center']['x'])**2+(c['z']-facts['center']['z'])**2)
        target_def='Campfire' if obstruction and not material_identity else 'Wall'
        placements=[]
        for c in cells:
            if any(abs(c['x']-p['x'])+abs(c['z']-p['z'])<3 for p in placements):continue
            preview=await rt.inspect_native('home/place_building',dict(defName=target_def,stuff='WoodLog',
                x=c['x'],z=c['z'],rotation='north',dryRun=True))
            if obstruction and preview.get('canPlace') is True:
                preview=await rt.inspect_native('home/place_building',dict(defName='Wall',stuff=obstruction_material,
                    x=c['x'],z=c['z'],rotation='north',dryRun=True))
            if preview.get('canPlace') is True:placements.append(dict(def_name=target_def,
                materials=[] if target_def=='Campfire' else ['WoodLog'],x=c['x'],z=c['z']))
            if len(placements)==2:break
        assert len(placements)==2
        goal=rt.current_plan.colony_goals['EnsureInitialShelter']=ColonyGoal(priority_class=2)
        steps,_=rt.controller.skills.steps('EnsureInitialShelter','native-recovery',[
            {'kind':'place_buildings','placements':placements}],facts)
        await rt.commit_strategy(CommitSteps(expected_revision=rt.current_plan.revision,reason='Native recovery acceptance',steps=steps).decision(rt.current_plan),
            actor='strategist',expected_token=rt.context_token,expected_revision=rt.chat_revision)
        goal.steps=[steps[0].id]
        obstruction_target=None
        async def observe_obstruction(built):
            async with asyncio.timeout(180):
                while True:
                    buildings=await rt.game.query('home/list_buildings',aggregate=False,playerOnly=True)
                    targets=[b for b in buildings['buildings'] if b['position']['x']==cell['x']
                             and b['position']['z']==cell['z'] and b.get('stuff')==obstruction_material]
                    report.setdefault('obstruction_observations',[]).append(targets)
                    if built and any(b.get('status')=='built' and not b.get('isBlueprint') and not b.get('isFrame') for b in targets):
                        return next(b for b in targets if b.get('status')=='built')
                    if not built and not targets:
                        return None
                    await advance_game(rt, 600, report, timeout=60)
                    async with rt.lock:await rt.refresh_clock_events()
        if obstruction:
            cell=placements[0]
            request=dict(defName='Wall',stuff=obstruction_material,x=cell['x'],z=cell['z'],rotation='north')
            preview=await rt.inspect_native('home/place_building',dict(request,dryRun=True))
            assert preview.get('canPlace') is True,preview
            await rt.game.invoke('home/place_building',dict(request,dryRun=False),allow_write=True)
            buildings=await rt.game.query('home/list_buildings',aggregate=False,playerOnly=True)
            obstruction_target=next(b for b in buildings['buildings'] if b['position']['x']==cell['x'] and b['position']['z']==cell['z'])
            if not material_identity:
                # A built wall blocks the campfire footprint; wall-to-wall material
                # replacement is a different vanilla placement contract.
                obstruction_target=await observe_obstruction(True)
            report['obstruction']=obstruction_target
        else:
            await designate(forbid)
        if rt.review_task and not rt.review_task.done():await rt.review_task
        async with rt.lock:await rt.refresh_clock_events()
        report['dispatch_preconditions']={'direction':rt.chat_revision,'handled':rt.handled_revision,
            'goal':goal.model_dump(),'clock':dict(rt.supervisor.state)}
        rt.controller.finish_review()
        rt.mode='automate'
        try:await rt.hands.advance(rt)
        finally:rt.mode='manual'
        p=rt.current_plan.progress[steps[0].id]
        if material_identity:
            assert obstruction and len(p.issued)==2 and all(r.get('outcome')=='placed' for r in p.issued.values()),p.model_dump()
            buildings=await rt.game.query('home/list_buildings',aggregate=False,playerOnly=True)
            targets=[b for b in buildings['buildings'] if any(b['position']['x']==c['x'] and b['position']['z']==c['z'] for c in placements)]
            assert len(targets)==2 and all(b.get('stuff')=='WoodLog' for b in targets),targets
            assert obstruction_target['thingId'] not in {b['thingId'] for b in targets}
            report.update(outcome='passed',issued=p.model_dump(),native_targets=targets,
                scope='Native material identity and ordinary blueprint replacement; no placement-recovery claim')
            return report
        assert p.state=='blocked' and p.failure.code==('construction_unavailable' if obstruction else 'construction_resources') and not p.issued,p.model_dump()
        report['blocked']=p.model_dump()
        async def recover():
            rt.mode='automate'
            try:return await recover_construction(rt,steps[0].id,token=rt.context_token,direction=rt.chat_revision,limit=3)
            finally:rt.mode='manual'
        assert not await recover()
        if obstruction:
            designator=await rt.controller.skills.designator('Designator_Deconstruct')
            report['obstruction_deconstruction']=await rt.game.invoke('rimworld/apply_architect_designator',
                dict(designatorId=designator,x=cell['x'],z=cell['z'],keepSelected=False),allow_write=True)
            await observe_obstruction(False)
        else:
            await designate(allow)
        report['recovery_preconditions']={'status':await rt.game.query('home/status',colonists=False,threats=True),
            'goal':rt.current_plan.colony_goals['EnsureInitialShelter'].model_dump(),
            'step':rt.current_plan.spec.steps[-1].model_dump(),'direction':rt.chat_revision,'token':rt.context_token}
        rt.controller.finish_review()
        assert await recover()
        assert p.state=='pending' and len(p.recovery_history)==1 and not p.issued
        report['recovered']=p.model_dump()
        rt.controller.finish_review()
        rt.mode='automate'
        try:await rt.hands.advance(rt)
        finally:rt.mode='manual'
        assert len(p.issued)==2 and all(r.get('confirmed') for r in p.issued.values()),p.model_dump()
        buildings=await rt.game.query('home/list_buildings',aggregate=False,playerOnly=True)
        targets=[b for b in buildings['buildings'] if any(b['position']['x']==c['x'] and b['position']['z']==c['z'] for c in placements)]
        assert len(targets)==2 and len({b['thingId'] for b in targets})==2
        assert rt.counters['model_calls']==0
        report.update(outcome='passed',issued=p.model_dump(),native_targets=targets,model_calls=rt.counters['model_calls'])
        return report
    finally:
        rt.execution_task=previous_task


async def run(args):
    root=isolated_root(args.source_root,args.output/'bridge');config=prepare(root)
    store=Store(args.output/'state.sqlite');rt=BridgeRuntime(store,root,fresh=True,headless=True)
    report={'outcome':'failed','save_edits':[],'scope':'Native prewrite stock recovery; blueprint receipts do not certify pawn construction'}
    try:
        report['manifest']=capture_manifest(Path(__file__).resolve().parents[1],root,config,{'mode':'no inference'})
        await ready(rt)
        await exercise_recovery(rt,report,obstruction=args.obstruction or args.material_identity,material_identity=args.material_identity)
    except Exception as error:report.update(error=repr(error),traceback=traceback.format_exc())
    finally:
        rt.execution_task=None
        await rt.stop();store.close()
        (args.output/'result.json').write_text(json.dumps(report,indent=2))
    print(json.dumps({'outcome':report['outcome'],'error':report.get('error')}),flush=True)
    return report['outcome']=='passed'


if __name__=='__main__':
    parser=argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--source-root',type=Path,required=True);parser.add_argument('--output',type=Path,required=True)
    parser.add_argument('--obstruction',action='store_true',help='Recover a campfire placement refusal after pawns build and deconstruct the exact conflicting wooden wall')
    parser.add_argument('--material-identity',action='store_true',help='Verify ordinary Steel blueprint replacement produces distinct Wood blueprints')
    raise SystemExit(0 if asyncio.run(run(parser.parse_args())) else 1)
