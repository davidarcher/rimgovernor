using RimGovernor.Brain.Bridge.Contracts;
using RimGovernor.Brain.Domain;
using static RimGovernor.Brain.Domain.Fact;
using BridgeCatalog = RimGovernor.Brain.Bridge.DefinitionCatalog;
using P = RimGovernor.Brain.Policy;

namespace RimGovernor.Brain.Host;

/// <summary>
/// Go <c>DecodeIdeology</c> (ideology.go): validates the frame's ideology section under an identity and resolves every def
/// name in it against the catalog's Ideology rows. A name the catalog lacks, or a section whose catalog has no Ideology
/// defs, is a contract failure; a row that is not valid fails the whole decode, nothing is skipped or defaulted.
/// </summary>
internal static class IdeologyDecoder
{
    /// <summary>Go <c>DecodeIdeology</c>.</summary>
    internal static P.Ideoligion Decode(PbObservations.IdeologySnapshot? v, PbCommon.Identity identity, BridgeCatalog catalog)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        if (v is null || !Passes(() => BridgeFunctions.ValidateContext(v.Context)) || !BridgeFunctions.SameIdentity(v.Context!.Identity, identity))
        {
            throw BridgeFunctions.Contract("invalid ideology context");
        }
        var defs = catalog.IdeologyDefs() ?? throw BridgeFunctions.Contract("ideology section without catalog ideology defs");
        if (!BridgeFunctions.IsValidId(v.IdeoId ?? "") || v.ObligationsActive is null || v.Believers is null || v.Believers < 0 || v.MinBelieversForObligations is null || v.MinBelieversForObligations < 0)
        {
            throw BridgeFunctions.Contract("invalid ideology section");
        }
        var facts = new P.IdeoligionFacts
        {
            IdeoId = v.IdeoId ?? "",
            Memes = [.. v.Memes],
            ObligationsActive = v.ObligationsActive.GetValueOrDefault(),
            Believers = v.Believers.GetValueOrDefault(),
            MinBelievers = v.MinBelieversForObligations.GetValueOrDefault(),
        };
        if (v.Fluid is not null || v.CanReform is not null || v.DevelopmentPoints is not null || v.ReformCount is not null || v.NextReformPoints is not null)
        {
            var fluid = v.Fluid.GetValueOrDefault();
            var canReform = v.CanReform.GetValueOrDefault();
            var points = v.DevelopmentPoints.GetValueOrDefault();
            var reforms = v.ReformCount.GetValueOrDefault();
            var next = v.NextReformPoints.GetValueOrDefault();
            if (v.Fluid is null || v.CanReform is null || v.DevelopmentPoints is null || v.ReformCount is null || v.NextReformPoints is null
                || points < 0 || reforms < 0 || next < 0
                || (!fluid && (canReform || points != 0 || reforms != 0 || next != 0))
                || (fluid && (next == 0 || canReform != (points >= next))))
            {
                throw BridgeFunctions.Contract("invalid ideoligion development state");
            }
            facts = facts with { Development = Known(new P.IdeoDevelopment(fluid, canReform, points, reforms, next)) };
        }
        foreach (var meme in v.Memes)
        {
            if (!catalog.HasMeme(meme))
            {
                throw BridgeFunctions.Contract($"ideology meme \"{meme}\" is not in the catalog");
            }
        }

        var ids = new HashSet<string>(StringComparer.Ordinal);
        void Claim(string id)
        {
            if (!BridgeFunctions.IsValidId(id) || !ids.Add(id))
            {
                throw BridgeFunctions.Contract($"invalid or duplicate ideology precept id \"{id}\"");
            }
        }
        void Precept(string id, string def)
        {
            Claim(id);
            if (!defs.Precepts.ContainsKey(def))
            {
                throw BridgeFunctions.Contract($"ideology precept \"{def}\" is not in the catalog");
            }
        }

        var precepts = new List<P.HeldPrecept>();
        foreach (var row in v.Precepts)
        {
            Precept(row.Id ?? "", row.DefName ?? "");
            precepts.Add(new P.HeldPrecept(row.Id ?? "", row.DefName ?? ""));
        }
        var roles = new List<P.HeldRole>();
        foreach (var row in v.Roles)
        {
            Claim(row.Id ?? "");
            if (!defs.Roles.ContainsKey(row.DefName ?? "") || row.Active is null)
            {
                throw BridgeFunctions.Contract($"ideology role \"{row.DefName}\" is not in the catalog or lacks its state");
            }
            var seen = new HashSet<string>(StringComparer.Ordinal);
            var held = new List<P.PawnId>();
            foreach (var pawn in row.Pawns)
            {
                if (!BridgeFunctions.ValidRef(pawn) || !seen.Add(pawn.Id ?? ""))
                {
                    throw BridgeFunctions.Contract("invalid or duplicate ideology role pawn");
                }
                held.Add(new P.PawnId(pawn.Id ?? ""));
            }
            roles.Add(new P.HeldRole(row.Id ?? "", row.DefName ?? "", row.Active.GetValueOrDefault(), held));
        }
        var rituals = new List<P.HeldRitual>();
        foreach (var row in v.Rituals)
        {
            Precept(row.Id ?? "", row.DefName ?? "");
            if ((row.Pattern is not null && !BridgeFunctions.IsValidId(row.Pattern)) || row.LastFinishedTick is null || row.ActiveObligations is null || row.ActiveObligations < 0 || row.RepeatPenaltyActive is null || row.Running is null)
            {
                throw BridgeFunctions.Contract($"invalid ideology ritual \"{row.Id}\"");
            }
            if (row.Pattern is not null && !defs.Rituals.ContainsKey(row.Pattern))
            {
                throw BridgeFunctions.Contract($"ideology ritual pattern \"{row.Pattern}\" is not in the catalog");
            }
            rituals.Add(new P.HeldRitual
            {
                Id = row.Id ?? "",
                Def = row.DefName ?? "",
                Pattern = row.Pattern ?? "",
                LastFinishedTick = row.LastFinishedTick.GetValueOrDefault(),
                ActiveObligations = row.ActiveObligations.GetValueOrDefault(),
                RepeatPenaltyActive = row.RepeatPenaltyActive.GetValueOrDefault(),
                Running = row.Running.GetValueOrDefault(),
            });
        }
        var buildings = new List<P.HeldBuilding>();
        foreach (var row in v.Buildings)
        {
            Precept(row.Id ?? "", row.DefName ?? "");
            if (row.Building is not null && !BridgeFunctions.IsValidId(row.Building))
            {
                throw BridgeFunctions.Contract($"invalid ideology building \"{row.Id}\"");
            }
            buildings.Add(new P.HeldBuilding(row.Id ?? "", row.DefName ?? "", row.Building ?? ""));
        }
        return new P.Ideoligion(defs, facts with { Precepts = precepts, Roles = roles, Rituals = rituals, Buildings = buildings });
    }

    private static bool Passes(Action check)
    {
        try
        {
            check();
            return true;
        }
        catch (BridgeContractException)
        {
            return false;
        }
    }
}
