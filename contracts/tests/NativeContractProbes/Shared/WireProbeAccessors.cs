using Common = RimGovernor.Contracts.Wire.Common;
using Authority = RimGovernor.Contracts.Wire.Authority;

// #3100: the wire replies are records whose oneof lives on a nested abstract `Outcome`/`State`/`Operation`
// record. The probes read replies as flat properties (`reply.Failure`, `status.Active`);
// these extension properties keep that reading shape, each returning null when another case is set, exactly
// like the protobuf oneof accessors they replace.
internal static class WireProbeAccessors
{
    extension(Authority.ControlRequest r)
    {
        public Authority.SetMode SetMode => (r.Operation as Authority.ControlRequestOperation.SetModeCase)?.Value;
        public Authority.Revoke Revoke => (r.Operation as Authority.ControlRequestOperation.RevokeCase)?.Value;
        public Authority.FocusMap FocusMap => r.Operation?.FocusMap;
    }
    extension(Authority.StatusReply r)
    {
        public Authority.Status Status => r.Outcome?.Status;
        public Common.Failure Failure => r.Outcome?.Failure;
    }
    extension(Authority.ControlReply r)
    {
        public Authority.Granted Granted => r.Outcome?.Granted;
        public Authority.Revoked Revoked => r.Outcome?.Revoked;
        public Authority.FocusedMap FocusedMap => r.Outcome?.FocusedMap;
        public Common.Failure Failure => r.Outcome?.Failure;
    }
    extension(Authority.Status s)
    {
        public Common.Unavailable Unavailable => s.State?.Unavailable;
        public Authority.InactiveAuthority Inactive => s.State?.Inactive;
        public Authority.ActiveAuthority Active => s.State?.Active;
    }
}
