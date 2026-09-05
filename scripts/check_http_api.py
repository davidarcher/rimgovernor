"""Compare a fresh Roslyn source audit with the reviewed OpenAPI contract.

Run ApiAudit first (see docs/OPENAPI.md). This checks coverage, handler changes,
and native DTO changes; it never regenerates the authored contract from C#.
"""
import argparse
import hashlib
import json
from pathlib import Path


def digest(value):
    def normalized(item):
        if isinstance(item, str):
            return item.replace('\r', '')
        if isinstance(item, list):
            return [normalized(x) for x in item]
        if isinstance(item, dict):
            return {k: normalized(v) for k, v in item.items()}
        return item
    return hashlib.sha256(json.dumps(normalized(value), sort_keys=True, separators=(',', ':')).encode()).hexdigest()


def check(document, audit):
    routes = {(r['method'].lower(), r['path']): r for r in audit['routes'] if r['registered']}
    # ApiServer.RegisterManualRoutes registers the SSE connection outside controllers.
    expected = set(routes) | {('get', '/api/v1/events')}
    actual = {(m, p) for p, methods in document['paths'].items() for m in methods}
    if actual != expected:
        raise ValueError(f'HTTP route drift: missing={expected-actual}, undocumented implementation gaps={actual-expected}')
    excluded = {(r['method'], r['path']) for r in document['x-unregistered-routes']}
    if excluded != {(r['method'], r['path']) for r in audit['routes'] if not r['registered']}:
        raise ValueError('Unregistered handler inventory changed; review registration.')
    for (method, path), route in routes.items():
        op = document['paths'][path][method]
        recorded = op.get('x-native-source-sha256')
        if recorded and recorded != digest({'body': route['body'], 'calls': route['calls']}):
            raise ValueError(f'Native handler changed: {method.upper()} {path}; review its contract.')
    for name, schema in document['components']['schemas'].items():
        recorded = schema.get('x-native-type-sha256')
        if recorded and recorded != digest(audit['types'].get(schema['x-native-type'])):
            raise ValueError(f'Native DTO changed: {name}; review its wire schema.')


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument('audit', type=Path)
    args = parser.parse_args()
    root = Path(__file__).resolve().parents[1]
    document = json.loads((root / 'integrations/RIMAPI/Contracts/rimapi.openapi.json').read_text())
    audit = json.loads(args.audit.read_text(encoding='utf-8-sig'))
    check(document, audit)
    print('OpenAPI route coverage and reviewed native signatures match.')


if __name__ == '__main__':
    main()
