"""Headless ownership/cleanup integration check; no model or combat victory claim."""
from rimgovernor.bridge import gabs_executable
import asyncio
import json
from pathlib import Path
from rimgovernor.bridge import bridge_session
from rimgovernor.bridge_game import BridgeGame
from rimgovernor.bridge_observation import observe
from rimgovernor.bridge_runtime import BridgeRuntime
from rimgovernor.colony_plan import Decision, PlanSpec
from rimgovernor.config import ModelRole
from rimgovernor.headless import prepare
from rimgovernor.store import Store


async def main():
    root=Path('.rimgovernor/bridge').resolve()
    async with bridge_session(gabs_executable(root),prepare(root)) as bridge:
        await bridge.core('games_start',gameId=bridge.game_id);await bridge.connect()
        await bridge.call('rimworld/load_game_ready',saveName='RimGovernor-tribal8-baseline',readiness='visual',
                          ignoreModCompatibility=True,timeoutMs=90000)
        await bridge.call('rimworld/set_time_speed',speed='Paused',ultraSpeedBoost=False)
        store=Store(root/'stand-down-smoke.sqlite');rt=BridgeRuntime(store,root,headless=True)
        rt.bridge=bridge;rt.game=BridgeGame(bridge)
        await rt.sync_identity();rt.batch=await observe(rt.game)
        rt.mode='automate';chosen=[]
        try:
            for pawn in rt.batch.summary.pawns:
                args={'action':'draft','pawn':pawn.thing_id,'dryRun':True}
                preview=await rt.game.invoke('home/order',args,allow_write=False)
                if preview.get('success') and not pawn.drafted:chosen.append(pawn.thing_id)
                if len(chosen)==2:break
            assert len(chosen)==2,'Need two draftable test pawns'
            ai,player=chosen
            await rt.native('home/order',{'action':'draft','pawn':ai,'dryRun':False})
            await rt.game.invoke('home/order',{'action':'draft','pawn':player,'dryRun':False},allow_write=True)
            assert ai in rt.draft_owners and player not in rt.draft_owners
            spec=PlanSpec(goals=['Return the AI-controlled pawn to work'],steps=[dict(id='stand-down',
                title='Stand down',action=dict(kind='stand_down',pawn_ids=chosen),
                completion_criteria='Owned draft is released; player draft preserved')])
            decision=Decision(expected_revision=rt.current_plan.revision,disposition='revise',plan=spec,
                assessment='Test cleanup',rationale='Selected task is finished',reply='Return to work')
            rt.current_plan.commit(decision,actor=ModelRole.STRATEGIST,tick=rt.batch.summary.end_tick)
            await rt.hands.advance(rt)
            result={identity:(await rt.game.invoke('home/order',dict(action='resolve',pawn=identity,dryRun=True)))['pawn']['drafted'] for identity in chosen}
            assert result[ai] is False and result[player] is True,result
            assert rt.mode=='automate' and rt.current_plan.progress['stand-down'].state=='complete'
            evidence=dict(drafted=result,mode=rt.mode,progress=rt.current_plan.progress['stand-down'].model_dump())
            (root/'stand-down-smoke.json').write_text(json.dumps(evidence,indent=2),encoding='utf8')
            print('PASS: AI draft released and verified; player draft preserved; Automate remained enabled',flush=True)
        finally:
            for identity in chosen:
                await rt.game.invoke('home/order',dict(action='undraft',pawn=identity,dryRun=False),allow_write=True)
            await rt.halt();await rt.router.close();store.close()


if __name__=='__main__':asyncio.run(main())
