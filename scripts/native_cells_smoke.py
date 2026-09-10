"""Validate native rectangular queries, sparse accounting and summary semantics."""
from rimbot.bridge import gabs_executable
import asyncio
import json
from pathlib import Path
from rimbot.bridge import bridge_session, BridgeError
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
        p=(await game.query('home/list_pawns',colonistsOnly=True))['pawns'][0]['position']
        args=dict(x=p['x'],z=p['z'],width=5,height=5,fields='terrain,roof,fogged,walkable,things',thingFields='build')
        full=await game.query('home/get_cells_plus',**args)
        corners=await game.invoke('home/get_cells_plus',dict(x0=p['x'],z0=p['z'],x1=p['x']+4,z1=p['z']+4,
            fields=args['fields'],thingFields='build'))
        assert full['cells']==corners['cells'] and full['cellCount']==25
        sparse=await game.query('home/get_cells_plus',**args,sparse=True)
        assert len(sparse['cells'])+sparse['cellsOmitted']==25
        summary=await game.query('home/get_cells_plus',**args,summary=True)
        assert summary['cells']==[] and summary['cellCount']==25 and summary['summary']
        assert all(set(c)<= {'x','z','terrainDefName','roofDefName','fogged','walkable','things'} for c in full['cells'])
        refusals=[]
        for invalid in (dict(args,fields='imaginary'),dict(args,x1=p['x']+10),dict(args,width=33,height=33)):
            try:
                await game.query('home/get_cells_plus',**invalid)
                raise AssertionError('Invalid spatial request accepted')
            except BridgeError as error:refusals.append(str(error))
        after=await game.query('home/status')
        assert after['time']['paused'] and before['time']['ticksGame']==after['time']['ticksGame']
        (root/'cells-smoke.json').write_text(json.dumps(dict(full=full,sparse=sparse,summary=summary,refusals=refusals),indent=2),encoding='utf8')
        print('PASS: extent/corner equivalence, sparse counts, summary, selected fields and invalid-request refusals; ticks unchanged',flush=True)


if __name__=='__main__':asyncio.run(main())


