"""Retain a real native Hunt designation's exact target through archival and simulation."""
import argparse,asyncio,json,time,traceback
from pathlib import Path
from rimbot.bridge_runtime import BridgeRuntime
from rimbot.campaign_manifest import capture_manifest
from rimbot.colony_plan import ColonyGoal,CommitSteps,Decision,PlanSpec
from rimbot.headless import isolated_root,prepare
from rimbot.hunting import screen_prey
from rimbot.store import Store
from deterministic_foothold import NoInference
from lifecycle_measurement import ledger_sample
from session_checkpoint_acceptance import ready

async def run(args):
    root=isolated_root(args.source_root,args.output/'bridge');config=prepare(root)
    path=args.output/'state.sqlite';store=Store(path)
    rt=BridgeRuntime(store,root,fresh=True,headless=True,model_factory=lambda _:NoInference())
    report={'outcome':'failed','scope':'Hunt designation and target retention, not killed prey or completed pawn labor','samples':[]}
    NoInference.attempts=0
    try:
        report['manifest']=capture_manifest(Path(__file__).resolve().parents[1],root,config,{'model':'no inference'})
        await ready(rt);rt.execution_task=asyncio.current_task()
        facts=await rt.game.query('home/colony_facts',planning=True)
        wildlife=await rt.game.query('home/list_pawns',wildOnly=True,animalsOnly=True,animals=True)
        candidates,screen=screen_prey(wildlife['pawns'],facts['center']);assert candidates,screen
        prey=candidates[0];method='hunt-'+prey['thingId']
        goal=rt.current_plan.colony_goals['EnsureFoodSupply']=ColonyGoal(priority_class=2)
        designator=await rt.controller.skills.designator('Designator_Hunt')
        steps,_=rt.controller.skills.steps('EnsureFoodSupply',method,[dict(kind='native_operation',
            tool='rimworld/apply_architect_designator',arguments=dict(designatorId=designator,
            x=prey['position']['x'],z=prey['position']['z'],keepSelected=False,dryRun=False))],facts)
        goal.steps=[steps[0].id];goal.evidence['methods']={method:goal.steps.copy()}
        await rt.commit_strategy(CommitSteps(expected_revision=rt.current_plan.revision,reason='Native hunting retention',steps=steps).decision(rt.current_plan),
            actor='strategist',expected_token=rt.context_token,expected_revision=rt.chat_revision)
        rt.mode='automate';await rt.hands.advance(rt);rt.mode='manual'
        plan=rt.current_plan;identity=steps[0].id
        assert plan.progress[identity].state=='complete',plan.progress[identity]
        observed=await rt.game.query('home/list_pawns',wildOnly=True,animalsOnly=True,animals=True)
        observed_prey=next(p for p in observed['pawns'] if p['thingId']==prey['thingId'])
        assert observed_prey['animals']['designations']['hunt'] is True
        target=dict(goal.evidence['hunting_targets'][identity]);progress=plan.progress[identity].model_dump()
        report['native_designation']={'prey':observed_prey,'target':target,'progress':progress,'step':steps[0].model_dump()}
        spec=plan.spec.model_dump();spec['steps']=[s for s in spec['steps'] if s['id']!=identity]
        decision=Decision(expected_revision=plan.revision,disposition='revise',assessment='Archive observed Hunt designation',
            rationale='Retain completed command evidence',reply='Retain completed command evidence',plan=PlanSpec.model_validate(spec))
        await rt.commit_strategy(decision,actor='strategist',expected_token=rt.context_token,expected_revision=rt.chat_revision)
        rt.persist()
        def verify():
            assert store.retired_action(rt.colony,identity)['progress']==progress
            assert store.retired_method(rt.colony,'EnsureFoodSupply',goal.method_epoch,method)=={'steps':[identity]}
            assert store.retired_goal_evidence(rt.colony,'EnsureFoodSupply','hunting_targets',identity)==target
            assert identity not in goal.evidence['hunting_targets']
            sample=ledger_sample(rt,path)
            assert all(sample['archive_hashes'][table] for table in ('retired_actions','retired_methods','retired_goal_evidence'))
            if report['samples']:assert sample['archive_hashes']==report['samples'][0]['archive_hashes']
            report['samples'].append(sample)
        verify();start=(await rt.game.query('home/status',colonists=False,threats=False))['time']['ticksGame']
        report['start_tick']=start;deadline=time.monotonic()+240
        while time.monotonic()<deadline:
            await rt.bridge.call('rimworld/set_time_speed',speed='Superfast',ultraSpeedBoost=False)
            await asyncio.sleep(2)
            await rt.bridge.call('rimworld/set_time_speed',speed='Paused',ultraSpeedBoost=False)
            status=await rt.game.query('home/status',colonists=False,threats=True)
            verify();tick=status['time']['ticksGame'];report['end_tick']=tick
            assert not status['threats'].get('hostileCount') and not status['threats'].get('huntingPredatorCount'),status
            if tick-start>=6000:break
        else:raise AssertionError('Native retention window timed out')
        assert NoInference.attempts==0 and rt.counters['model_calls']==0
        report['outcome']='passed'
    except Exception as error:report.update(error=str(error),traceback=traceback.format_exc())
    finally:
        rt.mode='manual';rt.execution_task=None
        await rt.stop();store.close()
        (args.output/'result.json').write_text(json.dumps(report,indent=2))
    print(json.dumps({k:report.get(k) for k in ('outcome','error','start_tick','end_tick')}),flush=True)

if __name__=='__main__':
    parser=argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--source-root',type=Path,required=True);parser.add_argument('--output',type=Path,required=True)
    asyncio.run(run(parser.parse_args()))
