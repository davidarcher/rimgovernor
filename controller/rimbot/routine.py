"""Immediate, validated setup commands; no extra model submission required."""
ROUTINE_ENDPOINTS={'orders_unforbid_all','post_things_set_forbidden','zone_growing_cells','post_map_zone_growing','post_map_zone_stockpile'}

def is_routine(action):
    return action.endpoint in ROUTINE_ENDPOINTS or (action.endpoint=='post_pawn_edit_status' and set(action.arguments)=={'pawn_id','hostility_response'} and action.arguments['hostility_response'] is not None)

async def execute_routine(rt,context,action,role):
    from .spatial import validate_orders
    identity=context['project']['project_id']
    project=next((p for p in rt.memory.get('projects',[]) if p['project_id']==identity),None)
    if project is None or project.get('status') in ('retired','suspended'):
        raise ValueError('Project is no longer active; no command executed')
    rt.check_generation()
    if rt.mode!='automate':raise ValueError('Automation is off; no command executed')
    await validate_orders(rt,project,[action])
    before={w['id'] for w in rt.memory['work']}
    try:
        await rt.execute(action,role)
    finally:
        rows=[w for w in rt.memory['work'] if w['id'] not in before]
        for w in rows:
            w['project_id']=project['project_id']
            if w['id'] not in project['work_ids']:project['work_ids'].append(w['id'])
        rt.persist()
    if not rows:return {'already_satisfied':True,'executed':False}
    status=rows[-1]['status']
    if status=='complete':
        rt.note('work_outcome',action.title,role=role,project_id=project['project_id'],results=[{'id':rows[-1]['id'],'title':action.title,'status':status}])
    return {'executed':status in ('complete','issued'),'verified':status=='complete','status':status,'detail':rows[-1]['detail'],'draft_retained':False,'next':'This command has already been sent; do not submit it again. Continue with remaining work.'}
