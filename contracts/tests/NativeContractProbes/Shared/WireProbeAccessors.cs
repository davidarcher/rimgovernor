
// #3100: the wire replies are records whose oneof lives on a nested abstract `Outcome`/`State`/`Operation`
// record. The probes read replies as flat properties (`reply.Failure`, `status.Active`);
// these extension properties keep that reading shape, each returning null when another case is set, exactly
// like the protobuf oneof accessors they replace.
internal static class WireProbeAccessors
{
    extension(RimGovernor.Brain.GameOrders.Authority.ControlRequest r)
    {
        public RimGovernor.Brain.GameOrders.Authority.SetMode? SetMode => (r.Operation as RimGovernor.Brain.GameOrders.Authority.ControlRequestOperation.SetModeCase)?.Value;
        public RimGovernor.Brain.GameOrders.Authority.Revoke? Revoke => (r.Operation as RimGovernor.Brain.GameOrders.Authority.ControlRequestOperation.RevokeCase)?.Value;
    }
    extension(RimGovernor.Brain.GameFacts.Authority.ControlReply r)
    {
        public RimGovernor.Brain.GameFacts.Authority.Granted? Granted => r.Outcome?.Granted;
        public RimGovernor.Brain.GameFacts.Authority.Revoked? Revoked => r.Outcome?.Revoked;
        public RimGovernor.Brain.GameFacts.Common.Failure? Failure => r.Outcome?.Failure;
    }
    extension(RimGovernor.Brain.GameFacts.Authority.Status s)
    {
        public RimGovernor.Brain.GameFacts.Common.Unavailable? Unavailable => s.State?.Unavailable;
        public RimGovernor.Brain.GameFacts.Authority.InactiveAuthority? Inactive => s.State?.Inactive;
        public RimGovernor.Brain.GameFacts.Authority.ActiveAuthority? Active => s.State?.Active;
    }
}
