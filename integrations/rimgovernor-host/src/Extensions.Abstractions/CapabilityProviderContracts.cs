using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using RimGovernor.Host.Contracts;

namespace RimGovernor.Host.Extensions.Abstractions;

public delegate Task<OperationEnvelope> NativeCapabilityHandler(CapabilityInvocation invocation, CancellationToken cancellationToken);

public sealed class NativeCapabilityRegistration
{
    public NativeCapabilityRegistration(CapabilityDescriptor descriptor, NativeCapabilityHandler handler = null)
    {
        Descriptor = descriptor;
        Handler = handler;
    }

    public CapabilityDescriptor Descriptor { get; }

    public NativeCapabilityHandler Handler { get; }

    public bool HasHandler => Handler != null;
}

public interface INativeCapabilityProvider
{
    string ProviderId { get; }

    IEnumerable<NativeCapabilityRegistration> GetCapabilities();
}
