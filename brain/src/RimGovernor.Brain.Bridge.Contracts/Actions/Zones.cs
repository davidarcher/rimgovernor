using Op = RimGovernor.Contracts.Wire.Operations;

namespace RimGovernor.Brain.Bridge.Contracts;

// The intents that edit
// ground, storage and the policy databases. Native checks the ground and the holders live when it applies.
// PreviewZone (the zones_preview call) is wire plumbing and is not part of this file.
public static class Zones
{
    /// <summary>The create shape of the one zone intent.</summary>
    public static Op.ZoneIntent ZoneConfiguration(ZoneCreate zone)
    {
        var command = new Op.ZoneIntent
        {
            Label = zone.Label,
            AddCells = new Op.Cells { Selection = new Op.CellList { Cells = [.. IntentActionRules.WireCells(zone.Cells)] } }, Delete = false, RequireCoveredEmpty = false, Kind = Op.ZoneType.Existing };
        if (zone.Kind == ZoneKind.Fishing)
        {
            command = command with { Kind = Op.ZoneType.Fishing };
            command = command with
            {
                Fishing = new Op.FishingSettings
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
                    AddCells = new Op.Cells
                    {
                        Selection = new Op.Rectangle
                        {
                            Origin = IntentActionRules.WireCell(rect.Origin),
                            Width = rect.Width,
                            Height = rect.Height,
                        },
                    },
                };
            }
            command = command with { Kind = Op.ZoneType.Stockpile };
            return command with
            {
                Stockpile = StockpileSettings(stockpile.Filter, stockpile.Priority),
            };
        }
        if (zone.Kind == ZoneKind.Growing)
        {
            command = command with { Kind = Op.ZoneType.Growing };
            return command with
            {
                Growing = new Op.GrowingSettings
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
    public static Op.StockpileSettings StockpileSettings(StockpileFilter filter, StockpilePriority priority)
    {
        var settings = new Op.StockpileSettings
        {
            Priority = new Op.StockpilePriorityChange.Selected(priority),
            Preset = new Op.StockpilePresetChange.Selected(filter.Base),
        };
        var patch = new Op.FilterPatch { Allow = [.. FilterSelectors(filter.Allow)], Disallow = [.. FilterSelectors(filter.Disallow)], HitPoints = new Op.HitPointRange.Preserve() };
        if (filter.HitPoints is { } hitPoints)
        {
            patch = patch with { HitPoints = new Op.HitPointRange.Selected(hitPoints.Min, hitPoints.Max) };
        }
        if (filter.QualityRange is { } quality)
        {
            patch = patch with { QualityMin = Domain.Wire.Name(quality.Min) };
            patch = patch with { QualityMax = Domain.Wire.Name(quality.Max) };
        }
        if (patch.Allow.Count != 0 || patch.Disallow.Count != 0 || patch.HitPoints is Op.HitPointRange.Selected || patch.QualityMin is not null)
        {
            settings = settings with { Filter = patch };
        }
        return settings;
    }

    private static List<Op.FilterSelector> FilterSelectors(EquatableArray<FilterSelector> rows)
    {
        var output = new List<Op.FilterSelector>();
        foreach (var s in rows)
        {
            switch (s.Kind)
            {
                case SelectorKind.ThingDef:
                    output.Add(new Op.FilterSelector { Definition = new Op.FilterSelectorDefinition.ThingDefCase(s.Name) });
                    break;
                case SelectorKind.CategoryDef:
                    output.Add(new Op.FilterSelector { Definition = new Op.FilterSelectorDefinition.CategoryDefCase(s.Name) });
                    break;
                case SelectorKind.SpecialFilter:
                    output.Add(new Op.FilterSelector { Definition = new Op.FilterSelectorDefinition.SpecialFilterDefCase(s.Name) });
                    break;
            }
        }
        return output;
    }

    private static Op.Action ZoneAction(Op.ZoneIntent intent) => new() { Intent = intent };

    /// <summary>The create shape of the zone arm for one zone_create, the configuration without a census token; native checks the ground live.</summary>
    internal static Op.Action ZoneCreateAction(DomainAction action) =>
        ZoneAction(ZoneConfiguration(action.ZoneCreate ?? throw BridgeContractException.Contract("not a zone create action")));

    /// <summary>The delete shape of the zone arm for one zone_delete.</summary>
    internal static Op.Action ZoneDeleteAction(DomainAction action)
    {
        var del = action.ZoneDelete ?? throw BridgeContractException.Contract("not a zone delete action");
        WireValidation.ValidId(del.Zone);
        return ZoneAction(new Op.ZoneIntent { Zone = IntentActionRules.NewRef(del.Zone), Delete = true, RequireCoveredEmpty = false, Kind = Op.ZoneType.Existing });
    }

    /// <summary>The cells shape of the zone arm for one zone_cell_edit.</summary>
    internal static Op.Action ZoneCellsAction(DomainAction action)
    {
        var e = action.ZoneCellEdit ?? throw BridgeContractException.Contract("not a zone cell edit action");
        var selection = new Op.Cells { Selection = new Op.CellList { Cells = [.. IntentActionRules.WireCells(e.Cells)] } };
        var intent = new Op.ZoneIntent { Zone = IntentActionRules.NewRef(e.Zone), Delete = false, RequireCoveredEmpty = false, Kind = Op.ZoneType.Existing };
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
    internal static Op.Action StockpileAction(DomainAction action)
    {
        var p = action.StockpilePatch ?? throw BridgeContractException.Contract("not a stockpile patch action");
        return ZoneAction(new Op.ZoneIntent { Zone = IntentActionRules.NewRef(p.Target), Stockpile = StockpileSettings(p.Filter, p.Priority), Delete = false, RequireCoveredEmpty = false, Kind = Op.ZoneType.Existing });
    }

    /// <summary>
    /// The AreaIntent of one bot-owned allowed area or the home area. Native resolves the bot
    /// area by its key label and never touches a player's area (NativeAreaIntent.cs).
    /// </summary>
    internal static Op.Action AreaAction(DomainAction action)
    {
        var v = action.Area ?? throw BridgeContractException.Contract("not an area action");
        var operation =
            v.Operation == AreaOperation.Create ? Op.AreaOperation.Create
            : v.Operation == AreaOperation.SetCells ? Op.AreaOperation.SetCells
            : v.Operation == AreaOperation.ClearCells ? Op.AreaOperation.ClearCells
            : v.Operation == AreaOperation.Delete ? Op.AreaOperation.Delete
            : throw BridgeContractException.Contract("unknown area operation");
        var intent = new Op.AreaIntent { Operation = operation, Home = false, PollutionClear = false };
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
            intent = intent with { Rects = [.. intent.Rects, new Op.AreaRect { MinX = r.MinX, MinZ = r.MinZ, MaxX = r.MaxX, MaxZ = r.MaxZ }] };
        }
        return new Op.Action { Intent = intent };
    }

    /// <summary>The PolicyPruneIntent of one database; native reassigns holders to their own per-pawn policy, then deletes (NativePolicyPrune.cs).</summary>
    internal static Op.Action PolicyPruneAction(DomainAction action)
    {
        var v =
            action.PolicyPrune
            ?? throw BridgeContractException.Contract("not a policy prune action");
        var database =
            v.Database == PolicyDatabase.Outfit ? Op.PolicyDatabase.Outfit
            : v.Database == PolicyDatabase.Drug ? Op.PolicyDatabase.Drug
            : v.Database == PolicyDatabase.Food ? Op.PolicyDatabase.Food
            : v.Database == PolicyDatabase.Reading ? Op.PolicyDatabase.Reading
            : v.Database == PolicyDatabase.AllowedArea ? Op.PolicyDatabase.AllowedArea
            : throw BridgeContractException.Contract("unknown policy database");
        return new Op.Action
        {
            Intent = new Op.PolicyPruneIntent { Database = database, DeleteIds = [.. v.Ids] },
        };
    }

    /// <summary>The BuildRoofIntent over canonical cells. Native designates vanilla BuildRoof live (NativeBuildRoof.cs).</summary>
    internal static Op.Action BuildRoofAction(DomainAction action)
    {
        var v = action.BuildRoof ?? throw BridgeContractException.Contract("not a build roof action");
        return new Op.Action { Intent = new Op.BuildRoofIntent { Cells = [.. IntentActionRules.WireCells(v.Cells)] } };
    }

    /// <summary>The RemoveRoofIntent over canonical cells. Native designates vanilla RemoveRoof live (NativeRemoveRoof.cs).</summary>
    internal static Op.Action RemoveRoofAction(DomainAction action)
    {
        var v = action.RemoveRoof ?? throw BridgeContractException.Contract("not a remove roof action");
        return new Op.Action { Intent = new Op.RemoveRoofIntent { Cells = [.. IntentActionRules.WireCells(v.Cells)] } };
    }

    /// <summary>The AreaPlantCutIntent over canonical cells. Native designates the cells' non-crop plants live (NativeAreaPlantCut.cs).</summary>
    internal static Op.Action AreaPlantCutAction(DomainAction action)
    {
        var v = action.AreaPlantCut ?? throw BridgeContractException.Contract("not an area plant cut action");
        return new Op.Action { Intent = new Op.AreaPlantCutIntent { Cells = [.. IntentActionRules.WireCells(v.Cells)] } };
    }
}
