"""Compare read-only Hands shell validation on unchanged native state."""
import time
from types import SimpleNamespace
from rimgovernor.colony_plan import ColonyPlan, PlanSpec, StepProgress
from rimgovernor.hands import Hands
from rimgovernor.spatial import room_placements
from placement_preview_acceptance import facts as strip_transport


async def compare_shell_preflight(rt):
    assert rt.bridge.placement_preview_batch_version == 1
    facts = await rt.game.query('home/colony_facts', planning=True)
    assert all(facts['definitions'][name]['available'] for name in ('Wall', 'Door'))
    free = {(c['x'], c['z']) for c in facts['cells']
            if c.get('walkable') and not c.get('occupied') and not c.get('zone')}
    center = facts['center']
    sites = sorted(free, key=lambda p: ((p[0]-center['x'])**2+(p[1]-center['z'])**2, p))
    site = next(((x, z) for x, z in sites if
        {(a, b) for a in range(x, x+4) for b in range(z-1, z+5)} <= free), None)
    assert site is not None, 'No observed clear shell and entrance approaches'
    x, z = site
    spec = PlanSpec(steps=[dict(id='shell', title='Shell validation', completion_criteria='Native construction',
        action=dict(kind='build_room_shell', bounds=dict(x=x, z=z, width=4, height=4),
                    wall_def='Wall', door_def='Door', entrance='north', materials=['WoodLog']))])
    plan = ColonyPlan(spec=spec, progress={'shell': StepProgress()})
    before = await rt.game.query('home/status')
    buildings = strip_transport(await rt.game.query('home/list_buildings', playerOnly=True))
    assert before['time']['paused']
    report = dict(passed=False, site=dict(x=x, z=z), samples=[])
    calls = []
    async def query(name, **args):
        calls.append(name)
        return await rt.game.query(name, **args)
    async def inspect(name, args):
        calls.append(name)
        return await rt.inspect_native(name, args)
    async def refuse_write(*args, **kwargs):
        raise AssertionError('Read-only preflight attempted a write')
    view = SimpleNamespace(current_plan=plan, mode='automate', context_token=rt.context_token,
        chat_revision=rt.chat_revision, handled_revision=rt.chat_revision, batch=rt.batch,
        game=SimpleNamespace(bridge=rt.bridge, query=query), inspect_native=inspect, native=refuse_write)
    for repeat in range(2):
        expected = None
        for coalesce in ((False, True) if repeat == 0 else (True, False)):
            calls.clear()
            began = time.perf_counter()
            result = await Hands().preflight_shell(view, room_placements(spec.steps[0].action),
                plan.progress['shell'], plan.revision, view.context_token, view.chat_revision, coalesce=coalesce)
            seconds = time.perf_counter()-began
            assert len(result) == 12 and all(r.get('validated') is True for r in result)
            if expected is not None:
                assert result == expected, 'Shared shell reads changed validation or selected material'
            expected = result
            report['samples'].append(dict(repeat=repeat, coalesce=coalesce, seconds=seconds,
                calls=list(calls), result=result))
    after = await rt.game.query('home/status')
    assert after['time']['paused'] and after['time']['ticksGame'] == before['time']['ticksGame']
    assert buildings == strip_transport(await rt.game.query('home/list_buildings', playerOnly=True))
    assert not plan.progress['shell'].issued
    report['passed'] = True
    return report
