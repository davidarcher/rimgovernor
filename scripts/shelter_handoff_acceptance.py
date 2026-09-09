"""Ordinary pawn construction followed by deterministic furnishing of a player shell."""
import argparse
import asyncio
import json
import time
from pathlib import Path
from deterministic_foothold import NoInference
from session_checkpoint_acceptance import ready
from rimbot.bridge_runtime import BridgeRuntime
from rimbot.headless import isolated_root,prepare
from rimbot.player_commands import apply_command
from rimbot.store import Store
from rimbot.colony_plan import ColonyGoal,CommitSteps


async def run(args):
    root=isolated_root(args.source_root,args.output/'bridge');prepare(root)
    store=Store(args.output/'state.sqlite')
    rt=BridgeRuntime(store,root,fresh=True,headless=True,model_factory=lambda _:NoInference())
    report={'outcome':'failed','history':[],'scope':'Native player shell construction and sleeping handoff; no survival claim'}
    try:
        await ready(rt)
        facts=await rt.game.query('home/colony_facts',planning=True)
        # Explicit fixture setup uses the production supply method and Hands.
        # Native stocks and saves are never synthesized or edited.
        while facts.get('forbiddenSupplies'):
            goal=rt.current_plan.colony_goals.setdefault('AllowStartingSupplies',ColonyGoal(priority_class=2,source='PLAYER'))
            method,actions=await rt.controller.skills.compile('AllowStartingSupplies',facts,[])
            steps,_=rt.controller.skills.steps('AllowStartingSupplies',method,actions,facts)
            await rt.commit_strategy(CommitSteps(expected_revision=rt.current_plan.revision,
                reason='Allow starting supplies for the player room',steps=steps).decision(rt.current_plan),
                actor='strategist',expected_token=rt.context_token,expected_revision=rt.chat_revision)
            for _ in steps:
                rt.manual_requests.extend((s.id,rt.context_token,rt.chat_revision) for s in steps
                    if rt.current_plan.progress[s.id].state=='pending')
                await rt.execute_manual_requests()
            assert all(rt.current_plan.progress[s.id].state=='complete' for s in steps)
            goal.evidence.setdefault('methods',{})[method]=[s.id for s in steps]
            facts=await rt.game.query('home/colony_facts',planning=True)
        layout=await rt.controller.skills.layout(facts)
        shell=rt.controller.skills.shell(layout)
        result=await apply_command(rt,{'kind':'BuildRoom','intent_id':'handoff-home','room':shell,'purpose':'shelter'},
                                   token=rt.context_token,revision=rt.chat_revision)
        await rt.execute_manual_requests()
        report.update(shell=shell,player_step=result['step'],initial_tick=rt.batch.summary.end_tick)
        rt.current_plan.control.setdefault('policy',{})['execution_speed']='Superfast'
        await rt.set_mode('automate')
        deadline=time.monotonic()+args.seconds
        while time.monotonic()<deadline:
            await asyncio.sleep(5)
            control=rt.current_plan.control
            row={'tick':rt.clock.get('ticksGame'),'mode':rt.mode,'phase':rt.phase,
                 'shelter':rt.current_plan.colony_goals.get('EnsureInitialShelter').model_dump()
                    if 'EnsureInitialShelter' in rt.current_plan.colony_goals else None,
                 'player_state':rt.current_plan.progress[result['step']].state,
                 'indoorSleepingCapacity':control.get('facts',{}).get('indoorSleepingCapacity'),
                 'adopted':control.get('adopted_shelter')}
            report['history'].append(row)
            (args.output/'progress.json').write_text(json.dumps(report,indent=2))
            print(json.dumps(row),flush=True)
            assert NoInference.attempts==0
            assert not any(s.action.kind=='build_room_shell' and s.source=='AUTOPILOT' for s in rt.current_plan.spec.steps)
            if (row['player_state']=='complete' and row['adopted'] and row['adopted']['intent']=='intent-handoff-home'
                    and (row['indoorSleepingCapacity'] or 0)>=facts['colonists']):
                await rt.set_mode('manual')
                rooms=await rt.game.query('home/list_rooms',cells=True,x=shell['bounds']['x']+1,z=shell['bounds']['z']+1)
                report.update(outcome='passed',rooms=rooms)
                break
        if report['outcome']!='passed':report['error']='Native handoff did not complete within the bounded trial'
    except Exception as error:
        report['error']=str(error)
        raise
    finally:
        report.update(plan=rt.current_plan.model_dump(),model_calls=rt.counters['model_calls'],model_attempts=NoInference.attempts)
        await rt.stop();store.close()
        (args.output/'result.json').write_text(json.dumps(report,indent=2))
    return report['outcome']=='passed'


if __name__=='__main__':
    parser=argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--source-root',type=Path,required=True)
    parser.add_argument('--output',type=Path,required=True)
    parser.add_argument('--seconds',type=int,default=600)
    args=parser.parse_args()
    raise SystemExit(0 if asyncio.run(asyncio.wait_for(run(args),args.seconds+240)) else 1)
