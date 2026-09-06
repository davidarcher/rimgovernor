"""Bounded strategic projection of native resource DTOs, never invented availability."""
from .http_models import MapResourceOverview

LIMITS={'terrain':12,'plants':8,'animals':8,'minerals':8,'supplies':12,'food_crops':12,'fishing':4}


def resource_brief(overview: MapResourceOverview):
    result=overview.model_dump(exclude=set(LIMITS))
    for name,limit in LIMITS.items():
        rows=getattr(overview,name)
        result[name]={'total_groups':len(rows),'omitted_groups':max(0,len(rows)-limit),
                      'items':[row.model_dump() for row in rows[:limit]]}
    result['supply_summary']=summarize_supplies([row.model_dump() for row in overview.supplies])
    result['follow_up']='get_map_resource_overview returns every group. Inspect the returned sample IDs or cells before issuing orders; this survey does not establish safe access.'
    return result


def summarize_supplies(rows):
    """Aggregate the complete native survey before any item-detail truncation."""
    nutrition={};materials={}
    fields=('quantity','allowed_quantity','forbidden_quantity','nearby_allowed_quantity','nearby_forbidden_quantity')
    def add(groups,key,row,factor):
        totals=groups.setdefault(key,{field:0 for field in fields})
        for field in fields:totals[field]+=row[field]*factor
    for row in rows:
        if row['nutrition_per_unit']>0:add(nutrition,row.get('food_type') or 'Unclassified',row,row['nutrition_per_unit'])
        categories=sorted(set(row.get('material_categories',[])))
        if categories:add(materials,' + '.join(categories),row,1)
    return {'nutrition':{k:{f:round(v,3) for f,v in totals.items()} for k,totals in nutrition.items()},
            'construction_materials':materials,
            'units':{'nutrition':'nutrition units by native food type','construction_materials':'item units by native material category; mixed-category items counted once'},
            'scope':'Explored loose stacks including stockpiles; excludes pawn inventories and containers. Allowed is not proof of reachability or safety. Food types differ in diet suitability and preparation needs; materials in a category are not interchangeable for every building. Chunks and unmined ore are not ready construction materials.'}
