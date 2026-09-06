"""Approve semantic objectives, then execute one native-system project at a time."""
import uuid
import asyncio
from .contracts import Decision, Proposal
from .semantic_models import ObjectiveProposal
from .model import ModelError


def reconcile_projects(memory):
    changed=False
    work={w['id']:w for w in memory.get('work',[])}
    for project in memory.get('projects',[]):
        if project.get('status') not in ('awaiting_work','orders_verified'):continue
        ids=project.get('work_ids',[])
        if not ids:continue
        rows=[work[i] for i in ids if i in work]
        if len(rows)!=len(ids):status='needs_review'
        elif all(w['status']=='complete' for w in rows):status='orders_verified'
        elif any(w['status'] in ('rejected','deferred','unresolved') for w in rows):status='needs_review'
        else:status='awaiting_work'
        if project.get('status')!=status:project['status']=status;changed=True
    return changed


def retain_project(memory,owner,objective):
    projects=memory.setdefault('projects',[])
    existing=next((p for p in projects if p['project_id']==objective.project_id),None) if objective.project_id else None
    if objective.project_id and (existing is None or existing['owner']!=owner):raise ValueError('Project must reference an existing objective owned by this manager')
    if existing is None:
        existing=next((p for p in projects if p['owner']==owner and p['kind']==objective.kind and p['outcome'].casefold().strip()==objective.outcome.casefold().strip()),None)
    if existing is None:
        existing={'project_id':uuid.uuid4().hex[:12],'owner':owner,'work_ids':[]};projects.append(existing)
    existing.update(objective.model_dump(exclude={'project_id'}),status='approved',feedback=[])
    return existing


async def semantic_review(rt,context,roles):
    proposals={}
    shared=await rt.manager_context(context)
    semaphore=asyncio.Semaphore(rt.settings.manager_parallelism)
    active=set()
    async def propose(role):
        async with semaphore:
            active.add(role)
            await rt.progress(active_roles=sorted(active))
            try:
                fresh={**shared,'assigned_task':context.get('assignments',{}).get(role),
                       'projects':[p for p in rt.memory.get('projects',[]) if p['owner']==role]}
                fresh.pop('assignments',None)
                proposal=await rt.planner.ask(role,fresh,ObjectiveProposal,rt.settings.reasoning)
                proposals[role]=proposal.model_dump()
                rt.note('proposal',proposal.summary,role=role,proposal=proposal.model_dump(),semantic=True)
            except (ModelError,ValueError) as error:
                rt.note('error',str(error),role=role)
            finally:
                active.discard(role)
                await rt.progress(active_roles=sorted(active))
    await asyncio.gather(*(propose(role) for role in dict.fromkeys(roles)))
    # Keep stable ordering independent of response completion timing.
    proposals={role:proposals[role] for role in roles if role in proposals}
    if not proposals:
        raise ModelError('No department submitted an objective review.')
    decision=await rt.planner.ask('Administrator: approve semantic objectives',
        {**context,'proposals':proposals,'semantic_objectives':True},Decision,rt.settings.reasoning)
    rt.note('arbitration',decision.response,role='Administrator',accepted=decision.accepted,deferred=decision.deferred)
    rt.reply(decision.response)
    if rt.mode!='automate':return
    approved=[]
    for owner in decision.accepted:
        for objective in ObjectiveProposal.model_validate(proposals[owner]).objectives:
            project=retain_project(rt.memory,owner,objective)
            if project not in approved:approved.append(project)
    rt.persist()
    for project in approved:
        rt.check_generation()
        if rt.mode!='automate':break
        role='Executor:'+project['kind']
        try:
            fresh=await rt.manager_context(context)
            fresh={k:v for k,v in fresh.items() if k not in ('plans','assignments')}
            fresh.update(project=project,assigned_task=project['outcome'])
            project['status']='inspecting';rt.persist()
            batch=await rt.planner.ask(role,fresh,Proposal,False)
            project['feedback']=batch.blockers
            rt.note('execution',batch.summary,role=role,project_id=project['project_id'],orders=len(batch.actions),blockers=batch.blockers)
            for action in batch.actions:
                before={w['id'] for w in rt.memory['work']}
                try:await rt.execute(action,role)
                finally:
                    for w in rt.memory['work']:
                        if w['id'] not in before:
                            w['project_id']=project['project_id'];project['work_ids'].append(w['id'])
                    rt.persist()
            project['status']='awaiting_work' if batch.actions else 'needs_review'
            reconcile_projects(rt.memory)
        except asyncio.CancelledError:
            project['status']='needs_review';project['feedback']=['Execution interrupted; inspect existing orders before continuing.']
            raise
        except (ModelError,ValueError,RuntimeError) as error:
            project['status']='needs_review';project['feedback']=[str(error)]
            rt.note('error',str(error),role=role,project_id=project['project_id'])
        finally:rt.persist()
