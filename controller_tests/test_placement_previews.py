from types import SimpleNamespace
import json
from unittest.mock import AsyncMock
import pytest
from rimbot.placement_previews import PlacementPreviews


def placements(count=20):
    return [SimpleNamespace(def_name='Wall', x=i, z=2, rotation='north', materials=['WoodLog', 'Steel'])
            for i in range(count)]


async def test_bounded_prefetch_preserves_order_and_material_fallback():
    async def invoke(tool, args, allow_write):
        assert allow_write is False
        if tool == 'home/placement_previews':
            candidates = json.loads(args['placements'])
            assert len(candidates) <= 16
            return dict(success=True, version=1, results=[dict(success=True, canPlace=p['x'] != 0, x=p['x']) for p in candidates])
        assert args['dryRun'] is True and args['stuff'] == 'Steel'
        return dict(success=True, canPlace=True, x=args['x'])
    game = SimpleNamespace(bridge=SimpleNamespace(placement_preview_batch_version=1), invoke=AsyncMock(side_effect=invoke))
    rows = placements()
    cache = PlacementPreviews(game, rows)
    assert (await cache.get(rows[0], 'WoodLog'))['canPlace'] is False
    assert (await cache.get(rows[0], 'Steel'))['canPlace'] is True
    for row in rows[1:]:
        assert (await cache.get(row, 'WoodLog'))['x'] == row.x
    assert [c.args[0] for c in game.invoke.await_args_list] == [
        'home/placement_previews', 'home/place_building', 'home/placement_previews']


async def test_no_cross_review_cache_and_old_companion_fallback():
    game = SimpleNamespace(invoke=AsyncMock(return_value={'success': True, 'canPlace': True}))
    row = placements(1)[0]
    for _ in range(2):
        cache = PlacementPreviews(game, [row])
        await cache.get(row, 'WoodLog')
        await cache.get(row, 'WoodLog')
    assert game.invoke.await_count == 2
    assert all(c.args[0] == 'home/place_building' for c in game.invoke.await_args_list)


@pytest.mark.parametrize('rows', [[], [None], [{'success': False, 'error': 'invalid definition'}]])
async def test_malformed_and_failed_batches_do_not_silently_fallback(rows):
    game = SimpleNamespace(bridge=SimpleNamespace(placement_preview_batch_version=1),
        invoke=AsyncMock(return_value=dict(success=True, version=1, results=rows)))
    row = placements(1)[0]
    with pytest.raises(ValueError):
        await PlacementPreviews(game, [row]).get(row, 'WoodLog')
    assert game.invoke.await_count == 1
