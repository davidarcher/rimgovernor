"""Native player-chat acceptance in an isolated, paused colony."""
import argparse
import asyncio
import json
import os
import socket
from pathlib import Path
from rimbot.bridge_runtime import BridgeRuntime
from rimbot.config import Settings
from rimbot.headless import isolated_root,prepare,prepare_rendered
from rimbot.store import Store


def work_priorities(roster):
    return {p['thingId']:{w['name']:w.get('priority') for w in p['work']['types']} for p in roster['pawns']}


async def run(args):
    if args.port:
        with socket.socket() as listener:
            listener.bind(('127.0.0.1',args.port))
    root=isolated_root(args.source_root,args.output/'bridge')
    prepare_rendered(root) if args.rendered else prepare(root)
    store=Store(args.output/'state.sqlite')
    rt=BridgeRuntime(store,root,fresh=True,headless=not args.rendered,settings=Settings(model=args.model,timeout_seconds=90))
    report={'outcome':'failed','cases':[]}
    server=server_task=None
    def record(case):
        report['cases'].append(case)
        (args.output/'progress.json').write_text(json.dumps(report,indent=2),encoding='utf8')
        print(json.dumps(case),flush=True)
    async def chat(prompt):
        print('PLAYER: '+prompt,flush=True)
        await rt.steer(prompt)
        revision=rt.chat_revision
        async with asyncio.timeout(180):
            while (rt.current_plan.control.get('interpreted_player_revision',0)<revision or rt.deliberating):
                await asyncio.sleep(.5)
        return [m['text'] for m in rt.chat if m.get('revision')==revision and m.get('kind')=='summary']
    try:
        if args.port:
            import uvicorn
            from rimbot.bridge_server import create_app
            server=uvicorn.Server(uvicorn.Config(create_app(rt),host='127.0.0.1',port=args.port,log_level='warning'))
            server_task=asyncio.create_task(server.serve())
        else: await rt.start()
        async with asyncio.timeout(120):
            while not rt.connected:
                if rt.phase=='Connection failed': raise ValueError('Native connection failed: '+str(rt.chat[-1:]))
                await asyncio.sleep(1)
        report.update(session_id=rt.context_token,initial_tick=rt.batch.summary.end_tick,model=args.model)
        roster=await rt.game.query('home/list_pawns',colonistsOnly=True,work=True)
        pawn=next(p for p in roster['pawns'] if any(w['name']=='Hauling' and w.get('disabled') is False
                  and w.get('priority',0)>0 for w in p['work']['types']))
        reply=await chat('Turn off hauling for '+pawn['name']+'. Do not change other work assignments.')
        observed=await rt.game.query('home/list_pawns',colonistsOnly=True,work=True)
        expected=work_priorities(roster);expected[pawn['thingId']]['Hauling']=0
        record({'command':'work','reply':reply,'passed':work_priorities(observed)==expected})
        reply=await chat('Get us to 20 days of food.')
        goal=rt.current_plan.colony_goals.get('EnsureFoodSupply')
        record({'command':'goal','reply':reply,'passed':bool(goal and goal.source=='PLAYER'
                                and goal.target.get('food_days')==20)})
        reply=await chat("Stop spending components unless they're needed for defense.")
        policy=rt.current_plan.control.get('resource_policy',{}).get('ComponentIndustrial',{})
        record({'command':'policy','reply':reply,'observed':dict(policy),
            'passed':rt.current_plan.control.get('resource_policy')=={
                'ComponentIndustrial':{'spending':'defense_only','reserve':0}}})
        if args.extended:
            reply=await chat('Set the component reserve to 40. Keep the current spending restriction.')
            policy=rt.current_plan.control.get('resource_policy',{}).get('ComponentIndustrial',{})
            record({'command':'explicit_reserve','reply':reply,'observed':dict(policy),
                'passed':policy=={'spending':'defense_only','reserve':40}})
            reply=await chat('Allow normal component spending. Keep the existing reserve unchanged.')
            policy=rt.current_plan.control.get('resource_policy',{}).get('ComponentIndustrial',{})
            record({'command':'spending_preserves_reserve','reply':reply,'observed':dict(policy),
                'passed':policy=={'spending':'normal','reserve':40}})
            reply=await chat('Set the component reserve to 0. Keep the spending policy unchanged.')
            policy=rt.current_plan.control.get('resource_policy',{}).get('ComponentIndustrial',{})
            record({'command':'clear_reserve','reply':reply,'observed':dict(policy),
                'passed':policy=={'spending':'normal','reserve':0}})
            research=await rt.game.invoke('home/research',{'locked':True})
            (args.output/'research-before.json').write_text(json.dumps(research,indent=2))
            available=[p for p in research.get('available',[]) if p.get('defName')!=(research.get('current') or {}).get('defName')]
            if not available: raise ValueError('No available research project in this fixture; no research acceptance claimed')
            project=available[0]
            before_steps={s.id for s in rt.current_plan.spec.steps}
            reply=await chat('Set research to '+project['label']+'.')
            selected=await rt.game.invoke('home/research',{})
            steps=[s for s in rt.current_plan.spec.steps if s.id not in before_steps and s.action.kind=='native_operation'
                   and s.action.tool=='home/research']
            record({'command':'research','requested':project,'reply':reply,'observed':selected.get('current'),
                'passed':bool((selected.get('current') or {}).get('defName')==project['defName'] and steps
                    and all(s.source=='PLAYER' and rt.current_plan.progress[s.id].state=='complete' for s in steps))})
            locked=research.get('locked',[])
            if not locked: raise ValueError('No locked project to verify native refusal')
            blocked=locked[0]
            before_steps={s.id for s in rt.current_plan.spec.steps}
            before_actions=rt.counters['actions']
            reply=await chat('Set research to '+blocked['label']+'. Do not choose a different project if this one is unavailable.')
            after=await rt.game.invoke('home/research',{})
            record({'command':'locked_research','requested':blocked,'reply':reply,
                'passed':after.get('current')==selected.get('current') and bool(reply)
                    and before_steps=={s.id for s in rt.current_plan.spec.steps}
                    and rt.counters['actions']==before_actions})
            reply=await chat('Cancel the food supply goal. Keep existing game orders in place.')
            goal=rt.current_plan.colony_goals.get('EnsureFoodSupply')
            record({'command':'cancel_goal','reply':reply,'passed':bool(goal and goal.cancelled)})
            food_steps=[s.id for s in rt.current_plan.spec.steps if s.goal_id=='EnsureFoodSupply']
            model_calls=rt.counters['model_calls']
            for _ in range(2):
                await rt.set_mode('automate')
                direction=rt.chat_revision
                async with asyncio.timeout(120):
                    while rt.handled_revision<direction or rt.deliberating: await asyncio.sleep(.1)
                await rt.set_mode('manual')
            goal=rt.current_plan.colony_goals.get('EnsureFoodSupply')
            record({'command':'cancellation_survives_autopilot','passed':bool(goal and goal.cancelled
                and food_steps==[s.id for s in rt.current_plan.spec.steps if s.goal_id=='EnsureFoodSupply']
                and rt.counters['model_calls']==model_calls),'model_calls':rt.counters['model_calls']-model_calls})
            reply=await chat('Resume the food supply goal, with a target of 12 days.')
            goal=rt.current_plan.colony_goals.get('EnsureFoodSupply')
            record({'command':'resume_goal','reply':reply,'passed':bool(goal and not goal.cancelled
                and goal.source=='PLAYER' and goal.target.get('food_days')==12)})
        status=await rt.game.query('home/status',colonists=False,threats=False)
        report.update(mode=rt.mode,paused=status['time']['paused'],actions=rt.counters['actions'])
        if all(r['passed'] for r in report['cases']) and rt.mode=='manual' and report['paused']:
            report['outcome']='passed'
    except Exception as error:
        report['error']=str(error)
    finally:
        try:
            if rt.connected and rt.bridge:
                async with rt.lock:
                    await rt.halt()
                    await rt.bridge.core('games_stop',gameId=rt.bridge.game_id)
                    rt.owned_game_stopped=True
        except Exception as error: report['cleanup_error']=str(error)
        if server:
            server.should_exit=True
            await server_task
        else: await rt.stop()
        report.update(plan=rt.current_plan.model_dump(),counters=rt.counters)
        (args.output/'result.json').write_text(json.dumps(report,indent=2),encoding='utf8')
        store.close()
    print(json.dumps({k:v for k,v in report.items() if k!='plan'}),flush=True)
    return report['outcome']=='passed'


if __name__=='__main__':
    parser=argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--source-root',type=Path,required=True)
    parser.add_argument('--output',type=Path,required=True)
    parser.add_argument('--model',default=os.environ.get('RIMBOT_MODEL','qwen3.5-4b'))
    parser.add_argument('--rendered',action='store_true',help='Run the disposable colony visibly')
    parser.add_argument('--port',type=int,help='Serve the disposable colony dashboard while testing')
    parser.add_argument('--extended',action='store_true',help='Also verify research, native refusal and cancellation across autonomous reviews')
    raise SystemExit(0 if asyncio.run(run(parser.parse_args())) else 1)
