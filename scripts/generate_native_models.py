"""Generate C# DTOs, Python models and wire manifests from the canonical schema.

Edit integrations/RIMAPI/Contracts/construction.openapi.json. No running game
needed. --check fails on generated drift; unsupported schema features fail.
"""
import argparse
import json
from pathlib import Path
from jsonschema import Draft202012Validator

def resolve(schema,definitions):
    if isinstance(schema,list):return [resolve(x,definitions) for x in schema]
    if not isinstance(schema,dict):return schema
    if '$ref' in schema:return resolve(definitions[schema['$ref'].split('/')[-1]],definitions)
    return {k:resolve(v,definitions) for k,v in schema.items()}

def supported(schema):
    allowed={'$ref','title','description','type','properties','required','additionalProperties','items','enum','anyOf','minimum','maximum','minItems','maxItems'}
    unknown=set(schema)-allowed
    if unknown:raise ValueError('Generator/validator does not yet support schema keywords: '+', '.join(sorted(unknown)))
    for child in schema.get('properties',{}).values():supported(child)
    if 'items' in schema:supported(schema['items'])
    for child in schema.get('anyOf',[]):supported(child)

def generate_csharp(document):
    enums={}
    def pascal(s):return ''.join(p[:1].upper()+p[1:] for p in s.split('_'))
    def typ(s,name):
        if '$ref' in s:return s['$ref'].split('/')[-1]
        if 'anyOf' in s:
            choices=[x for x in s['anyOf'] if x.get('type')!='null']
            if len(choices)!=1:raise ValueError('Only explicit nullable unions are supported.')
            base=typ(choices[0],name)
            return base+'?' if base in ('int','float','bool') else base
        if 'enum' in s:
            enums[name]='    [JsonConverter(typeof(StringEnumConverter))]\n    public enum '+name+' { '+', '.join(s['enum'])+' }\n'
            return name
        if s['type']=='array':return 'List<'+typ(s['items'],name+'Item')+'>'
        return {'string':'string','integer':'int','number':'float','boolean':'bool'}[s['type']]
    classes=[]
    for name,s in document['$defs'].items():
        if s['type']!='object':raise ValueError('Named definitions must be object types.')
        if set(s['required'])!=set(s['properties']) or s.get('additionalProperties') is not False:
            raise ValueError('Contracts require explicit fields and nullability.')
        fields=[]
        for key,prop in s['properties'].items():
            fields.append('        [JsonProperty('+json.dumps(key)+')] public '+typ(prop,name+pascal(key)+'Enum')+' '+pascal(key)+' { get; set; }')
        classes.append('    public sealed class '+name+'\n    {\n'+'\n'.join(fields)+'\n    }\n')
    return '// Generated from Contracts/construction.openapi.json. Do not edit.\nusing System.Collections.Generic;\nusing Newtonsoft.Json;\nusing Newtonsoft.Json.Converters;\nnamespace RIMAPI.Contracts\n{\n'+''.join(enums.values())+''.join(classes)+'}\n'

def generate(manifest):
    classes={}
    def annotation(schema):
        if 'anyOf' in schema:return ' | '.join(annotation(s) for s in schema['anyOf'])
        if 'enum' in schema:return 'Literal['+', '.join(repr(x) for x in schema['enum'])+']'
        kind=schema['type']
        if kind=='array':return 'list['+annotation(schema['items'])+']'
        if kind!='object':return {'string':'str','integer':'int','number':'float','boolean':'bool','null':'None'}[kind]
        name=schema['title']
        fields=[]
        for k,v in schema['properties'].items():
            bounds=[f'{py}={v[key]}' for key,py in [('minimum','ge'),('maximum','le'),('minItems','min_length'),('maxItems','max_length')] if key in v]
            fields.append(f'    {k}: {annotation(v)}'+(' = Field('+', '.join(bounds)+')' if bounds else ''))
        body='class '+name+'(NativeObject):\n'+'\n'.join(fields)+'\n'
        if name in classes and classes[name]!=body:raise ValueError('Conflicting native type: '+name)
        classes[name]=body
        return name
    requests={};responses={}
    for endpoint in manifest['endpoints']:
        requests[endpoint['name']]=annotation(endpoint['request_schema'])
        responses[endpoint['name']]=annotation(endpoint['response_schema'])
    annotation(manifest['error_schema'])
    header='"""Generated from Contracts/construction.openapi.json. Do not edit."""\nfrom typing import Literal\nfrom pydantic import BaseModel, ConfigDict, Field\n\nclass NativeObject(BaseModel):\n    model_config = ConfigDict(extra="forbid", strict=True)\n\n'
    result=header+'\n'.join(classes.values())
    for label,values in [('REQUEST_TYPES',requests),('RESPONSE_TYPES',responses)]:
        result+='\n'+label+' = {\n'+''.join(f'    {name!r}: {typ},\n' for name,typ in values.items())+'}\n'
    return result

if __name__=='__main__':
    p=argparse.ArgumentParser(description=__doc__);p.add_argument('--check',action='store_true');args=p.parse_args()
    root=Path(__file__).resolve().parents[1]
    api=json.loads((root/'integrations/RIMAPI/Contracts/construction.openapi.json').read_text())
    if api['openapi']!='3.1.0':raise ValueError('Expected OpenAPI 3.1.0.')
    endpoints=[]
    for path,operations in api['paths'].items():
        for method,op in operations.items():
            endpoints.append({'name':op['operationId'],'path':path,'method':method.upper(),'write':op['x-game-write'],'description':op['description'],
                'request_schema':op['requestBody']['content']['application/json']['schema'],
                'response_schema':op['responses']['200']['content']['application/json']['schema']})
    document={'$defs':api['components']['schemas'],'x-contract-version':api['x-contract-version'],'x-endpoints':endpoints,
              'x-error-schema':{'$ref':'#/components/schemas/ContractError'}}
    for schema in document['$defs'].values():
        Draft202012Validator.check_schema(schema);supported(schema)
    manifest={'version':document['x-contract-version'],'endpoints':resolve(document['x-endpoints'],document['$defs']),
              'error_schema':resolve(document['x-error-schema'],document['$defs']),
              'types':{name:resolve(value,document['$defs']) for name,value in document['$defs'].items()}}
    wire=json.dumps(manifest,indent=2)+'\n'
    native=root/'integrations/RIMAPI/Source/RIMAPI/RimworldRestApi/Contracts'
    outputs={root/'controller/rimbot/native_models.py':generate(manifest),root/'controller/rimbot/data/construction_contracts.json':wire,
             native/'ConstructionModels.g.cs':generate_csharp(document),native/'construction.contracts.json':wire}
    for path,text in outputs.items():
        if args.check:
            if not path.exists() or path.read_text(encoding='utf-8')!=text:raise SystemExit('Generated contract drift: '+str(path))
        else:
            path.parent.mkdir(parents=True,exist_ok=True);path.write_text(text,encoding='utf-8')
    print('Generated contracts match canonical schema.' if args.check else 'Generated C# DTOs, Python models and native wire contracts.')
