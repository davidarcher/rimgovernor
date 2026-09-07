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
        keep=('project_id','owner','kind','outcome','status','quantity','crop_def','target_cells','definition_requirements','constraints','success_signals','progress','feedback')
        out={k:v for k,v in p.items() if k in keep}
        if isinstance(out.get('progress'),dict):
            orders=out['progress'].pop('orders',[])
            from collections import Counter
            out['progress']['order_counts']=dict(Counter(w.get('status','unknown') for w in orders))
        return out
    if isinstance(result.get('spatial_reservations'),dict):
        result['spatial_reservations']={k:v for k,v in result['spatial_reservations'].items() if k not in ('observed_land','native_plans','colors')}
    if isinstance(result.get('project'),dict):result['project']=project(result['project'])
    if isinstance(result.get('projects'),list):result['projects']=[project(p) for p in result['projects']]
    if isinstance(result.get('work'),list):
        rows=result['work'];result['work']=[{k:w[k] for k in ('id','project_id','title','status','detail') if k in w} for w in rows[-12:]]
        result['work_omitted']=max(0,len(rows)-12)
    return result

def shorten(value,limit):
    if isinstance(value,str):return value if len(value)<=limit else value[:limit]+' [truncated; request narrower evidence]'
    if isinstance(value,list):
        return [shorten(v,limit) for v in value[:12]]+([{'omitted_rows':len(value)-12}] if len(value)>12 else [])
    if isinstance(value,dict):return {k:shorten(v,limit) for k,v in value.items()}
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
