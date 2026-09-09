"""Measure local chat interpretation against fixed facts; never execute game writes."""
import argparse
import asyncio
import json
import time
import hashlib
from math import sqrt
from pathlib import Path
from rimbot.config import Settings, ModelRole, load_model_routing
from rimbot.model import LocalModel
from rimbot.model_router import ModelRouter
from rimbot.player_commands import COMMAND,COMMAND_NAMES,semantic_tools,resolve_goal_id,resolve_resource
from rimbot.store import Store

CASES = [
    ('research', 'Set research to geothermal.', {'kind':'SetResearch','project':'GeothermalPower'}),
    ('food', 'Get us to 20 days of food.', {'kind':'CreateGoal','goal':'EnsureFoodSupply','food_days':20}),
    ('policy', "Stop spending components unless needed for defense.",
     {'kind':'ModifyResourcePolicy','resource':'ComponentIndustrial','spending':'defense_only'}),
    ('cancel', 'Cancel the food expansion.', {'kind':'CancelGoal','goal':'intent-food-expansion'}),
    ('cancel_supply', 'Cancel the food supply goal, not the expansion.', {'kind':'CancelGoal','goal':'EnsureFoodSupply'}),
    ('resume_supply', 'Resume the food supply goal with a target of 12 days.',
     {'kind':'CreateGoal','goal':'EnsureFoodSupply','food_days':12}),
    ('research_label', 'Set research to advanced lights.', {'kind':'SetResearch','project':'ColoredLights'}),
    ('steel_reserve', 'Keep 100 steel in reserve.',
     {'kind':'SetResourceReserve','resource':'Steel','reserve':100}),
    ('combined_component_policy', 'Keep 5 components in reserve and spend components only for defense.',
     [{'kind':'SetResourceReserve','resource':'ComponentIndustrial','reserve':5},
      {'kind':'ModifyResourcePolicy','resource':'ComponentIndustrial','spending':'defense_only'}]),
    ('two_resources', 'Stop spending steel and components.',
     [{'kind':'ModifyResourcePolicy','resource':resource,'spending':'stop'}
      for resource in ('Steel','ComponentIndustrial')]),
    ('different_resource_rules', 'Stop spending steel, but allow normal spending of components.',
     [{'kind':'ModifyResourcePolicy','resource':'Steel','spending':'stop'},
      {'kind':'ModifyResourcePolicy','resource':'ComponentIndustrial','spending':'normal'}]),
    ('clear_reserve_keep_spending', 'Clear the component reserve. Leave its spending restriction unchanged.',
     {'kind':'SetResourceReserve','resource':'ComponentIndustrial','reserve':0}),
    ('resume_keep_reserve', 'Resume normal steel spending without changing its reserve.',
     {'kind':'ModifyResourcePolicy','resource':'Steel','spending':'normal'}),
    ('three_commands', 'Set a 12-day food target, keep 80 steel in reserve, and stop spending components.',
     [{'kind':'CreateGoal','goal':'EnsureFoodSupply','food_days':12},
      {'kind':'SetResourceReserve','resource':'Steel','reserve':80},
      {'kind':'ModifyResourcePolicy','resource':'ComponentIndustrial','spending':'stop'}]),
    ('advanced_components', 'Stop spending advanced components. Leave ordinary components alone.',
     {'kind':'ModifyResourcePolicy','resource':'ComponentSpacer','spending':'stop'}),
    ('medicine', 'Keep 10 medicine in reserve. Leave herbal and glitterworld medicine unchanged.',
     {'kind':'SetResourceReserve','resource':'MedicineIndustrial','reserve':10}),
    ('explain', "Why aren't you building the workshop?", None),
]
FACTS = {
    'research_projects':[{'label':'Geothermal power','defName':'GeothermalPower'},
                         {'label':'Advanced lights','defName':'ColoredLights'}],
    'resources':{'ComponentIndustrial':10,'Steel':120,'ComponentSpacer':2,'Plasteel':30,
                 'MedicineHerbal':20,'MedicineIndustrial':10,'MedicineUltratech':2},
    'policyResources':{'ComponentIndustrial':'component','ComponentSpacer':'advanced component',
                       'Steel':'steel','Plasteel':'plasteel','MedicineHerbal':'herbal medicine',
                       'MedicineIndustrial':'medicine','MedicineUltratech':'glitterworld medicine'},
    'resource_policy':{'ComponentIndustrial':{'reserve':3,'spending':'defense_only'},
                       'Steel':{'reserve':40,'spending':'stop'}},
    'goals':{'intent-food-expansion':{'label':'food expansion','status':'active'},
             'EnsureFoodSupply':{'label':'food supply','status':'blocked','cancelled':True,'target':{'food_days':20}},
             'workshop':{'status':'blocked','reason':'Requires 160 steel; only 120 is unreserved'}},
}


def normalize(request):
    request=dict(request)
    if request['kind'] in ('ModifyResourcePolicy','SetResourceReserve'):
        request['resource']=resolve_resource(request['resource'],FACTS['policyResources'])
    if request['kind']=='CancelGoal': request['goal']=resolve_goal_id(request['goal'],FACTS['goals'])
    if request['kind']=='SetResearch':
        matches=[p['defName'] for p in FACTS['research_projects']
                 if request['project'].casefold() in (p['label'].casefold(),p['defName'].casefold())]
        if len(matches)==1: request['project']=matches[0]
    return request


def score(answer,expected):
    calls=answer.get('tool_calls',[])
    expected_requests = expected if isinstance(expected,list) else [expected] if expected else []
    row={'schema_failures':0,'semantic_errors':0,'unnecessary_calls':max(0,len(calls)-len(expected_requests)),
         'correct':False,'requests':[]}
    for call in calls:
        try:
            name=call.get('function',{}).get('name')
            if name not in COMMAND_NAMES: raise ValueError('Unsupported tool')
            request=COMMAND.validate_python(dict(json.loads(call['function']['arguments']),kind=name)).model_dump()
            row['requests'].append(request)
        except (ValueError,KeyError,TypeError): row['schema_failures']+=1
    if row['schema_failures']: return row
    try:
        # Compare the entire typed request, including default values. Extra writes
        # and unrequested numeric parameters cannot earn a passing score.
        canonical = lambda rows: sorted(json.dumps(normalize(COMMAND.validate_python(r).model_dump()),sort_keys=True) for r in rows)
        row['correct']=(canonical(row['requests'])==canonical(expected_requests)
            if expected_requests else not calls and all(word in (answer.get('content') or '').lower() for word in ('steel','160','120')))
    except ValueError: pass  # A valid schema with an unresolved goal is a semantic error.
    row['semantic_errors']=int(not row['correct'])
    return row


def reliability(rows):
    """Wilson interval describes these fixed cases, not all possible player commands."""
    n = len(rows)
    correct = sum(row['correct'] for row in rows)
    if not n: return {'correct':0,'total':0,'accuracy':None,'wilson95':None}
    p, z = correct/n, 1.96
    center = (p + z*z/(2*n))/(1+z*z/n)
    margin = z*sqrt(p*(1-p)/n+z*z/(4*n*n))/(1+z*z/n)
    return {'correct':correct,'total':n,'accuracy':p,'wilson95':[max(0,center-margin),min(1,center+margin)]}


async def run(output, *, model=None, repeats=1):
    output.mkdir(parents=True,exist_ok=False)
    store=Store(output/'metrics.sqlite')
    settings=Settings(**({'model':model} if model else {}))
    router=ModelRouter(load_model_routing(settings),store,LocalModel)
    rows=[]
    async def progress(_): pass
    tools=semantic_tools(resources=FACTS['policyResources'])
    manifest={'settings':settings.model_dump(),'repeats':repeats,
              'case_sha256':hashlib.sha256(json.dumps([CASES,FACTS,tools],sort_keys=True).encode()).hexdigest(),
              'scope':'Fixed-fact semantic interpretation only; no native gameplay acceptance or general reliability guarantee.'}
    (output/'manifest.json').write_text(json.dumps(manifest,indent=2),encoding='utf8')
    try:
        for repeat, (identity,prompt,expected) in ((r,case) for r in range(repeats) for case in CASES):
            start=time.monotonic()
            row={'id':identity,'repeat':repeat+1,'model':settings.model,'prompt':prompt,'expected':expected,'schema_failures':0,'semantic_errors':0,'unnecessary_calls':0,'correct':False}
            try:
                answer,usage=await router.complete(ModelRole.STRATEGIST,[
                    {'role':'system','content':'You interpret RimWorld player commands. Use semantic tools only for '
                     'explicit orders. Explain questions from facts. The deterministic validator owns legality and '
                     'execution; never claim a requested action has completed. Facts: '+json.dumps(FACTS)},
                    {'role':'user','content':prompt}], tools, progress)
                row.update(answer=answer,usage=usage,**score(answer,expected))
            except Exception as error:
                row['error']=str(error)
            row['seconds']=round(time.monotonic()-start,3)
            rows.append(row)
            (output/'results.json').write_text(json.dumps({'cases':rows,'game_writes':0},indent=2),encoding='utf8')
            print(json.dumps({k:v for k,v in row.items() if k not in ('answer','requests','usage')}),flush=True)
    finally:
        await router.close()
        store.close()
    report={'correct':sum(r['correct'] for r in rows),'total':len(rows),
            'schema_failures':sum(r['schema_failures'] for r in rows),
            'semantic_errors':sum(r['semantic_errors'] for r in rows),
            'request_errors':sum('error' in r for r in rows),
            'unnecessary_calls':sum(r['unnecessary_calls'] for r in rows),'game_writes':0,'cases':rows,
            'reliability':reliability(rows),'by_case':{identity:reliability([r for r in rows if r['id']==identity])
                for identity,_,_ in CASES},'manifest':manifest}
    (output/'results.json').write_text(json.dumps(report,indent=2),encoding='utf8')
    return report['correct']==len(CASES)*repeats


if __name__=='__main__':
    parser=argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--output',type=Path,required=True)
    parser.add_argument('--model',help='Exact local LM Studio model identifier')
    parser.add_argument('--repeats',type=int,default=1)
    args=parser.parse_args()
    if not 1<=args.repeats<=100: parser.error('--repeats must be between 1 and 100')
    raise SystemExit(0 if asyncio.run(run(args.output,model=args.model,repeats=args.repeats)) else 1)
