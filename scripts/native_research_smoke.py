"""Select an available native research project while paused; never add progress."""
from rimbot.bridge import gabs_executable
import asyncio
import json
from pathlib import Path
from rimbot.bridge import bridge_session
from rimbot.bridge_game import BridgeGame
from rimbot.bridge_runtime import BridgeRuntime
from rimbot.headless import prepare
from rimbot.store import Store


async def main():
    root=Path('.rimbot/bridge').resolve();evidence={}
    async with bridge_session(gabs_executable(root),prepare(root)) as bridge:
        await bridge.core('games_start',gameId=bridge.game_id);await bridge.connect()
        await bridge.call('rimworld/load_game_ready',saveName='RimBot-tribal8-baseline',readiness='visual',ignoreModCompatibility=True,timeoutMs=90000)
        await bridge.call('rimworld/set_time_speed',speed='Paused',ultraSpeedBoost=False)
        store=Store(root/'research-smoke.sqlite');rt=BridgeRuntime(store,root,headless=True)
        rt.bridge=bridge;rt.game=BridgeGame(bridge);await rt.sync_identity();rt.mode='automate'
        try:
            before=await rt.game.invoke('home/research',{})
            evidence['before']=before
            choices=[p for p in before['available'] if not p.get('knowledgeCategory')]
            assert choices,'No available ordinary research project in fixture'
            target=choices[0]['defName']
            preview=await rt.game.invoke('home/research',{'set':target,'dryRun':True})
            assert preview['applied'] is False
            result=await rt.native('home/research',{'set':target,'dryRun':False})
            evidence['result']=result
            assert result['observed_after']['current']['defName']==target
            again=await rt.native('home/research',{'set':target,'dryRun':False})
            assert again['receipt']['write']['changed'] is False
            assert again['observed_after']['current']['progress']==result['observed_after']['current']['progress']
            try:
                await rt.game.invoke('home/research',{'set':'NoSuchResearchFixture','dryRun':True})
                raise AssertionError('Unknown project accepted')
            except ValueError as error:
                assert 'No research project matches' in str(error)
            print('PASS: selected '+target+'; fresh readback, replay no-op and invalid-name refusal verified; no research progress added',flush=True)
        finally:
            (root/'research-smoke.json').write_text(json.dumps(evidence,indent=2),encoding='utf8')
            await rt.halt();await rt.router.close();store.close()


if __name__=='__main__':asyncio.run(main())
