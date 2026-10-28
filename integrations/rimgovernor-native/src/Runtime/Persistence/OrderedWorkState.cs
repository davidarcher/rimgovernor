using System.Collections.Generic;
using Verse;

namespace RimGovernor.Native;

/// <summary>Latest intervention provenance only; Verse owns the physical queue.</summary>
public sealed class OrderedWorkState : GameComponent
{
    private Dictionary<string, string> latest = new();
    public OrderedWorkState(Game game) { }

    public string? Read(string pawn) => latest.TryGetValue(pawn, out var json) ? json : null;

    public void Write(string pawn, string observation) => latest[pawn] = observation;

    public override void ExposeData()
    {
        Scribe_Collections.Look(ref latest, "rimgovernorOrderedWork", LookMode.Value, LookMode.Value);
        if (Scribe.mode == LoadSaveMode.PostLoadInit && latest == null) latest = new();
    }

    public static OrderedWorkState For(Game game)
    {
        var state = game.GetComponent<OrderedWorkState>();
        // State belongs to the early-loaded runtime so Verse can resolve its type in saves.
        if (state == null) { state = new OrderedWorkState(game); game.components.Add(state); }
        return state;
    }
}
