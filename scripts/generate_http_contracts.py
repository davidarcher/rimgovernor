"""Bundle the authored OpenAPI contract and generate Python wire models/client methods.

No source scraping or running game. ApiAudit separately checks implementation drift.
"""
import argparse
import copy
import json
import keyword
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
CONTRACTS = ROOT / 'integrations/RIMAPI/Contracts'


def bundle():
    document = json.loads((CONTRACTS / 'rimapi.openapi.json').read_text())
    construction = json.loads((CONTRACTS / 'construction.openapi.json').read_text())
    def rewrite(value, external=False):
        if isinstance(value, list):
            return [rewrite(item, external) for item in value]
        if not isinstance(value, dict):
            return value
        result = {}
        for key, item in value.items():
            if key == '$ref':
                if item.startswith('construction.openapi.json#/components/schemas/') or external:
                    item = '#/components/schemas/Construction_' + item.split('/')[-1]
                elif not item.startswith('#/components/schemas/'):
                    raise ValueError(f'Unsupported reference: {item}')
                result[key] = item
            else:
                result[key] = rewrite(item, external)
        return result
    document = rewrite(document)
    for name, schema in construction['components']['schemas'].items():
        document['components']['schemas']['Construction_' + name] = rewrite(schema, True)
    return document


def generate(document):
    schemas = copy.deepcopy(document['components']['schemas'])
    operations = {}
    for path, methods in document['paths'].items():
        for method, op in methods.items():
            name = op['operationId']
            if name in operations:
                raise ValueError(f'Duplicate operation ID: {name}')
            query = {'type': 'object', 'properties': {}, 'required': [], 'additionalProperties': False}
            for param in op.get('parameters', []):
                if param['in'] != 'query':
                    raise ValueError(f'Unimplemented parameter transport: {name}')
                query['properties'][param['name']] = param['schema']
                if param.get('required'):
                    query['required'].append(param['name'])
            query_name = name + '_Query'
            schemas[query_name] = query
            body = op.get('requestBody', {}).get('content', {}).get('application/json', {}).get('schema')
            response = op['responses'].get('200', {}).get('content', {}).get('application/json', {}).get('schema')
            operations[name] = {'path': path, 'method': method.upper(), 'query_schema': query,
                'query_model': query_name, 'body_schema': body, 'response_schema': response,
                'body_required': op.get('requestBody', {}).get('required', False),
                'query_fallback_required': op.get('x-query-fallback-required', []),
                'responses': op['responses']}
    def identifier(name):
        return name + '_' if keyword.iskeyword(name) or name in ('schema', 'validate', 'model_config', 'model_fields') else name
    def typ(schema):
        if '$ref' in schema:
            return schema['$ref'].split('/')[-1]
        if 'anyOf' in schema:
            return 'Union[' + ', '.join(typ(s) for s in schema['anyOf']) + ']'
        if 'enum' in schema:
            return 'Literal[' + ', '.join(repr(x) for x in schema['enum']) + ']'
        kind = schema.get('type')
        if kind == 'array':
            return 'list[' + typ(schema['items']) + ']'
        if kind == 'object':
            additional = schema.get('additionalProperties', True)
            return 'dict[str, ' + (typ(additional) if isinstance(additional, dict) else 'JsonValue') + ']'
        if kind is None and schema.get('x-dynamic-reason'):
            return 'JsonValue'
        return {'string': 'str', 'integer': 'int', 'number': 'float', 'boolean': 'bool', 'null': 'None'}[kind]
    lines = ['# Generated from Contracts/rimapi.openapi.json. Do not edit.',
        'from __future__ import annotations', 'from typing import Literal, Union',
        'from pydantic import BaseModel, ConfigDict, Field, JsonValue, RootModel, TypeAdapter', '',
        'class WireModel(BaseModel):', "    model_config = ConfigDict(strict=True, extra='forbid', protected_namespaces=())", '']
    # Scalar aliases must exist before model rebuild and TypeAdapter evaluation.
    for name, schema in schemas.items():
        if schema.get('type') != 'object':
            lines.append(name + ' = ' + typ(schema))
    lines.append('')
    classes = []
    for name, schema in schemas.items():
        if schema.get('type') != 'object':
            continue
        props = schema.get('properties')
        if props is None:
            lines += [f'class {name}(RootModel[{typ(schema)}]):', '    pass', '']
            classes.append(name)
            continue
        lines.append(f'class {name}(WireModel):')
        if schema.get('additionalProperties') is True:
            lines.append("    model_config = ConfigDict(strict=True, extra='allow', protected_namespaces=())")
        for field, value in props.items():
            field_type = typ(value)
            required = field in schema.get('required', [])
            if not required:
                field_type = f'Union[{field_type}, None]'
            options = [] if required else ['default=None']
            if identifier(field) != field:
                options.append('alias=' + repr(field))
            for source, target in [('minimum','ge'), ('maximum','le'), ('minItems','min_length'), ('maxItems','max_length')]:
                if source in value:
                    options.append(f'{target}={value[source]!r}')
            lines.append(f'    {identifier(field)}: {field_type} = Field(' + ', '.join(options) + ')')
        if not props:
            lines.append('    pass')
        lines.append('')
        classes.append(name)
    for name in classes:
        lines.append(name + '.model_rebuild()')
    for label, func in [('QUERY_TYPES', lambda o: o['query_model']),
                        ('BODY_TYPES', lambda o: typ(o['body_schema']) if o['body_schema'] else None),
                        ('RESPONSE_TYPES', lambda o: typ(o['response_schema']) if o['response_schema'] else None)]:
        lines += ['', label + ' = {']
        for name, op in operations.items():
            value = func(op)
            if value:
                lines.append(f'    {name!r}: TypeAdapter({value}),')
        lines.append('}')
    lines += ['', 'class HttpOperations:',
        '    async def _call(self, operation_id, *, query=None, body=None):',
        '        raise NotImplementedError', '']
    for name, op in operations.items():
        if not op['response_schema']:
            continue  # Streaming/raw transports must not be buffered as JSON.
        args = f'query: {op["query_model"]} | None = None'
        if op['body_schema']:
            args += f', body: {typ(op["body_schema"])} | None = None'
        lines += [f'    async def {name}(self, *, {args}) -> {typ(op["response_schema"])}:',
            f'        return await self._call({name!r}, query=query' + (', body=body)' if op['body_schema'] else ')'), '']
    return '\n'.join(lines).rstrip() + '\n', operations


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument('--check', action='store_true')
    args = parser.parse_args()
    document = bundle()
    from openapi_spec_validator import validate
    validate(document)
    models, operations = generate(document)
    outputs = {
        ROOT / 'controller/rimbot/http_models.py': models,
        ROOT / 'controller/rimbot/data/http_operations.json': json.dumps(operations, indent=2) + '\n',
        ROOT / 'controller/rimbot/data/rimapi.openapi.json': json.dumps(document, indent=2) + '\n',
        CONTRACTS / 'rimapi.bundled.openapi.json': json.dumps(document, indent=2) + '\n',
    }
    discovery = json.loads((ROOT / 'controller/contracts/discovery.openapi.json').read_text())
    validate(discovery)
    discovery_models, _ = generate(discovery)
    outputs[ROOT / 'controller/rimbot/discovery_models.py'] = discovery_models.replace('Contracts/rimapi.openapi.json', 'controller/contracts/discovery.openapi.json')
    for path, content in outputs.items():
        if args.check:
            if not path.exists() or path.read_text(encoding='utf-8') != content:
                raise SystemExit(f'Generated contract drift: {path}')
        else:
            path.write_text(content, encoding='utf-8')
    print(f'{len(operations)} HTTP operations; {len(document["components"]["schemas"])} schemas; generated files current.')


if __name__ == '__main__':
    main()
