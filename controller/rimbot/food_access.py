"""Shared-stock coverage under native dietary/access constraints.

Fractional nutrition allocation is a planning estimate, not an eating simulation.
Never sum each pawn's overlapping view of the same stack.
"""
import math
from collections import deque


def runway(access):
    unknown = {'food_runway_days': None}
    if not isinstance(access, dict):
        return {**unknown, 'access_unavailable': 'Native diet/access observation unavailable'}
    consumers, pools = access.get('consumers'), access.get('pools')
    if not isinstance(consumers, list) or not isinstance(pools, list):
        return {**unknown, 'access_unavailable': 'Incomplete native food observation'}
    demand = {}
    def number(n):
        return type(n) in (int, float) and math.isfinite(n) and n >= 0
    for row in consumers:
        identity, amount = row.get('pawn_id'), row.get('nutrition_per_day')
        if type(identity) is not int or identity in demand or not number(amount):
            return {**unknown, 'access_unavailable': 'Invalid consumer observation'}
        demand[identity] = amount
    for pool in pools:
        ids = pool.get('pawn_ids')
        if (not isinstance(ids, list) or any(type(i) is not int or i not in demand for i in ids)
                or len(set(ids)) != len(ids) or not ids or not number(pool.get('nutrition'))):
            return {**unknown, 'access_unavailable': 'Invalid food pool observation'}
    total_demand = sum(demand.values())
    if not total_demand:
        return {**unknown, 'access_unavailable': 'No positive observed food demand'}

    def covers(days):
        source, sink = ('source',), ('sink',)
        edges = {}
        def edge(a, b, capacity):
            edges.setdefault(a, {})[b] = capacity
            edges.setdefault(b, {})[a] = 0.
        for i, pool in enumerate(pools):
            node = ('pool', i)
            edge(source, node, pool['nutrition'])
            for identity in pool['pawn_ids']:
                edge(node, ('pawn', identity), pool['nutrition'])
        for identity, amount in demand.items():
            edge(('pawn', identity), sink, amount * days)
        needed = total_demand * days
        while needed > 1e-8:
            previous = {source: None}; queue = deque([source])
            while queue and sink not in previous:
                a = queue.popleft()
                for b, capacity in edges.get(a, {}).items():
                    if capacity > 1e-10 and b not in previous:
                        previous[b] = a; queue.append(b)
            if sink not in previous:
                return False
            amount = needed; b = sink
            while previous[b] is not None:
                a = previous[b]; amount = min(amount, edges[a][b]); b = a
            b = sink
            while previous[b] is not None:
                a = previous[b]; edges[a][b] -= amount; edges[b][a] += amount; b = a
            needed -= amount
        return True

    nutrition = sum(p['nutrition'] for p in pools)
    low, high = 0., nutrition / total_demand
    for _ in range(40):
        middle = (low + high) / 2
        if covers(middle): low = middle
        else: high = middle
    return {'food_runway_days': round(low, 2), 'nutrition_per_day': round(total_demand, 3),
            'accessible_nutrition': round(nutrition, 3), 'consumers': len(demand),
            'access_observed_tick': access.get('observed_tick'),
            'runway_scope': 'Fractional loose-food coverage at native fed demand, respecting native diet, food policy, forbidden state and reachability at Danger.Some. Excludes inventories, future harvests, spoilage and meal-size waste. Does not guarantee safe travel, caregiver delivery or time until starvation.'}
