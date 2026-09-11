"""Exercise current generated preview replies without importing controller runtime."""
import importlib.util
import json
from pathlib import Path
import sys

ROOT = Path(__file__).resolve().parents[1]


def main():
    path = ROOT / 'controller/rimgovernor/generated/placement_preview_responses.py'
    spec = importlib.util.spec_from_file_location('preview_responses', path)
    module = importlib.util.module_from_spec(spec)
    sys.modules[spec.name] = module
    spec.loader.exec_module(module)
    fixture = json.loads((ROOT / 'contracts/fixtures/placement-response-cases.json').read_text())
    for case in fixture['cases']:
        try:
            value = module.decode_preview_reply(case['json'])
        except (ValueError, TypeError):
            if case['accepted']:
                raise AssertionError(case['id'])
        else:
            if not case['accepted']:
                raise AssertionError(case['id'])
            encoded = json.dumps(module.to_wire(value))
            assert module.decode_preview_reply(encoded) == value, case['id']
    print(f"passed {len(fixture['cases'])} current preview reply cases")


if __name__ == '__main__':
    main()
