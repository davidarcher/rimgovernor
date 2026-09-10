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
from rimbot.session_checkpoint import create_checkpoint,prepare_resume,stop_for_restart
from rimbot.player_commands import apply_command


def work_priorities(roster):
    return {p['thingId']:{w['name']:w.get('priority') for w in p['work']['types']} for p in roster['pawns']}


async def run(args):
    if args.port:
        with socket.socket() as listener:
            listener.bind(('127.0.0.1',args.port))
    root=isolated_root(args.source_root,args.output/'bridge')
    prepare_rendered(root) if args.rendered else prepare(root)
    store=Store(args.output/'state.sqlite')
    settings=Settings(model=args.model,model_url=args.model_url,timeout_seconds=90)
    rt=BridgeRuntime(store,root,fresh=True,headless=not args.rendered,settings=settings)
    report={'outcome':'failed','cases':[]}
    server=server_task=None
    def record(case):
        report['cases'].append(case)
        (args.output/'progress.json').write_text(json.dumps(report,indent=2),encoding='utf8')
        print(json.dumps(case),flush=True)
    async def chat(prompt):
        import rimbot.planner as planner
        original=planner.apply_command
        requests=[]
        async def measured(*positional,**keywords):
            row={'request':positional[1]};requests.append(row)
            try:
                result=await original(*positional,**keywords)
                row['result']=json.loads(json.dumps(result));return result
            except Exception as error:
                row.update(error=str(error),error_type=type(error).__name__);raise
        print('PLAYER: '+prompt,flush=True)
        planner.apply_command=measured
        try:
            await rt.steer(prompt)
            revision=rt.chat_revision
            async with asyncio.timeout(180):
                while (rt.current_plan.control.get('interpreted_player_revision',0)<revision or rt.deliberating):
                    await asyncio.sleep(.5)
        finally:planner.apply_command=original
        report.setdefault('chat_requests',[]).append({'prompt':prompt,'revision':revision,'commands':requests})
        return [m['text'] for m in rt.chat if m.get('revision')==revision and m.get('kind')=='summary']
    async def research_matrix(boundary):
        catalog=await rt.game.invoke('home/research',{'locked':True})
        available=catalog.get('available',[])[:2]
        locked=catalog.get('locked',[])[:2]
        assert len(available)==2 and len(locked)==2, 'Research matrix needs two available and two locked native projects'
        for index,project in enumerate(available):
            identity=project['label'] if boundary=='before_load' else project['defName']
            reply=await chat('Set the selected research project to "'+identity+'".')
            current=await rt.game.invoke('home/research',{})
            record({'command':boundary+'_research_'+project['defName'],'reply':reply,
                'passed':(current.get('current') or {}).get('defName')==project['defName']})
        for project in locked:
            before=await rt.game.invoke('home/research',{})
            actions=rt.counters['actions']
            reply=await chat('Set research to '+project['defName']+' ('+project['label']+
                '). Submit that selection for native validation; if rejected, keep the existing selection and report the native reason.')
            after=await rt.game.invoke('home/research',{})
            record({'command':boundary+'_refusal_'+project['defName'],'reply':reply,
                'passed':bool(reply) and before.get('current')==after.get('current') and actions==rt.counters['actions']
                    and any(row.get('error_type')=='ResearchRefused' and row['request'].get('project','').casefold()
                        in (project['label'].casefold(),project['defName'].casefold())
                        for row in report['chat_requests'][-1]['commands'])})
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
        reply=await chat("Stop spending ordinary components unless they're needed for defense. Leave advanced components unchanged."
                         if args.explicit_resources else "Stop spending components unless they're needed for defense.")
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
            before_actions=rt.counters['actions']
            reply=await chat('Set a 12-day food target, keep 80 steel in reserve, and stop spending components.')
            goal=rt.current_plan.colony_goals.get('EnsureFoodSupply')
            policy=rt.current_plan.control.get('resource_policy',{})
            record({'command':'combined_goal_and_policies','reply':reply,'observed':json.loads(json.dumps(policy)),
                'passed':bool(goal and goal.target.get('food_days')==12 and goal.source=='PLAYER'
                    and policy=={'Steel':{'reserve':80,'spending':'normal'},
                                 'ComponentIndustrial':{'reserve':0,'spending':'stop'}}
                    and rt.counters['actions']-before_actions==2)})
            reply=await chat('Allow normal component spending, and allow steel spending only for defense. Keep both reserves unchanged.')
            policy=rt.current_plan.control.get('resource_policy',{})
            record({'command':'two_resource_policies','reply':reply,'observed':json.loads(json.dumps(policy)),
                'passed':policy=={'Steel':{'reserve':80,'spending':'defense_only'},
                                  'ComponentIndustrial':{'reserve':0,'spending':'normal'}}
                    and rt.counters['actions']-before_actions==4})
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
        if args.matrix:
            await research_matrix('before_load')
            for prompt in ('Maintain a stock of 50 steel.', 'Cancel the MaintainResource-Steel goal. Keep native orders.',
                           'Resume maintaining a stock of 50 steel.'):
                reply=await chat(prompt)
                goal=rt.current_plan.colony_goals.get('MaintainResource-Steel')
                record({'command':'resource_goal','prompt':prompt,'reply':reply,
                    'passed':bool(goal and goal.cancelled==prompt.startswith('Cancel') and goal.target=={'resource':'Steel','quantity':50})})
        archive_evidence={}
        if args.archive:
            calls=rt.counters['model_calls']
            first=next(s for s in rt.current_plan.spec.steps if s.source=='PLAYER'
                       and rt.current_plan.progress[s.id].state=='complete')
            exact={'step':first.model_dump(),'progress':rt.current_plan.progress[first.id].model_dump(),
                   'costs':rt.current_plan.control.get('costs',{}).get(first.id)}
            intents=json.loads(json.dumps(rt.current_plan.control.get('player_intents',{})))
            for reserve in range(101,182):
                result=await apply_command(rt,{'kind':'SetResourceReserve','resource':'Steel','reserve':reserve},
                    token=rt.context_token,revision=rt.chat_revision)
                await rt.execute_manual_requests()
                assert rt.current_plan.progress[result['step']].state=='complete', result
                assert len(rt.current_plan.spec.steps)<=80
            archive_evidence={first.id:exact}
            record({'command':'completed_player_capacity','iterations':81,'inference_calls':rt.counters['model_calls']-calls,
                'archived':rt.current_plan.control.get('archived_action_count'),
                'passed':rt.current_plan._archive_read(first.id)==exact and rt.counters['model_calls']==calls
                    and all(rt.current_plan.control['player_intents'].get(k)==v for k,v in intents.items())})
            reply=await chat('Keep 182 steel in reserve. Leave steel spending and every other resource unchanged.')
            record({'command':'chat_after_capacity','reply':reply,
                'passed':rt.current_plan.control['resource_policy']['Steel']['reserve']==182
                    and rt.current_plan._archive_read(first.id)==exact})
        if args.restart:
            checkpoint=await create_checkpoint(rt,rt.context_token)
            old_token=rt.context_token
            expected_plan=rt.current_plan.model_dump()
            expected_chat=list(rt.chat)
            old_counters=dict(rt.counters)
            await stop_for_restart(rt,old_token,checkpoint['manifest_path'])
            await rt.stop()
            store.close()
            data,state=prepare_resume(checkpoint['manifest_path'])
            store=Store(state/'bridge.sqlite')
            rt=BridgeRuntime(store,root,fresh=True,headless=not args.rendered,
                settings=settings,resume=checkpoint['manifest_path'])
            await rt.start()
            async with asyncio.timeout(120):
                while not rt.connected:
                    if rt.phase=='Connection failed': raise ValueError('Paired restart failed: '+str(rt.chat[-1:]))
                    await asyncio.sleep(.5)
            record({'command':'paired_restart','checkpoint':checkpoint,'old_token':old_token,
                'new_token':rt.context_token,'old_counters':old_counters,
                'passed':rt.context_token!=old_token and rt.mode=='manual'
                    and rt.batch.summary.end_tick in (data['tick'],data['tick']+1)
                    and rt.current_plan.model_dump()==expected_plan and rt.chat==expected_chat
                    and not rt.draft_owners and not rt.manual_requests and rt.counters['model_calls']==0})
            if archive_evidence:
                record({'command':'player_archive_after_native_restart',
                    'passed':all(rt.current_plan._archive_read(k)==v for k,v in archive_evidence.items())})
            if args.matrix:
                await research_matrix('after_load')
                for prompt in ('Stop maintaining the steel stock target. Keep existing game orders.',
                               'Maintain 60 steel again.'):
                    reply=await chat(prompt)
                    goal=rt.current_plan.colony_goals.get('MaintainResource-Steel')
                    record({'command':'resource_goal_after_load','prompt':prompt,'reply':reply,
                        'passed':bool(goal and goal.cancelled==prompt.startswith('Stop')
                            and goal.target=={'resource':'Steel','quantity':50 if prompt.startswith('Stop') else 60})})
            reply=await chat('Cancel the food supply goal. Keep existing game orders in place.')
            goal=rt.current_plan.colony_goals.get('EnsureFoodSupply')
            record({'command':'cancel_after_restart','reply':reply,'passed':bool(goal and goal.cancelled)})
            reply=await chat('Resume the food supply goal with a target of 12 days.')
            goal=rt.current_plan.colony_goals.get('EnsureFoodSupply')
            record({'command':'resume_after_restart','reply':reply,'passed':bool(goal and not goal.cancelled
                and goal.target.get('food_days')==12 and rt.current_plan.control.get('resource_policy')
                    ==expected_plan['control'].get('resource_policy'))})
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
    parser.add_argument('--model-url',default=os.environ.get('RIMBOT_MODEL_URL','http://127.0.0.1:1234/v1'))
    parser.add_argument('--rendered',action='store_true',help='Run the disposable colony visibly')
    parser.add_argument('--port',type=int,help='Serve the disposable colony dashboard while testing')
    parser.add_argument('--extended',action='store_true',help='Also verify research, native refusal and cancellation across autonomous reviews')
    parser.add_argument('--restart',action='store_true',help='Verify paired native restart, preserved plan/chat/policies and subsequent goal cancel/resume')
    parser.add_argument('--archive',action='store_true',help='Execute 81 explicit policy orders, retain exact PLAYER receipts and accept subsequent chat')
    parser.add_argument('--matrix',action='store_true',help='Two native research selections/refusals and a resource goal across paired restart')
    parser.add_argument('--explicit-resources',action='store_true',help='Disambiguate ordinary versus advanced components in the initial policy request')
    args=parser.parse_args()
    if args.restart and args.port: parser.error('--restart uses the headless controller lifecycle; omit --port')
    raise SystemExit(0 if asyncio.run(run(args)) else 1)
