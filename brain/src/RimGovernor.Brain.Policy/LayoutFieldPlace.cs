namespace RimGovernor.Brain.Policy;

// Field placement (#2920). A field node (layout_field_nodes.go) is placed by the embedder beside the room
// groups and written by the tiler as a field zone. It is a block like a room's, with three differences, all of
// them the outdoors:
//
//   - Site. Its mask is the soil the crop grows in (walkable, not rock, fertile to the crop's least), not the
//     core the rooms stand on, so a field lands on soil and a room is free to stand on soil a field does not
//     use: occupancy is shared, and coreObstacles no longer bans field cells once fields are nodes. A cell the
//     survey lacks (fog) is in no mask.
//   - Walls. It has none: it may touch a room's wall ring but not its interior or the ring itself, and may
//     touch another field.
//   - Cost. A field wants soil, so the soil and rock terms drop (the mask is the soil). Its centre and edge
//     terms are the mean per cell, one node's worth: a 400-cell field would otherwise weigh 20-50 times a
//     12-cell room in the cell terms, and the walk term, the trade between a field and a room, would never win
//     against it. The crop-flow link is priced by the nearest-edge distance (FieldGap), not GapDist, which
//     counts a wall.
//
// No constant is new: the shapes are the squarest rectangle and its turn, as a room's, and a field no rectangle
// of soil fits is unplaceable (resizing on demand is the next stage).
public static partial class PolicyConstants
{
    // Verdict reasons of field placement.
    public const string ReasonEmbedFieldPlaced = "embed_field_on_soil";
    public const string ReasonEmbedFieldNoSoil = "embed_field_no_soil";
    public const string ReasonTileField = "tile_field_zone";
}

/// <summary>A field node with its links to the room groups: the weights of its crop-flow edges, split over the groups holding the station at the other end.</summary>
public sealed record EmbedField(FieldNode Node, IReadOnlyList<GroupLink> Links);

/// <summary>Where a field stands.</summary>
public sealed record EmbeddedField(string Id, Rectangle Interior);

public sealed partial class Embedding
{
    /// <summary>The field's block.</summary>
    public (Rectangle Block, bool Ok) Field(string id)
    {
        foreach (var f in Fields)
        {
            if (f.Id == id)
            {
                return (f.Interior, true);
            }
        }
        return (default, false);
    }
}

internal partial class EmbedFixed
{
    /// <summary>Prices the soil each field may stand on: a prefix sum per least fertility, over the cells the survey holds that the core allows and that are not rock.</summary>
    private void FieldMasks(EmbedInput input, PlanScorer scorer)
    {
        var byFertility = new Dictionary<double, (Prefix Sum, List<Cell> Cells)>();
        var masks = new List<Prefix>();
        var cells = new List<IReadOnlyList<Cell>>();
        foreach (var f in input.Fields)
        {
            if (!byFertility.TryGetValue(f.Node.FertilityMin, out var m))
            {
                var soil = new HashSet<Cell>();
                foreach (var c in input.Survey.Cells)
                {
                    if (scorer.G.Core.Contains(c.Cell) && !c.Rock && c.Walkable && c.Fertility >= f.Node.FertilityMin)
                    {
                        soil.Add(c.Cell);
                    }
                }
                var list = new List<Cell>(soil);
                PolicyFunctions.SortCells(list);
                m = (new Prefix(input.Survey.Bounds.Width, input.Survey.Bounds.Height, c => PolicyFunctions.B2i(soil.Contains(c))), list);
                byFertility[f.Node.FertilityMin] = m;
            }
            masks.Add(m.Sum);
            cells.Add(m.Cells);
        }
        FieldMask = masks;
        FieldCells = cells;
    }
}

internal sealed partial class Embedder
{
    public bool IsField(int i) => i >= _nRooms;

    /// <summary>The cells a block of group i may start on: the core for a room, the soil of its crop for a field.</summary>
    public IReadOnlyList<Cell> CellsFor(int i) => IsField(i) ? FieldCells[i - _nRooms] : _core;

    /// <summary>Reports field i at r on its soil and clear of the other blocks: not over a room's interior or wall ring, not over another field.</summary>
    public bool FieldFits(int i, Rectangle r, IReadOnlyList<Rectangle> blocks)
    {
        var m = FieldMask[i - _nRooms];
        if (!m.Inside(r) || m.Sum(r) != (long)r.Width * r.Height)
        {
            return false;
        }
        for (var j = 0; j < blocks.Count; j++)
        {
            var b = blocks[j];
            if (j == i || b.Width == 0)
            {
                continue;
            }
            if (IsField(j))
            {
                if (PolicyFunctions.AxisGap(r.X, r.Width, b.X, b.Width) < 0 && PolicyFunctions.AxisGap(r.Z, r.Height, b.Z, b.Height) < 0)
                {
                    return false;
                }
            }
            else if (PolicyFunctions.Collides(r, b))
            {
                return false;
            }
        }
        return !HitsHeld(r);
    }

    /// <summary>A field's centre and edge terms, the mean per cell.</summary>
    public double FieldCellCost(Rectangle r) =>
        (double)-(Centre.Sum(r) + Edge.Sum(r)) / Math.Max(r.Width * r.Height, 1);

    /// <summary>The walking gap between group i at r and group j at b.</summary>
    public int Gap(int i, Rectangle r, int j, Rectangle b) =>
        IsField(i) || IsField(j) ? PolicyFunctions.FieldGap(r, b) : PolicyFunctions.GapDist(r, b);
}

internal sealed partial class EmbedRun
{
    /// <summary>Reads field id's block into the embedding: the field with its decision, and its per-node centre and edge terms in the score.</summary>
    public void EmbedField(Embedding output, string id, Rectangle b)
    {
        if (b.Width == 0)
        {
            output.Decisions.Add(new EmbedDecision { Group = -1, Other = -1, Field = id, Verdict = PolicyConstants.EmbedUnplaceable, Reason = PolicyConstants.ReasonEmbedFieldNoSoil });
            return;
        }
        double cells = Math.Max(b.Width * b.Height, 1);
        output.Fields.Add(new EmbeddedField(id, b));
        output.Decisions.Add(new EmbedDecision { Group = -1, Other = -1, Field = id, Verdict = PolicyConstants.EmbedPlace, Reason = PolicyConstants.ReasonEmbedFieldPlaced });
        output.Score.Centre += (int)(E.Centre.Sum(b) / cells);
        output.Score.Edge += (int)(E.Edge.Sum(b) / cells);
    }
}

public static partial class PolicyFunctions
{
    /// <summary>
    /// The graph's fields with their crop-flow edges moved onto the groups. A node held by several groups (its
    /// stations split) shares the edge's weight in proportion to its stations in each.
    /// </summary>
    public static IReadOnlyList<EmbedField> FieldEmbedLinks(StationGraph g, IReadOnlyList<RoomGroup> groups)
    {
        var total = new Dictionary<string, int>();
        foreach (var grp in groups)
        {
            foreach (var s in grp.Stations)
            {
                total[s.Node] = total.GetValueOrDefault(s.Node) + s.Count;
            }
        }
        var output = new List<EmbedField>();
        foreach (var f in g.Fields)
        {
            var weight = new Dictionary<int, double>();
            foreach (var e in g.Edges)
            {
                string other;
                if (f.Id == e.A)
                {
                    other = e.B;
                }
                else if (f.Id == e.B)
                {
                    other = e.A;
                }
                else
                {
                    continue;
                }
                if (total.GetValueOrDefault(other) == 0)
                {
                    continue;
                }
                foreach (var grp in groups)
                {
                    foreach (var s in grp.Stations)
                    {
                        if (s.Node == other)
                        {
                            weight[grp.Id] = weight.GetValueOrDefault(grp.Id) + (e.Weight * s.Count / total[other]);
                        }
                    }
                }
            }
            var links = new List<GroupLink>();
            foreach (var (to, w) in weight)
            {
                if (w > 0)
                {
                    links.Add(new GroupLink { To = to, Weight = w });
                }
            }
            links.Sort((a, b) => a.To - b.To);
            output.Add(new EmbedField(f, links));
        }
        return output;
    }

    /// <summary>
    /// The input with each field appended to the groups as a block of its area, and the reverse of its links
    /// on the groups it links (clustering's links run both ways). The input's groups are not changed.
    /// </summary>
    internal static EmbedInput WithFields(this EmbedInput input)
    {
        var groups = new List<RoomGroup>(input.Groups.Count + input.Fields.Count);
        foreach (var g in input.Groups)
        {
            groups.Add(g with { Links = [.. g.Links] });
        }
        foreach (var f in input.Fields)
        {
            var id = groups.Count;
            var links = new List<GroupLink>();
            foreach (var l in f.Links)
            {
                if (l.To < 0 || l.To >= input.Groups.Count || l.Weight <= 0)
                {
                    continue;
                }
                links.Add(new GroupLink { To = l.To, Weight = l.Weight });
                groups[l.To].Links.Add(new GroupLink { To = id, Weight = l.Weight });
            }
            groups.Add(new RoomGroup { Id = id, Area = f.Node.Area, Links = links });
        }
        return input with { Groups = groups };
    }

    /// <summary>
    /// The walking gap between a field and a block in cells: the cells between their nearest edges, floored at
    /// 1 (touching, as sharing a wall is for rooms), so LinkCost's gap-1 is the cells walked.
    /// </summary>
    internal static int FieldGap(Rectangle a, Rectangle b)
    {
        int sx = AxisGap(a.X, a.Width, b.X, b.Width), sz = AxisGap(a.Z, a.Height, b.Z, b.Height);
        return Math.Max(Math.Max(sx, 0) + Math.Max(sz, 0), 1);
    }

    /// <summary>Adds each placed field to the plan as a field zone, after the whole-map zones, and notes it. TileDecision.Field names the field.</summary>
    internal static void FieldZones(this Tiler t)
    {
        t.Plan = t.Plan with { Zones = [.. t.In.Zones] };
        foreach (var f in t.In.Embedding.Fields)
        {
            var z = FieldZone(f.Interior, t.In.Survey);
            if (z.Runs.Count == 0)
            {
                continue;
            }
            t.Plan = t.Plan with { Zones = [.. t.Plan.Zones, z] };
            t.Out.Decisions.Add(new TileDecision { Group = -1, Other = -1, Field = f.Id, Verdict = TilePlace, Reason = ReasonTileField });
        }
    }

    /// <summary>A placed field as a zone: its rows, less the cells the survey lacks (fogged cells get no zone).</summary>
    internal static LayoutZone FieldZone(Rectangle r, MapSurvey s)
    {
        var known = new HashSet<Cell>();
        foreach (var c in s.Cells)
        {
            if (c.Cell.X >= r.X && c.Cell.X < r.X + r.Width && c.Cell.Z >= r.Z && c.Cell.Z < r.Z + r.Height)
            {
                known.Add(c.Cell);
            }
        }
        var runs = new List<RowRun>();
        for (var row = r.Z; row < r.Z + r.Height; row++)
        {
            for (var x = r.X; x < r.X + r.Width; x++)
            {
                if (!known.Contains(new Cell(x, row)))
                {
                    continue;
                }
                var start = x;
                while (x + 1 < r.X + r.Width && known.Contains(new Cell(x + 1, row)))
                {
                    x++;
                }
                runs.Add(new RowRun(row, start, x - start + 1));
            }
        }
        return new LayoutZone { Kind = ZoneKind.Field, Runs = runs };
    }
}
