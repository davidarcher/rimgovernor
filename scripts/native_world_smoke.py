"""Read native climate and settlement context without opening the world view."""
from rimbot.bridge import gabs_executable
import asyncio
import json
from pathlib import Path
from rimbot.bridge import bridge_session
from rimbot.bridge_game import BridgeGame
from rimbot.headless import prepare


async def main():
    root=Path('.rimbot/bridge').resolve()
    async with bridge_session(gabs_executable(root),prepare(root)) as bridge:
        await bridge.core('games_start',gameId=bridge.game_id);await bridge.connect()
        await bridge.call('rimworld/load_game_ready',saveName='RimBot-tribal8-baseline',readiness='visual',ignoreModCompatibility=True,timeoutMs=90000)
        await bridge.call('rimworld/set_time_speed',speed='Paused',ultraSpeedBoost=False)
        game=BridgeGame(bridge)
        before=await game.query('home/status')
        result=await game.query('home/world',settlementRadius=15)
        empty=await game.invoke('home/world',{'settlementRadius':0})
        assert empty['settlements']==[]
        assert result.get('success') is True
        assert result['worldView']['shown'] is False
        after=await game.query('home/status')
        assert after['time']['paused'] and before['time']['ticksGame']==after['time']['ticksGame']
        (root/'world-smoke.json').write_text(json.dumps(result,indent=2),encoding='utf8')
        print('PASS: native world read, radius-zero filtering, unchanged paused ticks and no world-view display',flush=True)


if __name__=='__main__':asyncio.run(main())
