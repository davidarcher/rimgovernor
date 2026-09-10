"""Bounded, review-local prefetch of independent placement previews."""
import json
from dataclasses import dataclass


@dataclass
class PreviewCandidate:
    def_name: str
    x: int
    z: int
    rotation: str
    materials: list[str]


def preview_key(placement, material):
    return placement.def_name, placement.x, placement.z, placement.rotation, material


class PlacementPreviews:
    def __init__(self, game, placements, cache=None, *, inspect=None):
        self.game, self.placements = game, placements
        self.cache = cache if cache is not None else {}
        self.inspect = inspect

    async def invoke(self, tool, arguments):
        if self.inspect is not None:
            return await self.inspect(tool, arguments)
        return await self.game.invoke(tool, arguments, allow_write=False)

    async def get(self, placement, material):
        key = preview_key(placement, material)
        if key not in self.cache:
            supported = (getattr(self.game, 'batch_placement_previews', True) is not False
                         and getattr(getattr(self.game, 'bridge', None), 'placement_preview_batch_version', 0) == 1)
            primary = (placement.materials or [None])[0]
            if supported and material in (placement.materials or [None]):
                start = self.placements.index(placement)
                candidates = {}
                for candidate in self.placements[start:start+16]:
                    choices = candidate.materials or [None]
                    if material != primary and material not in choices:
                        continue
                    candidate_key = preview_key(candidate, choices[0] if material == primary else material)
                    if candidate_key not in self.cache:
                        candidates[candidate_key] = candidate
                keys = list(candidates)
                payload = await self.invoke('home/placement_previews', dict(placements=json.dumps([
                    dict(defName=k[0], x=k[1], z=k[2], rotation=k[3], stuff=k[4] or '') for k in keys])))
                rows = payload.get('results')
                if (payload.get('success') is not True or payload.get('version') != 1
                        or not isinstance(rows, list) or len(rows) != len(keys)
                        or any(not isinstance(row, dict) for row in rows)):
                    raise ValueError('Incomplete native placement preview batch')
                self.cache.update(zip(keys, rows))
            else:
                args = dict(defName=key[0], x=key[1], z=key[2], rotation=key[3], dryRun=True)
                if material is not None:
                    args['stuff'] = material
                self.cache[key] = await self.invoke('home/place_building', args)
        result = self.cache[key]
        if result.get('success') is False:
            raise ValueError(result.get('error') or 'Native placement preview failed')
        return result
