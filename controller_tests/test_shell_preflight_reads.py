import json
from types import SimpleNamespace
from unittest.mock import AsyncMock
import pytest
from rimgovernor.hands import Hands, Blocked
from rimgovernor.spatial import room_placements
from test_shell_site import runtime, census, zone
from test_construction_preflight import footprint


def batched_runtime():
    rt, state = runtime()
    rt.game.bridge = SimpleNamespace(placement_preview_batch_version=1)
    async def inspect(name, args):
        if name == 'home/placement_previews':
            return dict(success=True, version=1, results=[footprint(p, success=True, canPlace=True)
                for p in json.loads(args['placements'])])
        return footprint(args, success=True, canPlace=True)
    rt.inspect_native = AsyncMock(side_effect=inspect)
    return rt, state


async def preflight(rt, *, coalesce=True):
    return await Hands().preflight_shell(rt, room_placements(rt.current_plan.spec.steps[0].action),
        rt.current_plan.progress['room'], rt.current_plan.revision, rt.context_token, rt.chat_revision,
        coalesce=coalesce)


async def test_shared_shell_preflight_matches_individual_checks_with_fewer_reads():
    results, counts = [], []
    for coalesce in (False, True):
        rt, _ = batched_runtime()
        results.append(await preflight(rt, coalesce=coalesce))
        counts.append(rt.game.query.await_count + rt.inspect_native.await_count)
        rt.native.assert_not_awaited()
    assert results[0] == results[1] and len(results[0]) == 12
    assert counts == [48, 15]


async def test_shared_site_reads_do_not_survive_pass_or_authorize_a_write():
    rt, state = batched_runtime()
    await preflight(rt)
    state['zones'] = census(zone())
    with pytest.raises(Blocked) as rejected:
        await preflight(rt)
    assert rejected.value.failure.code == 'existing_zone'
    p = room_placements(rt.current_plan.spec.steps[0].action)[0]
    with pytest.raises(Blocked):
        await Hands().place(rt, p, rt.current_plan.progress['room'], '0',
                            rt.current_plan.revision, rt.context_token, rt.chat_revision)
    with pytest.raises(ValueError, match='cannot authorize writes'):
        await Hands().place(rt, p, rt.current_plan.progress['room'], '0',
                            rt.current_plan.revision, rt.context_token, rt.chat_revision, site_read=AsyncMock())
    rt.native.assert_not_awaited()


@pytest.mark.parametrize('change', ['direction', 'load', 'plan'])
async def test_invalidation_during_shared_pass_stops_before_write(change):
    rt, _ = batched_runtime()
    original = rt.game.query.side_effect
    count = 0
    async def query(name, **args):
        nonlocal count
        result = await original(name, **args)
        if name == 'home/list_buildings':
            count += 1
            if count == 2:
                if change == 'direction': rt.chat_revision += 1
                elif change == 'load': rt.context_token = 'new-load'
                else: rt.current_plan.revision += 1
        return result
    rt.game.query.side_effect = query
    with pytest.raises(InterruptedError):
        await preflight(rt)
    rt.native.assert_not_awaited()


async def test_final_spatial_preflight_uses_guarded_batch_but_write_preview_is_fresh():
    rt, _ = batched_runtime()
    await Hands().advance(rt, max_operations=1)
    assert rt.native.await_count == 1
    calls = [c.args[0] for c in rt.inspect_native.await_args_list]
    assert calls == ['home/placement_previews', 'home/place_building', 'home/placement_previews']
