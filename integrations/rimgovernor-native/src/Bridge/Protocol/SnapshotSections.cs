
namespace RimGovernor.Native
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

            internal RimGovernor.Brain.GameFacts.Observations.BundleSnapshot Frame() => new RimGovernor.Brain.GameFacts.Observations.BundleSnapshot
            {
                Failure = new RimGovernor.Brain.GameFacts.Common.Failure { RefusalClass = RimGovernor.Brain.GameFacts.Common.RefusalClass.Unclassified, Code = RimGovernor.Brain.GameFacts.Common.FailureCode.NativeFailure, Detail = "snapshot section " + Message },
            };
        }
    }
}
