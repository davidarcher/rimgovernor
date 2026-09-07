"""One observed construction budget. Native sites own commitments, not model claims."""
from collections import Counter
from pydantic import Field
from .contracts import Contract
from .http_models import MapResourceOverview, ConstructionWorkOverview
from .native_models import ConstructionRequest


class MaterialBalance(Contract):
    available: int = Field(ge=0)
    committed: int = Field(ge=0)
    reserve: int = Field(ge=0)
    spendable: int = Field(ge=0)
    deficit: int = Field(ge=0)


class ConstructionBudget(Contract):
    map_id: int
    observed_tick: int
    materials: dict[str, MaterialBalance]
    site_count: int
    scope: str = ('Explored allowed loose stacks, including stockpiles, minus remaining native '
                  'construction deliveries and policy reserves. Excludes inventories, future production '
                  'and other consumption. Availability is not proof of safe reachability. '
                  'New orders use native definition base costs; mod-specific cost adjustments may differ.')


class BudgetConflict(ValueError):
    def __init__(self,message,*,costs=None,shortage=None):
        super().__init__(message)
        self.costs=costs
        self.shortage=shortage


def balances(available, committed, reserves):
    result={}
    for name in sorted(set(available)|set(committed)|set(reserves)):
        stock=available.get(name,0);owed=committed.get(name,0);reserve=reserves.get(name,0)
        if any(not isinstance(v,int) or isinstance(v,bool) or v<0 for v in (stock,owed,reserve)):
            raise ValueError('Resource quantities must be nonnegative native item counts')
        free=stock-owed-reserve
        result[name]=MaterialBalance(available=stock,committed=owed,reserve=reserve,
                                    spendable=max(0,free),deficit=max(0,-free))
    return result


def allocate(materials, requests):
    """Stable priority-ordered requests; accept whole requests, never model arithmetic."""
    remaining={name:row.spendable for name,row in materials.items()}
    accepted=[];rejected={}
    for key,costs in requests:
        if any(not isinstance(v,int) or isinstance(v,bool) or v<0 for v in costs.values()):
            raise ValueError('Invalid construction cost')
        shortage={name:count-remaining.get(name,0) for name,count in costs.items() if count>remaining.get(name,0)}
        if shortage:
            rejected[key]=shortage
        else:
            accepted.append(key)
            for name,count in costs.items():remaining[name]=remaining.get(name,0)-count
    return accepted,rejected


async def observe_budget(rt):
    mid=rt.observation['map']['id']
    focus=rt.memory.get('colony_focus')
    if focus is None:raise BudgetConflict('Construction budget needs an observed colony location')
    obligations=Counter();seen=set();offset=0;total=None
    while True:
        page=ConstructionWorkOverview.model_validate(await rt.api.call('get_map_construction_work',
            {'map_id':mid,'offset':offset,'limit':32},fresh=True))
        if page.map_id!=mid or page.offset!=offset or (total is not None and page.total!=total):
            raise BudgetConflict('Construction changed during the budget survey; inspect again')
        total=page.total
        for site in page.sites:
            if site.thing_id in seen:raise BudgetConflict('Construction changed during pagination; inspect again')
            seen.add(site.thing_id)
            for material in site.materials:obligations[material.def_name]+=max(0,material.needed)
        if page.next_offset is None:break
        if page.next_offset<=offset:raise BudgetConflict('Construction pagination did not advance')
        offset=page.next_offset
    if len(seen)!=total:raise BudgetConflict('Incomplete construction obligations; budget unavailable')
    # Read loose stock AFTER obligations: deliveries during inspection can make the
    # estimate conservative, never count the same delivered stack as spare stock.
    # This is controller admission, not a lock on player orders or native consumption.
    overview=MapResourceOverview.model_validate(await rt.api.call('get_map_resource_overview',
        {'map_id':mid,'center_x':focus['x'],'center_z':focus['z'],'nearby_radius':40},fresh=True))
    stocks=Counter()
    for row in overview.supplies:stocks[row.def_name]+=row.allowed_quantity
    budget=ConstructionBudget(map_id=mid,observed_tick=page.observed_tick,site_count=len(seen),
        materials=balances(stocks,obligations,rt.memory.get('resource_reserves',{})))
    rt.memory['resource_budget']=budget.model_dump()
    rt.persist()
    return budget


async def quote(rt, request):
    """Only new placements cost more; existing sites already appear in obligations."""
    inspected=await rt.api.native.inspect(request)
    if not inspected.accepted:raise BudgetConflict('Native placement is not valid; inspect and correct it before budgeting')
    costs=Counter();definitions={};seen=set()
    for item in inspected.items:
        if item.state!='ready':continue
        p=item.placement
        key=(p.def_name,p.stuff_def_name,p.position.x,p.position.z,p.rotation)
        if key in seen:continue
        seen.add(key)
        if p.def_name not in definitions:
            offset=0
            while True:
                page=await rt.api.call('construction_definitions',{'map_id':request.map_id,'search':p.def_name,'offset':offset,'limit':32})
                found=next((d for d in page.items if d.def_name==p.def_name),None)
                if found is not None:definitions[p.def_name]=found;break
                if page.next_offset is None:raise BudgetConflict('Native costs unavailable for '+p.def_name)
                if page.next_offset<=offset:raise BudgetConflict('Definition pagination did not advance')
                offset=page.next_offset
        definition=definitions[p.def_name]
        for cost in definition.costs:costs[cost.def_name]+=cost.count
        if definition.stuff_count:
            if not p.stuff_def_name:raise BudgetConflict('Native material choice is missing')
            costs[p.stuff_def_name]+=definition.stuff_count
    return {name:count for name,count in costs.items() if count}


async def admit_construction(rt, action, role):
    request=ConstructionRequest.model_validate(action.arguments)
    costs=await quote(rt,request)
    if not costs:return  # Free/instant and existing placements need no material ceremony.
    budget=await observe_budget(rt)
    accepted,rejected=allocate(budget.materials,[('order',costs)])
    if rejected:
        shortage=rejected['order']
        rt.note('resource_conflict','Construction waits for materials: '+', '.join(f'{n} {k}' for k,n in shortage.items()),
                role=role,costs=costs,shortage=shortage,budget=budget.model_dump())
        raise BudgetConflict('Shared construction budget: short '+', '.join(f'{n} {k}' for k,n in shortage.items())+
                             '. Existing native orders retain their materials. Gather supplies, reduce this batch, or ask the administrator to revise priorities; do not repeat unchanged orders.',costs=costs,shortage=shortage)
    rt.note('resource_allocation','Construction fits the shared material budget',role=role,costs=costs,budget=budget.model_dump())


async def resource_review_due(rt,project,force=False):
    """Recheck an unchanged rejected batch without another executor inference."""
    from .project_schedule import evidence,REVIEW_TICKS
    pending=project.get('resource_request')
    if not pending or force:return True
    # Never apply an old cost to a changed project, observed site, or order state.
    if pending.get('evidence')!=evidence(project,rt.memory):return True
    if (rt.last_tick or 0)-pending['observed_tick']>=REVIEW_TICKS:return True
    budget=await observe_budget(rt)
    _,rejected=allocate(budget.materials,[('project',pending['costs'])])
    shortage=rejected.get('project',{})
    previous=pending.get('shortage',{})
    pending['shortage']=shortage
    pending['checked_tick']=rt.last_tick
    if shortage!=previous:
        rt.note('resource_conflict' if shortage else 'resource_allocation',
                'Project still waits for materials' if shortage else 'Materials available; project can be reconsidered',
                project_id=project['project_id'],shortage=shortage,costs=pending['costs'])
    if not shortage:project.pop('execution_review',None)
    rt.persist()
    return not shortage
