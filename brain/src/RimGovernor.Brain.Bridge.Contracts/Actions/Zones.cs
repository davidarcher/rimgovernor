using Op = RimGovernor.Protocol.Operations;

namespace RimGovernor.Brain.Bridge.Contracts;

// Go zone.go, area.go, build_roof.go, remove_roof.go, area_plant_cut.go and policy_prune.go: the intents that edit
// ground, storage and the policy databases. Native checks the ground and the holders live when it applies.
// PreviewZone (the zones_preview call) is wire plumbing and is not part of this file.
public static partial class BridgeFunctions
{
    /// <summary>Go's <c>ZoneConfiguration</c>: the create shape of the one zone intent.</summary>
    public static Op.ZoneIntent ZoneConfiguration(ZoneCreate zone)
    {
        var command = new Op.ZoneIntent
        {
            Label = zone.Label,
            AddCells = new Op.Cells { ExplicitCells = new Op.CellList { Cells = { WireCells(zone.Cells) } } },
        };
        switch (zone.Kind)
        {
            case ZoneKind.Fishing:
                command.Kind = Op.ZoneType.Fishing;
                command.Fishing = new Op.FishingSettings { PopulationFloor = DomainConstants.FishingPopulationFloor };
                if (zone.ExtendId.Length != 0)
                {
                    command.Zone = NewRef(zone.ExtendId);
                }
                break;
            case ZoneKind.Stockpile:
                // A cell-list stockpile keeps the explicit list built above.
                if (zone.Rectangle is { Width: > 0 } rect)
                {
                    command.AddCells = new Op.Cells { Rectangle = new Op.Rectangle { Origin = WireCell(rect.Origin), Width = rect.Width, Height = rect.Height } };
                }
                command.Kind = Op.ZoneType.Stockpile;
                command.Stockpile = StockpileSettings(zone.Filter ?? throw Contract("stockpile zone without a filter"), zone.Priority ?? throw Contract("stockpile zone without a priority"));
                break;
            default:
                command.Kind = Op.ZoneType.Growing;
                command.Growing = new Op.GrowingSettings { PlantDef = zone.Crop, AllowSow = true, AllowCut = true };
                break;
        }
        return command;
    }

    /// <summary>
    /// Go's <c>StockpileSettings</c>: the native StockpileSettings for a filter and priority. The filter's base is the
    /// preset, and a FilterPatch is sent only when the filter has selectors or ranges.
    /// </summary>
    public static Op.StockpileSettings StockpileSettings(StockpileFilter filter, StockpilePriority priority)
    {
        var settings = new Op.StockpileSettings
        {
            Priority = priority switch
            {
                StockpilePriority.Critical => Op.StoragePriority.Critical,
                StockpilePriority.Important => Op.StoragePriority.Important,
                StockpilePriority.Preferred => Op.StoragePriority.Preferred,
                StockpilePriority.Normal => Op.StoragePriority.Normal,
                StockpilePriority.Low => Op.StoragePriority.Low,
                _ => Op.StoragePriority.Unspecified,
            },
            Preset = filter.Base switch
            {
                FilterBase.Everything => Op.FilterPreset.Everything,
                FilterBase.Nothing => Op.FilterPreset.Nothing,
                FilterBase.Food => Op.FilterPreset.Food,
                FilterBase.Perishables => Op.FilterPreset.Perishables,
                FilterBase.Nonperishables => Op.FilterPreset.Nonperishables,
                FilterBase.OutdoorSafe => Op.FilterPreset.OutdoorSafe,
                FilterBase.IndoorOnly => Op.FilterPreset.IndoorOnly,
                _ => Op.FilterPreset.Unspecified,
            },
        };
        var patch = new Op.FilterPatch { Allow = { FilterSelectors(filter.Allow) }, Disallow = { FilterSelectors(filter.Disallow) } };
        if (filter.HitPoints is { } hitPoints)
        {
            patch.HitPointsMin = hitPoints.Min;
            patch.HitPointsMax = hitPoints.Max;
        }
        if (filter.QualityRange is { } quality)
        {
            patch.QualityMin = Domain.Wire.Name(quality.Min);
            patch.QualityMax = Domain.Wire.Name(quality.Max);
        }
        if (patch.Allow.Count != 0 || patch.Disallow.Count != 0 || patch.HasHitPointsMin || patch.HasQualityMin)
        {
            settings.Filter = patch;
        }
        return settings;
    }

    private static List<Op.FilterSelector> FilterSelectors(IEnumerable<FilterSelector> rows)
    {
        var output = new List<Op.FilterSelector>();
        foreach (var s in rows)
        {
            switch (s.Kind)
            {
                case SelectorKind.ThingDef:
                    output.Add(new Op.FilterSelector { ThingDef = s.Name });
                    break;
                case SelectorKind.CategoryDef:
                    output.Add(new Op.FilterSelector { CategoryDef = s.Name });
                    break;
                case SelectorKind.SpecialFilter:
                    output.Add(new Op.FilterSelector { SpecialFilterDef = s.Name });
                    break;
            }
        }
        return output;
    }

    private static Op.Action ZoneAction(Op.ZoneIntent intent) => new() { Zone = intent };

    /// <summary>Go's <c>zoneCreateAction</c>: the create shape of the zone arm for one zone_create, the configuration without a census token; native checks the ground live.</summary>
    private static Op.Action ZoneCreateAction(DomainAction action) =>
        ZoneAction(ZoneConfiguration(action.ZoneCreate ?? throw Contract("not a zone create action")));

    /// <summary>Go's <c>zoneDeleteAction</c>: the delete shape of the zone arm for one zone_delete.</summary>
    private static Op.Action ZoneDeleteAction(DomainAction action)
    {
        var del = action.ZoneDelete ?? throw Contract("not a zone delete action");
        ValidId(del.Zone);
        return ZoneAction(new Op.ZoneIntent { Zone = NewRef(del.Zone), Delete = true });
    }

    /// <summary>Go's <c>zoneCellsAction</c>: the cells shape of the zone arm for one zone_cell_edit.</summary>
    private static Op.Action ZoneCellsAction(DomainAction action)
    {
        var e = action.ZoneCellEdit ?? throw Contract("not a zone cell edit action");
        var selection = new Op.Cells { ExplicitCells = new Op.CellList { Cells = { WireCells(e.Cells) } } };
        var intent = new Op.ZoneIntent { Zone = NewRef(e.Zone) };
        if (e.Mode == CellEditMode.Remove)
        {
            intent.RemoveCells = selection;
        }
        else
        {
            intent.AddCells = selection;
        }
        return ZoneAction(intent);
    }

    /// <summary>Go's <c>stockpileAction</c>: the settings shape of the zone arm for one stockpile_patch, on a stockpile zone or a storage building alike.</summary>
    private static Op.Action StockpileAction(DomainAction action)
    {
        var p = action.StockpilePatch ?? throw Contract("not a stockpile patch action");
        return ZoneAction(new Op.ZoneIntent { Zone = NewRef(p.Target), Stockpile = StockpileSettings(p.Filter, p.Priority) });
    }

    /// <summary>
    /// Go's <c>areaAction</c>: the AreaIntent of one bot-owned allowed area or the home area. Native resolves the bot
    /// area by its key label and never touches a player's area (NativeAreaIntent.cs).
    /// </summary>
    private static Op.Action AreaAction(DomainAction action)
    {
        var v = action.Area ?? throw Contract("not an area action");
        var operation = v.Operation switch
        {
            AreaOperation.Create => Op.AreaOperation.Create,
            AreaOperation.SetCells => Op.AreaOperation.SetCells,
            AreaOperation.ClearCells => Op.AreaOperation.ClearCells,
            AreaOperation.Delete => Op.AreaOperation.Delete,
            _ => Op.AreaOperation.Unspecified,
        };
        var intent = new Op.AreaIntent { Operation = operation };
        if (v.PollutionClear)
        {
            intent.PollutionClear = true;
        }
        else if (v.Home)
        {
            intent.Home = true;
        }
        else
        {
            intent.Key = v.Key;
        }
        foreach (var r in v.Rects)
        {
            intent.Rects.Add(new Op.AreaRect { MinX = r.MinX, MinZ = r.MinZ, MaxX = r.MaxX, MaxZ = r.MaxZ });
        }
        return new Op.Action { Area = intent };
    }

    /// <summary>Go's <c>policyPruneAction</c>: the PolicyPruneIntent of one database; native reassigns holders to their own per-pawn policy, then deletes (NativePolicyPrune.cs).</summary>
    private static Op.Action PolicyPruneAction(DomainAction action)
    {
        var v = action.PolicyPrune ?? throw Contract("not a policy prune action");
        var database = v.Database switch
        {
            PolicyDatabase.Outfit => Op.PolicyDatabase.Outfit,
            PolicyDatabase.Drug => Op.PolicyDatabase.Drug,
            PolicyDatabase.Food => Op.PolicyDatabase.Food,
            PolicyDatabase.Reading => Op.PolicyDatabase.Reading,
            PolicyDatabase.AllowedArea => Op.PolicyDatabase.AllowedArea,
            _ => Op.PolicyDatabase.Unspecified,
        };
        return new Op.Action { PolicyPrune = new Op.PolicyPruneIntent { Database = database, DeleteIds = { v.Ids } } };
    }

    /// <summary>Go's <c>buildRoofAction</c>: the BuildRoofIntent over canonical cells. Native designates vanilla BuildRoof live (NativeBuildRoof.cs).</summary>
    private static Op.Action BuildRoofAction(DomainAction action)
    {
        var v = action.BuildRoof ?? throw Contract("not a build roof action");
        return new Op.Action { BuildRoof = new Op.BuildRoofIntent { Cells = { WireCells(v.Cells) } } };
    }

    /// <summary>Go's <c>removeRoofAction</c>: the RemoveRoofIntent over canonical cells. Native designates vanilla RemoveRoof live (NativeRemoveRoof.cs).</summary>
    private static Op.Action RemoveRoofAction(DomainAction action)
    {
        var v = action.RemoveRoof ?? throw Contract("not a remove roof action");
        return new Op.Action { RemoveRoof = new Op.RemoveRoofIntent { Cells = { WireCells(v.Cells) } } };
    }

    /// <summary>Go's <c>areaPlantCutAction</c>: the AreaPlantCutIntent over canonical cells. Native designates the cells' non-crop plants live (NativeAreaPlantCut.cs).</summary>
    private static Op.Action AreaPlantCutAction(DomainAction action)
    {
        var v = action.AreaPlantCut ?? throw Contract("not an area plant cut action");
        return new Op.Action { AreaPlantCut = new Op.AreaPlantCutIntent { Cells = { WireCells(v.Cells) } } };
    }
}
