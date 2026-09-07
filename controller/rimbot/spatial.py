import re
"""Visual site planning and controller-owned reservations over native map facts."""
import io
from PIL import Image, ImageDraw

SPATIAL_KINDS={'construction','growing','storage'}
PALETTE=['#58b7e8','#8bd66b','#e5ad61','#bd8bdf','#e87991','#5ad4c8']

def cells(region):
    result=set()
    for patch in region['patches']:
        if not 0<=patch['x2']-patch['x1']<=64 or not 0<=patch['z2']-patch['z1']<=64:raise ValueError('Invalid spatial patch bounds')
        run={(x,z) for z in range(patch['z1'],patch['z2']+1) for x in range(patch['x1'],patch['x2']+1)}
        if result & run:raise ValueError('Duplicate cells in region patches')
        result.update(run)
    return result

def boundary(points):
    return {p for p in points if any(q not in points for q in neighbors(p))}

def neighbors(p):
    x,z=p
    return ((x-1,z),(x+1,z),(x,z-1),(x,z+1))

def validate_layout(layout,area,projects,previous=()):
    observed={(c['position']['x'],c['position']['z']):c for c in area['cells']}
    ids={p['project_id'] for p in projects};seen=set();claimed=set();region_ids=set()
    kinds={p['project_id']:p.get('kind') for p in projects}
    by_id={r['id']:r for r in layout['regions']}
    for r in layout['regions']:
        parent=by_id.get(r.get('parent_id',''))
        if r.get('parent_id') and (parent is None or parent.get('parent_id') or parent['purpose']!='room' or r['purpose'] not in ('storage','path') or not cells(r)<=cells(parent)-boundary(cells(parent))):raise ValueError('Nested reservations must be storage/access wholly inside a room')
        if r['id'] in region_ids:raise ValueError('Duplicate region ID')
        region_ids.add(r['id'])
        if not set(r['project_ids'])<=ids:raise ValueError('Unknown project in layout')
        for project_id in r['project_ids']:
            kind=kinds[project_id]
            if kind=='growing' and r['purpose']!='farm':raise ValueError('Growing project requires a farm region')
            if kind=='storage' and r['purpose'] not in ('storage','room'):raise ValueError('Stockpile project requires storage land or a shared room')
            if kind=='construction' and r['purpose'] in ('farm','path'):raise ValueError('Construction cannot reserve crop or access land')
        points=cells(r)
        if not points or not points<=observed.keys():
            missing=sorted(points-observed.keys())
            bounds={'x_min':min(x for x,z in observed),'x_max':max(x for x,z in observed),'z_min':min(z for x,z in observed),'z_max':max(z for x,z in observed)}
            raise ValueError(f'Region {r["id"]} includes unexplored or unsurveyed cells: {missing[:8]}. Survey bounds: {bounds}. Correct THIS region; cells absent inside those bounds are also unknown. Other valid regions need not move.')
        for old in layout['regions']:
            if old['id']==r['id'] or not cells(old)&points:continue
            if r.get('parent_id')!=old['id'] and old.get('parent_id')!=r['id']:
                overlap=sorted(cells(old)&points)
                if r['purpose']==old['purpose']=='room':
                    raise ValueError(f"Spatial regions overlap: rooms {r['id']} and {old['id']}. If these projects use the same room, keep ONE room region containing both project_ids and remove the redundant room. Otherwise move their coordinates apart. Do not reserve furniture as a second overlapping room.")
                raise ValueError(f"Spatial regions overlap: {r['id']} ({r['purpose']}) and {old['id']} ({old['purpose']}) share {len(overlap)} cells, including {overlap[:8]}. Change patch coordinates so incompatible uses have ZERO shared cells. A farm cannot share or nest inside a room. Renaming or changing project_ids does not fix geometry. Storage/access may explicitly nest inside a room using parent_id.")
        seen |= points;claimed.update(r['project_ids'])
        visited={next(iter(points))};pending=list(visited)
        while pending:
            for n in neighbors(pending.pop()):
                if n in points and n not in visited:visited.add(n);pending.append(n)
        if visited!=points:raise ValueError('Region must be connected; use separate regions for separate patches')
        if r['purpose']=='room' and not points-boundary(points):
            width=max(x for x,z in points)-min(x for x,z in points)+1
            height=max(z for x,z in points)-min(z for x,z in points)+1
            raise ValueError(f"Room footprint has no interior: {r['id']} has actual outer bounds {width}x{height}, {len(points)} cells. Your rationale does not change these coordinates. For a rectangular room choose x1,z1 and set x2=x1+outer_width-1, z2=z1+outer_height-1, with space for walls and furniture in BOTH axes. Submit the filled rectangle, not a wall strip.")
        for p in points:
            c=observed[p]
            if c['zone_id'] is not None:
                expected={'farm':'Zone_Growing','storage':'Zone_Stockpile','room':'Zone_Stockpile'}.get(r['purpose'])
                shared_storage=r['purpose']=='room' and c['zone_type']=='Zone_Stockpile' and p not in boundary(points) and any(kinds[i]=='storage' for i in r['project_ids'])
                if not shared_storage and (c['zone_id'] not in r['reuse_zone_ids'] or c['zone_type']!=expected):
                    raise ValueError(f"{r['label']} overlaps existing {c['zone_type']} {c['zone_id']} at {p}")
            if r['purpose']=='farm' and (not c['plantable'] or c['fertility']<r['fertility_floor']):raise ValueError(f"Farm {r['id']} includes unsuitable terrain at {p}: fertility={c['fertility']}, plantable={c['plantable']}; exclude this cell from its patches")
    if set(layout['deferred'])-ids:raise ValueError('Unknown deferred project')
    if claimed & set(layout['deferred']):raise ValueError('Projects cannot be both reserved and deferred: '+', '.join(sorted(claimed & set(layout['deferred']))))
    if claimed|set(layout['deferred'])!=ids:raise ValueError('Missing site or deferral for project IDs: '+', '.join(sorted(ids-(claimed|set(layout['deferred'])))))
    for old in previous:
        new=next((r for r in layout['regions'] if r['id']==old['id']),None)
        if new is None or cells(new)!=cells(old) or new['purpose']!=old['purpose']:
            raise ValueError('Preserve existing reservations; changing an established site needs explicit replanning')


def display_label(label):
    label=re.sub(r'^RimBot\s*:\s*', '', label, flags=re.I)
    return re.sub(r'\s+for\s+project\s+\S+\s*$', '', label, flags=re.I).strip()


def render_map(area,regions=(),colors=None):
    points={(c['position']['x'],c['position']['z']):c for c in area['cells']}
    if not points:raise ValueError('No explored map cells')
    lo_x=min(x for x,z in points);hi_x=max(x for x,z in points);lo_z=min(z for x,z in points);hi_z=max(z for x,z in points)
    scale=16;margin=40
    image=Image.new('RGB',((hi_x-lo_x+1)*scale+margin*2,(hi_z-lo_z+1)*scale+margin*2+len(regions)*18),(25,29,34));d=ImageDraw.Draw(image)
    def box(x,z):
        a=margin+(x-lo_x)*scale;b=margin+(hi_z-z)*scale
        return a,b,a+scale-1,b+scale-1
    for (x,z),c in points.items():
        f=c['fertility'];color='#467735' if f>1 else '#5b6344' if f>=.7 else '#837459' if f>0 else '#454a50'
        if not c['walkable']:color='#27292c'
        d.rectangle(box(x,z),fill=color,outline='#353c3e')
        a,b,_,_=box(x,z)
        if c['roofed']:d.line((a,b,a+5,b),fill='#e1e1df',width=2)
        if c['zone_id'] is not None:d.rectangle((a+3,b+3,a+12,b+12),outline='#f5cb64')
        if c['thing_ids']:d.point((a+8,b+8),fill='white')
    for x in range(lo_x,hi_x+1):
        if x%5==0:d.text((box(x,lo_z)[0],18),str(x),fill='white')
    for z in range(lo_z,hi_z+1):
        if z%5==0:d.text((3,box(lo_x,z)[1]),str(z),fill='white')
    for i,r in enumerate(regions):
        color=(colors or {}).get(r["id"],PALETTE[i%len(PALETTE)])
        points=cells(r)
        for x,z in boundary(points):d.rectangle(box(x,z),outline=color,width=3)
        if points and r['purpose']!='path':
            x=sum(p[0] for p in points)//len(points);z=sum(p[1] for p in points)//len(points);a,b,_,_=box(x,z)
            label=display_label(r['label']);bounds=d.textbbox((a,b),label)
            d.rectangle(bounds,fill='#191d22');d.text((a,b),label,fill=color)
            direction=r.get('expansion_direction')
            if direction:
                dx,dz={'north':(0,-1),'south':(0,1),'east':(1,0),'west':(-1,0)}[direction]
                end=(a+dx*24,b+18+dz*24);d.line((a,b+18,*end),fill=color,width=2)
                d.ellipse((end[0]-3,end[1]-3,end[0]+3,end[1]+3),fill=color)
        d.text((margin,margin+(hi_z-lo_z+1)*scale+8+i*18),display_label(r['label']),fill=color)
    out=io.BytesIO();image.save(out,format='PNG');return out.getvalue()


def survey_runs(area):
    # Lossless horizontal runs: compact transport, not guessed or averaged terrain.
    groups=[]
    for c in sorted(area['cells'],key=lambda c:(c['position']['z'],c['position']['x'])):
        x,z=c['position']['x'],c['position']['z']
        facts={k:c[k] for k in ('terrain_def','fertility','roofed','walkable','zone_id','zone_type','plantable','encloses')}
        if groups and groups[-1]['z']==z and groups[-1]['x2']==x-1 and groups[-1]['facts']==facts:groups[-1]['x2']=x
        else:groups.append({'z':z,'x1':x,'x2':x,'facts':facts})
    return groups

def retain_valid_regions(layout,area,projects,previous=()):
    """Keep independently valid model-selected sites; defer the rest without moving them."""
    kept=[];reasons={}
    for region in layout.get('regions',[]):
        trial=kept+[region]
        claimed={i for r in trial for i in r['project_ids']}
        relevant=[p for p in projects if p['project_id'] in claimed]
        try:
            validate_layout({'regions':trial,'deferred':{}},area,relevant,
                            [r for r in previous if set(r['project_ids']) & claimed])
        except ValueError as error:
            for project_id in region['project_ids']:reasons[project_id]=str(error)
        else:kept.append(region)
    claimed={i for r in kept for i in r['project_ids']}
    deferred={p['project_id']:reasons.get(p['project_id'],layout.get('deferred',{}).get(p['project_id'],'No valid site selected')) for p in projects if p['project_id'] not in claimed}
    result={**layout,'regions':kept,'deferred':deferred,'summary':'Reserved valid sites; remaining projects need a new site.'}
    validate_layout(result,area,projects,previous)
    return result


async def prepare_layout(rt,context,projects):
    from .base_plan import prepare_master_plan
    if not any(p['kind'] in SPATIAL_KINDS and p.get('status')!='retired' for p in projects):return
    await prepare_master_plan(rt,context,projects)

async def validate_orders(rt,project,actions,complete=True):
    """Validate full native footprints, at drafting and again immediately before issuing."""
    if project.get('kind') in SPATIAL_KINDS:
        for action in actions:
            if action.endpoint!='post_order_designate_area':continue
            points=zone_cells(action)
            regions=rt.memory.get('spatial_layout',{}).get('regions',[])
            own=[r for r in regions if project['project_id'] in r['project_ids']]
            permitted=set().union(*(cells(r) for r in own)) if own else set()
            if not points<=permitted:
                raise ValueError('Area designation leaves this project site. Do not clear or deconstruct another project; request an ownership/scope review if broader changes are needed.')
            from .base_plan import validate_reserved_space
            validate_reserved_space(rt,project,points)
    spatial=[a for a in actions if a.endpoint=='construction_place' or a.endpoint in ('zone_growing_cells','post_map_zone_growing','post_map_zone_stockpile')]
    if not spatial:return
    if project.get('kind') in ('growing','storage'):
        await validate_zone_orders(rt,project,spatial)
        return
    layout=rt.memory.get('spatial_layout',{})
    own=[r for r in layout.get('regions',[]) if project['project_id'] in r['project_ids']]
    if not own:raise ValueError('No reserved site for this project. '+layout.get('deferred',{}).get(project['project_id'],''))
    allowed=set().union(*(cells(r) for r in own));lookup={p:r for r in own for p in cells(r)}
    enclosed=set();doors=set();solid=set();wall_order=False
    for action in spatial:
        if action.endpoint=='construction_place':
            footprints=(await rt.api.call('construction_footprints',action.arguments)).items
            for fp in footprints:
                occupied={(p.x,p.z) for p in fp.cells}
                from .base_plan import validate_reserved_space
                validate_reserved_space(rt,project,occupied)
                if not occupied<=allowed:raise ValueError('Building footprint leaves its shared reservation')
                if any(lookup[p]['purpose'] in ('farm','path') for p in occupied):raise ValueError('Building overlaps reserved crops or access')
                if fp.is_bed and any(p in boundary(cells(lookup[p])) and lookup[p]['purpose']=='room' for p in occupied):
                    interior=set().union(*(cells(r)-boundary(cells(r)) for r in own if r['purpose']=='room'))
                    raise ValueError(f'Bed occupies the planned room perimeter. Native occupied cells: {sorted(occupied)}. Available reserved interior cells: {sorted(interior)[:100]}. The entire bed footprint must fit inside; change its position or rotation using the native footprint result. If it cannot fit, report that the reserved room needs replanning instead of repeating inspections.')
                if fp.encloses:enclosed|=occupied;wall_order=True
                if fp.is_door:
                    if occupied & solid:raise ValueError('Conflicting construction: a wall and door occupy the same cells. Keep the entrance as a door in every order.')
                    doors|=occupied
                elif fp.encloses:
                    if occupied & doors:raise ValueError('Conflicting construction: a wall overwrites the planned door. Keep the entrance as a door in every order.')
                    solid|=occupied
        else:
            args=action.arguments
            if 'cells' in args:occupied={(c['x'],c['z']) for c in args['cells']}
            else:
                a,b=args['point_a'],args['point_b'];occupied={(x,z) for x in range(min(a['x'],b['x']),max(a['x'],b['x'])+1) for z in range(min(a['z'],b['z']),max(a['z'],b['z'])+1)}
            if not occupied<=allowed:raise ValueError('Zone leaves its reserved footprint; use exact cells for irregular fields')
            purpose='storage' if action.endpoint=='post_map_zone_stockpile' else 'farm'
            for p in occupied:
                r=lookup[p]
                shared_storage=purpose=='storage' and r['purpose']=='room' and p not in boundary(cells(r))
                if r['purpose']!=purpose and not shared_storage:raise ValueError('Zone conflicts with reserved land use')
    # Refresh native zones after planning; player edits never silently lose to reservations.
    from .base_plan import survey_master_area
    area=await survey_master_area(rt)
    observed={(c['position']['x'],c['position']['z']):c for c in area['cells']}
    for p in allowed:
        c=observed.get(p)
        if c is None:raise ValueError('Reserved site is no longer surveyed')
        if c['zone_id'] is not None and c['zone_id'] not in lookup[p]['reuse_zone_ids']:
            # A zone already created by this project can be observed without authorizing construction over it.
            if lookup[p]['purpose'] not in ('farm','storage'):
                child_storage=any(child.get('parent_id')==lookup[p]['id'] and child['purpose']=='storage' and p in cells(child) for child in layout['regions'])
                shared_storage=any(pj.get('kind')=='storage' and pj['project_id'] in lookup[p]['project_ids'] and pj.get('status')!='retired' for pj in rt.memory.get('projects',[])) and p not in boundary(cells(lookup[p]))
                if not ((child_storage or shared_storage) and c['zone_type']=='Zone_Stockpile'):raise ValueError('Player zone now overlaps construction reservation')
    if wall_order and complete:
        existing={p for p,c in observed.items() if c['encloses']}
        for r in own:
            if r['purpose']!='room' or not enclosed & cells(r):continue
            missing=boundary(cells(r))-(existing|enclosed)
            if missing:raise ValueError(f'Incomplete room perimeter: {len(missing)} cells missing. Submit the complete boundary with a door in one batch, not just corners.')
            if not (doors & boundary(cells(r))) and not any(observed[p].get('is_door',False) and p not in solid for p in boundary(cells(r))):raise ValueError('Room enclosure needs a door on its own perimeter; an interior door or a door being replaced by a wall does not provide an entrance')


async def show_native_plans(rt,layout):
    """Recover receipts by exact content; never overwrite player planning marks."""
    state=await rt.api.call('planning_state',{'map_id':rt.observation['map']['id']})
    marks=layout.setdefault('native_plans',{})
    colors=layout.setdefault('colors',{})
    for index,r in enumerate(layout['regions']):
        rt.check_generation()
        if rt.mode!='automate':return
        if r.get('parent_id'):continue  # Native plans cannot overlap their containing room. Dashboard shows the subregion.
        points=cells(r);label=display_label(r['label'])
        existing=next((p for p in state.plans if display_label(p.label)==label and {(c.x,c.z) for c in p.cells}==points),None)
        if existing:
            marks[r['id']]=existing.id
            colors[r['id']]=next((c.html_color for c in state.colors if c.def_name==existing.color_def),PALETTE[index%len(PALETTE)])
            continue
        if r['id'] in marks:
            rt.note('info','Player changed planning marks for '+r['label']+'; keeping the player edit.',role='Architect')
            continue
        overlaps=[p for p in state.plans if points & {(c.x,c.z) for c in p.cells}]
        if overlaps:
            # A changed/renamed mark is still occupied; never recreate over it.
            rt.note('info','Existing planning marks overlap '+display_label(r['label'])+'; keeping them.',role='Architect')
            continue
        if not state.colors:raise ValueError('Native planning colors unavailable')
        # Session and generation must still match after inference, before game mutation.
        game=await rt.api.call('get_game_state',{},fresh=True)
        if game.get('session_id')!=rt.observation['game'].get('session_id'):raise ValueError('Colony changed before planning marks')
        rt.check_generation()
        rt.executing=True
        try:
            result=await rt.api.call('planning_create',{'map_id':rt.observation['map']['id'],'label':label,'color_def':state.colors[index%len(state.colors)].def_name,'cells':[{'x':x,'z':z} for x,z in sorted(points)]},write=True)
            if {(c.x,c.z) for c in result.cells}!=points:raise ValueError('Native planning receipt differs from reserved cells')
            marks[r['id']]=result.id
            colors[r['id']]=state.colors[index%len(state.colors)].html_color
            rt.persist()
        finally:rt.executing=False


async def release_retired(rt,active):
    # Long-term land and realized footprints outlive individual work projects.
    saved=rt.memory.get('spatial_layout',{})
    changed=False
    for intent in saved.get('active_construction_intents',[]):
        if intent['project_id'] not in active and intent['status']!='project_retired':
            intent['status']='project_retired';changed=True
    if changed:rt.persist()


def zone_cells(action):
    args=action.arguments
    if 'cells' in args:
        points={(c['x'],c['z']) for c in args['cells']}
        if len(points)!=len(args['cells']):raise ValueError('Zone contains duplicate cells')
        return points
    a,b=args['point_a'],args['point_b']
    if abs(a['x']-b['x'])>64 or abs(a['z']-b['z'])>64:raise ValueError('Zone exceeds local survey bounds')
    return {(x,z) for x in range(min(a['x'],b['x']),max(a['x'],b['x'])+1) for z in range(min(a['z'],b['z']),max(a['z'],b['z'])+1)}

async def validate_zone_orders(rt,project,actions):
    """Zone specialists select sites against shared reservations and fresh native zones."""
    from .base_plan import survey_master_area
    area=await survey_master_area(rt)
    observed={(c['position']['x'],c['position']['z']):c for c in area['cells']}
    used=set()
    for action in actions:
        expected={'post_map_zone_stockpile'} if project['kind']=='storage' else {'zone_growing_cells','post_map_zone_growing'}
        if action.endpoint not in expected:raise ValueError('Zone specialist cannot place construction or another zone type')
        points=zone_cells(action)
        from .base_plan import validate_reserved_space
        validate_reserved_space(rt,project,points)
        plan=rt.memory.get('spatial_layout',{})
        if plan.get('version'):
            own=[r for r in plan['regions'] if project['project_id'] in r['project_ids']]
            permitted=set().union(*(cells(r) for r in own)) if own else set()
            if not points<=permitted:raise ValueError('Select a current increment with reserve_planned_site before placing zones; stay inside that footprint')
        if not points or not points<=observed.keys():raise ValueError('Choose zone cells from the explored construction_area survey near camp')
        if used & points:raise ValueError('Proposed zones overlap each other')
        used|=points
        for r in rt.memory.get('spatial_layout',{}).get('regions',[]):
            overlap=points & cells(r)
            if not overlap:continue
            own=project['project_id'] in r['project_ids']
            if own and len(r['project_ids'])==1 and r['purpose'] in ('farm','storage'):continue
            if project['kind']=='storage' and r['purpose']=='room' and overlap<=cells(r)-boundary(cells(r)):continue
            raise ValueError(f'Zone conflicts with {r["label"]} ({r["purpose"]}) at {sorted(overlap)[:6]}; choose free surveyed cells')
        minimum=0
        if project['kind']=='growing':
            defs=await rt.api.call('get_def_all',{})
            crop=next((d for d in (defs.get('plant_defs') or []) if d['def_name']==action.arguments['plant_def']),None)
            if crop is None:raise ValueError('Unknown crop; select an observed native plant definition')
            minimum=crop['fertility_min']
        for point in sorted(points):
            cell=observed[point]
            if cell['zone_id'] is not None:raise ValueError(f'Existing {cell["zone_type"]} at {point}; inspect or update that zone instead of creating another over it')
            if cell['encloses']:raise ValueError(f'Zone cell {point} contains enclosing construction')
            if project['kind']=='growing' and (not cell['plantable'] or cell['fertility']<minimum):raise ValueError(f'Zone cell {point} cannot grow {action.arguments["plant_def"]}: fertility {cell["fertility"]}, needs {minimum}; exclude it')
    if project['kind']=='growing':
        from .project_progress import validate_farm_expansion
        await validate_farm_expansion(rt,project,actions)
    # Native zone creation checks Zone.CanAddCell again.
    # Successful zones themselves are authoritative shared reservations, read afresh above.
