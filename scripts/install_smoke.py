"""Fixture setup creates packed furniture; normal pawn labor must install it."""
from rimbot.bridge import gabs_executable
import asyncio
import json
import time
from pathlib import Path
from rimbot.bridge import bridge_session, BridgeError
from rimbot.bridge_game import BridgeGame
from rimbot.bridge_runtime import BridgeRuntime
from rimbot.bridge_observation import observe
from rimbot.colony_plan import Decision, PlanSpec
from rimbot.config import ModelRole
from rimbot.headless import prepare, isolated_root
from rimbot.store import Store


async def main():
    root = isolated_root('.rimbot/bridge', Path('.rimbot')/f'install-smoke-{time.time_ns()}')
    async with bridge_session(gabs_executable(root), prepare(root)) as bridge:
        await bridge.core('games_start', gameId=bridge.game_id)
        await bridge.connect()
        await bridge.call('rimworld/load_game_ready', saveName='RimBot-tribal8-baseline', readiness='visual', timeoutMs=90000, ignoreModCompatibility=True)
        await bridge.call('rimworld/set_time_speed', speed='Paused', ultraSpeedBoost=False)
        fixture = (await bridge.call('test/packed_furniture')).structuredContent
        store = Store(root/'test.sqlite')
        rt = BridgeRuntime(store, root); rt.bridge = bridge; rt.game = BridgeGame(bridge)
        try:
            await rt.sync_identity(); rt.batch = await observe(rt.game); rt.mode = 'automate'
            args = None
            for dx in (3, -3, 5, -5):
                candidate = dict(thingId=fixture['thingId'], x=fixture['x']+dx, z=fixture['z'], rotation=1, dryRun=True)
                try:
                    await rt.game.invoke('home/install', candidate)
                    args = dict(candidate, dryRun=False); break
                except (ValueError, BridgeError):
                    pass
            assert args, 'No legal fixture destination'
            for bad in (dict(args, thingId='Thing_missing'), dict(args, x=-100, dryRun=True)):
                try:
                    await rt.game.invoke('home/install', bad, allow_write=True)
                except (ValueError, BridgeError):
                    pass
                else:
                    raise AssertionError('Invalid item/destination was accepted')
            spec = PlanSpec(steps=[dict(id='install', title='Install existing bed', completion_criteria='Exact bed installed',
                action=dict(kind='native_operation', tool='home/install', arguments=args))])
            decision = Decision(expected_revision=0, disposition='revise', assessment='Packed bed available',
                rationale='Reuse it', reply='Install the bed.', plan=spec)
            await rt.commit_strategy(decision, actor=ModelRole.STRATEGIST, expected_token=rt.context_token, expected_revision=rt.chat_revision)
            await rt.hands.advance(rt)
            progress = rt.current_plan.progress['install']
            assert progress.state == 'waiting', progress
            initial = await rt.game.invoke('home/install', dict(thingId=fixture['innerId'], dryRun=True))
            assert initial['state'] == 'queued', initial
            repeat = await rt.game.invoke('home/install', args, allow_write=True)
            assert repeat['blueprint']['thingId'] == initial['blueprint']['thingId']
            try:
                await rt.game.invoke('home/install', dict(args, x=args['x']+1), allow_write=True)
            except (ValueError, BridgeError):
                pass
            else:
                raise AssertionError('Conflicting destination accepted')
            await rt.control_clock('Superfast')
            deadline = time.monotonic()+60
            while time.monotonic() < deadline:
                await rt.supervisor.poll()
                await rt.projects.reconcile(rt.game)
                rt.reconcile_plan()
                if progress.state == 'complete': break
                await asyncio.sleep(.5)
            await rt.control_clock('Paused')
            report = dict(fixture=fixture, initial=initial, progress=progress.model_dump(), projects=rt.projects.dump())
            (root/'result.json').write_text(json.dumps(report, indent=2))
            assert progress.state == 'complete', report
            assert fixture['innerId'] in rt.projects.rows[0].matched_ids
            print(json.dumps(dict(installed=True, report=str(root/'result.json'))))
        finally:
            await rt.halt(); await rt.router.close(); store.close()


if __name__ == '__main__': asyncio.run(main())
