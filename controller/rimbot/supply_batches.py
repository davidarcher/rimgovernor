"""Compact a retained set of supply cells without touching cells outside it."""


def supply_rectangles(targets):
    remaining = {(p['x'], p['z']) for p in targets}
    result = []
    while remaining:
        x, z = min(remaining, key=lambda cell: (cell[1], cell[0]))
        width = 1
        while (x+width, z) in remaining:
            width += 1
        height = 1
        while all((column, z+height) in remaining for column in range(x, x+width)):
            height += 1
        remaining.difference_update((column, row) for column in range(x, x+width) for row in range(z, z+height))
        result.append(dict(x=x, z=z, width=width, height=height))
    return result
