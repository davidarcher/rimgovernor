"""Deterministic coverage selection using native restrictions and relevant skills."""
from collections import Counter
from .contracts import Action,Proposal
from .semantic_models import WorkPolicy


def allocate(policy, definitions, pawns, unavailable_ids=()):
    definitions={d['def_name']:d for d in definitions}
    names=[d.work_type for d in policy.coverage]
    if len(names)!=len(set(names)):raise ValueError('Use one coverage target per native work type')
    if any(n not in definitions for n in names+policy.protect):raise ValueError('Work policy contains an unknown native work type')
    roster={}
    for pawn in pawns:
        identity=pawn.get('colonist',{}).get('id');medical=pawn.get('colonist_medical_info') or {}
        if identity is None or identity in unavailable_ids or medical.get('is_dead') is not False or medical.get('is_downed') is not False:continue
        work=pawn.get('colonist_work_info') or {}
        roster[identity]={'name':pawn.get('colonist',{}).get('name') or str(identity),'priorities':{p['work_type']:p for p in (work.get('work_priorities') or [])},
                          'skills':{s['name']:s for s in (work.get('skills') or [])}}
    protected={};blockers=[]
    for work in policy.protect:
        providers=[i for i,p in roster.items() if (r:=p['priorities'].get(work)) is not None and r.get('is_totally_disabled') is False and r['priority']>0]
        if len(providers)==1:protected.setdefault(providers[0],set()).add(work)
        if not providers and work not in names:blockers.append('No enabled provider to protect for '+work+'; include a coverage target.')
    selected=[];changes=[];load=Counter()
    for demand in sorted(policy.coverage,key=lambda d:d.work_type not in policy.protect):
        relevant=definitions[demand.work_type].get('relevant_skills')
        if relevant is None:
            blockers.append('Native relevant skills unavailable for '+demand.work_type);continue
        candidates=[]
        for identity,pawn in roster.items():
            row=pawn['priorities'].get(demand.work_type)
            if row is None or row.get('is_totally_disabled') is not False:continue
            skills=[pawn['skills'].get(name) for name in relevant]
            if any(s is None or s.get('totally_disabled') is not False or s.get('permanently_disabled') is not False for s in skills):continue
            adequate=0<row['priority']<=demand.priority
            # Preserve existing coverage without assigning a protected sole provider
            # another newly promoted duty. Do not disable anyone's existing work.
            if not adequate and protected.get(identity,set())-{demand.work_type}:continue
            level=sum(s['level'] for s in skills)/max(1,len(skills))
            passion=sum(s.get('passion',0) for s in skills)
            candidates.append((not adequate,load[identity],-level,-passion,identity,row))
        chosen=sorted(candidates,key=lambda r:r[:5])[:demand.workers]
        if len(chosen)<demand.workers:
            blockers.append(f'{demand.work_type}: need {demand.workers} eligible workers, found {len(chosen)} without taking sole protected providers.')
        for _,_,level,passion,identity,row in chosen:
            load[identity]+=1
            selected.append({'pawn_id':identity,'pawn_name':roster[identity]['name'],'work_type':demand.work_type,'priority':min(row['priority'],demand.priority) if row['priority'] else demand.priority,'skill_score':-level})
            if not 0<row['priority']<=demand.priority:changes.append({'id':identity,'work':demand.work_type,'priority':demand.priority})
        if demand.work_type in policy.protect and len(chosen)==1:
            protected.setdefault(chosen[0][4],set()).add(demand.work_type)
    return selected,([] if blockers else changes),blockers


async def plan(rt,project,context):
    policy=WorkPolicy.model_validate(project['work_policy'])
    definitions=await rt.api.call('get_def_all',{'filters':['WorkTypeDefs']})
    unavailable={w['pawn_id'] for w in context.get('construction_work',{}).get('workers',[]) if w.get('drafted') or w.get('downed')}
    selected,changes,blockers=allocate(policy,definitions.get('work_type_defs') or [],rt.observation.get('pawns',[]),unavailable)
    settings=await rt.api.call('get_work_settings',{},fresh=True)
    actions=[]
    if not blockers:
        if settings['use_work_priorities'] is not True:
            actions.append(Action(title='Enable manual work priorities',endpoint='post_work_settings',arguments={'use_work_priorities':True}))
        if changes:actions.append(Action(title='Apply work coverage',endpoint='post_colonists_work_priority',arguments={'priorities':changes}))
    allocation={'selected':selected,'changes':changes,'blockers':blockers,'observed_tick':rt.last_tick,
                'scope':'Native capability and relevant skills; stable existing coverage first, then spread duties, skill, passion and ID. No job creation, drafting, forced labor, travel or fatigue optimization.'}
    if {k:v for k,v in project.get('work_allocation',{}).items() if k!='observed_tick'}!={k:v for k,v in allocation.items() if k!='observed_tick'}:
        rt.note('work_allocation','Work coverage needs review' if blockers else 'Work coverage selected from native eligibility',project_id=project['project_id'],allocation=allocation)
        if blockers:rt.memory['admin_requested']='Work coverage policy needs revision: '+'; '.join(blockers)
    project['work_allocation']=allocation
    return Proposal(summary='Apply observed work coverage' if actions else 'Work coverage inspected',actions=actions,blockers=blockers)
