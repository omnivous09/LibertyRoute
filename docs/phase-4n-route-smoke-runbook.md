# Phase 4N route observation and future smoke-test runbook

## Authorization boundary

Phase 4N-0 is read-only observation only. The `LibertyRoute.RouteObservation` tool may enumerate and canonicalize route and interface evidence and may write an explicitly requested evidence file. It has no authority or dependency capable of changing Windows networking. Its JSON is operator evidence, not D1 durable authority.

Phase 4N-1 is **BLOCKED**. Nothing in this runbook authorizes adding, deleting, or changing routes, DNS, proxy, firewall, adapters, tunnels, networking registry state, or services.

## 4N-0 observation

Run unelevated from the repository root:

```text
dotnet run --project tools/LibertyRoute.RouteObservation --
```

The conservative default reports `NO SAFE 4N ROUTE TARGET ON THIS MACHINE` because no interface has been explicitly proved dedicated and outside management paths. An operator may identify a pre-existing dedicated interface by numeric index and separately declare every known management interface:

```text
dotnet run --project tools/LibertyRoute.RouteObservation -- --dedicated-interface <index> --management-interface <index>
```

Use `--output <path>` only when a bounded JSON evidence file is required. Output is classified `REDUCED LIBERTYROUTE ROUTE STATE`: the strict tool-local read-only observer supplies canonical destination/prefix, next hop, interface index, route metric, and address family. It does not provide a complete native route baseline, native protocol/origin/lifetimes, or interface metric. Fuller native evidence is a 4N-1P prerequisite.

Observation completeness is an explicit safety gate. Failure to enumerate either IPv4 or IPv6 routes, failure to read any interface or address-family properties, a missing interface identity/index, malformed evidence, or any route/interface/value truncation makes the observation incomplete. An incomplete observation can never approve a target; missing evidence is never interpreted as absence.

The operator must identify remote-management endpoints and prove the selected interface is not their path. The tool does not make external traffic and does not pretend environment-specific management endpoints are known.

Read-only comparison may use `Get-NetRoute -AddressFamily IPv4`, `Get-NetIPInterface`, `Get-NetIPConfiguration`, and `route print -4`. Do not use mutating variants. Record disagreements; do not repair the machine during 4N-0.

## Why 4N-1 remains blocked

Three planning blockers remain:

1. There is no end-to-end initial mutation path with complete D1 `ExecutionStarted` protection.
2. Exact owned deletion is not supported through controlled recovery.
3. Arbitrary pre-existing route snapshots cannot be restored exactly.

High-risk prerequisites also remain: external-change TOCTOU between observation and action, and lack of exact native post-call read-back. Read-only `GetIpForwardTable2` success does not prove mutation ABI correctness.

## Conceptual future recovery (not authorized during 4N-0)

For a future, separately authorized initially absent TEST-NET `/32`, recovery would inspect the exact route, compare the complete owned tuple, remove it only when that exact tuple matches, then verify absence and management-path health. These are design concepts, not executable instructions or authority. No broad networking reset is acceptable.
