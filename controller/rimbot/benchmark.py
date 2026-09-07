"""Measurements, not a hardcoded colony policy."""
import json

def placement_receipts(work):
    """Count persisted native receipts, never infer acceptance from model prose."""
    attempted=accepted=rejected=unknown=0
    for order in work:
        action=order.get('action',{})
        if action.get('endpoint')!='construction_place':continue
        count=len(action.get('arguments',{}).get('buildings',[]))
        attempted+=count
        items=(order.get('native_result') or {}).get('items',[])
        accepted+=sum(i.get('state') in ('blueprint','frame','built') for i in items)
        rejected+=sum(i.get('state')=='rejected' for i in items)
        known=sum(i.get('state') in ('blueprint','frame','built','rejected') for i in items)
        unknown+=max(0,count-known)
    return {'attempted_placements':attempted,'accepted_native_placements':accepted,
            'rejected_native_placements':rejected,'unknown_placement_outcomes':unknown,
            'scope':'Persisted construction attempts; acceptance may refer to existing objects, not new construction.'}

class SetupMetrics:
    def __init__(self, initial_buildings, target_defs, target_count):
        self.initial_ids={b['thing_id'] for b in initial_buildings if b['state']=='built'}
        self.target_defs=set(target_defs);self.target_count=target_count
        self.last_tick=None;self.last_idle=0;self.idle_pawn_ticks=0
        self.completed_ids=set();self.peak_excess_capacity=0
        self.progress_transitions=0;self.previous_sites={}
        self.current_capacity=0;self.first_progress_seconds=None

    def sample(self, work, buildings, elapsed=None):
        previous_progress=self.progress_transitions
        tick=work['observed_tick'];idle=sum(w['idle'] for w in work['workers'])
        if self.last_tick is not None:
            if tick<self.last_tick:raise ValueError('Game tick rewound during benchmark')
            self.idle_pawn_ticks+=(tick-self.last_tick)*self.last_idle
        self.last_tick=tick;self.last_idle=idle
        for site in work['sites']:
            key=(site['def_name'],site['position']['x'],site['position']['z'])
            progress=(site['stage'],site['work_done'],sum(m['needed'] for m in site['materials']))
            previous=self.previous_sites.get(key)
            if previous and (progress[1]>previous[1] or progress[2]<previous[2] or (previous[0]=='blueprint' and progress[0]=='frame')):self.progress_transitions+=1
            self.previous_sites[key]=progress
        built=[b for b in buildings if b['state']=='built']
        self.completed_ids.update(b['thing_id'] for b in built if b['thing_id'] not in self.initial_ids)
        capacity=sum(b['def_name'] in self.target_defs for b in built)
        self.current_capacity=capacity
        if self.progress_transitions>previous_progress and self.first_progress_seconds is None:
            self.first_progress_seconds=elapsed
        self.peak_excess_capacity=max(self.peak_excess_capacity,max(0,capacity-self.target_count))
        return capacity==self.target_count

    def report(self, events, started):
        actions=sorted([e for e in events if e['kind']=='action' and e.get('endpoint')],key=lambda e:e['at'])
        calls=[e for e in events if e['kind']=='model_call']
        failures=[e for e in events if e['kind']=='model_failure']
        roles={}
        for event in events:
            if event['kind'] not in ('model_call','model_failure','executor_schedule'):continue
            row=roles.setdefault(event.get('role','Unknown'),{'calls':0,'failures':0,'seconds':0,'executor_invocations':0,'executor_skips':0})
            if event['kind'] in ('model_call','model_failure'):
                row['calls' if event['kind']=='model_call' else 'failures']+=1
                row['seconds']=round(row['seconds']+event.get('seconds',0),3)
            elif event.get('decision') in ('invoked','skipped'):
                row['executor_invocations' if event['decision']=='invoked' else 'executor_skips']+=1
        return {'first_order_seconds':round(actions[0]['at']-started,3) if actions else None,
                'intended_target_count':self.target_count,
                'first_useful_order_seconds':None,
                'useful_order_unknown_reason':'No causal attribution from commands to usable goal effects yet; first_order_seconds is not a usefulness measure.',
                'observed_target_objects':self.current_capacity,
                'usable_target_capacity':None,
                'usable_capacity_unknown_reason':'Object counts do not establish ownership or access to each sleeping place.',
                'first_observed_construction_progress_seconds':self.first_progress_seconds,
                'roles':roles,
                'orders_issued':len(actions),'completed_new_objects':len(self.completed_ids),
                'sampled_idle_pawn_ticks':self.idle_pawn_ticks,'observed_progress_transitions':self.progress_transitions,
                'peak_excess_target_capacity':self.peak_excess_capacity,
                'rejected_model_calls':sum(e['kind']=='model_diagnostic' and bool(e.get('error')) for e in events),
                'model_calls':len(calls),'model_seconds':sum(e.get('seconds',0) for e in calls),
                'model_failures':len(failures),'failure_details':[{'role':e.get('role'),'error':e.get('error')} for e in failures],
                'peak_reported_input_tokens':max((e.get('usage',{}).get('prompt_tokens',0) for e in calls),default=0) if calls else None,
                'observed_tool_results':sum(e['kind']=='tool_result' for e in events),
                'input_tokens':sum(e.get('usage',{}).get('prompt_tokens',0) for e in calls),
                'output_tokens':sum(e.get('usage',{}).get('completion_tokens',0) for e in calls)}
