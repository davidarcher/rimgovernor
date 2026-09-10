"""Verify native corner replacement and material-loss retirement."""
import argparse
import asyncio
from pathlib import Path

from storeroom_acceptance import run


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--source-root', type=Path, required=True)
    parser.add_argument('--output', type=Path, required=True)
    parser.add_argument('--seconds', type=int, default=900)
    args = parser.parse_args()
    args.wall_upgrade = args.corner = args.material_loss = True
    raise SystemExit(0 if asyncio.run(run(args)) else 1)
