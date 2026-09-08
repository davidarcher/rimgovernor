"""Real native hands, scripted strategist commitments, no auxiliary inference."""
import asyncio
import json
from pathlib import Path
from rimbot.bridge import bridge_session
from rimbot.bridge_game import BridgeGame
from rimbot.bridge_observation import observe
from rimbot.bridge_runtime import BridgeRuntime
from rimbot.colony_plan import Decision, PlanSpec
from rimbot.store import Store

async def main(headless=False, build=False):
    root=Path('.rimbot/bridge').resolve()
    answer=None
    class Brain:
        calls=0
        async def complete(self,*args):
            self.calls+=1
            return {'role':'assistant','tool_calls':[{'id':'commit','type':'function','function':{'name':'commit_plan','arguments':answer.model_dump_json()}}]},{}
        async def close(self):pass
    brain=Brain()
    from rimbot.headless import prepare
    configuration=prepare(root) if headless else root/'config'
    async with bridge_session(root/'gabs/gabs-v1.1.1-windows-amd64/gabs.exe', configuration) as bridge:
        await bridge.core('games_start',gameId=bridge.game_id);await bridge.connect()
        await bridge.call('rimworld/load_game_ready',saveName='RimBot-tribal8-baseline',readiness='visual',timeoutMs=90000,ignoreModCompatibility=headless)
        await bridge.call('rimworld/set_time_speed',speed='Paused',ultraSpeedBoost=False)
        store=Store(root/'strategy-smoke.sqlite')
        rt=BridgeRuntime(store,root,model_factory=lambda _:brain)
        rt.bridge=bridge;rt.game=BridgeGame(bridge)
        await rt.sync_identity();rt.batch=await observe(rt.game);rt.strategic_state.update(rt.batch)
        pawn=rt.batch.summary.pawns[0]
        sleep_cell={'x':pawn.position.x+3,'z':pawn.position.z}
        spec=PlanSpec(goals=['Consolidate starting supplies'],long_term='Stable tribal colony',right_now='Create the starter stockpile',steps=[{
            'id':'stockpile','title':'Starting supplies','completion_criteria':'Zone exists at the committed cell',
            'action':{'kind':'create_zone','zone_type':'stockpile','label':'Strategy pipeline probe',
                'patches':[{'x':pawn.position.x,'z':pawn.position.z,'width':1,'height':1}]}},
            {'id':'sleep','title':'Temporary sleeping capacity','completion_criteria':'Sleeping spot exists immediately',
             'action':{'kind':'place_buildings','placements':[dict(def_name='SleepingSpot',**sleep_cell)]}},
            {'id':'animal-sleep','title':'Animal sleeping spot','completion_criteria':'Animal spot exists immediately',
             'action':{'kind':'place_buildings','placements':[dict(def_name='AnimalSleepingSpot',x=pawn.position.x+6,z=pawn.position.z)]}},
            {'id':'wall','title':'Construction probe','completion_criteria':'Wall built by colonist labor',
             'action':{'kind':'place_buildings','placements':[dict(def_name='Wall',materials=['WoodLog'],x=pawn.position.x+9,z=pawn.position.z)]}}])
        answer=Decision(expected_revision=rt.current_plan.revision,disposition='revise',assessment='Supplies need storage',rationale='Use a nearby clear cell',reply='Set up the supplies area.',plan=spec)
        rt.mode='automate'
        try:
            await rt.planner.play_bridge()
            assert rt.current_plan.revision>0 and brain.calls==1
            before=rt.counters['actions']
            await rt.hands.advance(rt)
            progress=rt.current_plan.progress['stockpile']
            assert progress.state=='complete',progress.model_dump()
            sleeping=rt.current_plan.progress['sleep']
            assert sleeping.state=='complete',{'progress':sleeping.model_dump(),'projects':rt.projects.dump(),'native':await rt.game.query('home/list_buildings',match='SleepingSpot',aggregate=False,playerOnly=True)}
            animal=rt.current_plan.progress['animal-sleep']
            wall=rt.current_plan.progress['wall']
            assert animal.state=='complete',animal.model_dump()
            assert wall.state=='blocked' and wall.failure.code=='construction_unavailable',wall.model_dump()
            blocked_wall=wall.model_dump()
            supplies=await rt.game.query('home/list_things',ownership='ours',x=pawn.position.x,z=pawn.position.z,radius=20,maxPositionsPerDef=10)
            wood=next(r for r in supplies['things'] if r['defName']=='WoodLog')
            stack=wood['positions'][0]['thingId']
            orders=await rt.game.invoke('rimworld/list_architect_designators',{'categoryId':'Orders'})
            allow=next(d for d in orders['designators'] if d['className']=='RimWorld.Designator_Unforbid')
            position=wood['positions'][0]
            allowed=await rt.game.invoke('rimworld/apply_architect_designator',{'designatorId':allow['id'],'x':position['x'],'z':position['z'],'dryRun':False,'keepSelected':False},allow_write=True)
            updated=await rt.game.query('home/list_things',ownership='ours',maxPositionsPerDef=0)
            assert next(r for r in updated['things'] if r['defName']=='WoodLog')['oursUnforbidden']>=5,allowed
            # New evidence permits an explicit retry commitment, not an automatic retry.
            answer=answer.model_copy(update={'expected_revision':rt.current_plan.revision,'plan':spec,'retry_steps':['wall']})
            await rt.planner.play_bridge()
            await rt.hands.advance(rt)
            wall=rt.current_plan.progress['wall']
            assert wall.state=='waiting',wall.model_dump()
            wall_native=await rt.game.query('home/list_buildings',match='Wall',x=pawn.position.x+9,z=pawn.position.z,radius=1,aggregate=False,playerOnly=True)
            matches=[b for b in wall_native['buildings'] if b['position']=={'x':pawn.position.x+9,'z':pawn.position.z}]
            assert len(matches)==1 and matches[0]['status']=='blueprint',matches
            assert matches[0]['workToBuild']>0,matches
            await rt.hands.advance(rt)
            assert rt.counters['actions']==before+4 and brain.calls==2
            await rt.projects.reconcile(rt.game);rt.reconcile_plan();rt.persist()
            assert progress.state=='complete' and wall.state=='waiting'
            status=await rt.game.query('home/status')
            assert status['time']['paused'] and status['time']['ticksGame']==rt.batch.summary.end_tick
            evidence={'paused_tick':status['time']['ticksGame'],'strategist_calls':brain.calls,'native_actions':rt.counters['actions']-before,'progress':progress.model_dump(),'plan_revision':rt.current_plan.revision,'sleeping':sleeping.model_dump(),'animal_sleeping':animal.model_dump(),'wall':wall.model_dump(),'wall_native':matches,'forbidden_wall':blocked_wall,'allowed_stack':stack}
            if build:
                history=[]
                start=asyncio.get_running_loop().time()
                await rt.game.invoke('rimworld/set_time_speed',{'speed':'Superfast','ultraSpeedBoost':False},allow_write=True)
                try:
                    while asyncio.get_running_loop().time()-start < 120:
                        await asyncio.sleep(2)
                        await rt.projects.reconcile(rt.game,only_id=wall.project_id)
                        rt.reconcile_plan()
                        people=await rt.game.query('home/list_pawns')
                        jobs=[{'name':p['name'],'job':p['job']} for p in people['pawns']]
                        history.append({'elapsed':round(asyncio.get_running_loop().time()-start,1),'state':wall.state,'jobs':jobs})
                        if len(history)%5==0:print('Construction:',history[-1],flush=True)
                        if wall.state=='complete':break
                finally:
                    await rt.game.invoke('rimworld/set_time_speed',{'speed':'Paused','ultraSpeedBoost':False},allow_write=True)
                evidence['construction_history']=history
                final=await rt.game.query('home/list_buildings',match='Wall',x=pawn.position.x+9,z=pawn.position.z,radius=1,aggregate=False,playerOnly=True)
                built=[b for b in final['buildings'] if b['position']=={'x':pawn.position.x+9,'z':pawn.position.z}]
                evidence['finished_wall']=built
                evidence['wall']=wall.model_dump()
                evidence['finished_tick']=(await rt.game.query('home/status'))['time']['ticksGame']
                (root/'strategy-construction-smoke.json').write_text(json.dumps(evidence,indent=2),encoding='utf8')
                assert wall.state=='complete' and len(built)==1 and built[0]['status']=='built',(wall.model_dump(),history[-3:])
                await rt.hands.advance(rt)
                assert rt.counters['actions']==before+4 and brain.calls==2
                print('PASS: pawn labor completed the wall; native built state and plan completion agree; no replay writes',flush=True)
            (root/'strategy-smoke.json').write_text(json.dumps(evidence,indent=2),encoding='utf8')
            print('PASS: committed plan -> native zone and two instant spots complete; normal wall initially queues as a blueprint; replay issued no duplicate and no model call',flush=True)
        finally:
            await rt.game.invoke('home/zone_cells',{'op':'delete','zone':'Strategy pipeline probe','dryRun':False},allow_write=True)
            await rt.halt();await rt.router.close();store.close()

if __name__=='__main__':
    import argparse
    parser=argparse.ArgumentParser();parser.add_argument('--headless',action='store_true')
    parser.add_argument('--build',action='store_true')
    args=parser.parse_args()
    asyncio.run(main(args.headless,args.build))
