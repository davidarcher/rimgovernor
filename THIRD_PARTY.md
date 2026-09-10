# Upstream sources

## Go dependencies

The migration module pins Go 1.27.1, the official
[MCP Go SDK v1.7.0](https://github.com/modelcontextprotocol/go-sdk/tree/v1.7.0)
and [modernc SQLite v1.58.0](https://pkg.go.dev/modernc.org/sqlite@v1.58.0).
Exact transitive versions and integrity hashes are in `go/go.mod` and `go/go.sum`.
The SDK is in an MIT/Apache-2.0 licensing transition; its complete upstream notice
is retained in `third_party/go-mcp-sdk-LICENSE`. Modernc's BSD-3-Clause notice is
in `third_party/go-modernc-sqlite-LICENSE`; upstream SQLite and sqlite-vec notices
are retained alongside it. Package sources remain in the Go module cache.
Distribution must include applicable transitive notices with the packaged binary;
G01.11 owns that packaging gate. These dependencies currently serve foundation
tests, not a production controller.

## Dashboard

`dashboard/src` and `dashboard/public` started from IlyaChichkov/rimapi-dashboard
at `152454bbcc8ab7d2b3e6cfff797f6a1df03d36b1`. Its MIT license and copyright notice
remain in `third_party/rimapi-dashboard-LICENSE`. The current React/Vite application
uses RimGovernor's local controller API.

## Colony Bridge and companion formatters

Native companion sources and selected Python formatters originate from
Snowstar38/rimworld-claude-harness at
`89c2e90fedd51419a3db55a7f9865b0aef29b270`, copied for authorized local research.
No license file was present in that reviewed checkout; this does not imply
redistribution rights. Pinned sources and modifications are recorded in
[Colony Bridge provenance](integrations/colony-bridge/PROVENANCE.md) and
[formatter provenance](controller/rimgovernor/vendor/PROVENANCE.md).
RimWorld, Harmony and RimBridgeServer SDK assemblies are referenced, not bundled.
Placement preview parsing also references the Newtonsoft.Json assembly supplied
by the installed RimBridgeServer; its source and binary are not bundled here.
GABS and installed game prerequisites are supplied separately.

The original pawn-image integration uses RimWorld's native portraits and item icons;
game artwork is not bundled. Offscreen timing/culling was informed by local RimMolt
inspection, as recorded in the companion provenance file; no RimMolt source was copied.

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
