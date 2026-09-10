from itertools import combinations

from rimgovernor.supply_batches import supply_rectangles


def test_every_small_irregular_subset_preserves_exact_scope_without_overlap():
    grid = [(x, z) for x in range(3) for z in range(3)]
    for size in range(9):
        for subset in combinations(grid, size):
            targets = [dict(x=x, z=z) for x, z in subset]
            rectangles = supply_rectangles(targets)
            cells = [(x, z) for rect in rectangles
                     for x in range(rect['x'], rect['x']+rect['width'])
                     for z in range(rect['z'], rect['z']+rect['height'])]
            assert len(cells) == len(set(cells))
            assert set(cells) == set(subset)
            assert rectangles == supply_rectangles(list(reversed(targets)))


def test_contiguous_stacks_batch_and_duplicate_cells_are_not_reissued():
    targets = [dict(x=x, z=z) for x in range(4) for z in range(2)]
    assert supply_rectangles(targets + targets) == [dict(x=0, z=0, width=4, height=2)]


def test_separated_stacks_never_allow_intervening_player_cells():
    assert supply_rectangles([dict(x=3, z=8), dict(x=5, z=8)]) == [
        dict(x=3, z=8, width=1, height=1), dict(x=5, z=8, width=1, height=1)]
