
namespace RimGovernor.Brain.GameAccess;

// The intents that edit
// ground, storage and the policy databases. Native checks the ground and the holders live when it applies.
// PreviewZone (the zones_preview call) is wire plumbing and is not part of this file.
public static class Zones
{
    /// <summary>The create shape of the one zone intent.</summary>
    public static RimGovernor.Brain.GameOrders.Operations.ZoneIntent ZoneConfiguration(ZoneCreate zone)
    {
        var command = new RimGovernor.Brain.GameOrders.Operations.ZoneIntent
        {
            Label = zone.Label,
            AddCells = new RimGovernor.Brain.GameOrders.Operations.Cells { Selection = new RimGovernor.Brain.GameFacts.Operations.CellList { Cells = [.. IntentActionRules.WireCells(zone.Cells)] } }, Delete = false, RequireCoveredEmpty = false, Kind = RimGovernor.Brain.GameOrders.Operations.ZoneType.Existing };
        if (zone.Kind == ZoneKind.Fishing)
        {
            command = command with { Kind = RimGovernor.Brain.GameOrders.Operations.ZoneType.Fishing };
            command = command with
            {
                Fishing = new RimGovernor.Brain.GameOrders.Operations.FishingSettings
                {
                    PopulationFloor = ZoneCreateConstants.FishingPopulationFloor,
                },
            };
            if (zone.ExtendId.Length != 0)
            {
                command = command with { Zone = IntentActionRules.NewRef(zone.ExtendId) };
            }
            return command;
        }
        if (zone.Stockpile is { } stockpile)
        {
            // A cell-list stockpile keeps the explicit list built above.
            if (stockpile.Ground is StockpileGround.Dragged { Rectangle: { Width: > 0 } rect })
            {
                command = command with
                {
                    AddCells = new RimGovernor.Brain.GameOrders.Operations.Cells
                    {
                        Selection = new RimGovernor.Brain.GameOrders.Operations.Rectangle
                        {
                            Origin = IntentActionRules.WireCell(rect.Origin),
                            Width = rect.Width,
                            Height = rect.Height,
                        },
                    },
                };
            }
            command = command with { Kind = RimGovernor.Brain.GameOrders.Operations.ZoneType.Stockpile };
            return command with
            {
                Stockpile = StockpileSettings(stockpile.Filter, stockpile.Priority),
            };
        }
        if (zone.Kind == ZoneKind.Growing)
        {
            command = command with { Kind = RimGovernor.Brain.GameOrders.Operations.ZoneType.Growing };
            return command with
            {
                Growing = new RimGovernor.Brain.GameOrders.Operations.GrowingSettings
                {
                    PlantDef = zone.Crop,
                },
            };
        }
        throw BridgeContractException.Contract("unknown zone kind");
    }

    /// <summary>
    /// The native StockpileSettings for a filter and priority. The filter's base is the
    /// preset, and a FilterPatch is sent only when the filter has selectors or ranges.
    /// </summary>
    public static RimGovernor.Brain.GameOrders.Operations.StockpileSettings StockpileSettings(StockpileFilter filter, StockpilePriority priority)
    {
        var settings = new RimGovernor.Brain.GameOrders.Operations.StockpileSettings
        {
            Priority = new RimGovernor.Brain.GameOrders.Operations.StockpilePriorityChange.Selected(priority),
            Preset = new RimGovernor.Brain.GameOrders.Operations.StockpilePresetChange.Selected(filter.Base),
        };
        var patch = new RimGovernor.Brain.GameOrders.Operations.FilterPatch { Allow = [.. FilterSelectors(filter.Allow)], Disallow = [.. FilterSelectors(filter.Disallow)], HitPoints = new RimGovernor.Brain.GameOrders.Operations.HitPointRange.Preserve() };
        if (filter.HitPoints is { } hitPoints)
        {
            patch = patch with { HitPoints = new RimGovernor.Brain.GameOrders.Operations.HitPointRange.Selected(hitPoints.Min, hitPoints.Max) };
        }
        if (filter.QualityRange is { } quality)
        {
            patch = patch with { QualityMin = Domain.Wire.Name(quality.Min) };
            patch = patch with { QualityMax = Domain.Wire.Name(quality.Max) };
        }
        if (patch.Allow.Count != 0 || patch.Disallow.Count != 0 || patch.HitPoints is RimGovernor.Brain.GameOrders.Operations.HitPointRange.Selected || patch.QualityMin is not null)
        {
            settings = settings with { Filter = patch };
        }
        return settings;
    }

    private static List<RimGovernor.Brain.GameOrders.Operations.FilterSelector> FilterSelectors(EquatableArray<FilterSelector> rows)
    {
        var output = new List<RimGovernor.Brain.GameOrders.Operations.FilterSelector>();
        foreach (var s in rows)
        {
            switch (s.Kind)
            {
                case SelectorKind.ThingDef:
                    output.Add(new RimGovernor.Brain.GameOrders.Operations.FilterSelector { Definition = new RimGovernor.Brain.GameOrders.Operations.FilterSelectorDefinition.ThingDefCase(s.Name) });
                    break;
                case SelectorKind.CategoryDef:
                    output.Add(new RimGovernor.Brain.GameOrders.Operations.FilterSelector { Definition = new RimGovernor.Brain.GameOrders.Operations.FilterSelectorDefinition.CategoryDefCase(s.Name) });
                    break;
                case SelectorKind.SpecialFilter:
                    output.Add(new RimGovernor.Brain.GameOrders.Operations.FilterSelector { Definition = new RimGovernor.Brain.GameOrders.Operations.FilterSelectorDefinition.SpecialFilterDefCase(s.Name) });
                    break;
            }
        }
        return output;
    }

    private static RimGovernor.Brain.GameOrders.Operations.Action ZoneAction(RimGovernor.Brain.GameOrders.Operations.ZoneIntent intent) => new() { Intent = intent };

    /// <summary>The create shape of the zone arm for one zone_create, the configuration without a census token; native checks the ground live.</summary>
    internal static RimGovernor.Brain.GameOrders.Operations.Action ZoneCreateAction(DomainAction action) =>
        ZoneAction(ZoneConfiguration(action.ZoneCreate ?? throw BridgeContractException.Contract("not a zone create action")));

    /// <summary>The delete shape of the zone arm for one zone_delete.</summary>
    internal static RimGovernor.Brain.GameOrders.Operations.Action ZoneDeleteAction(DomainAction action)
    {
        var del = action.ZoneDelete ?? throw BridgeContractException.Contract("not a zone delete action");
        WireValidation.ValidId(del.Zone);
        return ZoneAction(new RimGovernor.Brain.GameOrders.Operations.ZoneIntent { Zone = IntentActionRules.NewRef(del.Zone), Delete = true, RequireCoveredEmpty = false, Kind = RimGovernor.Brain.GameOrders.Operations.ZoneType.Existing });
    }

    /// <summary>The cells shape of the zone arm for one zone_cell_edit.</summary>
    internal static RimGovernor.Brain.GameOrders.Operations.Action ZoneCellsAction(DomainAction action)
    {
        var e = action.ZoneCellEdit ?? throw BridgeContractException.Contract("not a zone cell edit action");
        var selection = new RimGovernor.Brain.GameOrders.Operations.Cells { Selection = new RimGovernor.Brain.GameFacts.Operations.CellList { Cells = [.. IntentActionRules.WireCells(e.Cells)] } };
        var intent = new RimGovernor.Brain.GameOrders.Operations.ZoneIntent { Zone = IntentActionRules.NewRef(e.Zone), Delete = false, RequireCoveredEmpty = false, Kind = RimGovernor.Brain.GameOrders.Operations.ZoneType.Existing };
        if (e.Mode == CellEditMode.Remove)
        {
            intent = intent with { RemoveCells = selection };
        }
        else
        {
            intent = intent with { AddCells = selection };
        }
        return ZoneAction(intent);
    }

    /// <summary>The settings shape of the zone arm for one stockpile_patch, on a stockpile zone or a storage building alike.</summary>
    internal static RimGovernor.Brain.GameOrders.Operations.Action StockpileAction(DomainAction action)
    {
        var p = action.StockpilePatch ?? throw BridgeContractException.Contract("not a stockpile patch action");
        return ZoneAction(new RimGovernor.Brain.GameOrders.Operations.ZoneIntent { Zone = IntentActionRules.NewRef(p.Target), Stockpile = StockpileSettings(p.Filter, p.Priority), Delete = false, RequireCoveredEmpty = false, Kind = RimGovernor.Brain.GameOrders.Operations.ZoneType.Existing });
    }

    /// <summary>
    /// The AreaIntent of one bot-owned allowed area or the home area. Native resolves the bot
    /// area by its key label and never touches a player's area (NativeAreaIntent.cs).
    /// </summary>
    internal static RimGovernor.Brain.GameOrders.Operations.Action AreaAction(DomainAction action)
    {
        var v = action.Area ?? throw BridgeContractException.Contract("not an area action");
        var operation =
            v.Operation == AreaOperation.Create ? RimGovernor.Brain.GameOrders.Operations.AreaOperation.Create
            : v.Operation == AreaOperation.SetCells ? RimGovernor.Brain.GameOrders.Operations.AreaOperation.SetCells
            : v.Operation == AreaOperation.ClearCells ? RimGovernor.Brain.GameOrders.Operations.AreaOperation.ClearCells
            : v.Operation == AreaOperation.Delete ? RimGovernor.Brain.GameOrders.Operations.AreaOperation.Delete
            : throw BridgeContractException.Contract("unknown area operation");
        var intent = new RimGovernor.Brain.GameOrders.Operations.AreaIntent { Operation = operation, Home = false, PollutionClear = false };
        if (v.PollutionClear)
        {
            intent = intent with { PollutionClear = true };
        }
        else if (v.Home)
        {
            intent = intent with { Home = true };
        }
        else
        {
            intent = intent with { Key = v.Key };
        }
        foreach (var r in v.Rects)
        {
            intent = intent with { Rects = [.. intent.Rects, new RimGovernor.Brain.GameOrders.Operations.AreaRect { MinX = r.MinX, MinZ = r.MinZ, MaxX = r.MaxX, MaxZ = r.MaxZ }] };
        }
        return new RimGovernor.Brain.GameOrders.Operations.Action { Intent = intent };
    }

    /// <summary>The PolicyPruneIntent of one database; native reassigns holders to their own per-pawn policy, then deletes (NativePolicyPrune.cs).</summary>
    internal static RimGovernor.Brain.GameOrders.Operations.Action PolicyPruneAction(DomainAction action)
    {
        var v =
            action.PolicyPrune
            ?? throw BridgeContractException.Contract("not a policy prune action");
        var database =
            v.Database == PolicyDatabase.Outfit ? RimGovernor.Brain.GameOrders.Operations.PolicyDatabase.Outfit
            : v.Database == PolicyDatabase.Drug ? RimGovernor.Brain.GameOrders.Operations.PolicyDatabase.Drug
            : v.Database == PolicyDatabase.Food ? RimGovernor.Brain.GameOrders.Operations.PolicyDatabase.Food
            : v.Database == PolicyDatabase.Reading ? RimGovernor.Brain.GameOrders.Operations.PolicyDatabase.Reading
            : v.Database == PolicyDatabase.AllowedArea ? RimGovernor.Brain.GameOrders.Operations.PolicyDatabase.AllowedArea
            : throw BridgeContractException.Contract("unknown policy database");
        return new RimGovernor.Brain.GameOrders.Operations.Action
        {
            Intent = new RimGovernor.Brain.GameOrders.Operations.PolicyPruneIntent { Database = database, DeleteIds = [.. v.Ids] },
        };
    }

    /// <summary>The BuildRoofIntent over canonical cells. Native designates vanilla BuildRoof live (NativeBuildRoof.cs).</summary>
    internal static RimGovernor.Brain.GameOrders.Operations.Action BuildRoofAction(DomainAction action)
    {
        var v = action.BuildRoof ?? throw BridgeContractException.Contract("not a build roof action");
        return new RimGovernor.Brain.GameOrders.Operations.Action { Intent = new RimGovernor.Brain.GameOrders.Operations.BuildRoofIntent { Cells = [.. IntentActionRules.WireCells(v.Cells)] } };
    }

    /// <summary>The RemoveRoofIntent over canonical cells. Native designates vanilla RemoveRoof live (NativeRemoveRoof.cs).</summary>
    internal static RimGovernor.Brain.GameOrders.Operations.Action RemoveRoofAction(DomainAction action)
    {
        var v = action.RemoveRoof ?? throw BridgeContractException.Contract("not a remove roof action");
        return new RimGovernor.Brain.GameOrders.Operations.Action { Intent = new RimGovernor.Brain.GameOrders.Operations.RemoveRoofIntent { Cells = [.. IntentActionRules.WireCells(v.Cells)] } };
    }

    /// <summary>The AreaPlantCutIntent over canonical cells. Native designates the cells' non-crop plants live (NativeAreaPlantCut.cs).</summary>
    internal static RimGovernor.Brain.GameOrders.Operations.Action AreaPlantCutAction(DomainAction action)
    {
        var v = action.AreaPlantCut ?? throw BridgeContractException.Contract("not an area plant cut action");
        return new RimGovernor.Brain.GameOrders.Operations.Action { Intent = new RimGovernor.Brain.GameOrders.Operations.AreaPlantCutIntent { Cells = [.. IntentActionRules.WireCells(v.Cells)] } };
    }
}
