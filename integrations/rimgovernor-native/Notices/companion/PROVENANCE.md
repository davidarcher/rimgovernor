# Colony bridge source notice

The companion includes source from Snowstar38/rimworld-claude-harness,
[revision 89c2e90fedd51419a3db55a7f9865b0aef29b270](https://github.com/Snowstar38/rimworld-claude-harness/tree/89c2e90fedd51419a3db55a7f9865b0aef29b270/companion/src).
Upstream namespaces and authorship remain in the retained source. The reviewed
checkout supplied no license file; local research authorization does not establish
redistribution permission.

RimGovernor extends the companion with guarded game operations, observations,
saved identity, clock supervision and rendering. Current behavior belongs in the
[developer docs](../../../../docs/README.md); changes and verification belong in commits.

RimWorld and Harmony assemblies are referenced from installed dependencies; their
binaries and game artwork are not bundled. The GABP host (a vendored fork of
RimBridgeServer and Lib.GAB, with Newtonsoft.Json) is bundled; see `../host/PROVENANCE.md`.
