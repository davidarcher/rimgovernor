# Compiled observation boundary checks

Build this net472 project and run its executable with the private Bridge DLL,
Runtime directory, SDK directory, licensed game managed directory and Harmony
directory, in that order (same layout as native-proto-placement).

The suite loads actual compiled adapters and uses the actual SDK binder. It checks
presence, bounded unique geometry, overflow-safe rectangles, unsupported fields,
finite radius, and the Go singleton map-dimension request with all fields false.
It does not create game state or claim native pawn/threat/cell outcomes.

This slice supports status summary and optional need/hediff detail. Unrequested
sections have explicit issues; missing trackers are unavailable, not healthy zero.
Cell reads support terrain, roof, visibility and traversal only; other requested
fields fail Unsupported. Exact selection order is retained; rectangles use z/x
order and report their inclusive bounding rectangle. No frozen cursor support or
CAS token issuance is claimed. Collection/reply overflow cannot truncate success.
Root integration owns capability advertisement and fresh-game acceptance.
