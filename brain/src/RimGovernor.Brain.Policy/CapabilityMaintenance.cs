using RimGovernor.Contracts.Wire.Observations;

namespace RimGovernor.Brain.Policy;

public enum CapabilityDevelopmentKind { Acquisition, Maintenance, UnsupportedImprovement }
/// <summary>One assigned native requirement. Alternatives are OR; distinct requirements are AND.</summary>
public sealed record AssignedCapability(string Id, ServiceTarget Target, PawnId Pawn,
    EquatableArray<SkillRequirement> Alternatives, CapabilityDevelopmentKind Kind);

public static class CapabilityMaintenanceRules
{
    /// <summary>Only actually held active roles establish maintenance requirements. Unfilled roles are
    /// acquisition, and unlocked practice ceilings never establish a desired level.</summary>
    public static EquatableArray<AssignedCapability> AssignedCombatCapabilities(Fact<Ideoligion> ideology)
    {
        if (!ideology.TryGetValue(out var read)) return [];
        var result = new List<AssignedCapability>();
        foreach (var role in read.Facts.Roles.Where(r => r.Active).OrderBy(r => r.Id, StringComparer.Ordinal))
        {
            if (!read.Defs.Roles.TryGetValue(role.Def, out var definition)) continue;
            for (var index = 0; index < definition.Requirements.Count; index++)
            {
                var requirement = definition.Requirements[index];
                if (!requirement.Skills.Any(s => s.Skill is "Shooting" or "Melee")) continue;
                // Keep noncombat OR branches: cooking can satisfy a mixed native requirement.
                foreach (var pawn in role.Pawns.OrderBy(p => p.Value, StringComparer.Ordinal))
                {
                    var id = role.Id + "/" + index + "/" + pawn.Value;
                    result.Add(new(id, ServiceTarget.ForNeed(IdeologyFamily.Roles, role.Id,
                        "assigned_role_min_skill_any/" + index), pawn,
                        EquatableArray.From(requirement.Skills), CapabilityDevelopmentKind.Maintenance));
                }
            }
        }
        return EquatableArray.From(result);
    }

}
