"""Observed progress, distinct from model reports and issued orders."""
from collections import Counter
from .spatial import cells, boundary

async def farm_progress(rt):
    mid=rt.observation['map']['id']
    zones=await rt.api.call('get_map_zones',{'map_id':mid},fresh=True)
    rows=[]
    for zone in zones.get('zones',[]):
        if zone['type']!='Zone_Growing':continue
        detail=await rt.api.call('get_map_zone_growing',{'map_id':mid,'zone_id':zone['id']},fresh=True)
        rows.append({'zone_id':zone['id'],'cells':zone['cells_count'],'crop':detail.get('plant_def_name'),'plants_present':detail.get('plant_count'),'sowing_allowed':detail.get('is_sowing'),'growth_progress':detail.get('growth_progress')})
    return {'zones':rows,'zone_count':len(rows),'designated_cells':sum(r['cells'] for r in rows),'meaning':'Zone creation is immediate. These cells already exist even if sowing has not started. Inspect sowing/access/season/labor blockers instead of adding replacement fields.'}

async def refresh_progress(rt,context):
    projects=[p for p in rt.memory.get('projects',[]) if p.get('status')!='retired']
    farming=await farm_progress(rt) if any(p['kind']=='growing' for p in projects) else None
    rooms=[r for r in rt.memory.get('spatial_layout',{}).get('regions',[]) if r['purpose']=='room']
    area=None
    if rooms and rt.memory.get('colony_focus'):
        area=(await rt.api.call('construction_area',{'map_id':rt.observation['map']['id'],'center':rt.memory['colony_focus'],'radius':24})).model_dump()
    lookup={(c['position']['x'],c['position']['z']):c for c in (area or {}).get('cells',[])}
    for project in projects:
        progress={'observed_tick':rt.last_tick,'source':'live game','orders':[{'title':w.get('title',w['id']),'status':w['status']} for w in rt.memory.get('work',[]) if w['id'] in project.get('work_ids',[])]}
        if project['kind']=='growing':
            progress.update(farming)
            crop=project.get('crop_def');target=project.get('target_cells')
            existing=sum(r['cells'] for r in farming['zones'] if r['crop']==crop) if crop else None
            progress.update(crop=crop,target_cells=target,matching_cells=existing,remaining_cells=max(0,target-existing) if target and existing is not None else None)
            progress['target_status']='specified' if crop and target else 'Administrator must choose one native crop and a total colony-wide cell target before expansion.'
        if project['kind']=='construction':
            own=[r for r in rooms if project['project_id'] in r['project_ids']]
            points=set().union(*(cells(r) for r in own)) if own else set()
            buildings=[b for b in context.get('construction_state',{}).get('buildings',[]) if (b['position']['x'],b['position']['z']) in points]
            progress['buildings']=[{'def_name':d,'state':state,'count':count} for (d,state),count in sorted(Counter((b['def_name'],b['state']) for b in buildings).items())]
            interior=set().union(*(cells(r)-boundary(cells(r)) for r in own)) if own else set()
            progress['roof']={'interior_cells':len(interior),'observed_cells':len(interior & lookup.keys()),'roofed_cells':sum(lookup[c]['roofed'] for c in interior if c in lookup)}
            progress['meaning']='Building counts distinguish built, frames and blueprints. Roof coverage and furniture are separate requirements. Old model reports are not current blockers.'
        if project.get('feedback'):
            rt.note('project_feedback_history','Previous review feedback superseded by a fresh observation',project_id=project['project_id'],feedback=project['feedback'])
        project['feedback']=[]
        project['progress']=progress
    return {**context,'projects':projects}

async def validate_farm_expansion(rt,project,actions):
    from .spatial import zone_cells
    orders=[a for a in actions if a.endpoint in ('zone_growing_cells','post_map_zone_growing')]
    if not orders:return
    target=project.get('target_cells');crop=project.get('crop_def')
    if not target or not crop:
        rt.memory['admin_requested']='Growing project needs one crop_def and total target_cells based on current food demand and existing fields.'
        raise ValueError(rt.memory['admin_requested'])
    current=await farm_progress(rt)
    existing=sum(z['cells'] for z in current['zones'] if z['crop']==crop)
    if any(a.arguments['plant_def']!=crop for a in orders):raise ValueError('Use the approved crop_def; another crop needs a separate objective.')
    added=len(set().union(*(zone_cells(a) for a in orders)))
    if existing+added>target:raise ValueError(f'Existing {crop} zones already cover {existing} cells; target {target}, remaining {max(0,target-existing)}. Missing sowing does not authorize another field.')


def routing_error(project):
    import re
    if project.get('kind')!='storage' and re.match(r'^(?:place|create|make|establish|configure)\s+(?:(?:a|an|the|general|small|new|nearby)\s+)*stockpile\b',project.get('outcome',''),re.I):
        return 'Stockpile creation/configuration requires kind=storage. Correct this project; it cannot be executed as construction.'
    return None
