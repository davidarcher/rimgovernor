"""Native-grounded development through ordinary shared construction and work."""
from collections import deque
from .strategic_state import fingerprint
from .colony_skills import SkillBlocked, native


def development_nodes(facts):
    data = facts.get('development')
    if not isinstance(data, dict):
        return []
    count = facts['colonists']
    nodes = []
    if facts.get('comfortUpkeep') is None or facts['comfortUpkeep']:
        nodes.append(('EnsureComfort', 4))
    if facts.get('indoorSleepingCapacity', 0) <= count:
        nodes.append(('EnsureExpansion', 4))
    return nodes


async def placement(rt, facts, definition, *, indoors=None, near=None, radius=22, goal=None, rotations='North', avoid=()):
    from .shelter_handoff import safe_rotation
    definition_data = facts.get('definitions', {}).get(definition, {})
    if definition_data.get('available') is not True:
        if goal is not None and definition_data.get('available') is False:
            required = goal.evidence.setdefault('required_capabilities', [])
            if definition not in required:required.append(definition)
        raise SkillBlocked('Native construction prerequisite unavailable: '+definition)
    minimum = definition_data.get('constructionSkill', 0)
    if minimum:
        people = (await rt.game.query('home/list_pawns', colonistsOnly=True, bio=True, work=True))['pawns']
        capable = [p for p in people if not any(p.get(k) for k in ('dead','downed','drafted','mentalState'))
                   and any(s.get('name')=='Construction' and (s.get('level') or 0)>=minimum
                           and s.get('disabled') is not True for s in (p.get('bio') or {}).get('skills',[]))
                   and any(w.get('name')=='Construction' and w.get('disabled') is False and (w.get('priority') or 0)>0
                           for w in (p.get('work') or {}).get('types',[]))]
        if not capable:
            raise SkillBlocked(f'Native construction requires an available assigned level {minimum} builder: {definition}')
    center = near or facts['center']
    cells = {(c['x'], c['z']): c for c in facts['cells']}
    free = {p for p, c in cells.items() if c.get('walkable') is True and not c.get('occupied') and not c.get('zone')
            and (indoors is None or c.get('indoors') is indoors)}
    free -= set(avoid)
    from .shelter_handoff import entrance_aisle
    for step in rt.current_plan.spec.steps:
        if step.action.kind == 'build_room_shell':
            shell = step.action.model_dump()
            bounds = shell['bounds']
            interior = {(x,z) for x in range(bounds['x']+1,bounds['x']+bounds['width']-1)
                        for z in range(bounds['z']+1,bounds['z']+bounds['height']-1)}
            free -= entrance_aisle(shell, interior)
    candidates = sorted((p for p in free if max(abs(p[0]-center['x']), abs(p[1]-center['z'])) <= radius),
                        key=lambda p: ((p[0]-center['x'])**2+(p[1]-center['z'])**2, p))
    for x, z in candidates[:64]:
        args = dict(defName=definition, x=x, z=z, rotation=rotations, dryRun=True)
        if definition_data.get('stuff'):
            args['stuff'] = definition_data['stuff']
        preview = await rt.inspect_native('home/place_building', args)
        rows = [r for r in preview.get('rotations', []) if safe_rotation(r)]
        if preview.get('canPlace') is not True or (rotations != 'all' and len(rows) != 1):
            continue
        for row in rows[:4]:
            footprint = {(p['x'], p['z']) for p in row.get('occupiedCells', [])}
            rotation = row.get('rotation', 'north').lower()
            if footprint and footprint <= free and rotation in ('north', 'east', 'south', 'west'):
                value = dict(def_name=definition, x=x, z=z)
                if rotation != 'north': value['rotation'] = rotation
                if definition_data.get('stuff'):
                    value['materials'] = [definition_data['stuff']]
                return {'kind':'place_buildings', 'placements':[value]}
    raise SkillBlocked('No safe observed placement for '+definition+' in bounded native search')


async def power_method(rt, facts):
    data = facts.get('development')
    if not data or not isinstance(data.get('power'), list):
        raise SkillBlocked('Native electrical topology unavailable')
    rows = data['power']
    consumers = [b for b in rows if b['baseW'] < 0]
    producers = [b for b in rows if b['baseW'] > 0]
    target = next((c for c in consumers if not c['powered'] or c['net'] is None or
                   sum(p['outputW'] for p in rows if p['net'] == c['net']) < 0), None)
    if target is None:
        return None
    connected = [p for p in producers if p['net'] is not None and p['net'] == target['net']]
    if connected and sum(p['baseW'] for p in connected) >= -sum(p['baseW'] for p in consumers if p['net'] == target['net']):
        # A built fuel generator waits for ordinary hauling/refueling. Its output
        # and the consumer's PowerOn remain the completion evidence.
        return None
    if producers and not connected:
        cells = {(c['x'],c['z']) for c in facts['cells'] if c.get('supportsLight') is True}
        start = (target['x'],target['z'])
        destinations = {(p['x'],p['z']) for p in producers}
        queue, previous = deque([start]), {start: None}
        end = None
        while queue and len(previous) <= 2048:
            point = queue.popleft()
            if point in destinations:
                end = point
                break
            for nxt in ((point[0]+1,point[1]),(point[0]-1,point[1]),(point[0],point[1]+1),(point[0],point[1]-1)):
                if nxt in cells and nxt not in previous:
                    previous[nxt] = point
                    queue.append(nxt)
        if end is None:
            raise SkillBlocked('No observed electrical route to the existing generator')
        path = []
        while end is not None:
            path.append(end)
            end = previous[end]
        placements = []
        existing = {(b['x'],b['z']) for b in data['furniture'] if b['defName'] == 'PowerConduit'}
        # Existing generators transmit through their native occupied footprint.
        # Start the new conduit beside that footprint, not inside the producer.
        existing.update((cell['x'],cell['z']) for p in producers for cell in p.get('occupiedCells',[]))
        for x,z in path:
            if (x,z) in existing:
                continue
            preview = await rt.inspect_native('home/place_building',dict(defName='PowerConduit',x=x,z=z,dryRun=True))
            if preview.get('canPlace') is not True:
                raise SkillBlocked(f'Native electrical-route placement refused at {x},{z}: {preview.get("rotations")}')
            placements.append(dict(def_name='PowerConduit',x=x,z=z))
            if len(placements) == 8:
                break
        if placements:
            return 'connect-'+fingerprint(placements)[:12], [{'kind':'place_buildings','placements':placements}]
        return None
    action = await placement(rt,facts,'WoodFiredGenerator',near=target,radius=6,
                             goal=rt.current_plan.colony_goals.get('EnsureBasicPower'))
    return 'generate-'+fingerprint(action)[:12], [action]


async def development_method(skills, goal_id, facts, people):
    rt = skills.rt
    if goal_id == 'EnsureBasicPower':
        return await power_method(rt, facts)
    data = facts.get('development')
    if not data:
        raise SkillBlocked('Native development facts unavailable')
    if goal_id == 'EnsureExpansion':
        from .capacity_growth import grow_shelter
        return await grow_shelter(skills, dict(facts,colonists=facts['colonists']+1), goal_id=goal_id)
    if goal_id == 'EnsureComfort':
        from .comfort_upkeep import comfort_method
        return await comfort_method(rt, facts)
    raise SkillBlocked('Unknown development method')
