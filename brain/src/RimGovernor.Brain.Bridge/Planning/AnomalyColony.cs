using RimGovernor.Brain.Policy;
using RimGovernor.Brain.Facts;
using RimGovernor.Brain.Bridge.Contracts;
using O = RimGovernor.Contracts.Wire.Observations;
namespace RimGovernor.Brain.Bridge;

public static class AnomalyColonyReads
{
    /// <summary>
    /// The colony's Anomaly read: Inactive without Anomaly; Active with GameComponent_Anomaly's level state and the
    /// monolith's own read (null while it is not spawned). A monolith read that disagrees with MonolithSpawned breaks
    /// the contract.
    /// </summary>
    public static AnomalyState AnomalyOf(O.AnomalySection section)
    {
        if (section is not O.AnomalySection.Active { Facts: var a })
        {
            return new AnomalyState.Inactive();
        }
        var i = a.Incidents;
        if (i.MonolithSpawned != (a.Monolith is not null))
        {
            throw new BridgeContractException($"monolith_spawned is {i.MonolithSpawned} but the monolith block is {(a.Monolith is null ? "absent" : "present")}");
        }
        return new AnomalyState.Active(a);
    }
}
