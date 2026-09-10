"""Verify obsolete starter-stock allow recovery through fresh native reads only."""
import argparse,asyncio,json,traceback
from pathlib import Path
from rimgovernor.bridge_runtime import BridgeRuntime
from rimgovernor.campaign_manifest import capture_manifest
from rimgovernor.colony_plan import ColonyGoal,CommitSteps,PlanStep
from rimgovernor.headless import isolated_root,prepare
from rimgovernor.store import Store
from rimgovernor.supply_recovery import recover_starting_supplies
from session_checkpoint_acceptance import ready
from deterministic_foothold import NoInference

async def run(args):
    root=isolated_root(args.source_root,args.output/'bridge');config=prepare(root)
    store=Store(args.output/'state.sqlite');rt=BridgeRuntime(store,root,fresh=True,headless=True,model_factory=lambda _:NoInference())
    report={'outcome':'failed','save_edits':[]};NoInference.attempts=0
    try:
        report['manifest']=capture_manifest(Path(__file__).resolve().parents[1],root,config,{'model':'no inference'})
        await ready(rt);rt.execution_task=asyncio.current_task()
        facts=await rt.game.query('home/colony_facts',planning=True)
        positions=facts['forbiddenSupplies'];assert positions
        designator=await rt.controller.skills.designator('Designator_Unforbid');target=positions[0]
        step=PlanStep(id='allow-probe',title='Allow starting stock',source='AUTOPILOT',goal_id='AllowStartingSupplies',
            completion_criteria='Original starter stock allowed',action=dict(kind='native_operation',tool='rimworld/apply_architect_designator',
                arguments=dict(designatorId=designator,x=target['x'],z=target['z'],dryRun=False,keepSelected=False)))
        rt.current_plan.colony_goals['AllowStartingSupplies']=ColonyGoal(priority_class=2,steps=[step.id])
        await rt.commit_strategy(CommitSteps(expected_revision=rt.current_plan.revision,reason='Native starter stock recovery',steps=[step]).decision(rt.current_plan),
            actor='strategist',expected_token=rt.context_token,expected_revision=rt.chat_revision)
        report['ordinary_allow_receipts']=[]
        for position in positions:
            report['ordinary_allow_receipts'].append(await rt.game.invoke('rimworld/apply_architect_designator',
                dict(designatorId=designator,x=position['x'],z=position['z'],keepSelected=False),allow_write=True))
        rt.mode='automate'
        before=rt.counters['actions'];await rt.hands.advance(rt)
        progress=rt.current_plan.progress[step.id]
        assert progress.state=='blocked' and progress.failure.code=='starting_supplies_unavailable',progress.model_dump()
        report['blocked']=progress.model_dump()
        assert await recover_starting_supplies(rt,step.id,token=rt.context_token,direction=rt.chat_revision)
        assert progress.state=='complete' and len(progress.recovery_history)==1
        assert rt.counters['actions']==before and NoInference.attempts==0 and rt.counters['model_calls']==0
        report.update(outcome='passed',reconciled=progress.model_dump(),native_actions_before=before,native_actions_after=rt.counters['actions'])
    except Exception as error:report.update(error=str(error),traceback=traceback.format_exc())
    finally:
        rt.mode='manual';rt.execution_task=None
        await rt.stop();store.close()
        (args.output/'result.json').write_text(json.dumps(report,indent=2))
    print(json.dumps({k:report.get(k) for k in ('outcome','error','native_actions_before','native_actions_after')}),flush=True)

if __name__=='__main__':
    parser=argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--source-root',type=Path,required=True);parser.add_argument('--output',type=Path,required=True)
    asyncio.run(run(parser.parse_args()))
