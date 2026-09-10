"""Native-grounded development through ordinary shared construction and work."""
from collections import deque
from .strategic_state import fingerprint
from .colony_skills import SkillBlocked, native


def development_nodes(facts):
    data = facts.get('development')
    if not isinstance(data, dict):
        return []
    furniture = data['furniture']
    count = facts['colonists']
    comfortable = sum(b['slots'] for b in furniture if b['defName'] == 'Bed' and b['indoors']) >= count
    comfortable &= all(any(b['defName'] == name and (b['indoors'] or name == 'HorseshoesPin')
                           for b in furniture) for name in ('Table1x2c', 'DiningChair', 'HorseshoesPin'))
    nodes = []
    if not comfortable:
        nodes.append(('EnsureComfort', 4))
    research = data['research']
    if research['current'] or research['available'] or not any(b['defName'] == 'SimpleResearchBench' for b in furniture):
        nodes.append(('EnsureResearch', 4))
    if facts.get('indoorSleepingCapacity', 0) <= count:
        nodes.append(('EnsureExpansion', 4))
    return nodes


async def placement(rt, facts, definition, *, indoors=None, near=None, radius=22):
    from .shelter_handoff import safe_rotation
    definition_data = facts.get('definitions', {}).get(definition, {})
    if definition_data.get('available') is not True:
        raise SkillBlocked('Native construction prerequisite unavailable: '+definition)
    center = near or facts['center']
    cells = {(c['x'], c['z']): c for c in facts['cells']}
    free = {p for p, c in cells.items() if c.get('walkable') is True and not c.get('occupied')
            and (indoors is None or c.get('indoors') is indoors)}
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
        args = dict(defName=definition, x=x, z=z, rotation='North', dryRun=True)
        if definition_data.get('stuff'):
            args['stuff'] = definition_data['stuff']
        preview = await rt.inspect_native('home/place_building', args)
        rows = [r for r in preview.get('rotations', []) if safe_rotation(r)]
        if preview.get('canPlace') is not True or len(rows) != 1:
            continue
        footprint = {(p['x'], p['z']) for p in rows[0].get('occupiedCells', [])}
        if footprint and footprint <= free:
            value = dict(def_name=definition, x=x, z=z)
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
        for x,z in path:
            if (x,z) in existing:
                continue
            preview = await rt.inspect_native('home/place_building',dict(defName='PowerConduit',x=x,z=z,dryRun=True))
            if preview.get('canPlace') is not True:
                raise SkillBlocked('Native electrical-route placement refused; existing orders preserved')
            placements.append(dict(def_name='PowerConduit',x=x,z=z))
            if len(placements) == 8:
                break
        if placements:
            return 'connect-'+fingerprint(placements)[:12], [{'kind':'place_buildings','placements':placements}]
        return None
    action = await placement(rt,facts,'WoodFiredGenerator',near=target,radius=6)
    return 'generate-'+fingerprint(action)[:12], [action]


async def development_method(skills, goal_id, facts, people):
    rt = skills.rt
    goal = rt.current_plan.colony_goals[goal_id]
    if goal_id == 'EnsureBasicPower':
        return await power_method(rt, facts)
    data = facts.get('development')
    if not data:
        raise SkillBlocked('Native development facts unavailable')
    furniture = data['furniture']
    if goal_id == 'EnsureResearch':
        research = data['research']
        if not any(b['defName'] == 'SimpleResearchBench' for b in furniture):
            action = await placement(rt, facts, 'SimpleResearchBench', indoors=True)
            return 'research-bench-'+fingerprint(action)[:8], [action]
        if research['current']:
            goal.evidence['research'] = research
            return None
        candidates = sorted(research['available'], key=lambda p:(p['cost'],p['defName']))
        if not candidates:
            raise SkillBlocked('No native available research project; prerequisites required')
        requested = goal.target.get('project')
        prerequisites = facts.get('definitions',{}).get('WoodFiredGenerator',{}).get('researchPrerequisites',[])
        preferred = [p for p in candidates if p['defName'] == requested or not requested and p['defName'] in prerequisites]
        if requested in research['finished']:
            return None
        if requested and not preferred:
            projects = {p['defName']:p for p in research.get('projects', [])}
            ancestors, pending = set(), [requested]
            while pending:
                name = pending.pop()
                if name in ancestors or name in research['finished']:
                    continue
                ancestors.add(name)
                pending.extend(projects.get(name, {}).get('prerequisites', []))
            preferred = [p for p in candidates if p['defName'] in ancestors]
            if not preferred:
                raise SkillBlocked('Requested native research prerequisites unavailable: '+requested)
        selected = (preferred or candidates)[0]['defName']
        goal.evidence['research'] = research
        return 'research-'+selected, [native('home/research', set=selected)]
    if goal_id == 'EnsureExpansion':
        from .capacity_growth import grow_shelter
        return await grow_shelter(skills, dict(facts,colonists=facts['colonists']+1), goal_id=goal_id)
    if goal_id == 'EnsureComfort':
        beds = sum(b['slots'] for b in furniture if b['defName']=='Bed' and b['indoors'])
        definition = 'Bed' if beds < facts['colonists'] else next((name for name in
            ('Table1x2c','DiningChair','HorseshoesPin') if not any(b['defName']==name and
                (b['indoors'] or name=='HorseshoesPin') for b in furniture)), None)
        if not definition:
            return None
        action = await placement(rt, facts, definition, indoors=definition!='HorseshoesPin')
        return 'comfort-'+fingerprint(action)[:12], [action]
    raise SkillBlocked('Unknown development method')
