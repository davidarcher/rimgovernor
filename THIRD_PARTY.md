# Upstream sources

## Dashboard

`dashboard/src` and `dashboard/public` started from IlyaChichkov/rimapi-dashboard
at `152454bbcc8ab7d2b3e6cfff797f6a1df03d36b1`. Its MIT license and copyright notice
remain in `third_party/rimapi-dashboard-LICENSE`. The current React/Vite application
uses RimBot's local controller API.

## Colony Bridge and companion formatters

Native companion sources and selected Python formatters originate from
Snowstar38/rimworld-claude-harness at
`89c2e90fedd51419a3db55a7f9865b0aef29b270`, copied for authorized local research.
No license file was present in that reviewed checkout; this does not imply
redistribution rights. Pinned sources and modifications are recorded in
[Colony Bridge provenance](integrations/colony-bridge/PROVENANCE.md) and
[formatter provenance](controller/rimbot/vendor/PROVENANCE.md).
RimWorld, Harmony and RimBridgeServer SDK assemblies are referenced, not bundled.
GABS and installed game prerequisites are supplied separately.

## Headless adapter

The adapter derives from IlyaChichkov/HeadlessRimPatch at
`d3c5539ff62c19e76ab8e5d1a268d1dca461e161`, GPL-3.0. Its license and
[local changes](integrations/headless-rim/PROVENANCE.md) remain beside the source.

## Retained source records

`third_party/RLE-LICENSE` preserves the MIT notice for previously incorporated
AppSprout-dev/RLE source at `3220bf84d8befc7a252caf383daec88df8d90645`.
`integrations/patches` retains RIMAPI source patch records; these are not a runtime
backend or current build instructions. RIMAPI is not an active submodule.
Keep license/provenance records when pruning project documentation.
