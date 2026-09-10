"""Native research labor from zero progress, guarded selection and capability readback.

Run inside an isolated container_worker. The disposable save adds one ordinary
wood research bench and enables Research work; no research points are injected.
"""
from copy import deepcopy
import argparse
import asyncio
import hashlib
import json
import time
import xml.etree.ElementTree as ET
from pathlib import Path
from rimbot.bridge import bridge_session, gabs_executable, BridgeError, runtime_file_read
from rimbot.bridge_game import BridgeGame
from rimbot.bridge_runtime import BridgeRuntime
from rimbot.bridge_observation import observe
from rimbot.clock_control import PlayClock
from rimbot.colony_plan import ColonyGoal, CommitSteps
from rimbot.headless import prepare
from rimbot.research import refresh, method
from rimbot.store import Store
from rimbot.config import ModelRole


class ResearchClock(PlayClock):
    async def call(self, **arguments):
        if arguments.get('op') in ('status', 'events'):
            reply = await runtime_file_read(self.bridge.call, 'home/supervised_play', **arguments)
            result = reply.structuredContent
            if not isinstance(result, dict) or result.get('success') is not True:
                raise ValueError('Native clock read was not confirmed')
            return result
        return await super().call(**arguments)


async def poll_research_clock(clock, report):
    try:
        await clock.poll()
    except BridgeError as error:
        if not all(part in error.detail.casefold() for part in (
                'failed to claim runtime ownership', 'a launch claim for',
                'was published while preparing this operation', 're-check games_status and retry')):
            raise
        await clock.bridge.core('games_status', gameId=clock.bridge.game_id)
        latest = await clock.call(op='status')
        # Observe before scheduling another heartbeat. Never replay start,
        # selection or game orders after a transport failure.
        if (not latest.get('active') or latest.get('owner') != clock.owner
                or latest.get('epoch') != clock.epoch or latest.get('leaseRemainingMs', 0) <= 0):
            raise
        report.setdefault('clock_claim_refusals', []).append(dict(error=str(error), observed=latest))
        clock.absorb(latest)


def fixture(root):
    path = root/'profile/Saves/RimBot-tribal8-baseline.rws'
    original = path.read_bytes()
    # The supplied baseline contains legacy non-UTF8 pawn names. Normalize only
    # this disposable profile; retain both hashes and the normalization choice.
    tree = ET.fromstring(original.decode('utf-8', errors='replace'))
    things = tree.find('.//maps/li/things')
    pawns = [p for p in things if p.findtext('def') == 'Human']
    pawn = pawns[0]
    faction = pawn.findtext('faction')
    occupied = {p.findtext('pos') for p in things if not p.findtext('def', '').startswith('Plant_')}
    position = next(f'({x}, 0, {z})' for z in range(118, 130) for x in range(138, 151)
                    if all(f'({a}, 0, {b})' not in occupied for a in range(x-1, x+2) for b in range(z-1, z+2)))
    x, _, z = map(int, position.strip('()').split(','))
    cleared = [p for p in things if p.findtext('def', '').startswith('Plant_')
               and p.findtext('pos') in {f'({a}, 0, {b})' for a in range(x-1,x+2) for b in range(z-1,z+2)}]
    for plant in cleared: things.remove(plant)
    bench = ET.SubElement(things, 'thing', Class='Building_ResearchBench')
    for key, value in dict(def_='SimpleResearchBench', id='SimpleResearchBench999999', map='0',
                           pos=position, rot='0', faction=faction, hitPoints='200', stuff='WoodLog').items():
        ET.SubElement(bench, 'def' if key == 'def_' else key).text = value
    food = [p for p in things if p.findtext('def') == 'Pemmican']
    for stack in food: stack.find('forbidden').text = 'False'
    template = food[0]
    for index in range(24):
        stack = deepcopy(template)
        stack.find('id').text = 'Pemmican' + str(1000000 + index)
        stack.find('pos').text = f'({138 + index % 6}, 0, {130 + index // 6})'
        things.append(stack)
    tree.find('.//storyteller/difficulty').text = 'Peaceful'
    ET.ElementTree(tree).write(path, encoding='utf-8', xml_declaration=True)
    return dict(source_sha256=hashlib.sha256(original).hexdigest(),
                fixture_sha256=hashlib.sha256(path.read_bytes()).hexdigest(), bench=position,
                changes=['Added one ordinary wood SimpleResearchBench', 'Normalized legacy UTF8 names',
                         'Allowed starting pemmican and added 24 matching native-size stacks', 'Selected native Peaceful preset'],
                research_progress_modified=False, fixture_plants_cleared=len(cleared))


async def run(args):
    root, output = args.source_root.resolve(), args.output.resolve()
    output.mkdir(parents=True, exist_ok=False)
    report = {'passed': False, 'samples': [], 'cases': []}
    def save(): (output/'result.json').write_text(json.dumps(report, indent=2), encoding='utf8')
    def check(name, passed, **evidence):
        report['cases'].append(dict(name=name, passed=bool(passed), **evidence)); save()
        print(name, bool(passed), flush=True)
        assert passed, name
    store = Store(output/'state.sqlite')
    rt = BridgeRuntime(store, root, headless=True)
    try:
        report['fixture'] = fixture(root); save()
        async with bridge_session(gabs_executable(root), prepare(root)) as bridge:
            rt.bridge = bridge; rt.game = BridgeGame(bridge)
            await bridge.core('games_start', gameId=bridge.game_id); await bridge.connect()
            await bridge.call('rimworld/load_game_ready', saveName='RimBot-tribal8-baseline',
                              readiness='visual', ignoreModCompatibility=True, timeoutMs=90000)
            await bridge.call('rimworld/set_time_speed', speed='Paused', ultraSpeedBoost=False)
            await rt.sync_identity(); rt.mode = 'automate'
            rt.batch = await observe(rt.game)
            rt.current_plan.colony_goals['EnsureInitialShelter'] = ColonyGoal(priority_class=2,
                evidence={'required_capabilities': ['Bed']})
            people = (await rt.game.query('home/list_pawns', colonistsOnly=True, bio=True, work=True, health=True))['pawns']
            researchers = [p for p in people if any(w.get('name') == 'Research' and w.get('disabled') is False
                                                  for w in (p.get('work') or {}).get('types', []))]
            check('eligible_researchers', researchers)
            researcher = max(researchers, key=lambda pawn: (next((s.get('level', 0) or 0
                for s in pawn.get('bio', {}).get('skills', []) if s['name'] == 'Intellectual'), 0), pawn['thingId']))
            report['assigned_researcher'] = researcher['thingId']
            for pawn in researchers:
                await rt.native('home/pawn_config', {'pawn': pawn['thingId'],
                    'work': 'Research=' + ('1' if pawn is researcher else '0'), 'dryRun': False})
            people = (await rt.game.query('home/list_pawns', colonistsOnly=True, bio=True, work=True, health=True))['pawns']
            facts = await rt.game.query('home/colony_facts', planning=True)
            check('bed_initially_locked', facts['definitions']['Bed']['available'] is False)
            await refresh(rt, facts, people, [('EnsureInitialShelter', 2)])
            goal = rt.current_plan.colony_goals['EnsureResearch']
            check('needs_driven_queue', goal.status == 'active', evidence=goal.evidence)
            name, actions = await method(rt)
            refused = False
            try:
                await rt.game.invoke('home/research', dict(actions[0]['arguments'], mapId=rt.identity['mapId']+1), allow_write=True)
            except (ValueError, BridgeError) as error: refused = 'guarded selection refused' in str(error)
            check('native_map_guard', refused)
            steps, _ = rt.controller.skills.steps('EnsureResearch', name, actions, facts)
            await rt.commit_strategy(CommitSteps(expected_revision=rt.current_plan.revision,
                reason='Native B20 research acceptance', steps=steps).decision(rt.current_plan),
                actor=ModelRole.STRATEGIST, expected_token=rt.context_token, expected_revision=rt.chat_revision)
            goal.steps.extend(s.id for s in steps)
            goal.evidence.setdefault('methods', {})[name] = [s.id for s in steps]
            await rt.hands.advance(rt)
            check('hands_selected', all(rt.current_plan.progress[s.id].state == 'complete' for s in steps),
                  progress={s.id:rt.current_plan.progress[s.id].model_dump() for s in steps})
            project = actions[0]['arguments']['set']
            before = await rt.game.invoke('home/research', {'dryRun': True, 'finished': True})
            check('selection_is_not_completion', before['current']['progress'] == 0 and project not in before['finished'])
            refused = False
            try:
                await rt.game.invoke('home/research', {'set': project, 'expectedCurrent': '', 'dryRun': False, 'watch': False, **{k: rt.identity[k] for k in ('colonyId','loadToken','mapId')}}, allow_write=True)
            except (ValueError, BridgeError) as error: refused = 'guarded selection refused' in str(error)
            check('native_current_guard', refused)
            clock = ResearchClock(bridge, test_acceleration=getattr(args, 'accelerated', False))
            deadline = time.monotonic() + args.timeout
            while time.monotonic() < deadline:
                clock.allow_resume()
                await clock.change('Superfast', max_ticks=12000)
                while True:
                    await asyncio.sleep(2)
                    await poll_research_clock(clock, report)
                    if not clock.state.get('active'): break
                snapshot = await rt.game.invoke('home/research', {'dryRun': True, 'finished': True})
                people_now = await rt.game.query('home/list_pawns', colonistsOnly=True, needs=True, health=True)
                report['samples'].append(dict(clock=clock.state, current=snapshot.get('current'), finished=snapshot['finished'],
                    researcher=next(p for p in people_now['pawns'] if p['thingId'] == report['assigned_researcher'])))
                save()
                print('research', (snapshot.get('current') or {}).get('progress'), clock.state.get('stopReason'), flush=True)
                if project in snapshot['finished']: break
                if clock.state.get('stopReason') not in ('tick_budget', 'letter_pause'):
                    raise AssertionError(clock.state)
            check('ordinary_research_completed', project in snapshot['finished'])
            unlocked = await rt.game.invoke('home/research', {'capability': 'ThingDef:Bed', 'dryRun': True})
            check('native_capability_unlocked', unlocked['capability']['researchReady'] is True, evidence=unlocked['capability'])
            facts = await rt.game.query('home/colony_facts', planning=True)
            check('dependent_construction_available', facts['definitions']['Bed']['available'] is True)
            await refresh(rt, facts, people, [('EnsureInitialShelter', 2)])
            check('goal_complete_after_unlock', goal.status == 'complete')
            report['passed'] = True
    finally:
        save()
        await rt.halt(); await rt.router.close(); store.close()


if __name__ == '__main__':
    parser = argparse.ArgumentParser()
    parser.add_argument('--accelerated', action='store_true', help='Use bounded supervised native Ultrafast boost')
    parser.add_argument('--source-root', type=Path, required=True)
    parser.add_argument('--output', type=Path, required=True)
    parser.add_argument('--timeout', type=int, default=1200)
    asyncio.run(run(parser.parse_args()))
