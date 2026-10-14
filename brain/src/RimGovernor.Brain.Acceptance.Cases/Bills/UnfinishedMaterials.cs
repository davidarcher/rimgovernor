namespace RimGovernor.Brain.Acceptance.Cases;

internal static class UnfinishedMaterials
{
    internal static AcceptanceCase Case() => new()
    {
        Name = "bills/unfinished-materials",
        Scope = "Prepared native objects prove bound unfinished ingredient reads, unchanged read binding, pause/resume, native iteration notification and item cancellation. Unit tests cannot prove native object links or lifecycle callbacks; this is not pawn crafting completion.",
        Start = new LabStart(), RequiredOps = ["test/unfinished_material_probe"],
        QuietWorld = true, Crew = new Crew { Size = 1 }, Budget = TimeSpan.FromMinutes(2),
        Run = async (s, ct) =>
        {
            s.Report["native_lifecycle"] = await s.Harness.CallAsync("unfinished-materials", "test/unfinished_material_probe", [], ct).ConfigureAwait(false);
        },
    };
}
