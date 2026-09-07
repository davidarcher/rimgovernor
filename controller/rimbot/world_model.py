"""Small deterministic projections and state transitions over existing observations."""
from .contracts import Contract


class MedicalFacts(Contract):
    observed_pawns: int
    missing_pawns: int
    urgent_pawn_ids: list[int]
    downed_pawn_ids: list[int]
    tendable_pawn_ids: list[int]
    basis: str = 'Native current life-threatening hediff flags; permanent injuries and can-ever-kill are not emergencies.'


def derive(observation):
    urgent=[];downed=[];tendable=[];seen=0
    for pawn in observation.get('pawns',[]):
        identity=pawn.get('colonist',{}).get('id')
        medical=pawn.get('colonist_medical_info')
        if identity is None or not isinstance(medical,dict) or medical.get('hediffs') is None:continue
        seen+=1
        if medical.get('is_dead'):continue
        if medical.get('is_downed'):downed.append(identity)
        if any(h.get('is_currently_life_threatening') is True for h in medical['hediffs']):urgent.append(identity)
        if any(h.get('tendable_now') is True for h in medical['hediffs']):tendable.append(identity)
    game=observation.get('game',{})
    expected=max(len(observation.get('pawns',[])),game.get('colonist_count',0))
    result={'observed_tick':game.get('game_tick',0),'medical':MedicalFacts(observed_pawns=seen,
        missing_pawns=max(0,expected-seen),urgent_pawn_ids=urgent,downed_pawn_ids=downed,tendable_pawn_ids=tendable).model_dump()}
    power=observation.get('power')
    if isinstance(power,dict) and all(k in power for k in ('current_power','consumption_power_on','currently_stored_power')):
        result['power']={'aggregate_headroom_watts':power['current_power']-power['consumption_power_on'],
            'stored_energy':power['currently_stored_power'],
            'scope':'Map aggregate, not proof separate power networks are connected. Battery ETA unavailable without network-specific flow.'}
    else:result['power']={'unavailable':'No current native power observation'}
    farm=observation.get('farm')
    if isinstance(farm,dict):
        result['crops']={k:farm[k] for k in ('total_growing_zones','total_plants','total_expected_yield','total_infected_plants') if k in farm}
        result['crops']['scope']='Native current crop counts and harvestable product units. Conditional timing is reported separately in forecast; product units are not nutrition.'
    return result


def transition(state,key,condition,tick,roles,urgent=False):
    """Unknown does not clear an alert. Clear only after 250 stable game ticks."""
    entry=state.setdefault(key,{'active':False})
    if condition is None:
        entry.pop('clear_since',None)
        return None
    if condition:
        entry.pop('clear_since',None)
        if entry['active']:return None
        entry['active']=True
    elif entry['active']:
        entry.setdefault('clear_since',tick)
        if tick-entry['clear_since']<250:return None
        entry.update(active=False);entry.pop('clear_since',None)
    else:return None
    return {'type':'derived_risk','roles':roles,'urgent':urgent and entry['active'],
            'data':{'risk':key,'active':entry['active'],'observed_tick':tick}}


def update(rt):
    facts=derive(rt.observation);rt.memory['world_facts']=facts
    from .food_forecast import forecast
    facts['food']=forecast(rt.memory,rt.observation)
    from .harvest_forecast import forecast as harvest_forecast
    facts.setdefault('crops', {})['forecast']=harvest_forecast(rt.memory,rt.observation)
    state=rt.memory.setdefault('risk_state',{})
    medical=facts['medical'];tick=facts['observed_tick']
    condition=True if medical['urgent_pawn_ids'] else (None if medical['missing_pawns'] else False)
    events=[]
    event=transition(state,'medical_emergency',condition,tick,['Survival'],True)
    if event:events.append(event)
    events.extend(incapacitation_events(state,rt.observation,tick))
    food=facts['food'];days=food.get('stock_depletion_days')
    coverage=food.get('food_runway_days')
    active_access=state.get('food_access_shortfall',{}).get('active',False)
    event=transition(state,'food_access_shortfall',None if coverage is None else coverage < (4 if active_access else 2),tick,['Survival'])
    if event:events.append(event)
    active=state.get('food_stock_decline',{}).get('active',False)
    condition=(days<(4 if active else 2)) if days is not None else (False if food.get('net_loss_per_day',1)<=0 else None)
    event=transition(state,'food_stock_decline',condition,tick,['Survival'])
    if event:events.append(event)
    power=facts['power'];headroom=power.get('aggregate_headroom_watts')
    event=transition(state,'power_deficit',None if headroom is None else headroom<0,tick,['Infrastructure'])
    if event:events.append(event)
    for event in events:
        rt.note('risk_transition',event['data']['risk'].replace('_',' ')+(' detected' if event['data']['active'] else ' cleared'),
                **event['data'],roles=event['roles'],urgent=event['urgent'])
    sync_interrupts(rt)
    return events


def incapacitation_events(state,observation,tick):
    """Per-pawn changes: a second downed worker is not an unchanged colony flag."""
    prefix='pawn_incapacitated:'
    observed={}
    for pawn in observation.get('pawns',[]):
        identity=pawn.get('colonist',{}).get('id')
        medical=pawn.get('colonist_medical_info') or {}
        if identity is None:continue
        downed=medical.get('is_downed')
        # A missing pawn, death or missing field is not evidence of recovery.
        observed[prefix+str(identity)]=downed if medical.get('is_dead') is False and isinstance(downed,bool) else None
    keys={k for k in state if k.startswith(prefix)}|{k for k,v in observed.items() if v is True}
    events=[]
    for key in sorted(keys):
        event=transition(state,key,observed.get(key),tick,['Survival'],True)
        if event:
            event['data']['pawn_id']=int(key[len(prefix):])
            events.append(event)
    return events


def sync_interrupts(rt):
    emergency=rt.memory.get('risk_state',{}).get('medical_emergency',{}).get('active',False)
    for project in rt.memory.get('projects',[]):
        if project.get('status')=='retired':continue
        if project.get('admin_hold'):
            if project.get('status')!='suspended':
                project['admin_hold']['resume_status']=project['status']
                project['status']='suspended'
            continue
        interruption=project.get('interruption')
        exempt=project.get('priority')=='urgent' or project.get('kind') in ('care','supply_access','security')
        if emergency and not exempt and interruption is None:
            project['interruption']={'reason':'medical_emergency','resume_status':project.get('status','approved')}
            project['status']='suspended'
            rt.note('project_suspended','Pausing new orders while urgent care is assessed',project_id=project['project_id'])
        elif emergency and not exempt and interruption is not None:
            if project.get('status')!='suspended':
                interruption['resume_status']=project.get('status','needs_review')
                project['status']='suspended'
        elif interruption and interruption.get('reason')=='medical_emergency' and (not emergency or exempt):
            project['status']=interruption['resume_status'];project.pop('interruption')
            project.pop('execution_review',None)
            rt.note('project_resumed','Project can continue',project_id=project['project_id'])
