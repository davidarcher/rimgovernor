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
    Task<(ConstructionDeficitRead Read, BridgeResult Result)> IRoundsTradeSource.ReadConstructionDeficitsAsync(C.Identity identity, CancellationToken ct) =>
        ReadConstructionDeficitsAsync(identity, ct);

    Task<(TradersRead Read, BridgeResult Result)> IRoundsTradeSource.ListTradersAsync(C.Identity identity, CancellationToken ct) =>
        ListTradersAsync(identity, ct);

    Task<(TradeSessionRead Read, BridgeResult Result)> IRoundsTradeSource.ReadTradeSessionAsync(C.Identity identity, CancellationToken ct) =>
        ReadTradeSessionAsync(identity, ct);

    Task<(TradeSheetRead Read, BridgeResult Result)> IRoundsTradeSource.ReadTradeSheetAsync(C.Identity identity, CancellationToken ct) =>
        ReadTradeSheetAsync(identity, ct);

    Task<(TradeSessionRead Read, BridgeResult Result)> ITradeNative.ReadTradeSessionAsync(C.Identity identity, CancellationToken ct) =>
        ReadTradeSessionAsync(identity, ct);

    Task<(TradersRead Read, BridgeResult Result)> ICommsTradeRequestNative.ListTradersAsync(C.Identity identity, CancellationToken ct) =>
        ListTradersAsync(identity, ct);

    Task<(ObservationsPb.TradeAcquisition Acquisition, BridgeResult Result)> ICommsTradeRequestNative.ReadTradeAcquisitionAsync(C.Identity identity, OperationsPb.FormCaravanIntent? intent, CancellationToken ct) =>
        ReadTradeAcquisitionAsync(identity, intent, ct);

    Task<(ObservationsPb.TradeAcquisition Acquisition, BridgeResult Result)> ITradeAcquisitionSource.ReadTradeAcquisitionAsync(C.Identity identity, OperationsPb.FormCaravanIntent? intent, CancellationToken ct) =>
        ReadTradeAcquisitionAsync(identity, intent, ct);

    Task<(ObservationsPb.TradeAcquisition Acquisition, BridgeResult Result)> IExportSource.ReadTradeAcquisitionAsync(C.Identity identity, OperationsPb.FormCaravanIntent? intent, CancellationToken ct) =>
        ReadTradeAcquisitionAsync(identity, intent, ct);

    Task<(ObservationsPb.TradeAcquisition Acquisition, BridgeResult Result)> ITradeMissionPackSource.ReadTradeAcquisitionAsync(C.Identity identity, OperationsPb.FormCaravanIntent? intent, CancellationToken ct) =>
        ReadTradeAcquisitionAsync(identity, intent, ct);

    Task<(WorldProgressionRead Read, BridgeResult Result)> IExportSource.ReadWorldProgressionAsync(C.Identity identity, bool full, CancellationToken ct) =>
        ReadWorldProgressionAsync(identity, full, ct);

    Task<(WorldProgressionRead Read, BridgeResult Result)> ITradeMissionNative.ReadWorldProgressionAsync(C.Identity identity, bool full, CancellationToken ct) =>
        ReadWorldProgressionAsync(identity, full, ct);

    Task<(WorldProgressionRead Read, BridgeResult Result)> ISettlementOptionsSource.ReadWorldProgressionAsync(C.Identity identity, bool full, CancellationToken ct) =>
        ReadWorldProgressionAsync(identity, full, ct);

    Task<(WorldRead Read, BridgeResult Result)> IExportSource.ReadWorldAsync(C.Identity identity, int tile, double radius, CancellationToken ct) =>
        ReadWorldAsync(identity, tile, radius, ct);

    Task<(WorldRead Read, BridgeResult Result)> ISettlementOptionsSource.ReadWorldAsync(C.Identity identity, int home, double radius, CancellationToken ct) =>
        ReadWorldAsync(identity, home, radius, ct);

    Task<(IReadOnlyDictionary<string, long> Cargo, BridgeResult Result)> ITradeMissionNative.ReadPawnCargoAsync(C.Identity identity, IReadOnlyList<string> pawns, CancellationToken ct) =>
        ReadPawnCargoAsync(identity, pawns, ct);

    Task<(LifecyclePb.SaveReply Reply, BridgeResult Result)> ILifecycleSaver.SaveAsync(LifecyclePb.SaveRequest request, CancellationToken ct) =>
        SaveAsync(request, ct);
}
