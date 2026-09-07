"""Bound model-facing context without changing native API results or queued orders."""
import copy,json

def encoded_size(value):
    # Images are encoded separately by the vision model; reserve tokens, not base64 bytes.
    if isinstance(value,dict) and value.get('type')=='image_url':return 8192
    if isinstance(value,dict):return 16+sum(len(str(k).encode())+encoded_size(v) for k,v in value.items())
    if isinstance(value,list):return 8+sum(encoded_size(v) for v in value)
    return len(json.dumps(value,ensure_ascii=False).encode('utf-8'))+4

def project_context(context):
    result=copy.deepcopy(context)
    def project(p):
        keep=('project_id','owner','kind','outcome','status','quantity','crop_def','target_cells','definition_requirements','constraints','success_signals','progress','outcome_evidence','feedback','priority','resource_request','interruption')
        out={k:v for k,v in p.items() if k in (*keep,'after_projects','deadline_tick','deadline_overdue','dependency_blockers','work_policy','work_allocation')}
        if isinstance(out.get('progress'),dict):
            orders=out['progress'].pop('orders',[])
            from collections import Counter
            out['progress']['order_counts']=dict(Counter(w.get('status','unknown') for w in orders))
        return out
    if isinstance(result.get('spatial_reservations'),dict):
        result['spatial_reservations']={k:v for k,v in result['spatial_reservations'].items() if k not in ('observed_land','native_plans','colors')}
        for name in ('regions','corridors','defensive_lines','reserved_regions'):
            for region in result['spatial_reservations'].get(name,[]):
                if region.get('patches'):region['patches']=merge_patches(region['patches'])
    kind=result.get('project',{}).get('kind')
    if kind:
        if kind!='work_assignment':result.pop('native_work_types',None)
        # Other projects explain ownership, not their entire execution history.
        if 'projects' in result:
            result['projects']=[{k:v for k,v in p.items() if k in ('project_id','owner','kind','outcome','status','quantity','crop_def','target_cells')}
                                for p in result['projects'] if p.get('project_id')!=result['project'].get('project_id')]
        if isinstance(result.get('spatial_reservations'),dict):
            result['spatial_reservations']={k:v for k,v in result['spatial_reservations'].items()
                if k in ('version','summary','zones','regions','corridors','defensive_lines','reserved_regions')}
        if kind not in ('construction','growing','storage'):
            result.pop('spatial_reservations',None)
        # Supply details remain queryable; don't preload unrelated map systems.
        groups={
            'supply_access':('supplies','supply_summary'),
            'construction':('supplies','supply_summary'),
            'storage':('supplies','supply_summary'),
            'growing':('terrain','food_crops','supply_summary'),
        }.get(kind)
        if groups and 'resource_overview' in result:
            source=result['resource_overview']
            result['resource_overview']={k:v for k,v in source.items() if k in (*groups,'available','reason','follow_up')}
    if isinstance(result.get('project'),dict):result['project']=project(result['project'])
    if isinstance(result.get('projects'),list):result['projects']=[project(p) for p in result['projects']]
    if isinstance(result.get('work'),list):
        rows=result['work'];result['work']=[{k:w[k] for k in ('id','project_id','title','status','detail') if k in w} for w in rows[-12:]]
        result['work_omitted']=max(0,len(rows)-12)
    return result


def merge_patches(patches):
    """Lossless union into rectangles; preserve holes and all reserved cells."""
    rows={}
    for p in patches:
        for z in range(p['z1'],p['z2']+1):
            rows.setdefault(z,set()).update(range(p['x1'],p['x2']+1))
    rectangles=[];active={}
    for z,xs in sorted(rows.items()):
        spans=[]
        for x in sorted(xs):
            if spans and spans[-1][1]+1==x:spans[-1][1]=x
            else:spans.append([x,x])
        next_active={}
        for a,b in spans:
            previous=active.get((a,b))
            if previous is not None and previous['z2']==z-1:
                previous['z2']=z
            else:
                previous={'x1':a,'x2':b,'z1':z,'z2':z};rectangles.append(previous)
            next_active[(a,b)]=previous
        active=next_active
    return rectangles

def shorten(value,limit):
    if isinstance(value,str):return value if len(value)<=limit else value[:limit]+' [truncated; request narrower evidence]'
    if isinstance(value,list):
        return [shorten(v,limit) for v in value[:12]]+([{'omitted_rows':len(value)-12}] if len(value)>12 else [])
    if isinstance(value,dict):
        # Indexed spatial transport is one inseparable observation. Truncating
        # classes separately from coordinates corrupts the map's meaning.
        spatial={'terrain_grid','terrain_classes','terrain_rectangles_x1_z1_x2_z2_class','terrain_runs_z_x1_x2_class','survey_bounds'}
        return {k:v if k in spatial else shorten(v,limit) for k,v in value.items()}
    return value

def fit_request(messages,tools,context_tokens,output_tokens):
    budget=context_tokens-output_tokens-2048
    if budget<4096:raise ValueError('Context window must leave at least 4096 input tokens after output allowance and safety reserve')
    messages=copy.deepcopy(messages);tools=copy.deepcopy(tools)
    size=lambda:encoded_size(messages)+encoded_size(tools)
    original=size()
    if original<=budget:return messages,tools,{'input_budget':budget,'budget_units':original,'compacted':False}
    # Remove old complete conversational groups. Preserve instructions, initial context,
    # and the latest assistant/tool group; never leave orphaned tool responses.
    while size()>budget:
        starts=[i for i,m in enumerate(messages) if m['role']=='assistant']
        if len(starts)<2:break
        del messages[starts[0]:starts[1]]
    # Descriptions may be shortened, but argument types, enums and required fields remain intact.
    def schema_trim(v):
        if isinstance(v,dict):return {k:(val[:160] if k=='description' and isinstance(val,str) else schema_trim(val)) for k,val in v.items() if not (k=='title' and isinstance(val,str))}
        if isinstance(v,list):return [schema_trim(x) for x in v]
        return v
    if size()>budget:tools=schema_trim(tools)
    for limit in (1800,600,200):
        if size()<=budget:break
        for message in messages:
            if message['role'] in ('system','assistant'):continue
            content=message.get('content')
            if isinstance(content,str):
                try:content=json.dumps(shorten(json.loads(content),limit),ensure_ascii=False,separators=(',',':'))
                except (ValueError,TypeError):content=shorten(content,limit)
                message['content']=content
            elif isinstance(content,list):
                for block in content:
                    if block.get('type')=='text':
                        try:block['text']=json.dumps(shorten(json.loads(block['text']),limit),ensure_ascii=False,separators=(',',':'))
                        except ValueError:block['text']=shorten(block['text'],limit)
    # An enormous tool result can still dominate (for example a whole-map survey).
    for message in messages:
        if size()<=budget:break
        if message['role']=='tool' and encoded_size(message)>2048:
            message['content']=json.dumps({'omitted':True,'reason':'Tool result exceeds request budget; query a smaller area or filtered list. Do not assume missing facts.'})
    if size()>budget:raise ValueError(f'Request cannot fit safely: {size()} conservative input units, budget {budget}. Narrow the project context or tool surface; no inference request sent.')
    return messages,tools,{'input_budget':budget,'budget_units':size(),'original_units':original,'compacted':True}
