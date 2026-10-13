namespace RimGovernor.Brain.Policy;

/// <summary>The Medical department's stockpile: the hospital's medicine store, a 2x2 beside the medical beds, declared once and sized once.</summary>
internal sealed class MedicalOwner : IStoreOwner
{
    public Department Department => Department.Medical;

    public RoomDemand RoomDemand(StoreView v) => PolicyFunctions.DeclaredDemand(v, Stores(v));

    public IReadOnlyList<Store> Stores(StoreView v)
    {
        var (medicine, ok) = v.MedicineStore();
        return ok ? [medicine!] : [];
    }
}
