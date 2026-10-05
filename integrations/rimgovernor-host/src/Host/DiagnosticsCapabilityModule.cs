using System;
using RimGovernor.Host.Core;

namespace RimGovernor.Host;

internal sealed class DiagnosticsCapabilityModule
{
    private readonly OperationJournal _journal;
    private readonly LogJournal _logJournal;

    public DiagnosticsCapabilityModule(OperationJournal journal, LogJournal logJournal)
    {
        _journal = journal ?? throw new ArgumentNullException(nameof(journal));
        _logJournal = logJournal ?? throw new ArgumentNullException(nameof(logJournal));
    }

    public object GetBridgeStatus()
    {
        return RimWorldWaits.GetBridgeStatus(_journal, _logJournal);
    }

    public object ListLogs(int limit = 50, string minimumLevel = "info", long afterSequence = 0, string operationId = null, string rootOperationId = null, string capabilityId = null)
    {
        return new
        {
            logs = _logJournal.GetEntries(limit, minimumLevel, afterSequence, operationId, rootOperationId, capabilityId)
        };
    }
}
