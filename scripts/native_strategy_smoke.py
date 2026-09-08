"""Real native hands, one scripted strategist decision, no auxiliary inference."""
import asyncio
import json
from pathlib import Path
from types import SimpleNamespace
from rimbot.bridge import bridge_session
from rimbot.bridge_game import BridgeGame
from rimbot.bridge_observation import observe
from rimbot.bridge_runtime import BridgeRuntime
from rimbot.colony_plan import Decision, PlanSpec
from rimbot.store import Store

async def main():
    root=Path('.rimbot/bridge').resolve()
    answer=None
    class Brain:
        calls=0
        async def complete(self,*args):
            self.calls+=1
            return {'role':'assistant','tool_calls':[{'id':'commit','type':'function','function':{'name':'commit_plan','arguments':answer.model_dump_json()}}]},{}
        async def close(self):pass
    brain=Brain()
    async with bridge_session(root/'gabs/gabs-v1.1.1-windows-amd64/gabs.exe', root/'config') as bridge:
        await bridge.core('games_start',gameId=bridge.game_id);await bridge.connect()
        await bridge.call('rimworld/load_game_ready',saveName='RimBot-tribal8-baseline',readiness='visual',timeoutMs=90000)
        await bridge.call('rimworld/set_time_speed',speed='Paused',ultraSpeedBoost=False)
        store=Store(root/'strategy-smoke.sqlite')
        rt=BridgeRuntime(store,root,model_factory=lambda _:brain)
        rt.bridge=bridge;rt.game=BridgeGame(bridge)
        await rt.sync_identity();rt.batch=await observe(rt.game);rt.strategic_state.update(rt.batch)
        pawn=rt.batch.summary.pawns[0]
        spec=PlanSpec(goals=['Consolidate starting supplies'],long_term='Stable tribal colony',right_now='Create the starter stockpile',steps=[{
            'id':'stockpile','title':'Starting supplies','completion_criteria':'Zone exists at the committed cell',
            'action':{'kind':'create_zone','zone_type':'stockpile','label':'Strategy pipeline probe',
                'patches':[{'x':pawn.position.x,'z':pawn.position.z,'width':1,'height':1}]}}])
        answer=Decision(expected_revision=rt.current_plan.revision,disposition='revise',assessment='Supplies need storage',rationale='Use a nearby clear cell',reply='Set up the supplies area.',plan=spec)
        rt.mode='automate'
        try:
            await rt.planner.play_bridge()
            assert rt.current_plan.revision>0 and brain.calls==1
            before=rt.counters['actions']
            await rt.hands.advance(rt)
            progress=rt.current_plan.progress['stockpile']
            assert progress.state=='complete',progress.model_dump()
            await rt.hands.advance(rt)
            assert rt.counters['actions']==before+1 and brain.calls==1
            await rt.projects.reconcile(rt.game);rt.reconcile_plan();rt.persist()
            assert progress.state=='complete'
            evidence={'strategist_calls':brain.calls,'native_actions':rt.counters['actions']-before,'progress':progress.model_dump(),'plan_revision':rt.current_plan.revision}
            (root/'strategy-smoke.json').write_text(json.dumps(evidence,indent=2),encoding='utf8')
            print('PASS: committed plan -> native validated zone -> observed completion; replay issued no duplicate and no model call',flush=True)
        finally:
            await rt.game.invoke('home/zone_cells',{'op':'delete','zone':'Strategy pipeline probe','dryRun':False},allow_write=True)
            await rt.halt();await rt.router.close();store.close()

if __name__=='__main__':asyncio.run(main())
