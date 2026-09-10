"""Compare native placement previews without issuing construction or advancing time."""
import time
import json
from rimgovernor.bridge import BridgeError


def facts(payload):
    return {k: v for k, v in payload.items() if k not in ('operation', 'unknownArguments', 'unknownArgumentsWarning')}


async def compare_placement_previews(rt):
    assert rt.bridge.placement_preview_batch_version == 1, 'Native batch capability missing'
    pawn = rt.batch.summary.pawns[0]
    x, z = pawn.position.x, pawn.position.z
    candidates = [dict(defName='Wall', x=x+i%4, z=z+3+i//4, rotation='north', stuff='WoodLog') for i in range(10)]
    candidates += [dict(defName=name, x=x+i, z=z+7, rotation='north', stuff=material)
                   for i, (name, material) in enumerate([('Door','WoodLog'), ('Door','Steel'),
                                                        ('SleepingSpot',''), ('SleepingSpot','')])]
    candidates += [dict(defName='Wall', x=-1, z=-1, rotation='north', stuff='WoodLog'),
                   dict(defName='RimGovernorMissingDefinitionForAcceptance', x=x, z=z, rotation='north', stuff='')]
    arguments = dict(placements=json.dumps(candidates))
    before = await rt.game.query('home/status')
    buildings = facts(await rt.game.query('home/list_buildings', playerOnly=True))
    assert before['time']['paused']
    report = dict(passed=False, candidates=candidates, samples=[])
    for repeat in range(4):
        expected = None
        for batched in ((False, True) if repeat % 2 == 0 else (True, False)):
            began = time.perf_counter()
            if batched:
                response = await rt.game.invoke('home/placement_previews', arguments)
                rows = response['results']
            else:
                rows = []
                for candidate in candidates:
                    try:
                        rows.append(await rt.game.invoke('home/place_building', dict(candidate, dryRun=True)))
                    except BridgeError as error:
                        rows.append(error.result.structuredContent)
            elapsed = time.perf_counter()-began
            normalized = [facts(row) for row in rows]
            assert len(normalized) == 16
            if expected is not None:
                assert normalized == expected, 'Batch changed native placement verdicts or geometry'
            expected = normalized
            report['samples'].append(dict(repeat=repeat, batched=batched, seconds=elapsed, results=normalized,
                                           timing=response.get('timing') if batched else None))
    for bad in (dict(placements=json.dumps([dict(candidates[0], dryRun=False)])),
                dict(placements=json.dumps(candidates+[candidates[0]])),
                dict(placements=json.dumps([dict(candidates[0], x=1.5)]))):
        try:
            await rt.bridge.call('home/placement_previews', **bad)
        except BridgeError:
            pass
        else:
            raise AssertionError('Malformed or oversized batch was accepted')
    after = await rt.game.query('home/status')
    assert after['time']['paused'] and before['time']['ticksGame'] == after['time']['ticksGame']
    assert buildings == facts(await rt.game.query('home/list_buildings', playerOnly=True)), 'Preview changed construction'
    report['passed'] = True
    return report
