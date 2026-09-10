"""Check static G01 domain coverage without importing the Python runtime.

Native-tool rows conservatively include exact native names declared or referenced in
controller source. Discovery and computed names remain a separately inventoried
boundary: this check does not prove native availability, permissions or gameplay.
"""
import argparse
import ast
from collections import Counter
import json
from pathlib import Path
import re
import subprocess

ROOT = Path(__file__).resolve().parents[1]
TOOL = re.compile(r'(?:home|rimworld)/[a-z][a-z0-9_]*|games_[a-z][a-z0-9_]*')


def literal_fields(path, field):
    tree = ast.parse(path.read_text(encoding='utf-8-sig'))
    for cls in (n for n in tree.body if isinstance(n, ast.ClassDef)):
        for node in cls.body:
            if (isinstance(node, ast.AnnAssign) and isinstance(node.target, ast.Name)
                    and node.target.id == field and isinstance(node.annotation, ast.Subscript)
                    and isinstance(node.annotation.value, ast.Name)
                    and node.annotation.value.id == 'Literal'):
                values = node.annotation.slice
                values = values.elts if isinstance(values, ast.Tuple) else [values]
                for value in values:
                    if isinstance(value, ast.Constant) and isinstance(value.value, str):
                        yield value.value, cls.name, node.lineno


def inventory_sources(root=ROOT):
    result = {}
    tracked = subprocess.run(['git', 'ls-files', '-z', '--', 'controller'], cwd=root, check=True, capture_output=True, text=True).stdout
    for relative in sorted(name for name in tracked.split('\0') if name.endswith('.py')):
        path = root / relative
        relative = path.relative_to(root).as_posix()
        result['module:' + relative] = {'path': relative}
        for node in ast.walk(ast.parse(path.read_text(encoding='utf-8-sig'))):
            if isinstance(node, ast.Constant) and isinstance(node.value, str) and TOOL.fullmatch(node.value):
                result.setdefault('native-tool:' + node.value, {'path': relative, 'line': node.lineno})
    for category, name, field in [('semantic-command', 'player_commands', 'kind'),
                                   ('action-kind', 'colony_plan', 'kind'),
                                   ('completion-kind', 'colony_plan', 'completion')]:
        path = root / 'controller' / 'rimgovernor' / (name + '.py')
        for value, symbol, line in literal_fields(path, field):
            result[category + ':' + value] = {'path': path.relative_to(root).as_posix(), 'symbol': symbol, 'line': line}
    result['native-tool:dynamic-discovery'] = {'path': 'controller/rimgovernor/bridge.py', 'symbol': 'BridgeClient.call'}
    result['native-tool:argument-sensitive-dispatch'] = {'path': 'controller/rimgovernor/bridge_game.py', 'symbol': 'is_write'}
    return result


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--check', action='store_true', help='Validate inventory coverage (the default).')
    parser.parse_args()
    manifest = json.loads((ROOT / 'contracts/domain-inventory.json').read_text(encoding='utf-8'))
    assert manifest['version'] == 1
    assert re.fullmatch(r'[0-9a-f]{40}', manifest['source_revision'])
    items = manifest['items']
    ids = [row['id'] for row in items]
    assert len(ids) == len(set(ids)), 'Duplicate inventory IDs'
    expected = inventory_sources()
    assert set(ids) == set(expected), f'Missing: {set(expected)-set(ids)}; stale: {set(ids)-set(expected)}'
    for row in items:
        assert set(row) == {'id', 'category', 'source', 'go_package', 'owner_chunk', 'fixtures', 'native_scenarios', 'status', 'notes'}, row['id']
        assert row['source'] == expected[row['id']], f'Stale source location: {row["id"]}'
        assert row['status'] in {'pending', 'in-progress', 'migrated', 'retained-tooling', 'retired'}
        assert re.fullmatch(r'G01\.\d{2}[a-z]?', row['owner_chunk']), row['id']
        assert re.fullmatch(r'go/(?:cmd/rimgovernor|internal/(?:wire|domain|bridge|store|policy|hands|runtime|model|server|presentation|testkit)(?:/[a-z][a-z0-9_]*)*)', row['go_package']), row['id']
        assert row['notes'], row['id']
        assert isinstance(row['fixtures'], list) and isinstance(row['native_scenarios'], list)
        for path in row['fixtures'] + row['native_scenarios']:
            assert (ROOT / path).is_file(), (row['id'], path)
    print(json.dumps({'items': len(items), 'categories': dict(Counter(row['category'] for row in items)),
                      'source_revision': manifest['source_revision']}, indent=2))


if __name__ == '__main__':
    main()
