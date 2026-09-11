"""Check generated Python request decoders against shared structural fixtures.

This imports only the generated contract module, never the production controller.
Successful round trips compare decoded values, not signed serialization bytes.
"""
from __future__ import annotations

import argparse
from dataclasses import asdict, dataclass, is_dataclass
import importlib.util
import json
from pathlib import Path
import sys
from typing import Callable, Literal, cast


ROOT = Path(__file__).resolve().parents[1]
Target = Literal['outer_arguments', 'placements']
Decoder = Callable[[str | bytes], object]


@dataclass(frozen=True)
class Replacement:
    token: str
    text: str
    count: int


@dataclass(frozen=True)
class FixtureCase:
    identifier: str
    target: Target
    document: str
    accepted: bool


def object_value(value: object, label: str) -> dict[str, object]:
    if not isinstance(value, dict) or any(not isinstance(key, str) for key in value):
        raise ValueError(f'{label} must be an object with string keys')
    return cast(dict[str, object], value)


def string_value(value: object, label: str) -> str:
    if not isinstance(value, str):
        raise ValueError(f'{label} must be a string')
    return value


def list_value(value: object, label: str) -> list[object]:
    if not isinstance(value, list):
        raise ValueError(f'{label} must be a list')
    return cast(list[object], value)


def replacement(value: object) -> Replacement:
    row = object_value(value, 'replacement')
    if set(row) != {'token', 'text', 'count'}:
        raise ValueError('replacement requires exactly token, text and count')
    count = row['count']
    if type(count) is not int or not 0 <= count <= 65536:
        raise ValueError('replacement count must be an integer in 0..65536')
    token = string_value(row['token'], 'replacement token')
    text = string_value(row['text'], 'replacement text')
    if not token or token in text:
        raise ValueError('replacement token must be nonempty and absent from its text')
    return Replacement(token, text, count)


def expand_input(value: object) -> str:
    row = object_value(value, 'input')
    document = string_value(row.get('json'), 'input JSON')
    kind = row.get('kind')
    if kind == 'raw':
        if set(row) != {'kind', 'json'}:
            raise ValueError('raw input requires exactly kind and json')
        return document
    if kind != 'template' or set(row) != {'kind', 'json', 'replacements'}:
        raise ValueError('template input requires kind, json and replacements')
    seen: set[str] = set()
    for value in list_value(row['replacements'], 'replacements'):
        item = replacement(value)
        if item.token in seen or document.count(item.token) != 1:
            raise ValueError('each replacement token must occur exactly once')
        seen.add(item.token)
        if len(document) + len(item.text) * item.count > 8 * 1024 * 1024:
            raise ValueError('expanded fixture exceeds harness size limit')
        document = document.replace(item.token, item.text * item.count)
    return document


def load_cases(path: Path) -> list[FixtureCase]:
    data = object_value(json.loads(path.read_text(encoding='utf8')), 'fixture')
    if type(data.get('version')) is not int or data['version'] != 1:
        raise ValueError('unsupported fixture version')
    result: list[FixtureCase] = []
    seen: set[str] = set()
    for value in list_value(data.get('cases'), 'cases'):
        row = object_value(value, 'case')
        identifier = string_value(row.get('id'), 'case ID')
        if not identifier or identifier in seen:
            raise ValueError('case IDs must be nonempty and unique')
        seen.add(identifier)
        target_value = row.get('target')
        if target_value not in ('outer_arguments', 'placements'):
            raise ValueError(f'{identifier}: unsupported validation target')
        target = cast(Target, target_value)
        canonical = object_value(row.get('canonical'), 'canonical expectation')
        accepted = canonical.get('accepted')
        if type(accepted) is not bool:
            raise ValueError(f'{identifier}: acceptance must be boolean')
        result.append(FixtureCase(identifier, target, expand_input(row.get('input')), accepted))
    if not result:
        raise ValueError('empty request fixture set')
    return result


def load_decoders(path: Path) -> dict[Target, Decoder]:
    name = '_placement_request_contract_under_test'
    spec = importlib.util.spec_from_file_location(name, path)
    if spec is None or spec.loader is None:
        raise ValueError(f'cannot load generated module: {path}')
    module = importlib.util.module_from_spec(spec)
    # Dataclasses resolve postponed annotations through their registered module.
    sys.modules[name] = module
    spec.loader.exec_module(module)
    result: dict[Target, Decoder] = {}
    for target, symbol in (
        ('outer_arguments', 'decode_placement_preview_arguments'),
        ('placements', 'decode_placement_batch'),
    ):
        function = getattr(module, symbol, None)
        if not callable(function):
            raise ValueError(f'generated module lacks {symbol}')
        result[cast(Target, target)] = cast(Decoder, function)
    return result


def json_value(value: object) -> object:
    if is_dataclass(value) and not isinstance(value, type):
        return asdict(value)
    if isinstance(value, list):
        return [json_value(item) for item in value]
    raise ValueError('generated decoder must return a dataclass or list of dataclasses')


def check_cases(cases: list[FixtureCase], decoders: dict[Target, Decoder]) -> int:
    round_trips = 0
    for case in cases:
        decoder = decoders[case.target]
        try:
            value = decoder(case.document)
        except ValueError as error:
            if case.accepted:
                raise ValueError(f'{case.identifier}: valid fixture refused: {error}') from error
            continue
        if not case.accepted:
            raise ValueError(f'{case.identifier}: invalid fixture accepted')
        encoded = json.dumps(json_value(value), ensure_ascii=False, allow_nan=False)
        if decoder(encoded) != value or decoder(encoded.encode('utf8')) != value:
            raise ValueError(f'{case.identifier}: decoded-value round trip changed')
        round_trips += 1
    for target, decoder in decoders.items():
        try:
            decoder(b'"\xff"')
        except ValueError:
            continue
        raise ValueError(f'{target}: invalid UTF-8 was accepted')
    return round_trips


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--check', action='store_true', help='Check only (the default).')
    parser.add_argument('--module', type=Path,
                        default=ROOT / 'contracts/generated/python/placement_preview_requests.py')
    args = parser.parse_args()
    try:
        cases = load_cases(ROOT / 'contracts/fixtures/placement-request-cases.json')
        count = check_cases(cases, load_decoders(args.module))
    except (ValueError, OSError) as error:
        print(f'Placement request check failed: {error}', file=sys.stderr)
        return 1
    print(f'Python placement request checks passed: {len(cases)} shared cases, '
          f'{count} decoded-value round trips and two invalid-UTF8 refusals.')
    print('Structural generated-code checks only; native consumer and gameplay acceptance remain pending.')
    return 0


if __name__ == '__main__':
    raise SystemExit(main())
