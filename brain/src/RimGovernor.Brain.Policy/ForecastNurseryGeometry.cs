using RimGovernor.Brain.GameFacts.Observations;

namespace RimGovernor.Brain.Policy;

/// <summary>Pure geometry over one native topology. The equations and traversal ordering are the installed 1.7
/// GenClamor, RegionTraverser and GenSight implementations; no pawn's observed position is retained here.</summary>
internal sealed class ForecastNurseryGeometry(NurseryGeometryRead read)
{
    private readonly Dictionary<int, NurseryRegionRead> regions = read.Regions.ToDictionary(r => r.Id);

    internal bool Hears(Cell source, Cell listener, double hearing, int openedNursery = -1)
    {
        if (!double.IsFinite(hearing) || hearing is < 0 or > 1) throw new RequiredFactException("nursery hearing capacity");
        var radius = (float)read.ScreamRadius * (float)hearing;
        if (hearing == 0 || Distance(source, listener) > radius * radius) return false;
        var start = Region(source); var target = Region(listener);
        if (start < 0 || target < 0) return false;
        var seen = new HashSet<int> { start }; var queue = new Queue<int>(); queue.Enqueue(start);
        var counted = 0;
        while (queue.Count != 0)
        {
            var id = queue.Dequeue(); var region = Row(id);
            if (id == target) return true;
            if (!region.Door && ++counted >= read.ClamorRegions) return false;
            foreach (var next in region.Neighbours)
            {
                var row = Row(next);
                var opened = openedNursery >= 0 && row.Neighbours.Any(n => Row(n).Room == openedNursery);
                if ((!row.Door || row.Open || opened) && seen.Add(next)) queue.Enqueue(next);
            }
        }
        return false;
    }

    /// <summary>SocialInteractionUtility.IsGoodPositionForInteraction calls GenSight.LineOfSight(skipFirstCell:true).
    /// Native deliberately tests neither endpoint. Preserve its asymmetric tie direction as well as the crossed cells.</summary>
    internal bool Social(Cell actor, Cell recipient, int openedNursery = -1)
    {
        Index(actor); Index(recipient);
        if (Distance(actor, recipient) > read.SocialRadius * read.SocialRadius) return false;
        return SightCells(actor, recipient).All(cell => read.SeeOver[Index(cell)] || openedNursery >= 0
            && Region(cell) is var id && id >= 0 && Row(id).Door && Row(id).Neighbours.Any(n => Row(n).Room == openedNursery));
    }

    internal static IEnumerable<Cell> SightCells(Cell actor, Cell recipient)
    {
        var tie = actor.X != recipient.X ? actor.X < recipient.X : actor.Z < recipient.Z;
        var dx = Math.Abs(recipient.X - actor.X); var dz = Math.Abs(recipient.Z - actor.Z);
        var count = 1 + dx + dz; var x = actor.X; var z = actor.Z;
        var stepX = recipient.X > actor.X ? 1 : -1; var stepZ = recipient.Z > actor.Z ? 1 : -1;
        dx *= 4; dz *= 4; var error = dx / 2 - dz / 2;
        while (count > 1)
        {
            var cell = new Cell(x, z);
            if (cell != actor) yield return cell;
            if (error > 0 || error == 0 && tie) { x += stepX; error -= dz; }
            else { z += stepZ; error += dx; }
            count--;
        }
    }

    private NurseryRegionRead Row(int id) => regions.GetValueOrDefault(id)
        ?? throw new RequiredFactException("nursery region:" + id);
    private int Region(Cell cell) => read.CellRegions[Index(cell)];
    private int Index(Cell cell)
    {
        if (read.Width <= 0 || read.Height <= 0 || read.CellRegions.Count != read.Width * read.Height || read.SeeOver.Count != read.CellRegions.Count
            || cell.X < 0 || cell.X >= read.Width || cell.Z < 0 || cell.Z >= read.Height)
            throw new RequiredFactException("nursery geometry cell:" + cell);
        return cell.X + cell.Z * read.Width;
    }
    private static float Distance(Cell a, Cell b) => (float)(a.X - b.X) * (a.X - b.X) + (float)(a.Z - b.Z) * (a.Z - b.Z);
}
