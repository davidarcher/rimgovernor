"""Bounded strategic projection of native resource DTOs, never invented availability."""
from .http_models import MapResourceOverview

LIMITS={'terrain':12,'plants':8,'animals':8,'minerals':8,'supplies':12,'food_crops':12,'fishing':4}


def resource_brief(overview: MapResourceOverview):
    result=overview.model_dump(exclude=set(LIMITS))
    for name,limit in LIMITS.items():
        rows=getattr(overview,name)
        result[name]={'total_groups':len(rows),'omitted_groups':max(0,len(rows)-limit),
                      'items':[row.model_dump() for row in rows[:limit]]}
    result['follow_up']='get_map_resource_overview returns every group. Inspect the returned sample IDs or cells before issuing orders; this survey does not establish safe access.'
    return result
