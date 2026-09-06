"""Generate the manager catalog from the authored OpenAPI contract."""
import json
import re
import argparse
from pathlib import Path
from generate_http_contracts import bundle

root = Path(__file__).resolve().parents[1]
document = bundle()
native_paths = set(json.loads((root / 'integrations/RIMAPI/Contracts/construction.openapi.json').read_text())['paths'])
def expand(schema):
    if isinstance(schema, list): return [expand(x) for x in schema]
    if not isinstance(schema, dict): return schema
    if '$ref' in schema:
        return expand(document['components']['schemas'][schema['$ref'].split('/')[-1]])
    return {k:expand(v) for k,v in schema.items() if not k.startswith('x-')}

routes = []
for path, methods in document['paths'].items():
    if path in native_paths or path.startswith('/api/v2/construction/') or path == '/api/v1/events': continue
    for method, op in methods.items():
        body = op.get('requestBody', {}).get('content', {}).get('application/json', {}).get('schema')
        schema = expand(body) if body else {'type':'object','properties':{},'additionalProperties':False}
        params = [p for p in op.get('parameters',[]) if p['in']=='query']
        schema['properties'].update({p['name']:expand(p['schema']) for p in params})
        schema['required'] = sorted(set(schema.get('required',[])) | {p['name'] for p in params if p.get('required')})
        def strict(value):
            if isinstance(value,dict):
                if 'properties' in value:value['additionalProperties']=False
                for child in value.values():strict(child)
            elif isinstance(value,list):
                for child in value:strict(child)
        strict(schema)
        name=method+'_'+re.sub(r'[^a-zA-Z0-9]+','_',path.removeprefix('/api/v1/')).strip('_')
        routes.append({'name':name,'operation_id':op['operationId'],'method':method.upper(),'path':path,
            'category':op['tags'][0],'description':op.get('summary','')+'. '+op.get('description',''),
            'transport':'json' if body else 'query','query_keys':[p['name'] for p in params], 'schema':schema})
output={'source':'Contracts/rimapi.openapi.json','revision':document['info']['version'],'endpoints':sorted(routes,key=lambda e:e['name'])}
parser=argparse.ArgumentParser();parser.add_argument('--check',action='store_true');args=parser.parse_args()
destination=root/'controller/rimbot/data/catalog.json';content=json.dumps(output,indent=2)+'\n'
if args.check:
    if destination.read_text()!=content:raise SystemExit('Manager catalog drift; regenerate from OpenAPI.')
else:destination.write_text(content)
print(f'Generated {len(routes)} manager contracts from OpenAPI.')
