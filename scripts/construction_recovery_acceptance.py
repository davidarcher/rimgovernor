"""Recover a costed native construction after ordinary forbidding makes stock unavailable."""
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


async def exercise_recovery(rt,report=None,*,obstruction=False):
    if report is None:report={}
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
        if obstruction:
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
        placements=[]
        for c in cells:
            preview=await rt.inspect_native('home/place_building',dict(defName='Wall',stuff='WoodLog',
                x=c['x'],z=c['z'],rotation='north',dryRun=True))
            if obstruction and preview.get('canPlace') is True:
                preview=await rt.inspect_native('home/place_building',dict(defName='Wall',stuff='Steel',
                    x=c['x'],z=c['z'],rotation='north',dryRun=True))
            if preview.get('canPlace') is True:placements.append(dict(def_name='Wall',materials=['WoodLog'],x=c['x'],z=c['z']))
            if len(placements)==2:break
        assert len(placements)==2
        goal=rt.current_plan.colony_goals['EnsureInitialShelter']=ColonyGoal(priority_class=2)
        steps,_=rt.controller.skills.steps('EnsureInitialShelter','native-recovery',[
            {'kind':'place_buildings','placements':placements}],facts)
        await rt.commit_strategy(CommitSteps(expected_revision=rt.current_plan.revision,reason='Native recovery acceptance',steps=steps).decision(rt.current_plan),
            actor='strategist',expected_token=rt.context_token,expected_revision=rt.chat_revision)
        goal.steps=[steps[0].id]
        obstruction_target=None
        if obstruction:
            cell=placements[0]
            request=dict(defName='Wall',stuff='Steel',x=cell['x'],z=cell['z'],rotation='north')
            preview=await rt.inspect_native('home/place_building',dict(request,dryRun=True))
            assert preview.get('canPlace') is True,preview
            await rt.game.invoke('home/place_building',dict(request,dryRun=False),allow_write=True)
            buildings=await rt.game.query('home/list_buildings',aggregate=False,playerOnly=True)
            obstruction_target=next(b for b in buildings['buildings'] if b['position']['x']==cell['x'] and b['position']['z']==cell['z'])
            report['obstruction']=obstruction_target
        else:
            await designate(forbid)
        rt.mode='automate'
        try:await rt.hands.advance(rt)
        finally:rt.mode='manual'
        p=rt.current_plan.progress[steps[0].id]
        assert p.state=='blocked' and p.failure.code==('construction_unavailable' if obstruction else 'construction_resources') and not p.issued,p.model_dump()
        report['blocked']=p.model_dump()
        async def recover():
            rt.mode='automate'
            try:return await recover_construction(rt,steps[0].id,token=rt.context_token,direction=rt.chat_revision,limit=3)
            finally:rt.mode='manual'
        assert not await recover()
        if obstruction:
            identity=rt.identity;target=obstruction_target
            arguments=dict(colonyId=identity['colonyId'],loadToken=identity['loadToken'],mapId=identity['mapId'],
                thing=target['thingId'],expectedDef='Wall',expectedStuff='Steel',
                x=target['position']['x'],z=target['position']['z'])
            report['obstruction_cancel_preview']=(await rt.bridge.call('home/cancel_construction',**arguments,dryRun=True)).structuredContent
            report['obstruction_cancel']=(await rt.bridge.call('home/cancel_construction',**arguments,dryRun=False)).structuredContent
            assert report['obstruction_cancel'].get('removed') is True
        else:
            await designate(allow)
        report['recovery_preconditions']={'status':await rt.game.query('home/status',colonists=False,threats=True),
            'goal':rt.current_plan.colony_goals['EnsureInitialShelter'].model_dump(),
            'step':rt.current_plan.spec.steps[-1].model_dump(),'direction':rt.chat_revision,'token':rt.context_token}
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
        await exercise_recovery(rt,report,obstruction=args.obstruction)
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
    parser.add_argument('--obstruction',action='store_true',help='Recover a known prewrite placement refusal after ordinary cancellation of an exact conflicting Steel wall blueprint')
    raise SystemExit(0 if asyncio.run(run(parser.parse_args())) else 1)
