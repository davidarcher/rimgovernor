namespace RimGovernor.Brain.Policy;

// Go: combat_batch.go

public static partial class PolicyFunctions
{
    /// <summary>
    /// Recognizes only current observable settings/positions. A historical attack, dose, or accepted job is
    /// never invented from a later frame.
    /// </summary>
    public static bool CombatBatchObserved(CombatBatch batch, CombatView view)
    {
        var pawns = new Dictionary<PawnId, CombatPawnState>();
        foreach (var p in view.Pawns.Items)
        {
            pawns[p.Id] = p;
        }

        var doorsKnown = view.DoorStates.TryGetValue(out var doors);
        foreach (var order in batch.Orders.Items)
        {
            var present = pawns.TryGetValue((PawnId)order.Pawn, out var p);
            switch (order.Kind)
            {
                case "draft":
                    if (!view.Orderable.Items.Contains((PawnId)order.Pawn))
                    {
                        return false;
                    }

                    break;
                case "move":
                    if (!present || !p!.Cell.TryGetValue(out var cell) || cell != order.Cell)
                    {
                        return false;
                    }

                    break;
                case "fire_mode":
                    if (!present || p!.FireMode.Length == 0 || p.FireMode != order.FireMode)
                    {
                        return false;
                    }

                    break;
                case "door":
                    {
                        if (!doorsKnown)
                        {
                            return false;
                        }

                        var found = false;
                        foreach (var d in doors.Items)
                        {
                            if (d.Cell != order.Cell)
                            {
                                continue;
                            }

                            found = true;
                            switch (order.Door)
                            {
                                case "hold_open":
                                case "close":
                                    if (!d.HoldOpen.TryGetValue(out var holdOpen) || holdOpen != (order.Door == "hold_open"))
                                    {
                                        return false;
                                    }

                                    break;
                                case "forbid":
                                case "allow":
                                    if (!d.Forbidden.TryGetValue(out var forbidden) || forbidden != (order.Door == "forbid"))
                                    {
                                        return false;
                                    }

                                    break;
                                default:
                                    return false;
                            }
                        }

                        if (!found)
                        {
                            return false;
                        }

                        break;
                    }

                default:
                    return false;
            }
        }

        return true;
    }
}
