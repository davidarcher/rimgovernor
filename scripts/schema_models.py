"""Generate strict Python DTOs from JSON Schema."""
def resolve(schema,definitions):
    if isinstance(schema,list):return [resolve(x,definitions) for x in schema]
    if not isinstance(schema,dict):return schema
    if '$ref' in schema:return resolve(definitions[schema['$ref'].split('/')[-1]],definitions)
    return {k:resolve(v,definitions) for k,v in schema.items()}

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
    header='"""Generated from data/bridge_observation.schema.json. Do not edit."""\nfrom typing import Literal\nfrom pydantic import BaseModel, ConfigDict, Field\n\nclass NativeObject(BaseModel):\n    model_config = ConfigDict(extra="forbid", strict=True)\n\n'
    result=header+'\n'.join(classes.values())
    for label,values in [('REQUEST_TYPES',requests),('RESPONSE_TYPES',responses)]:
        result+='\n'+label+' = {\n'+''.join(f'    {name!r}: {typ},\n' for name,typ in values.items())+'}\n'
    return result
