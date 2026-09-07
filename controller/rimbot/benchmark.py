"""Measurements, not a hardcoded colony policy."""
import json

class SetupMetrics:
    def __init__(self, initial_buildings, target_defs, target_count):
        self.initial_ids={b['thing_id'] for b in initial_buildings if b['state']=='built'}
        self.target_defs=set(target_defs);self.target_count=target_count
        self.last_tick=None;self.last_idle=0;self.idle_pawn_ticks=0
        self.completed_ids=set();self.peak_excess_capacity=0
        self.progress_transitions=0;self.previous_sites={}

    def sample(self, work, buildings):
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
        self.peak_excess_capacity=max(self.peak_excess_capacity,max(0,capacity-self.target_count))
        return capacity>=self.target_count

    def report(self, events, started):
        actions=[e for e in events if e['kind']=='action' and e.get('endpoint')]
        calls=[e for e in events if e['kind']=='model_call']
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
                'roles':roles,
                'orders_issued':len(actions),'completed_new_objects':len(self.completed_ids),
                'sampled_idle_pawn_ticks':self.idle_pawn_ticks,'observed_progress_transitions':self.progress_transitions,
                'peak_excess_target_capacity':self.peak_excess_capacity,
                'rejected_model_calls':sum(e['kind']=='model_diagnostic' and bool(e.get('error')) for e in events),
                'model_calls':len(calls),'model_seconds':sum(e.get('seconds',0) for e in calls),
                'input_tokens':sum(e.get('usage',{}).get('prompt_tokens',0) for e in calls),
                'output_tokens':sum(e.get('usage',{}).get('completion_tokens',0) for e in calls)}
