"""Furnish a completed player shelter using observed rooms and native footprints."""


def completed_shelters(plan):
    for identity,goal in sorted(plan.colony_goals.items()):
        request=goal.evidence.get('request',{})
        if (not goal.cancelled and goal.status=='complete' and request.get('kind')=='BuildRoom'
                and request.get('purpose','shelter')=='shelter'):
            yield identity,request['room']


async def verified_room(rt, shell):
    from .colony_skills import SkillBlocked
    bounds=shell['bounds'];x,z=bounds['x'],bounds['z']
    width,height=bounds['width'],bounds['height']
    interior={(a,b) for a in range(x+1,x+width-1) for b in range(z+1,z+height-1)}
    census=await rt.game.query('home/list_rooms',cells=True,x=x+1,z=z+1)
    if census.get('success') is not True:
        raise SkillBlocked('Shelter room observations are unavailable')
    rooms=[room for room in census.get('rooms',[]) if room.get('cellsComplete') is True
           and {(p['x'],p['z']) for p in room.get('cells',[])}==interior]
    if len(rooms)!=1 or rooms[0].get('properRoom') is not True:
        raise SkillBlocked('Completed shelter no longer matches an enclosed native room; inspect its shell')
    room=rooms[0]
    if type(room.get('openRoofCount')) is not int:
        raise SkillBlocked('Shelter roof coverage is unknown')
    if room['openRoofCount']:
        rt.current_plan.control['simulation_needed']=True
        return None
    if room.get('psychologicallyOutdoors') is not False:
        raise SkillBlocked('Shelter does not have verified indoor conditions')
    return room,interior


def entrance_aisle(shell,interior):
    bounds=shell['bounds']
    if shell['entrance'] in ('north','south'):
        return {(a,b) for a,b in interior if a==bounds['x']+bounds['width']//2}
    return {(a,b) for a,b in interior if b==bounds['z']+bounds['height']//2}


def safe_rotation(row):
    """Native blockingThings is a footprint census, not a list of refusals."""
    if row.get('accepted') is not True or not isinstance(row.get('blockingThings'),list):return False
    for thing in row['blockingThings']:
        if (thing.get('category') in (None,'Building') or thing.get('isBlueprint') is not False
                or thing.get('isFrame') is not False or thing.get('frameWouldBeCancelled') is not False
                or thing.get('wouldBeWiped') is not False):return False
    return True


async def sleeping_handoff(rt, facts, identity, shell, *, reserved_cells=()):
    from .colony_skills import SkillBlocked
    observed=await verified_room(rt,shell)
    if observed is None:return None
    room,interior=observed
    required=max(0,facts['colonists']-facts.get('indoorSleepingCapacity',0))
    if not required:return None
    # Keep a continuous central aisle from the requested entrance through the room.
    aisle=entrance_aisle(shell,interior)
    reserved=set(aisle)|set(reserved_cells);placements=[];attempts=0;rejections=[]
    for a,b in sorted(interior,key=lambda p:(p[1],p[0])):
        if (a,b) in reserved:continue
        for rotation in ('north','east'):
            if attempts>=128:break
            attempts+=1
            preview=await rt.inspect_native('home/place_building',{
                'defName':'SleepingSpot','x':a,'z':b,'rotation':rotation,'dryRun':True})
            rows=[r for r in preview.get('rotations',[]) if safe_rotation(r)]
            if preview.get('canPlace') is not True or len(rows)!=1:
                if len(rejections)<6:rejections.append(preview)
                continue
            footprint={(p['x'],p['z']) for p in rows[0].get('occupiedCells',[])}
            if not footprint or not footprint<=interior or footprint&reserved:continue
            placements.append({'def_name':'SleepingSpot','x':a,'z':b,'rotation':rotation})
            reserved|=footprint
            break
        if len(placements)==required:break
    if len(placements)<required:
        rt.current_plan.colony_goals['EnsureInitialShelter'].evidence['sleeping_fit']={
            'required':required,'selected':len(placements),'previews':attempts,'rejections':rejections}
        raise SkillBlocked('Shelter has insufficient verified sleeping space; expand or refine this room')
    if identity is not None:
        rt.current_plan.control['adopted_shelter']={'intent':identity,'bounds':shell['bounds'],'room_id':room['id']}
    method=('player-sleep-' if identity is not None else 'starter-sleep-')+str(facts['colonists'])
    return method,[{'kind':'place_buildings','placements':placements}]


def player_shelter(plan):
    for identity,goal in sorted(plan.colony_goals.items()):
        request=goal.evidence.get('request',{})
        if not goal.cancelled and request.get('kind')=='BuildRoom' and request.get('purpose','shelter')=='shelter':
            return identity,goal,request['room']
    return None


async def furniture_handoff(rt, selection, definition=None):
    """Place service furniture or food storage inside the player's chosen shelter."""
    from .colony_skills import SkillBlocked
    identity,goal,shell=selection
    if goal.status!='complete':
        rt.current_plan.control['simulation_needed']=True
        return None
    observed=await verified_room(rt,shell)
    if observed is None:return None
    room,interior=observed
    bounds=shell['bounds']
    buildings=await rt.game.query('home/list_buildings',x=bounds['x']+bounds['width']//2,
        z=bounds['z']+bounds['height']//2,radius=max(bounds['width'],bounds['height']),aggregate=False,playerOnly=False)
    if buildings.get('success') is not True or buildings.get('skipped',{}).get('byMaxDetailed'):
        raise SkillBlocked('Adopted-room building footprints are unavailable or truncated')
    reserved=entrance_aisle(shell,interior)
    for building in buildings.get('buildings',[]):
        position=building.get('position')
        if not position:raise SkillBlocked('Adopted-room building position is unreadable')
        rect=building.get('occupies')
        footprint={(a,b) for a in range(rect['minX'],rect['maxX']+1) for b in range(rect['minZ'],rect['maxZ']+1)} if rect else {
            (position['x'],position['z'])}
        reserved|=footprint
        if definition and building.get('defName')==definition and footprint<=interior and not building.get('isBlueprint') and not building.get('isFrame'):
            rt.current_plan.control['simulation_needed']=True
            return None
    attempts=0
    for a,b in sorted(interior,key=lambda p:(-p[1],-p[0]) if definition else (-p[1],p[0])):
        if attempts>=128:break
        if definition:
            if (a,b) in reserved:continue
            attempts+=1
            preview=await rt.inspect_native('home/place_building',{'defName':definition,'x':a,'z':b,'rotation':'north','dryRun':True})
            rows=[r for r in preview.get('rotations',[]) if safe_rotation(r)]
            if preview.get('canPlace') is not True or len(rows)!=1:continue
            footprint={(p['x'],p['z']) for p in rows[0].get('occupiedCells',[])}
            if footprint and footprint<=interior and not footprint&reserved:
                return [{'kind':'place_buildings','placements':[{'def_name':definition,'x':a,'z':b}]}]
        else:
            footprint={(x,z) for x in range(a,a+3) for z in range(b,b+3)}
            if not footprint<=interior or footprint&reserved:continue
            attempts+=1
            preview=await rt.inspect_native('home/zone_cells',{'op':'create','zoneType':'stockpile',
                'label':'RimBot food','cells':';'.join(f'{x},{z}' for x,z in sorted(footprint)),
                'preset':'food','priority':'Important','dryRun':True})
            if preview.get('cellsAccepted')!=9 or len(preview.get('cells',[]))!=9:continue
            if any(c.get('takenFrom') for c in preview['cells']):continue
            return [{'kind':'create_zone','zone_type':'stockpile','label':'RimBot food',
                     'patches':[{'x':a,'z':b,'width':3,'height':3}],'preset':'food','priority':'Important'}]
    raise SkillBlocked('No verified space for '+(definition or 'food storage')+' in the player shelter; refine or expand it')
