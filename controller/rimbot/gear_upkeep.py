"""Bounded native apparel improvements through the shared plan and Hands."""
from .strategic_state import fingerprint


def needs_upkeep(observation):
    if not isinstance(observation, dict) or observation.get('success') is not True:
        return True
    pawns = observation.get('pawns')
    if not isinstance(pawns, list) or not pawns:
        return True
    return any(type(p.get('deficit')) is not bool or p['deficit'] or not isinstance(p.get('candidates'), list)
               or p['candidates'] for p in pawns)


def compile_upkeep(goal, observation):
    from .colony_skills import SkillBlocked
    if not isinstance(observation, dict) or observation.get('success') is not True:
        raise SkillBlocked('Native gear observation unavailable; upkeep is unknown')
    goal.evidence['gear'] = observation
    choices = [(p, c) for p in observation.get('pawns', []) if not p.get('blocker')
               for c in p.get('candidates', [])]
    choices.sort(key=lambda pair: (-pair[1]['gain'], pair[0]['pawn'], pair[1]['target']))
    for pawn, candidate in choices:
        arguments = dict(pawn=pawn['pawn'], target=candidate['target'], expectedLoadout=pawn['loadout'], dryRun=False)
        method = 'wear-' + fingerprint(arguments)[:16]
        if not goal.method_seen(method):
            return method, [dict(kind='native_operation', tool='home/gear_upkeep', arguments=arguments,
                                 completion='pawn_gear')]
    raise SkillBlocked('No new eligible apparel replacement; inspect outfit, forced gear, access and production capacity')


async def compile_method(rt, facts):
    """Try existing gear first, then at most one ordinary bill for a worn garment."""
    from .colony_skills import SkillBlocked, native
    from .production_policy import ingredient_deficits
    goal = rt.current_plan.colony_goals['MaintainEquipment']
    observation = facts.get('gearUpkeep')
    try:
        return compile_upkeep(goal, observation)
    except SkillBlocked:
        if not observation or observation.get('success') is not True:
            raise
        if any(p.get('candidates') for p in observation.get('pawns', [])):
            raise  # Existing or previously attempted items precede further spending.
    needs = [(p, n) for p in observation['pawns'] if not p.get('blocker')
             for n in p.get('replacementNeeds', [])]
    listing = await rt.game.invoke('home/bills', dict(action='list', dryRun=True)) if needs else {}
    if needs and listing.get('success') is not True:
        raise SkillBlocked('Native workshop observation unavailable')
    for pawn, need in sorted(needs, key=lambda row: (row[0]['pawn'], row[1]['defName'])):
        method = 'produce-' + fingerprint(dict(pawn=pawn['pawn'], loadout=pawn['loadout'], need=need))[:16]
        if goal.method_seen(method):
            return None  # The bounded bill must yield an eligible item; the watchdog bounds failed labor.
        for bench in sorted(listing.get('benches', []), key=lambda b: b['thingId']):
            if any(b.get('active') is True and any(p.get('defName') == need['defName']
                    for p in b.get('products', [])) for b in bench.get('bills', [])):
                return None  # Preserve existing player production and its filters.
            recipes = await rt.game.invoke('home/bills', dict(action='recipes', bench=bench['thingId'], dryRun=True))
            for recipe in sorted(recipes.get('recipes') or [], key=lambda r: r['defName']):
                if not any(p.get('defName') == need['defName'] for p in recipe.get('products', [])):
                    continue
                if not recipe.get('availableNow') or not recipe.get('availableOnNow'):
                    continue
                costs = ingredient_deficits(recipe, facts.get('resources', {}))
                material = need.get('stuff')
                from .production_policy import production_budgets
                floors, stopped = production_budgets(rt.current_plan)
                available = {r: max(0, n-floors.get(r, 0)) for r, n in facts.get('resources', {}).items()}
                chosen = []
                for slot in costs:
                    # A stuff-bearing product retains its inspected fabric; other ingredient slots use native alternatives.
                    options = [c for c in slot if c['resource'] not in stopped
                        and available.get(c['resource'], 0) >= c['required']]
                    if material and any(c['resource'] == material for c in slot):
                        options = [c for c in options if c['resource'] == material]
                    if not options: break
                    choice = min(options, key=lambda c: (c['required'], c['resource']))
                    chosen.append(choice['resource'])
                    available[choice['resource']] -= choice['required']
                if len(chosen) != len(costs) or (material and material not in chosen): continue
                arguments = dict(action='add', bench=bench['thingId'], recipe=recipe['defName'],
                    repeatMode='RepeatCount', repeatCount=1, only=','.join(sorted(set(chosen))), watch=False)
                goal.evidence.setdefault('procurement', {})[method] = dict(pawn=pawn['pawn'], loadout=pawn['loadout'], need=need)
                goal.evidence['work_types'] = recipe.get('workTypes', [])
                return method, [native('home/bills', **arguments)]
    raise SkillBlocked('No eligible replacement or affordable native workshop recipe; procurement needs attention')
