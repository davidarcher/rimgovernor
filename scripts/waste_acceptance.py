"""Native waste hauling through shared goals and Hands in a disposable Docker worker."""
import asyncio
import json
import os
from pathlib import Path

from rimbot.bridge import bridge_session, gabs_executable
from rimbot.bridge_game import BridgeGame
from rimbot.bridge_runtime import BridgeRuntime
from rimbot.bridge_observation import observe
from rimbot.colony_plan import CommitSteps
from rimbot.player_commands import apply_command
from rimbot.store import Store
from rimbot.waste_management import refresh, pending_items


async def run():
    root = Path(os.environ['RIMBOT_BRIDGE_ROOT'])
    report = {'passed': False, 'cases': [], 'scope': 'Scripted native waste hauling; no model inference'}
    def record(name, passed, **evidence):
        report['cases'].append(dict(name=name, passed=bool(passed), **evidence))
        (root/'waste-result.json').write_text(json.dumps(report, indent=2))
        print(name, bool(passed), flush=True)
        assert passed, (name, evidence)
    async with bridge_session(gabs_executable(root, root/'config-headless'), root/'config-headless') as bridge:
        try:
            await bridge.core('games_start', gameId=bridge.game_id)
            await bridge.connect()
            await bridge.call('rimworld/load_game_ready', saveName='RimBot-tribal8-baseline',
                              readiness='visual', ignoreModCompatibility=True, timeoutMs=120000)
            await bridge.call('rimworld/set_time_speed', speed='Paused', ultraSpeedBoost=False)
            burial = os.environ.get('RIMBOT_WASTE_BURIAL') == '1'
            fixture = (await bridge.call('test/waste_fixture', burial=burial)).structuredContent
            targets = [fixture['corpse']] if burial else [fixture['corpse'], fixture['unwanted']]
            report['fixture'] = fixture
            rt = BridgeRuntime(Store(root/'waste.sqlite'), root, headless=True)
            rt.bridge, rt.game = bridge, BridgeGame(bridge)
            await rt.sync_identity()
            rt.batch = await observe(rt.game)
            rt.mode = 'automate'
            report['identity'] = rt.context_token
            for name in ('home/waste_state', 'home/manage_waste'):
                report[name] = (await bridge.detail(name)).structuredContent
            protected = (await bridge.call('home/manage_waste', thingId=fixture['protectedItem'],
                pawn=fixture['pawn'], unwanted=fixture['protectedItem'], dryRun=True)).structuredContent
            record('forbidden_possession_refused', protected.get('accepted') is False, receipt=protected)
            if burial:
                untouched = (await bridge.call('home/waste_state')).structuredContent
                row = next(r for r in untouched['items'] if r['thingId'] == fixture['corpse'])
                record('named_colonist_protected_without_burial_policy', row['eligible'] is False, item=row)
            await apply_command(rt, {'kind': 'CreateGoal', 'goal': 'MaintainWaste', 'unwanted': [] if burial else [fixture['unwanted']], 'bury': [fixture['corpse']] if burial else []},
                                token=rt.context_token, revision=rt.chat_revision)
            goal = rt.current_plan.colony_goals['MaintainWaste']
            await refresh(rt)
            record('exact_waste_observed', set(targets) <=
                {r['thingId'] for r in pending_items(rt.current_plan.control['waste'])}, state=rt.current_plan.control['waste'])
            for attempt in range(3):
                rt.batch = await observe(rt.game)
                await refresh(rt)
                if not pending_items(rt.current_plan.control['waste']):
                    break
                method, actions = await rt.controller.skills.compile('MaintainWaste', {}, [])
                steps, _ = rt.controller.skills.steps('MaintainWaste', method, actions, {'definitions': {}})
                await rt.commit_strategy(CommitSteps(expected_revision=rt.current_plan.revision,
                    reason='Native waste containment acceptance', steps=steps).decision(rt.current_plan), actor='strategist',
                    expected_token=rt.context_token, expected_revision=rt.chat_revision)
                goal.steps.extend(s.id for s in steps)
                goal.evidence.setdefault('methods', {})[method] = [s.id for s in steps]
                await rt.hands.advance(rt)
                progress = rt.current_plan.progress[steps[0].id]
                record('order_waits_for_labor_' + str(attempt), progress.state == 'waiting', progress=progress.model_dump(mode='json'))
                for _ in range(30):
                    await bridge.call('home/supervised_play', op='start', speed='Superfast', maxTicks=300)
                    async with asyncio.timeout(45):
                        while True:
                            clock = (await bridge.call('home/supervised_play', op='status')).structuredContent
                            if not clock['active']:
                                break
                            await asyncio.sleep(.2)
                    rt.batch = await observe(rt.game)
                    await refresh(rt)
                    if progress.state != 'waiting':
                        break
                record('actual_delivery_' + str(attempt), progress.state == 'complete',
                       progress=progress.model_dump(mode='json'), state=rt.current_plan.control['waste'])
            state = rt.current_plan.control['waste']
            record('exposure_reduced_without_destruction', all(next(r for r in state['items'] if r['thingId'] == target)['state'] == ('buried' if burial else 'relocated')
                for target in targets), state=state)
            record('no_remaining_exposed_targets', pending_items(state) == [])
            if burial:
                refusal = (await bridge.call('home/manage_waste', thingId=fixture['corpse'], pawn=fixture['pawn'],
                                             bury=fixture['corpse'], dryRun=True)).structuredContent
                record('grave_cannot_be_exhumed', refusal.get('accepted') is False, receipt=refusal)
            report['passed'] = True
        finally:
            (root/'waste-result.json').write_text(json.dumps(report, indent=2))
            await bridge.core('games_stop', gameId=bridge.game_id)


if __name__ == '__main__':
    asyncio.run(run())
