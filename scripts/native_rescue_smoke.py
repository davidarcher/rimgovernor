"""Native rescue observation/refusal check; does not claim a completed rescue."""
import asyncio
import json
from pathlib import Path
from rimbot.bridge import bridge_session, BridgeError
from rimbot.bridge_game import BridgeGame
from rimbot.headless import prepare


async def main():
    root=Path('.rimbot/bridge').resolve()
    async with bridge_session(root/'gabs/gabs-v1.1.1-windows-amd64/gabs.exe',prepare(root)) as bridge:
        await bridge.core('games_start',gameId=bridge.game_id)
        await bridge.connect()
        await bridge.call('rimworld/load_game_ready',saveName='RimBot-tribal8-baseline',
            readiness='visual',ignoreModCompatibility=True,timeoutMs=90000)
        await bridge.call('rimworld/set_time_speed',speed='Paused',ultraSpeedBoost=False)
        game=BridgeGame(bridge)
        rows=(await game.query('home/list_pawns',colonistsOnly=True,health=True))['pawns']
        assert len(rows)==8
        assert all('carriedThingId' in p and 'bedThingId' in p['health'] for p in rows)
        healthy=[p for p in rows if not p['downed'] and not p['dead']]
        args=dict(action='rescue',pawn=healthy[0]['thingId'],target=healthy[1]['thingId'],dryRun=True)
        refusal=None
        try:
            result=await game.invoke('home/order',args,allow_write=False)
            assert result.get('success') is False,'Healthy standing pawn unexpectedly accepted for rescue'
            refusal=result
        except BridgeError as error:
            assert 'CanRescueNow' in str(error),str(error)
            refusal=str(error)
        (root/'rescue-smoke.json').write_text(json.dumps(dict(pawns=rows,refusal=refusal),indent=2),encoding='utf8')
        print('PASS: native carry/bed fields present; standing-pawn rescue refused. Delivery not tested.',flush=True)


if __name__=='__main__':asyncio.run(main())
