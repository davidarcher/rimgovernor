"""Generate the typed observation projection from its canonical JSON Schema."""
import argparse
import json
from pathlib import Path
from schema_models import generate, resolve

ROOT = Path(__file__).resolve().parents[1]
schema = json.loads((ROOT / 'controller/rimbot/data/bridge_observation.schema.json').read_text())
resolved = resolve({'$ref': schema['$ref']}, schema['$defs'])
manifest = {'endpoints': [{'name': 'observe', 'request_schema': resolved,
                          'response_schema': resolved}], 'error_schema': resolved}
output = generate(manifest).split('\nREQUEST_TYPES =')[0]
target = ROOT / 'controller/rimbot/bridge_models.py'
parser = argparse.ArgumentParser(description=__doc__)
parser.add_argument('--check', action='store_true')
if parser.parse_args().check:
    if not target.exists() or target.read_text(encoding='utf8') != output:
        raise SystemExit('Bridge observation models need regeneration')
else:
    target.write_text(output, encoding='utf8')
