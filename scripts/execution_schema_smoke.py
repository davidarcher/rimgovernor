"""Real local-model commitment using a discovered contract, then paused readback."""
import asyncio
import json
from pathlib import Path
from rimbot.bridge import bridge_session
from rimbot.bridge_game import BridgeGame
from rimbot.bridge_observation import observe
from rimbot.bridge_runtime import BridgeRuntime
from rimbot.colony_plan import CommitSteps
from rimbot.config import Settings, ModelRole
from rimbot.consultation import structured_tool
from rimbot.execution_contracts import ExecutionContracts
from rimbot.headless import prepare, isolated_root
from rimbot.model import LocalModel
from rimbot.native_contracts import validate_arguments
from rimbot.store import Store


async def main():
    import time
    root=isolated_root('.rimbot/bridge',Path('.rimbot')/f'execution-schema-{time.time_ns()}')
    async with bridge_session(root/'gabs/gabs-v1.1.1-windows-amd64/gabs.exe',prepare(root)) as bridge:
        await bridge.core('games_start',gameId=bridge.game_id);await bridge.connect()
        await bridge.call('rimworld/load_game_ready',saveName='RimBot-tribal8-baseline',readiness='visual',timeoutMs=90000,ignoreModCompatibility=True)
        await bridge.call('rimworld/set_time_speed',speed='Paused',ultraSpeedBoost=False)
        store=Store(root/'execution-schema-smoke.sqlite')
        rt=BridgeRuntime(store,root);rt.bridge=bridge;rt.game=BridgeGame(bridge)
        model=LocalModel(Settings(model='qwen3.5-9b'))
        try:
            await rt.sync_identity();rt.batch=await observe(rt.game);rt.mode='automate'
            pawn=rt.batch.summary.pawns[0].thing_id
            tools=[structured_tool('commit_steps','Commit the requested change',CommitSteps.model_json_schema())]
            contracts=ExecutionContracts(tools)
            contracts.expose('home/pawn_config',await rt.game.describe('home/pawn_config'))
            async def progress(_):pass
            answer,usage=await model.complete([{'role':'user','content':
                f'Paused disposable test. Commit exactly one native operation enabling self-tending for pawn {pawn}. '
                f'Current revision {rt.current_plan.revision}. Change no other setting. Use the supplied native argument schema.'}],tools,True,progress)
            calls=answer.get('tool_calls',[]);assert len(calls)==1,answer
            call=calls[0]['function'];assert call['name']=='commit_steps',call
            args=json.loads(call['arguments'])
            validate_arguments('commit_steps',tools[0]['function']['parameters'],args)
            proposal=CommitSteps.model_validate(args);assert len(proposal.steps)==1
            action=proposal.steps[0].action
            assert action.kind=='native_operation' and action.tool=='home/pawn_config'
            assert action.arguments.get('pawn')==pawn and action.arguments.get('selfTend')=='on'
            assert not {k:v for k,v in action.arguments.items()
                if k not in ('pawn','selfTend','dryRun','watch','watchSeconds') and v not in (None,'')},action.arguments
            await rt.commit_strategy(proposal.decision(rt.current_plan),actor=ModelRole.STRATEGIST,
                expected_token=rt.context_token,expected_revision=rt.chat_revision)
            await rt.hands.advance(rt)
            after=await rt.game.invoke('home/pawn_config',{'pawn':pawn,'dryRun':True})
            assert after['after']['settings']['selfTend'] is True,after
            status=await rt.game.query('home/status',colonists=False,threats=False)
            assert status['time']['paused']
            report={'model':model.settings.model,'usage':usage,'proposal':args,'self_tend_verified':True,
                'paused':True,'progress':rt.current_plan.progress[proposal.steps[0].id].model_dump()}
            (root/'execution-schema-smoke.json').write_text(json.dumps(report,indent=2))
            print(json.dumps(report),flush=True)
        finally:
            await rt.halt();await rt.router.close();await model.close();store.close()


if __name__=='__main__':asyncio.run(main())
