"""Native letter discovery and stale-ID refusal; no synthetic notifications."""
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
        before=await game.invoke('rimworld/list_letters',{'limit':1000})
        assert before['truncated'] is False
        try:
            await game.invoke('rimworld/dismiss_letter',{'letterId':'MissingLetterFixture'},allow_write=True)
            raise AssertionError('Unknown letter accepted')
        except BridgeError as error:
            refusal=str(error)
        after=await game.invoke('rimworld/list_letters',{'limit':1000})
        assert before['letters']==after['letters']
        (root/'letters-smoke.json').write_text(json.dumps(dict(before=before,refusal=refusal,after=after),indent=2),encoding='utf8')
        print('PASS: native letter listing and invalid-ID refusal; notification stack unchanged',flush=True)


if __name__=='__main__':asyncio.run(main())
