"""Administrator holds on existing projects, independent of medical interruptions."""

def validate_controls(decision,projects):
    known={p['project_id']:p for p in projects if p.get('status')!='retired'}
    suspend=set(decision.suspend_projects);resume=set(decision.resume_projects)
    if (suspend|resume)-known.keys() or suspend&resume or (suspend|resume)&set(decision.retire_projects):
        raise ValueError('Suspend/resume must reference active existing projects and cannot conflict with retirement or each other')
    if len(resume)!=len(decision.resume_projects):raise ValueError('Duplicate resume project')
    if any(not reason.strip() for reason in decision.suspend_projects.values()):raise ValueError('Project hold requires a reason')
    if any(not known[p].get('admin_hold') for p in resume):raise ValueError('Resume only administrator-held projects')


def apply_controls(rt,decision):
    for project in rt.memory.get('projects',[]):
        identity=project['project_id']
        if identity in decision.suspend_projects:
            hold=project.setdefault('admin_hold',{'resume_status':project.get('status','approved')})
            if project.get('status')!='suspended':hold['resume_status']=project.get('status','approved')
            reason=decision.suspend_projects[identity].strip()
            changed=hold.get('reason')!=reason
            hold['reason']=reason;project['status']='suspended'
            if changed:rt.note('project_suspended',reason,project_id=identity,source='administrator')
        elif identity in decision.resume_projects:
            hold=project.pop('admin_hold')
            project['status']=hold['resume_status'];project.pop('execution_review',None)
            rt.note('project_resumed','Administrator released the hold',project_id=identity,source='administrator')
