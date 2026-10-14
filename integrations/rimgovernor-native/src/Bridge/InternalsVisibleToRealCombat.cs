// The headless real-engine host (integrations/rimgovernor-realcombat) hosts CombatMirror and NativeCombatOrders as
// compiled here, so the offline fight is observed and commanded by the same code as a live game (#3142).
[assembly: System.Runtime.CompilerServices.InternalsVisibleTo("RimGovernor.RealCombat.Native")]
