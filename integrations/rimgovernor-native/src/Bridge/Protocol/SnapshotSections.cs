#nullable enable
using Common = RimGovernor.Contracts.Wire.Common;
using Obs = RimGovernor.Contracts.Wire.Observations;

namespace HomeBridge.BridgeTools
{
    internal static partial class SnapshotFrames
    {
        /// A frame section's read threw. The stream publishes a frame
        /// carrying only the Failure, never the frame with the section omitted.
        internal sealed class SnapshotSectionException : System.Exception
        {
            private SnapshotSectionException(string section, System.Exception inner) : base(section + ": " + inner.GetType().Name + ": " + inner.Message, inner) { }

            internal static T Read<T>(string section, System.Func<T> read)
            {
                try { return read(); }
                catch (System.Exception ex)
                {
                    ObservationWork.Failed(section, ex);
                    throw new SnapshotSectionException(section, ex);
                }
            }

            internal Obs.BundleSnapshot Frame() => new Obs.BundleSnapshot
            {
                Failure = new Common.Failure { RefusalClass = RimGovernor.Contracts.Wire.Common.RefusalClass.Unclassified, Code = Common.FailureCode.NativeFailure, Detail = "snapshot section " + Message },
            };
        }
    }
}
