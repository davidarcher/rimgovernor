"""Compile a chosen room envelope into native construction placements."""
from pydantic import Field
from .contracts import Contract, Action
from .spatial import cells, boundary, neighbors

class Entrance(Contract):
    x:int
    z:int

class Enclosure(Contract):
    region_id:str
    wall_def:str = Field(description='Observed native wall building definition')
    wall_material:str
    door_def:str = Field(description='Observed native door building definition')
    door_material:str
    entrances:list[Entrance] = Field(min_length=1,max_length=8)

async def compile_enclosure(rt,project,request):
    region=next((r for r in rt.memory.get('spatial_layout',{}).get('regions',[]) if r['id']==request.region_id and project['project_id'] in r['project_ids'] and r['purpose']=='room'),None)
    if region is None:raise ValueError('Choose a room reservation belonging to this project')
    points=cells(region);edge=boundary(points);inside=points-edge
    doors={(p.x,p.z) for p in request.entrances}
    if len(doors)!=len(request.entrances):raise ValueError('Duplicate entrance')
    for p in doors:
        if p not in edge or not any(n in inside for n in neighbors(p)) or not any(n not in points for n in neighbors(p)):
            raise ValueError('Entrance must connect the room interior to outside through its perimeter, not a corner')
    area=(await rt.api.call('construction_area',{'map_id':rt.observation['map']['id'],'center':rt.memory['colony_focus'],'radius':24})).model_dump()
    observed={(c['position']['x'],c['position']['z']):c for c in area['cells']}
    buildings=[]
    for x,z in sorted(edge):
        cell=observed.get((x,z))
        if cell is None:raise ValueError('Room includes unsurveyed cells')
        door=(x,z) in doors
        if cell['encloses']:
            if door and not cell['is_door']:raise ValueError(f'Entrance {(x,z)} is occupied by a wall or rock. Designate its removal and wait before compiling; no demolition is implicit.')
            continue # Reuse observed walls, rock and existing doors.
        buildings.append({'def_name':request.door_def if door else request.wall_def,'stuff_def_name':request.door_material if door else request.wall_material,'position':{'x':x,'z':z},'rotation':0})
    if not buildings:return None
    args={'map_id':rt.observation['map']['id'],'buildings':buildings}
    footprints=(await rt.api.call('construction_footprints',args)).items
    if len(footprints)!=len(buildings):raise ValueError('Native footprint count mismatch')
    for b,fp in zip(buildings,footprints):
        point=(b['position']['x'],b['position']['z'])
        if {(p.x,p.z) for p in fp.cells}!={point} or not fp.encloses or fp.is_door!=(point in doors):
            raise ValueError('Selected definitions must be single-cell enclosing walls and doors')
    inspection=await rt.api.call('construction_inspect',args)
    if not inspection.accepted:raise ValueError('Native placement rejected: '+'; '.join(i.reason for i in inspection.items if i.reason))
    state=await rt.api.call('construction_state',{'map_id':args['map_id']})
    return Action(title='Enclose '+region['label'],endpoint='construction_place',arguments=args,observation_basis=state.revision)
