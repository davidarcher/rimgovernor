"""Check or regenerate synthetic Python serialization comparison fixtures.

Uses real constructors/serializers and existing recovery test helpers. Never connects
to a game or opens a database. Run in the controller test environment.
"""
import argparse
import ast
import base64
from copy import deepcopy
import hashlib
import json
from pathlib import Path
import runpy
import sys

ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(ROOT / 'controller'))
DESTINATION = ROOT / 'contracts/fixtures/serialization-baseline.json'
REVISION = '28d115ee2e87dea536737f87b19b445a6c5b4b10'


def provenance(path, symbol):
    tree = ast.parse((ROOT / path).read_text(encoding='utf-8'))
    name = symbol
    if '.' in symbol:
        owner, name = symbol.split('.', 1)
        tree = next(n for n in ast.walk(tree) if isinstance(n, ast.ClassDef) and n.name == owner)
    node = next(n for n in ast.walk(tree)
                if isinstance(n, (ast.FunctionDef, ast.AsyncFunctionDef)) and n.name == name)
    return {'path': path, 'symbol': symbol, 'line': node.lineno}


def build():
    from rimgovernor.colony_plan import PlanStep, StepProgress

    archive = runpy.run_path(str(ROOT / 'controller_tests/test_plan_archive.py'))
    base = archive['action']('action-0').model_dump(mode='json')
    source = provenance('controller/rimgovernor/colony_plan.py', 'signature')
    cases = []

    def action_case(name, arguments, note, **optional):
        data = deepcopy(base)
        data['action']['arguments'] = arguments
        data['action'].update(optional)
        step = PlanStep.model_validate(data)
        serialized = step.action.model_dump_json().encode('utf-8')
        signature = step.signature()
        if hashlib.sha256(serialized).hexdigest() != signature:
            raise ValueError('Production signature no longer hashes serialized action bytes')
        cases.append({'id': 'serialization.' + name, 'category': 'action_serialization',
                      'source': source, 'input': data['action'],
                      'action_utf8_base64': base64.b64encode(serialized).decode('ascii'),
                      'signature': signature, 'notes': note})

    original = dict(base['action']['arguments'])
    action_case('key_order_original', original, 'Exact arguments from test_plan_archive.action(action-0).')
    action_case('key_order_reversed', dict(reversed(list(original.items()))),
                'Same values, reversed argument insertion order; Python signature preserves this order.')
    action_case('unicode', {'pawn': 'Thing_Human1', 'text': 'Café 雪 🐾 e\u0301'},
                'Synthetic Unicode, supplementary-plane emoji and decomposed accent; no Unicode normalization.')
    action_case('escapes', {'pawn': 'Thing_Human1', 'text': '"quoted"\\path\n\r\t\b\f\x00<>&\u2028'},
                'Synthetic quotes, slash, control characters and HTML-sensitive characters; retain serializer escaping.')
    for name, value in [('integer', 1), ('float', 1.0), ('zero', 0), ('negative_zero', -0.0), ('exponent', 1e-7)]:
        action_case('number_' + name, {'pawn': 'Thing_Human1', 'value': value},
                    'Synthetic generic argument exercises Python numeric JSON encoding; native eligibility is not asserted.')
    action_case('argument_missing', {'pawn': 'Thing_Human1'}, 'Optional generic argument absent.')
    action_case('argument_null', {'pawn': 'Thing_Human1', 'value': None}, 'Generic argument explicitly null; preserve presence.')
    action_case('optional_fields_null', original, 'Explicit null optional typed fields serialize identically to omission.',
                medical_effect=None, caravan_target=None, wall_guard=None)

    by_id = {row['id'].removeprefix('serialization.'): row for row in cases}
    relations = [('key_order_original', 'key_order_reversed', False),
                 ('number_integer', 'number_float', False),
                 ('number_zero', 'number_negative_zero', False),
                 ('argument_missing', 'argument_null', False),
                 ('key_order_original', 'optional_fields_null', True)]
    for left, right, equal in relations:
        if (by_id[left]['signature'] == by_id[right]['signature']) != equal:
            raise ValueError('Python signature relation changed: ' + left + '/' + right)

    medical = runpy.run_path(str(ROOT / 'controller_tests/test_medical_recovery.py'))
    plan, people = medical['fixture']()
    # The existing test's "unconfirmed" parameter changes only this marker.
    plan.progress['tend'].issued['0']['confirmed'] = False
    before = plan.progress['tend'].model_dump_json()
    medical['recover'](plan, people)
    progress = plan.progress['tend']
    if progress.state != 'blocked' or progress.recovery_history or progress.model_dump_json() != before:
        raise ValueError('Unconfirmed treatment no longer retains its blocked issued scope')
    raw = progress.model_dump_json().encode('utf-8')
    if StepProgress.model_validate_json(raw).model_dump_json().encode('utf-8') != raw:
        raise ValueError('Progress JSON round-trip changed bytes')
    cases.append({'id': 'serialization.uncertain_treatment', 'category': 'uncertain_progress',
                  'source': provenance('controller_tests/test_medical_recovery.py', 'test_unsafe_or_player_owned_work_never_reissues'),
                  'parameter': 'unconfirmed', 'step': plan.spec.steps[0].model_dump(mode='json'),
                  'progress': progress.model_dump(mode='json'),
                  'progress_utf8_base64': base64.b64encode(raw).decode('ascii'),
                  'notes': 'Exact synthetic test case retains load_token, issued_tick, player_direction, order_generation and patient_order_generation. Existing retryable failure cannot override unconfirmed issued marker; recovery stays blocked. No native receipt or game outcome claimed.'})
    return {'version': 1, 'source_revision': REVISION,
            'status': 'synthetic_comparison_only_native_acceptance_pending',
            'normalization': 'None. Base64 contains exact UTF-8 serializer bytes, including argument insertion order.',
            'provenance': [provenance('controller_tests/test_plan_archive.py', 'action'),
                           provenance('controller/rimgovernor/colony_plan.py', 'NativeOperation.serialize'),
                           provenance('controller_tests/test_medical_recovery.py', 'fixture')],
            'limits': 'Native argument schemas are not exercised. Boundary rejection, tick ranges, archive and checkpoint matrices remain consumer-chunk acceptance.',
            'signature_relations': [{'left': 'serialization.' + left, 'right': 'serialization.' + right, 'equal': equal}
                                    for left, right, equal in relations], 'items': cases}


def verify(actual, expected):
    # Dict equality erases integer/float and insertion-order distinctions.
    if json.dumps(actual, ensure_ascii=False) != json.dumps(expected, ensure_ascii=False):
        raise ValueError('Serialization baseline drift; inspect the change before regenerating')


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    modes = parser.add_mutually_exclusive_group()
    modes.add_argument('--write', action='store_true')
    modes.add_argument('--check', action='store_true', help='Default: compare without writing.')
    parser.add_argument('--self-test', action='store_true', help='Also prove an altered signature is rejected.')
    args = parser.parse_args()
    expected = build()
    encoded = json.dumps(expected, ensure_ascii=False, indent=2) + '\n'
    if len(encoded.encode('utf-8')) >= 20_000:
        raise ValueError('Keep the intentional fixture below 20 KB')
    if args.write:
        DESTINATION.parent.mkdir(parents=True, exist_ok=True)
        DESTINATION.write_text(encoded, encoding='utf-8', newline='\n')
    else:
        verify(json.loads(DESTINATION.read_text(encoding='utf-8')), expected)
    if args.self_test:
        changed = deepcopy(expected)
        changed['items'][0]['signature'] = '0' * 64
        try:
            verify(changed, expected)
        except ValueError:
            print('Verified deliberate altered-signature rejection')
        else:
            raise ValueError('Checker accepted a changed signature')
    print(f'Verified {len(expected["items"])} serialization cases ({len(encoded.encode("utf-8"))} bytes)')


if __name__ == '__main__':
    main()
