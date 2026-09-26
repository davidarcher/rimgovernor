using System;

// Single-entry-point dispatcher for the consolidated native contract probes. Invoke as:
//   dotnet run --project contracts/tests/NativeContractProbes.csproj -- <probe-name> [args...]
// <probe-name> selects which probe's Invoke(...) runs; remaining args are forwarded exactly as
// each probe consumed them before the 25-project -> 1-project consolidation (a Category 3/4
// bridge DLL path plus optional dependency directories, or nothing for Category 1/2 probes).
internal static class NativeContractProbesDispatcher
{
    private static int Main(string[] args)
    {
        if (args.Length == 0)
        {
            PrintUsage();
            return 1;
        }
        var probe = args[0];
        var rest = new string[args.Length - 1];
        Array.Copy(args, 1, rest, 0, rest.Length);
        try
        {
            switch (probe)
            {
                // ---- Category 1/2: in-process fake-Verse compile / pure logic (no args) ----
                case "native-production-bill-settings": NativeProductionBillSettingsProbe.Invoke(); return 0;
                case "native-roof-support": NativeRoofSupportProbe.Invoke(); return 0;
                case "native-home-coverage": NativeHomeCoverageProbe.Invoke(); return 0;
                case "native-authority": NativeAuthorityProbe.Invoke(); return 0;
#if !HAVE_HARMONY
                case "native-authority-control": NativeAuthorityControlProbe.Invoke(); return 0;
#else
                case "native-authority-control":
                    Console.Error.WriteLine("native-authority-control's fake NativeAuthorityHooks stand-in is excluded when $(HarmonyAssembly) is supplied; not available in this build.");
                    return 1;
#endif
                case "native-authority-status": NativeAuthorityStatusProbe.Invoke(); return 0;
                case "native-clock": NativeClockProbe.Invoke(); return 0;
                case "native-observation-work": NativeObservationWorkProbe.Invoke(); return 0;
                case "native-threat-classifier": NativeThreatClassifierProbe.Invoke(); return 0;
                case "native-attempt-ledger": NativeAttemptLedgerProbe.Invoke(); return 0;
                case "native-construction-causality": NativeConstructionCausalityProbe.Invoke(); return 0;

#if HAVE_HARMONY
                case "native-authority-hooks": NativeAuthorityHooksProbe.Invoke(); return 0;
                case "native-operation-envelope": NativeOperationEnvelopeProbe.Invoke(); return 0;
#else
                case "native-authority-hooks":
                case "native-operation-envelope":
                    Console.Error.WriteLine(probe + " requires $(HarmonyAssembly) to be supplied at build time; not available in this build.");
                    return 1;
#endif

                // ---- Category 4: hybrid fake-Verse + real-DLL reflection ----
                case "native-proto-boundary": NativeProtoBoundaryProbe.Invoke(rest); return 0;

                // ---- Category 3: reflection-over-real-compiled-DLL (args[0] = bridge DLL path) ----
                case "native-combat-causality": return NativeCombatCausalityProbe.Invoke(rest);
                case "native-combat-operations": NativeCombatOperationsProbe.Invoke(rest); return 0;
                case "native-draft-operations": NativeDraftOperationsProbe.Invoke(rest); return 0;
                case "native-movement-operations": NativeMovementOperationsProbe.Invoke(rest); return 0;
                case "native-pawn-control-state": return NativePawnControlStateProbe.Invoke(rest);
                case "native-pawn-observations": return NativePawnObservationsProbe.Invoke(rest);
                case "native-proto-buildings": return NativeProtoBuildingsProbe.Invoke(rest);
                case "native-proto-observations": return NativeProtoObservationsProbe.Invoke(rest);
                case "native-proto-placement": return NativeProtoPlacementProbe.Invoke(rest);
                case "native-proto-presentation": return NativeProtoPresentationProbe.Invoke(rest);
                case "native-proto-research": return NativeProtoResearchProbe.Invoke(rest);
                case "native-proto-rooms": return NativeProtoRoomsProbe.Invoke(rest);
                case "native-proto-supplies": return NativeProtoSuppliesProbe.Invoke(rest);

#if HAVE_HARMONY_AND_RIMWORLD
                case "native-explosive-causality": return NativeExplosiveCausalityProbe.Invoke(rest);
                case "native-ranged-causality": return NativeRangedCausalityProbe.Invoke(rest);
#else
                case "native-explosive-causality":
                case "native-ranged-causality":
                    Console.Error.WriteLine(probe + " requires $(HarmonyAssembly) and $(RimWorldManagedDir) to be supplied at build time; not available in this build.");
                    return 1;
#endif

#if HAVE_RIMBRIDGE_SDK
                case "native-journal-cache": NativeJournalCacheProbe.Invoke(); return 0;
#else
                case "native-journal-cache":
                    Console.Error.WriteLine("native-journal-cache requires $(RimBridgeSdkDir) and $(RimWorldManagedDir) to be supplied at build time; not available in this build.");
                    return 1;
#endif

                default:
                    Console.Error.WriteLine("Unknown probe: " + probe);
                    PrintUsage();
                    return 1;
            }
        }
        catch (Exception error)
        {
            Console.Error.WriteLine(error);
            return 1;
        }
    }

    private static void PrintUsage()
    {
        Console.Error.WriteLine("Usage: dotnet run --project contracts/tests/NativeContractProbes.csproj -- <probe-name> [args...]");
        Console.Error.WriteLine("Valid probe names:");
        foreach (var name in new[]
        {
            "native-authority", "native-authority-control", "native-authority-status", "native-clock",
            "native-attempt-ledger", "native-construction-causality", "native-observation-work",
            "native-threat-classifier",
            "native-authority-hooks",
            "native-operation-envelope", "native-proto-boundary", "native-combat-causality",
            "native-combat-operations", "native-draft-operations", "native-movement-operations",
            "native-pawn-control-state", "native-pawn-observations", "native-proto-buildings",
            "native-proto-observations", "native-proto-placement", "native-proto-presentation",
            "native-proto-research", "native-proto-rooms", "native-proto-supplies",
            "native-explosive-causality", "native-ranged-causality", "native-journal-cache",
        }) Console.Error.WriteLine("  " + name);
    }
}
