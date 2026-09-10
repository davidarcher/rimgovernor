"""Scout test on a disposable baseline. Run with controller/game closed."""
from rimbot.bridge import gabs_executable
import asyncio
import json
from pathlib import Path
from rimbot.bridge import bridge_session
from rimbot.bridge_game import BridgeGame
from rimbot.bridge_observation import observe
from rimbot.bridge_runtime import BridgeRuntime
from rimbot.config import ModelRole, ModelRouting, Settings
from rimbot.store import Store


async def main():
    root=Path('.rimbot/bridge').resolve()
    settings=Settings(model='qwen3.5-9b',max_output_tokens=2048,reasoning=False)
    routing=ModelRouting(roles={ModelRole.STRATEGIST:settings,ModelRole.ANALYST:settings})
    async with bridge_session(gabs_executable(root),root/'config') as bridge:
        await bridge.core('games_start',gameId=bridge.game_id)
        await bridge.connect()
        await bridge.call('rimworld/load_game_ready',saveName='RimBot-tribal8-baseline',readiness='visual',timeoutMs=90000)
        await bridge.call('rimworld/set_time_speed',speed='Paused',ultraSpeedBoost=False)
        store=Store(root/'scout-smoke.sqlite')
        rt=BridgeRuntime(store,root,routing=routing)
        rt.bridge=bridge;rt.game=BridgeGame(bridge)
        try:
            await rt.sync_identity();rt.batch=await observe(rt.game);rt.strategic_state.update(rt.batch)
            before=rt.batch.summary.end_tick
            assert rt.batch.native['status_after']['time']['paused'],'Pause the disposable game first'
            result=await rt.scout('Inspect current construction using home/list_buildings (describe filters first). '
                'Are there unfinished buildings and what native evidence explains their blockers? '
                'If none are observed, say so with the query scope. Do not infer food days or suggest new projects.',
                ['construction'],expected_token=rt.context_token,expected_revision=rt.chat_revision)
            after=await rt.game.query('home/status')
            assert after['time']['paused'] and after['time']['ticksGame']==before
            assert result['investigation']['reads']>0 and rt.current_plan.revision==0
            output=dict(result=result,metrics=rt.router.metrics,unchanged_tick=before)
            (root/'scout-smoke.json').write_text(json.dumps(output,indent=2),encoding='utf8')
            print(json.dumps(output,indent=2),flush=True)
        finally:
            await rt.router.close();store.close()


if __name__=='__main__':asyncio.run(main())
