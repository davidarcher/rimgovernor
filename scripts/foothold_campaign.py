"""Prepare a fresh native start, then run accelerated production foothold acceptance."""
import argparse
import asyncio
import json
from pathlib import Path
from types import SimpleNamespace

import deterministic_foothold
import prepare_scenario


async def run(args):
    args.output.mkdir(parents=True, exist_ok=False)
    report = dict(passed=False, seed=args.seed, biome=args.biome, colonists=args.count,
                  stability_days=args.stability_days, difficulty=args.difficulty,
                  scenario=args.scenario, hunting='disabled by player work policy' if args.disable_hunting else 'enabled',
                  accelerated=True, start_type='fresh_native_generation')
    prepared = args.output/'prepared'
    try:
        await prepare_scenario.run(SimpleNamespace(source_root=args.source_root, output=prepared,
            scenario=args.scenario, count=args.count, seed=args.seed, biome=args.biome, difficulty=args.difficulty))
        report['preparation'] = json.loads((prepared/'scenario-preparation.json').read_text())
        report['passed'] = await deterministic_foothold.run(SimpleNamespace(
            source_root=prepared, output=args.output/'campaign', checkpoint=None,
            source_snapshot=True, rendered=False, seconds=args.seconds, speed='Superfast',
            stability_days=args.stability_days, accelerated=True, food_observer=True,
            food_target_days=7, disable_hunting=args.disable_hunting))
    except Exception as error:
        report['error'] = repr(error)
    finally:
        (args.output/'campaign-result.json').write_text(json.dumps(report, indent=2), encoding='utf8')
    return report['passed']


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--source-root', type=Path, required=True)
    parser.add_argument('--output', type=Path, required=True)
    parser.add_argument('--seed', required=True)
    parser.add_argument('--scenario', default='LostTribe', help='Installed native ScenarioDef')
    parser.add_argument('--disable-hunting', action='store_true', help='Separately labelled crop-focused acceptance')
    parser.add_argument('--biome', default='TemperateForest')
    parser.add_argument('--count', type=int, choices=range(1, 11), default=8)
    parser.add_argument('--difficulty', default='Rough')
    parser.add_argument('--seconds', type=int, default=7200)
    parser.add_argument('--stability-days', type=deterministic_foothold.stability_days, default=3)
    raise SystemExit(0 if asyncio.run(run(parser.parse_args())) else 1)
