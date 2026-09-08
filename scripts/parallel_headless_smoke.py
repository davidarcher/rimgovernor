"""Two native games: independent clocks, then stop one without stopping its peer."""
import asyncio
from contextlib import AsyncExitStack
import json
from pathlib import Path
import time
from rimbot.bridge import bridge_session
from rimbot.headless import isolated_root, prepare


async def main(output):
    output.mkdir(parents=True,exist_ok=False)
    roots=[isolated_root('.rimbot/bridge',output/str(i)) for i in range(2)]
    began=time.monotonic()
    async with AsyncExitStack() as stack:
        clients=[await stack.enter_async_context(bridge_session(
            root/'gabs/gabs-v1.1.1-windows-amd64/gabs.exe',prepare(root))) for root in roots]
        async def start(client):
            await client.core('games_start',gameId=client.game_id)
            await client.connect()
            await client.call('rimworld/load_game_ready',saveName='RimBot-tribal8-baseline',
                readiness='visual',timeoutMs=90000,ignoreModCompatibility=True)
            await client.call('rimworld/set_time_speed',speed='Paused',ultraSpeedBoost=False)
        await asyncio.gather(*(start(c) for c in clients))
        async def status(client):return (await client.call('home/status',colonists=False,threats=False)).structuredContent['time']
        before=await asyncio.gather(*(status(c) for c in clients))
        await clients[0].call('rimworld/set_time_speed',speed='Superfast',ultraSpeedBoost=False)
        await asyncio.sleep(1)
        after=await asyncio.gather(*(status(c) for c in clients))
        assert after[0]['ticksGame']>before[0]['ticksGame'],after
        assert after[1]['ticksGame']==before[1]['ticksGame'],after
        await clients[0].core('games_kill',gameId=clients[0].game_id)
        peer=await status(clients[1])
        assert peer['ticksGame']==before[1]['ticksGame'],peer
        report={'two_games_loaded':True,'independent_clocks':True,'peer_alive_after_other_stopped':True,
            'elapsed_seconds':round(time.monotonic()-began,1),'before':before,'after':after}
        (output/'result.json').write_text(json.dumps(report,indent=2))
        print(json.dumps(report),flush=True)


if __name__=='__main__':
    import argparse
    parser=argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--output',type=Path,required=True)
    asyncio.run(main(parser.parse_args().output))
