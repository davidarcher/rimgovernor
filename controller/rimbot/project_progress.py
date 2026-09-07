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

def site_recovery(site, eligibility):
    """Keep persistent eligibility separate from temporary native worker failures."""
    result={'thing_id':site['thing_id'],'def_name':site['def_name'],'stage':site['stage'],
            'work_done':site.get('work_done',0),'work_total':site.get('work_total',0)}
    restrictions=eligibility.get('restrictions',[])
    if restrictions:
        result.update(state='needs_alternative',reasons=restrictions,
                      next_action='Choose an eligible alternative. Cancel only the obsolete project blueprint with the native cancel action, then inspect before replacement. Priorities cannot repair these restrictions.')
    elif site.get('targeted_by'):
        result.update(state='targeted',pawn_ids=site['targeted_by'],
                      next_action='Observe delivery/work changes; a current target is not proof of completion. Do not duplicate this site.')
    else:
        missing=[m for m in site.get('materials',[]) if m['needed']>0]
        workers=site.get('workers',[])
        if not any(w['can_construct'] for w in workers):
            result.update(state='worker_blocked',reasons=list(dict.fromkeys(w['reason'] for w in workers if w.get('reason'))),
                          next_action='Resolve the reported native worker condition. Do not assume idle means construction is disabled.')
        elif missing:
            result.update(state='awaiting_materials',materials=missing,
                          next_action='Resolve remaining delivery requirements. Allowed stock is not necessarily accessible; do not create replacement buildings.')
        else:
            result.update(state='ready_for_work',next_action='Existing site can be worked. Check ordinary labor and time progression; do not duplicate it.')
    return result

async def refresh_progress(rt,context):
    projects=[p for p in rt.memory.get('projects',[]) if p.get('status')!='retired']
    farming=await farm_progress(rt) if any(p['kind']=='growing' for p in projects) else None
    rooms=[r for r in rt.memory.get('spatial_layout',{}).get('regions',[]) if r['purpose']=='room']
    area=None
    if rooms and rt.memory.get('colony_focus'):
        area=(await rt.api.call('construction_area',{'map_id':rt.observation['map']['id'],'center':rt.memory['colony_focus'],'radius':32},fresh=True)).model_dump()
    lookup={(c['position']['x'],c['position']['z']):c for c in (area or {}).get('cells',[])}
    eligibility={}
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
            from .room_outcomes import assess_interior
            progress['enclosures']=[assess_interior(cells(r)-boundary(cells(r)),
                (rt.observation.get('rooms') or {}).get('rooms')) for r in own]
            progress['meaning']='Building counts distinguish built, frames and blueprints. Roof coverage and furniture are separate requirements. Old model reports are not current blockers.'
            owned_positions={(b['position']['x'],b['position']['z']) for w in rt.memory.get('work',[]) if w['id'] in project.get('work_ids',[]) for b in w.get('action',{}).get('arguments',{}).get('buildings',[])}
            sites=[s for s in context.get('construction_work',{}).get('sites',[]) if (s['position']['x'],s['position']['z']) in points|owned_positions]
            progress['sites']=[]
            for site in sites:
                name=site['def_name']
                if name not in eligibility:
                    definitions=await rt.api.call('construction_definitions',{'map_id':rt.observation['map']['id'],'search':name,'offset':0,'limit':32})
                    definition=next((d for d in definitions.items if d.def_name==name),None)
                    eligibility[name]=definition.eligibility.model_dump() if definition else {}
                progress['sites'].append(site_recovery(site,eligibility[name]))
            progress['sites_complete']=context.get('construction_work',{}).get('next_offset') is None and 'sites' in context.get('construction_work',{})

        if project.get('feedback'):
            rt.note('project_feedback_history','Previous review feedback superseded by a fresh observation',project_id=project['project_id'],feedback=project['feedback'])
        project['feedback']=[]
        project['progress']=progress
        from .project_outcomes import assess
        previous=project.get('outcome_evidence')
        current=assess(project)
        project['outcome_evidence']=current
        # Emit only changed evidence, never a breadcrumb on every poll or tick.
        if previous and any(previous.get(k)!=current[k] for k in ('status','checks','review_required')):
            rt.note('project_outcome','Project evidence changed',project_id=project['project_id'],outcome_evidence=current)
    return {**context,'projects':projects}

async def validate_farm_expansion(rt,project,actions):
    from .spatial import zone_cells
    orders=[a for a in actions if a.endpoint in ('zone_growing_cells','post_map_zone_growing')]
    if not orders:return
    target=project.get('target_cells');crop=project.get('crop_def')
    if not target or not crop:
        from .admin_requests import request_review
        reason='Growing project needs one crop_def and total target_cells based on current food demand and existing fields.'
        request_review(rt,project['project_id']+':crop_target',reason)
        raise ValueError(reason)
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
