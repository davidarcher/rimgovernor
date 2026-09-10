"""Native site-search and material-preview equivalence without game writes."""
import time
from rimbot.development import placement
from rimbot.colony_skills import SkillBlocked
from rimbot.placement_previews import PlacementPreviews, PreviewCandidate
from placement_preview_acceptance import facts as strip_transport


async def compare_search_previews(rt):
    assert rt.bridge.placement_preview_batch_version == 1, 'Native placement batching unavailable'
    facts = await rt.game.query('home/colony_facts', planning=True)
    definition = facts['definitions']['SleepingSpot']
    assert definition['available'] and (definition['width'], definition['height']) == (1, 2)
    before = await rt.game.query('home/status')
    buildings = strip_transport(await rt.game.query('home/list_buildings', playerOnly=True))
    assert before['time']['paused']
    free = [c for c in facts['cells'] if c.get('walkable') and not c.get('occupied') and not c.get('zone')]
    assert len(free) >= 12, 'Insufficient observed candidate cells'
    material = facts['definitions']['Wall']['stuff']
    assert material and material != 'Steel'
    candidates = [PreviewCandidate('Wall', c['x'], c['z'], 'north', [material, 'Steel']) for c in free[:12]]
    avoid = {(c['x'], c['z']) for c in free if (c['x']+c['z']) % 2}
    report = dict(passed=False, samples=[])
    previous_setting = getattr(rt.game, 'batch_placement_previews', True)
    try:
        for scenario in ('first_site', 'excluded_footprints', 'material_alternatives'):
            for repeat in range(2):
                expected = None
                for batched in ((False, True) if repeat == 0 else (True, False)):
                    rt.game.batch_placement_previews = batched
                    started = time.perf_counter()
                    if scenario == 'material_alternatives':
                        previews = PlacementPreviews(rt.game, candidates)
                        result = [strip_transport(await previews.get(p, stuff))
                                  for stuff in (material, 'Steel') for p in candidates]
                    else:
                        try:
                            result = await placement(rt, facts, 'SleepingSpot',
                                avoid=avoid if scenario == 'excluded_footprints' else ())
                        except SkillBlocked as error:
                            result = dict(blocked=str(error))
                    seconds = time.perf_counter()-started
                    if expected is not None:
                        assert result == expected, 'Batch changed selected site, refusal, or material facts'
                    expected = result
                    if scenario == 'first_site':
                        assert 'blocked' not in result, 'No native first-site result'
                    if scenario == 'excluded_footprints':
                        assert 'blocked' in result, 'Excluded sleeping footprint was accepted'
                    report['samples'].append(dict(scenario=scenario, repeat=repeat, batched=batched, seconds=seconds, result=result))
        after = await rt.game.query('home/status')
        assert after['time']['paused'] and after['time']['ticksGame'] == before['time']['ticksGame']
        assert buildings == strip_transport(await rt.game.query('home/list_buildings', playerOnly=True))
        report['passed'] = True
        return report
    finally:
        rt.game.batch_placement_previews = previous_setting
