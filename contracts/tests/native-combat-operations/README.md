# Canonical combat checks

Build `NativeCombatOperations.csproj` and run the resulting `net472` executable
with the private compiled bridge DLL and dependency directories (game managed,
RimBridgeServer SDK, Harmony, and private runtime if separate). The executable
loads actual compiled adapters and official generated Protobuf messages.

The adapter supports spawned pawn targets, explicit Melee, ordinary direct-bullet
Ranged and Auto resolving through native weapon choice. Explosive, overhead,
beam and custom projectile paths require their own attribution and remain unsupported.
Both attacker and target require canonical snapshots; the attacker also requires
an eligible existing owned draft. Native violence capability, reach, melee verb,
and requested hostility, standing and colony health predicates are checked again
immediately before the ordinary attack order. Weapon and detailed health values
are fresh native guards, not additional advertised snapshot domains.

Applied proves the observed attack job, not combat completion. Completed requires
the exact synchronous native damage hook to establish that this attacker/job
caused target death, or downing when a standing target was required. An unrelated
death is TargetDead; a vanished job without causal outcome is Unknown. Queued or
current jobs remain Pending. Original identity is required even for latched
terminal evidence. Later Manual does not erase a previously observed causal
terminal outcome.

Ranged launches retain exact projectile identity through ordinary flight and job
completion. Impact still requires the original authority and claim; post-Manual
hits cannot create completion evidence. Missing tracking produces Unknown unless
independent terminal damage or an interruption is already proved.

Run the melee and ranged damage hook tests, pawn control state, movement, draft admission,
authority hooks and operation envelope neighbors. Native gameplay acceptance is
required separately; these contract and guard checks do not establish combat.
