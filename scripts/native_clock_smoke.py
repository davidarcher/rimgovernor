"""Disposable native clock test; run with the controller and game closed."""
from rimbot.bridge import gabs_executable
import asyncio
import json
from pathlib import Path
from types import SimpleNamespace
from rimbot.bridge import bridge_session
from rimbot.bridge_game import BridgeGame
from rimbot.bridge_runtime import BridgeRuntime
from rimbot.clock_control import PlayClock
from rimbot.store import Store


async def until(test, timeout=8):
    end = asyncio.get_running_loop().time()+timeout
    while asyncio.get_running_loop().time() < end:
        value = await test()
        if value:
            return value
        await asyncio.sleep(.2)
    raise AssertionError('Native condition was not observed before deadline')


async def main():
    root=Path('.rimbot/bridge').resolve()
    evidence={}
    async with bridge_session(gabs_executable(root), root/'config') as bridge:
        await bridge.core('games_start', gameId=bridge.game_id)
        await bridge.connect()
        await bridge.call('rimworld/load_game_ready', saveName='RimBot-tribal8-baseline', readiness='visual', timeoutMs=90000)
        game = BridgeGame(bridge)
        clock = PlayClock(bridge)
        await clock.change('Paused')
        # No controller heartbeat: the native watcher alone must stop the game.
        status = await game.query('home/status'); before = status['time']['ticksGame']
        native = (await bridge.call('home/supervised_play', op='start', owner='lease-probe', leaseMs=1000, speed='Normal', hostileWithin=40)).structuredContent
        assert native['active'], native
        async def expired():
            value=(await bridge.call('home/supervised_play', op='status')).structuredContent
            return value if not value['active'] else None
        expired_state = await until(expired)
        after = await game.query('home/status')
        assert expired_state['stopReason']=='lease_expired' and expired_state['pauseVerified'], expired_state
        assert after['time']['paused'] and after['time']['ticksGame']>before
        evidence['lease_expiry'] = {'ticks': after['time']['ticksGame']-before, 'native': expired_state}
        print('PASS: native lease expiry pauses without controller heartbeat', flush=True)
        # Outside pause uses the same native time state change as a player pause.
        assert (await clock.change('Normal'))['active']
        await bridge.call('rimworld/set_time_speed', speed='Paused', ultraSpeedBoost=False)
        async def outside_pause():
            await clock.poll()
            return clock.hold == 'external_pause'
        await until(outside_pause)
        try:
            await clock.change('Normal')
            raise AssertionError('External pause was overridden')
        except ValueError as error:
            assert 'player must enable' in str(error)
        evidence['external_pause'] = dict(clock.state)
        print('PASS: external pause requires explicit player resume', flush=True)
        clock.allow_resume()
        assert (await clock.change('Normal'))['active']
        await clock.change('Paused')
        # Exercise the actual runtime write-ahead ledger and orderly stop.
        store=Store(root/'clock-smoke.sqlite')
        rt=BridgeRuntime(store, root, model_factory=lambda _: SimpleNamespace())
        rt.bridge, rt.game = bridge, game
        await rt.sync_identity()
        rt.mode='automate'
        status=await game.query('home/status')
        pawn=next(p for p in status['colonists'] if not p.get('incapableOfViolence') and not p.get('drafted'))
        identity=str(pawn['thingId']); cell=pawn['position']
        try:
            destination=None
            for dx,dz in ((3,0),(-3,0),(0,3),(0,-3)):
                args={'action':'goto','pawn':identity,'x':cell['x']+dx,'z':cell['z']+dz,'dryRun':True}
                preview=await game.invoke('home/order',args,allow_write=True)
                if preview.get('success'):
                    destination=args;break
            assert destination, 'No reachable nearby movement probe'
            destination['dryRun']=False
            await rt.native('home/order',destination)
            assert identity in rt.draft_owners
            assert (await rt.control_clock('Normal'))['active']
            async def arrived():
                await rt.supervisor.poll()
                current=(await game.invoke('home/order',{'action':'resolve','pawn':identity,'dryRun':True},allow_write=True))['pawn']
                return current if current['position']['x']==destination['x'] and current['position']['z']==destination['z'] else None
            arrival=await until(arrived)
            evidence['movement']={'destination':destination,'observed':arrival}
        finally:
            await rt.halt()
            final=(await game.invoke('home/order',{'action':'resolve','pawn':identity,'dryRun':True},allow_write=True))['pawn']
            assert final['drafted'] is False and not rt.draft_owners, final
            store.close()
        evidence['cleanup']='runtime undrafted its pawn and native clock paused'
        (root/'clock-smoke.json').write_text(json.dumps(evidence,indent=2),encoding='utf8')
        print('PASS: pawn moved under native supervision; runtime cleanup undrafted and paused',flush=True)

if __name__=='__main__':
    asyncio.run(main())
