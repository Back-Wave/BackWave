namespace BackWave.Oracle;

// The single source of truth for the transient Oracle connectivity/timeout error codes. A cold-booting
// fleet can hit these listener/handshake-storm and connection-lost conditions without having caused them,
// so the migrator rides them out and the store tags them transient. Both reference this list so the two
// stay in lockstep.
//   12170 TNS connect timeout            12541 no listener
//   12514 listener does not know service 12518 listener could not hand off
//   12537 connection closed              12570 packet reader failure
//   3113  end-of-file on channel         3114  not connected
//   28    session killed                 1033  database initializing/shutting down
//   12154 could not resolve connect id (transient during cold DNS/TNS)
internal static class OracleFaultCodes
{
    // True when the ORA code is one of the transient connectivity/timeout faults above.
    internal static bool IsConnectivityFault(int code) => code is
        12170 or 12541 or 12514 or 12518 or 12537 or 12570 or 3113 or 3114 or 28 or 1033 or 12154;
}
