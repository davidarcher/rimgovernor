"""Bounded additions on freshly observed free land; existing rooms/zones stay intact."""
from math import ceil
from .colony_policy import starter_layouts
from .strategic_state import fingerprint


def protected_cells(plan):
    cells={cell for rectangle in plan.spec.reserved_walkways for cell in rectangle.cells()}
    rooms=[plan.control.get('layout',{}).get('room')]
    rooms.extend(plan.control.get('shelter_expansions',[]))
    rooms.extend(step.action.bounds.model_dump() for step in plan.spec.steps if step.action.kind=='build_room_shell')
    for room in rooms:
        if room:
            cells.update((x,z) for x in range(room['x']-1,room['x']+room['width']+1)
                for z in range(room['z']-2,room['z']+room['height']+1))
    for step in plan.spec.steps:
        if step.action.kind=='create_zone':
            cells.update(cell for patch in step.action.patches for cell in patch.cells())
    return cells


def growth_fields(plan,facts,target_days=7):
    from .food_capacity import field_target,field_coverage
    rice=facts.get('definitions',{}).get(facts.get('foodCrop') or 'Plant_Rice',{})
    nutrition,days=rice.get('harvestNutrition'),rice.get('growDays')
    if not nutrition or not days:return []
    target=field_target(facts,target_days)
    if target is None:return []
    coverage=field_coverage(facts,target_days,cells='usableCells')
    if coverage is None:return []
    needed=max(0,ceil(target*(1-coverage)-1e-9))
    blocked=protected_cells(plan)
    cells={(c['x'],c['z']):c for c in facts.get('cells',[]) if c.get('walkable') is True
        and not c.get('occupied') and not c.get('zone') and not c.get('roofed')
        and c.get('fertility',0)>=rice.get('fertilityMin',1)}
    center=facts['center'];ordered=sorted(cells,key=lambda p:((p[0]-center['x'])**2+(p[1]-center['z'])**2,p))
    patches=[];selected=set()
    for size in (4,3,2,1):
        for x,z in ordered:
            if len(selected)>=needed or len(patches)>=32:return patches
            footprint={(a,b) for a in range(x,x+size) for b in range(z,z+size)}
            if footprint&(blocked|selected) or not footprint<=cells.keys():continue
            patches.append({'x':x,'z':z,'width':size,'height':size});selected|=footprint
    return patches


async def grow_shelter(skills,facts,goal_id='EnsureInitialShelter'):
    from .colony_skills import SkillBlocked
    from .shelter_handoff import sleeping_handoff
    from .colony_plan import RoomShell
    from .hands import room_placements
    rt=skills.rt;plan=rt.current_plan;goal=plan.colony_goals[goal_id]
    for room in plan.control.get('shelter_expansions',[]):
        key=fingerprint(room)[:8];shell=skills.shell({'room':room})
        if not goal.method_seen('expand-shell-'+key):return 'expand-shell-'+key,[shell]
        method='expand-sleep-'+key+'-'+str(facts['colonists'])+'-'+str(facts.get('indoorSleepingCapacity',0))
        if goal.method_seen(method):continue
        try:
            result=await sleeping_handoff(rt,facts,None,shell,allow_partial=True)
        except SkillBlocked as error:
            if 'insufficient verified sleeping space' not in str(error):raise
            continue
        return (method,result[1]) if result else None
    protected=protected_cells(plan)
    observed=dict(facts,cells=[dict(c,occupied=True) if (c['x'],c['z']) in protected else c for c in facts.get('cells',[])])
    from .spatial import room_entrance
    cells={(c['x'],c['z']):c for c in observed['cells']}
    candidates=[]
    for layout in starter_layouts(observed):
        (x,z),(dx,dz)=room_entrance(RoomShell.model_validate(skills.shell(layout)))
        if all(cells.get(p,{}).get('walkable') is True and not cells.get(p,{}).get('occupied')
               for p in ((x-dx,z-dz),(x+dx,z+dz))):
            candidates.append(layout)
    for layout in candidates[:rt.controller.policy.max_method_attempts]:
        shell=skills.shell(layout)
        for p in room_placements(RoomShell.model_validate(shell)):
            preview=await rt.inspect_native('home/place_building',dict(defName=p.def_name,x=p.x,z=p.z,
                rotation=p.rotation,stuff='WoodLog',dryRun=True))
            if preview.get('canPlace') is not True:break
        else:
            room=layout['room'];plan.control.setdefault('shelter_expansions',[]).append(room)
            return 'expand-shell-'+fingerprint(room)[:8],[shell]
    raise SkillBlocked('No verified free shelter expansion site in bounded native search')
