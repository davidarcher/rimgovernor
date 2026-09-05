# Upstream sources

## RIMAPI Dashboard

`dashboard/src` and `dashboard/public` started from IlyaChichkov/rimapi-dashboard at
`152454bbcc8ab7d2b3e6cfff797f6a1df03d36b1`. Its MIT license and copyright notice
are preserved in `third_party/rimapi-dashboard-LICENSE`.

Our changes add the manager workspace, local Python proxy, video client,
Vite build, and regression tests. Refresh failures retain last good data;
map requests use the selected map instead of a hardcoded zero. Inspector
features remain derived from upstream, not a claim of comprehensive validation.

## RLE

`controller/rimbot/vendor/sse_client.py` is from AppSprout-dev/RLE at
`3220bf84d8befc7a252caf383daec88df8d90645`, with its MIT license in
`third_party/RLE-LICENSE`. It supplies reconnecting SSE transport. The current
controller does not inherit RLE's action resolver, terrain-placement heuristics,
or editor operations. The full RLE benchmark package requires Python 3.14;
the core RimBot application requires Python 3.12 or later.

## RIMAPI

`integrations/RIMAPI` is a pinned upstream submodule at
`dfa4b2909e132081898845d0c4936fcefa86c91c` (v1.10.0). It retains upstream Git
history and its GPLv3 license. No mod DLL is installed by the new launcher.

`scripts/generate_catalog.py` extracts HTTP route and DTO contract metadata
from that checkout into `controller/rimbot/data/catalog.json`. This is an API
contract snapshot, not a new game engine or a registry of native gizmos.
Runtime discovery intersects these contracts with the installed server's routes.

No public GitHub forks or pushes were performed. Local source pins make updates
reviewable; publish our forks explicitly when ready to maintain upstream patches.
