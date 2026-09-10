"""Native population acceptance from a prepared fixture, through shared Hands."""
import asyncio
import json
import os
import time
from pathlib import Path
from rimbot.bridge import bridge_session, gabs_executable
from rimbot.bridge_game import BridgeGame
from rimbot.bridge_observation import observe as batch
from rimbot.bridge_runtime import BridgeRuntime
from rimbot.colony_plan import CommitSteps
from rimbot.player_commands import apply_command
from rimbot.population import observe, compile_method, refresh, SkillBlocked, goal_id
from rimbot.store import Store


async def run():
    root = Path(os.environ['RIMBOT_BRIDGE_ROOT'])
    report = {'passed': False, 'scope': 'Prepared candidate; ordinary native capture, care, recruitment and integration', 'cases': [], 'observations': []}
    def save(): (root/'population.json').write_text(json.dumps(report, indent=2))
    def check(name, value, **evidence):
        report['cases'].append(dict(name=name, passed=bool(value), **evidence)); save()
        print(name, bool(value), flush=True)
        assert value, name
    async with bridge_session(gabs_executable(root, root/'config'), root/'config') as bridge:
        rt = None
        try:
            await bridge.core('games_start', gameId=bridge.game_id)
            await bridge.connect()
            await bridge.call('rimworld/load_game_ready', saveName='RimBot-tribal8-baseline', readiness='visual', ignoreModCompatibility=True, timeoutMs=120000)
            await bridge.call('rimworld/set_time_speed', speed='Paused', ultraSpeedBoost=False)
            fixture = (await bridge.call('test/population_setup')).structuredContent
            report['fixture'] = fixture; save()
            game = BridgeGame(bridge)
            rt = BridgeRuntime(Store(root/'population.sqlite'), root, headless=False)
            rt.bridge, rt.game, rt.mode = bridge, game, 'manual'
            await rt.sync_identity(); rt.batch = await batch(game)
            report['identity'] = rt.context_token
            report['schemas'] = {name: (await bridge.detail(name)).structuredContent for name in ('home/population', 'home/order')}
            candidate = fixture['candidate']; identity = goal_id(candidate)
            async def command(**payload):
                return await apply_command(rt, payload, token=rt.context_token, revision=rt.chat_revision)
            await command(kind='SetPopulationPolicy', maximum=20, food_days=1)
            await command(kind='SetPopulationDecision', pawn=candidate, decision='recruit')
            visitor = fixture['visitor']; visitor_identity = goal_id(visitor)
            await command(kind='SetPopulationDecision', pawn=visitor, decision='rescue')
            initial = await observe(rt)
            check('candidate_not_admitted', next(p for p in initial['people'] if p['thingId'] == candidate)['admitted'] is False, snapshot=initial)
            goal = rt.current_plan.colony_goals[identity]
            capture_seen = False; recruited = False; fed = False; rescued = False; tended = False
            deadline = time.monotonic() + 1200
            while time.monotonic() < deadline:
                facts = await game.query('home/colony_facts', planning=True)
                people = (await game.query('home/list_pawns', colonistsOnly=True, work=True, bio=True, equipment=True))['pawns']
                await refresh(rt, facts, people)
                current = next((p for p in facts['population']['people'] if p['thingId'] == candidate), None)
                visitor_state = next((p for p in facts['population']['people'] if p['thingId'] == visitor), None)
                if visitor_state:
                    rescued |= visitor_state.get('guest') is True and visitor_state.get('prisoner') is False and bool(visitor_state.get('bed'))
                if current: tended |= current.get('needsTend') is False
                report['observations'].append({'tick': facts['tick'], 'candidate': current, 'goal': goal.model_dump(mode='json')}); save()
                if current:
                    capture_seen |= current.get('prisoner') is True and bool(current.get('bed'))
                    fed |= (current.get('food') or 0) > .3
                    recruited |= current.get('admitted') is True
                if goal.status == 'complete' and rt.current_plan.colony_goals[visitor_identity].status == 'complete': break
                for active_identity in (identity, visitor_identity):
                  active_goal = rt.current_plan.colony_goals[active_identity]
                  if active_goal.status == 'complete': continue
                  try:
                    compiled = await compile_method(rt, active_identity, facts, people)
                    if compiled:
                        method, actions = compiled
                        check('method_not_repeated_'+method, not active_goal.method_seen(method))
                        steps, _ = rt.controller.skills.steps(active_identity, method, actions, facts)
                        await rt.commit_strategy(CommitSteps(expected_revision=rt.current_plan.revision, reason='Population acceptance '+method, steps=steps).decision(rt.current_plan),
                            actor='strategist', expected_token=rt.context_token, expected_revision=rt.chat_revision)
                        active_goal.steps.extend(s.id for s in steps)
                        active_goal.evidence.setdefault('methods', {})[method] = [s.id for s in steps]
                        rt.manual_requests.extend((s.id, rt.context_token, rt.chat_revision) for s in steps)
                        await rt.execute_manual_requests()
                        check('hands_'+method, all(rt.current_plan.progress[s.id].state in ('complete', 'waiting') for s in steps),
                              progress={s.id: rt.current_plan.progress[s.id].model_dump(mode='json') for s in steps})
                  except SkillBlocked as error:
                    report['last_blocker'] = str(error); save()
                # Native simulation, bounded by fresh ticks; no labor is forced or simulated by the probe.
                await bridge.call('rimworld/set_time_speed', speed='Superfast', ultraSpeedBoost=False)
                start = facts['tick']
                until = time.monotonic() + 25
                while time.monotonic() < until:
                    await asyncio.sleep(1)
                    seen = await observe(rt)
                    if seen['tick'] - start >= 6000: break
                await bridge.call('rimworld/set_time_speed', speed='Paused', ultraSpeedBoost=False)
                rt.batch = await batch(game); rt.reconcile_plan()
            check('actual_capture_into_prison_bed', capture_seen)
            check('actual_food_care', fed)
            check('actual_tending', tended)
            check('actual_rescue', rescued)
            check('actual_recruitment', recruited)
            check('actual_integration', goal.status == 'complete', goal=goal.model_dump(mode='json'))
            report['passed'] = True
        except Exception as error:
            report['error'] = repr(error)
            raise
        finally:
            save()
            if rt: rt.store.close()
            await bridge.core('games_stop', gameId=bridge.game_id)


if __name__ == '__main__': asyncio.run(run())
