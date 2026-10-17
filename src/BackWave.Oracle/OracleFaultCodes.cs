namespace BackWave.Oracle;

// The single source of truth for the transient Oracle connectivity/timeout error codes. A cold-booting
// fleet can hit these listener/handshake-storm and connection-lost conditions without having caused them,
// and a pump that loses its database in a store outage (network loss, a stop, a restart) sees the same
// codes until the database answers again. So the migrator rides them out and the store tags them
// transient: the worker group degrades and retries on the next poll instead of halting. Both reference
// this list so the two stay in lockstep.
//   12170 TNS connect timeout            12541 no listener
//   12514 listener does not know service 12518 listener could not hand off
//   12537 connection closed              12570 packet reader failure
//   3113  end-of-file on channel         3114  not connected
//   28    session killed                 1033  database initializing/shutting down
//   1089  shutdown in progress           1109  database not open (a restart opens it after the listener)
//   12154 could not resolve connect id (transient during cold DNS/TNS)
//   12545 target host does not exist (the host name stops resolving in a network outage)
// A mistyped host name also raises 12545 (like 12154), so it leaves the group degraded and never halts it.
// ODP.NET managed raises its own codes when a connect fails:
//   50201 failed to connect to server (connection refused while the database is down, or a service name
//         the listener does not know during a restart; a wrong service name also raises it, so that too
//         leaves the group degraded and never halts it)
//   50000 connection request timed out (the host does not answer)
//   50012 pooled connection request timed out (the pool has no free connection)
internal static class OracleFaultCodes
{
    // True when the ORA or ODP.NET code is one of the transient connectivity/timeout faults above.
    internal static bool IsConnectivityFault(int code) => code is
        12170 or 12541 or 12514 or 12518 or 12537 or 12570 or 3113 or 3114 or 28 or 1033 or 12154
        or 1089 or 1109 or 12545 or 50201 or 50000 or 50012;
}
