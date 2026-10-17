using RimGovernor.Brain.Bridge.Contracts;
using RimGovernor.Brain.BuildingRuntime;
using RimGovernor.Brain.Observation;
using C = RimGovernor.Contracts.Wire.Common;
using ObservationsPb = RimGovernor.Contracts.Wire.Observations;
using OperationsPb = RimGovernor.Contracts.Wire.Operations;
using LifecyclePb = RimGovernor.Contracts.Wire.Lifecycle;

namespace RimGovernor.Brain.Host;

// The building runtime's trade, world and lifecycle sources: see FacadeNativeClient.Interfaces.cs.
internal sealed partial class FacadeNativeClient
{
    Task<ConstructionDeficitRead> IRoundsTradeSource.ReadConstructionDeficitsAsync(C.Identity identity, CancellationToken ct) =>
        ReadConstructionDeficitsAsync(identity, ct);

    Task<TradersRead> IRoundsTradeSource.ListTradersAsync(C.Identity identity, CancellationToken ct) =>
        ListTradersAsync(identity, ct);

    Task<TradeSessionRead> IRoundsTradeSource.ReadTradeSessionAsync(C.Identity identity, CancellationToken ct) =>
        ReadTradeSessionAsync(identity, ct);

    Task<TradeSheetRead> IRoundsTradeSource.ReadTradeSheetAsync(C.Identity identity, CancellationToken ct) =>
        ReadTradeSheetAsync(identity, ct);

    Task<TradeSessionRead> ITradeNative.ReadTradeSessionAsync(C.Identity identity, CancellationToken ct) =>
        ReadTradeSessionAsync(identity, ct);

    Task<TradersRead> ICommsTradeRequestNative.ListTradersAsync(C.Identity identity, CancellationToken ct) =>
        ListTradersAsync(identity, ct);

    Task<ObservationsPb.TradeAcquisition> ICommsTradeRequestNative.ReadTradeAcquisitionAsync(C.Identity identity, OperationsPb.FormCaravanIntent? intent, CancellationToken ct) =>
        ReadTradeAcquisitionAsync(identity, intent, ct);

    Task<ObservationsPb.TradeAcquisition> ITradeAcquisitionSource.ReadTradeAcquisitionAsync(C.Identity identity, OperationsPb.FormCaravanIntent? intent, CancellationToken ct) =>
        ReadTradeAcquisitionAsync(identity, intent, ct);

    Task<ObservationsPb.TradeAcquisition> IExportSource.ReadTradeAcquisitionAsync(C.Identity identity, OperationsPb.FormCaravanIntent? intent, CancellationToken ct) =>
        ReadTradeAcquisitionAsync(identity, intent, ct);

    Task<ObservationsPb.TradeAcquisition> ITradeMissionPackSource.ReadTradeAcquisitionAsync(C.Identity identity, OperationsPb.FormCaravanIntent? intent, CancellationToken ct) =>
        ReadTradeAcquisitionAsync(identity, intent, ct);

    Task<WorldProgressionRead> IExportSource.ReadWorldProgressionAsync(C.Identity identity, CancellationToken ct) =>
        ReadWorldProgressionAsync(identity, ct);

    Task<WorldProgressionRead> ITradeMissionNative.ReadWorldProgressionAsync(C.Identity identity, CancellationToken ct) =>
        ReadWorldProgressionAsync(identity, ct);

    Task<WorldProgressionRead> ISettlementOptionsSource.ReadWorldProgressionAsync(C.Identity identity, CancellationToken ct) =>
        ReadWorldProgressionAsync(identity, ct);

    Task<WorldRead> IExportSource.ReadWorldAsync(C.Identity identity, int tile, double radius, CancellationToken ct) =>
        ReadWorldAsync(identity, tile, radius, ct);

    Task<WorldRead> ISettlementOptionsSource.ReadWorldAsync(C.Identity identity, int home, double radius, CancellationToken ct) =>
        ReadWorldAsync(identity, home, radius, ct);

    Task<IReadOnlyDictionary<string, long>> ITradeMissionNative.ReadPawnCargoAsync(C.Identity identity, IReadOnlyList<string> pawns, CancellationToken ct) =>
        ReadPawnCargoAsync(identity, pawns, ct);

    Task<LifecyclePb.SaveReply> ILifecycleSaver.SaveAsync(LifecyclePb.SaveRequest request, CancellationToken ct) =>
        SaveAsync(request, ct);
}
