"""Decision context projections; native endpoint responses are never rewritten."""
from copy import deepcopy
from .contracts import Contract

class ColonyDecisionFacts(Contract):
    game: dict
    map: dict
    weather: dict
    alerts: list[dict]
    alerts_omitted: int
    workers: list[dict]
    workers_omitted: int
    pending_construction: int | None
    pawns: list[dict]
    pawns_omitted: int
    construction_counts: list[dict]


def decision_context(context,observation):
    workers=context.get('construction_work',{}).get('workers',[])
    alerts=observation.get('alerts',[])
    if isinstance(alerts,dict):alerts=alerts.get('items',[])
    pawns=observation.get('pawns',[])
    from collections import Counter
    counts=Counter((b['def_name'],b['state']) for b in context.get('construction_state',{}).get('buildings',[]))
    facts=ColonyDecisionFacts(
        game={k:v for k,v in observation.get('game',{}).items() if k in ('game_tick','colonist_count','is_paused')},
        map={k:v for k,v in observation.get('map',{}).items() if k in ('id','size','is_player_home')},
        weather=observation.get('weather',{}),alerts=alerts[:8],alerts_omitted=max(0,len(alerts)-8),
        workers=workers[:12],workers_omitted=max(0,len(workers)-12),
        pending_construction=context.get('construction_work',{}).get('total'),
        pawns=[{k:v for k,v in p.get('colonist',{}).items() if k in ('id','name','health','mood','position')} for p in pawns[:12]],
        pawns_omitted=max(0,len(pawns)-12),construction_counts=[{'def_name':name,'state':stage,'count':count} for (name,stage),count in sorted(counts.items())])
    keep=('player_direction','goals','plans','assigned_task','projects','proposals','semantic_objectives','strategy_guidance','colony_focus','work_settings','work','escalation','cancelled_projects','coordination_request','construction_budget')
    result={k:context[k] for k in keep if k in context}
    if 'world_facts' in context:result['world_facts']=context['world_facts']
    if 'native_work_types' in context:result['native_work_types']=context['native_work_types']
    result['colony']=facts.model_dump()
    result['labor_state']={'queued_construction_sites':facts.pending_construction,'meaning':'Idle alone does not imply disabled work. Create actual blueprints, zones, bills or designations when no jobs exist; inspect priorities only for a demonstrated work-setting blocker.'}
    if 'resource_overview' in context:
        resources=deepcopy(context['resource_overview'])
        for group in resources.values():
            if isinstance(group,dict) and isinstance(group.get('items'),list) and 'omitted_groups' in group and all(group is not resources.get(name) for name in ('terrain','food_crops','supplies','animals')):
                extra=max(0,len(group['items'])-3)
                group['items']=group['items'][:3];group['omitted_groups']+=extra
        # Strategic decisions use aggregates; item IDs/details remain available to executors.
        if 'supply_summary' in resources:resources.pop('supplies',None)
        result['resource_overview']=resources
        terrains=resources.get('terrain',{}).get('items',[])
        crops=resources.get('food_crops',{}).get('items',[])
        result['crop_land_comparison']={
            'scope':'Fertility comparison of observed terrain groups only; still check exact cells, pollution, season and access. Base grow days exclude nightly rest.',
            'crops':[{'def_name':crop['def_name'],'min_fertility':crop['min_fertility'],
                      'nearby_fertility_eligible_cells':crop.get('nearby_fertility_eligible_cells'),
                      'matching_nearby_terrain':[{'def_name':t['def_name'],'fertility':t['fertility'],'nearby_cells':t['nearby_cells'],'nearest_cell':t.get('nearest_cell')} for t in terrains if t.get('nearby_cells',0)>0 and t.get('fertility',0)>=crop['min_fertility']]}
                     for crop in crops if 'min_fertility' in crop and 'def_name' in crop]}

    work=context.get('construction_work',{})
    if 'sites' in work:
        result['construction_work']={k:v for k,v in work.items() if k!='workers'}
        result['construction_work']['sites']=work['sites'][:4]
        result['construction_work']['shown']=len(work['sites'][:4])
        result['construction_work']['omitted']=max(0,work['total']-len(work['sites'][:4]))
    else:result['construction_work']=work
    return result


def administrator_context(context):
    """Arbitration needs complete proposals, not repeated executor lookup data."""
    result = deepcopy(context)
    # Department-wide concerns were repeated once per candidate. Keep one copy
    # with explicit references; preserve every concern and candidate objective.
    concerns={}
    for candidate in result.get('proposals',{}).values():
        blockers=candidate.pop('blockers',[])
        if blockers:
            key=next((k for k,v in concerns.items() if v==blockers),None)
            if key is None:key='concerns_'+str(len(concerns)+1);concerns[key]=blockers
            candidate['blockers_ref']=key
        objective=candidate.get('objective',{})
        candidate['objective']={k:v for k,v in objective.items() if v is not None and v!='' and v!=[] and v!={}}
    if concerns:result['department_concerns']=concerns
    optional=('work_policy','after_projects','deadline_tick','quantity','crop_def','target_cells','definition_requirements','constraints','feedback','dependency_blockers')
    for project in result.get('projects',[]):
        for key in optional:
            if project.get(key) in (None,'',[],{}):project.pop(key,None)
    # Strategic worker facts are already carried in colony.workers; native
    # per-site recovery belongs to the executor and its project progress.
    result.pop('construction_work',None)
    resources = result.get('resource_overview', {})
    for name in ('plants', 'animals', 'minerals', 'terrain'):
        for row in resources.get(name, {}).get('items', []):
            location = row.pop('location', None)
            if location:
                row['nearby_count'] = location.get('nearby_count')
                row['nearest_distance'] = location.get('nearest_distance')
            row.pop('nearest_cell', None)
    # Native food_crops already reports each crop's fertility threshold and
    # eligible nearby cell count. Exact terrain matching belongs to placement.
    if resources.get('food_crops'):
        result.pop('crop_land_comparison', None)
    for row in resources.get('terrain', {}).get('items', []):
        for key in set(row)-{'def_name', 'fertility', 'nearby_cells', 'visible_cells'}:
            row.pop(key)
    if 'native_work_types' in result:
        result['native_work_types'] = [{k: v for k, v in row.items() if k in ('def_name', 'label')}
                                       for row in result['native_work_types']]
    guides = result.get('strategy_guidance', [])
    selected = [g for g in guides if g.get('id') == 'first-days'][:1]
    selected += [g for g in guides if g not in selected][:1-len(selected)]
    result['strategy_guidance'] = selected
    result['strategy_guidance_omitted'] = len(guides)-len(selected)
    return result
