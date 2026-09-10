"""Bounded, review-local prefetch of independent first-choice placement previews."""
import json


def preview_key(placement, material):
    return placement.def_name, placement.x, placement.z, placement.rotation, material


class PlacementPreviews:
    def __init__(self, game, placements, cache=None):
        self.game, self.placements = game, placements
        self.cache = cache if cache is not None else {}

    async def get(self, placement, material):
        key = preview_key(placement, material)
        if key not in self.cache:
            supported = getattr(getattr(self.game, 'bridge', None), 'placement_preview_batch_version', 0) == 1
            primary = (placement.materials or [None])[0]
            if supported and material == primary:
                start = self.placements.index(placement)
                candidates = {}
                for candidate in self.placements[start:start+16]:
                    candidate_key = preview_key(candidate, (candidate.materials or [None])[0])
                    if candidate_key not in self.cache:
                        candidates[candidate_key] = candidate
                keys = list(candidates)
                payload = await self.game.invoke('home/placement_previews', dict(placements=json.dumps([
                    dict(defName=k[0], x=k[1], z=k[2], rotation=k[3], stuff=k[4] or '') for k in keys])), allow_write=False)
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
                self.cache[key] = await self.game.invoke('home/place_building', args, allow_write=False)
        result = self.cache[key]
        if result.get('success') is False:
            raise ValueError(result.get('error') or 'Native placement preview failed')
        return result
