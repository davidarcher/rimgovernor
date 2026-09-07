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
