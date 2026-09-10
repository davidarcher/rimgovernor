"""Check G01 interface inventory against source without importing the runtime."""
import argparse
import ast
import json
from pathlib import Path
import re


ROOT = Path(__file__).resolve().parents[1]
GO_PACKAGES = {'go/cmd/rimgovernor'} | {
    'go/internal/' + name for name in ('wire', 'domain', 'bridge', 'store', 'policy',
                                      'hands', 'runtime', 'model', 'server',
                                      'presentation', 'testkit')}


def expected_ids():
    expected = set()
    modules = ('bridge_server', 'dashboard_controls', 'video_stream',
               'colony_people', 'local_colonies', 'scenario_dashboard')
    for module in modules:
        tree = ast.parse((ROOT / f'controller/rimgovernor/{module}.py').read_text(encoding='utf8'))
        prefix = ''
        for node in ast.walk(tree):
            if isinstance(node, ast.Call) and isinstance(node.func, ast.Name) and node.func.id == 'APIRouter':
                prefix = next((ast.literal_eval(k.value) for k in node.keywords if k.arg == 'prefix'), '')
        for node in ast.walk(tree):
            if not isinstance(node, (ast.FunctionDef, ast.AsyncFunctionDef)):
                continue
            for dec in node.decorator_list:
                if not isinstance(dec, ast.Call) or not isinstance(dec.func, ast.Attribute):
                    continue
                if dec.func.attr in ('get', 'post', 'delete', 'put', 'patch', 'websocket'):
                    expected.add(f'interface:{module}:{dec.func.attr.upper()} {prefix}{ast.literal_eval(dec.args[0])}')
    for module, classes in [('config', ('Settings', 'ModelRouting')),
                            ('colony_policy', ('ColonyPolicy',)),
                            ('controller_settings', ('PolicyChanges', 'PolicyUpdate'))]:
        tree = ast.parse((ROOT / f'controller/rimgovernor/{module}.py').read_text(encoding='utf8'))
        for cls in tree.body:
            if isinstance(cls, ast.ClassDef) and cls.name in classes:
                for node in cls.body:
                    if isinstance(node, ast.AnnAssign):
                        expected.add(f'interface:config:{cls.name}.{node.target.id}')
    for module in ('__main__', 'container_worker', 'container_input_cache'):
        tree = ast.parse((ROOT / f'controller/rimgovernor/{module}.py').read_text(encoding='utf8'))
        for node in ast.walk(tree):
            if isinstance(node, ast.Call) and isinstance(node.func, ast.Attribute) and node.func.attr == 'add_argument':
                expected.add(f'interface:cli:{module}:{ast.literal_eval(node.args[0])}')
    tree = ast.parse((ROOT / 'controller/rimgovernor/local_colonies.py').read_text(encoding='utf8'))
    discovery = next(node for node in tree.body
                     if isinstance(node, ast.FunctionDef) and node.name == 'docker_executable')
    for node in ast.walk(discovery):
        if isinstance(node, ast.For) and isinstance(node.target, ast.Tuple):
            if [part.id for part in node.target.elts] == ['base', 'suffix']:
                for base, _ in ast.literal_eval(node.iter):
                    expected.add('interface:env-discovery:' + base)
    files = list((ROOT / 'controller/rimgovernor').glob('*.py')) + [
        ROOT / 'containers/compose.yaml', ROOT / 'containers/colonies.compose.yaml']
    for path in files:
        for name in re.findall(r'\bRIMGOVERNOR_[A-Z_]+\b', path.read_text(encoding='utf8')):
            expected.add('interface:env:' + name)
    return expected


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--check', action='store_true', help='Validate checked-in inventory (also the default).')
    parser.parse_args()
    data = json.loads((ROOT / 'contracts/interface-inventory.json').read_text(encoding='utf8'))
    assert data['version'] == 1
    assert re.fullmatch('[0-9a-f]{40}', data['source_revision'])
    ids = set()
    for row in data['items']:
        assert row['id'] not in ids, f'Duplicate ID: {row["id"]}'
        ids.add(row['id'])
        for key in ('category', 'go_package', 'owner_chunk', 'status', 'notes'):
            assert row[key], f'{row["id"]}: missing {key}'
        assert row['go_package'] in GO_PACKAGES, f'Unapproved package: {row["go_package"]}'
        if row['id'].startswith('interface:cli:container_input_cache:'):
            assert row['category'] == 'configuration-cli-tooling'
            assert row['status'] == 'tooling-only-adapter-pending'
        source = ROOT / row['source']['path']
        assert source.is_file(), source
        if 'line' in row['source']:
            assert 1 <= row['source']['line'] <= len(source.read_text(encoding='utf8').splitlines())
        for field in ('fixtures', 'native_scenarios'):
            assert isinstance(row[field], list)
            for path in row[field]:
                assert (ROOT / path.split('::')[0]).is_file(), path
    expected = expected_ids()
    assert not expected - ids, f'Uninventoried source surfaces: {sorted(expected - ids)}'
    source_categories = {'http-route', 'websocket-route', 'configuration-field',
                         'configuration-environment', 'configuration-cli',
                         'configuration-cli-tooling'}
    checked = {row['id'] for row in data['items'] if row['category'] in source_categories
               and not row['id'].startswith(('interface:env-pass-through:', 'interface:cli:launch.ps1:'))}
    assert not checked - expected, f'Stale source surfaces: {sorted(checked - expected)}'
    print(f'Interface inventory OK: {len(ids)} rows; {len(expected)} source-derived route/configuration IDs.')
    print('Checks source coverage and locators only; endpoint behavior, native outcomes and Go parity remain pending.')


if __name__ == '__main__':
    main()
