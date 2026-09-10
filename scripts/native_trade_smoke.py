"""Native trade discovery/session check. No transaction is claimed by this test."""
from rimgovernor.bridge import gabs_executable
import asyncio
import json
from pathlib import Path
from rimgovernor.bridge import bridge_session, BridgeError
from rimgovernor.bridge_game import BridgeGame
from rimgovernor.headless import prepare


async def main():
    root=Path('.rimgovernor/bridge').resolve()
    async with bridge_session(gabs_executable(root),prepare(root)) as bridge:
        await bridge.core('games_start',gameId=bridge.game_id);await bridge.connect()
        await bridge.call('rimworld/load_game_ready',saveName='RimGovernor-tribal8-baseline',readiness='visual',ignoreModCompatibility=True,timeoutMs=90000)
        await bridge.call('rimworld/set_time_speed',speed='Paused',ultraSpeedBoost=False)
        game=BridgeGame(bridge)
        status=await game.invoke('home/trade',{'action':'status'})
        assert status['sessionActive'] is False
        traders=await game.invoke('home/trade',{'action':'list_traders'})
        try:
            preview=await game.invoke('home/trade',{'action':'preview'})
            assert preview.get('success') is False
            refusal=preview
        except BridgeError as error:
            assert 'session' in str(error).lower()
            refusal=str(error)
        (root/'trade-smoke.json').write_text(json.dumps(dict(status=status,traders=traders,refusal=refusal),indent=2),encoding='utf8')
        print('PASS: native trade discovery/status and no-session refusal. No exchange tested.',flush=True)


if __name__=='__main__':asyncio.run(main())
