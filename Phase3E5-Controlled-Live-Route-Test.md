# LibertyRoute Phase 3E5 Controlled Live Route Test

Status: procedure design only. No live route mutation is authorized or executed by this document.

## Current Read-Only Preflight

Observed with read-only Windows commands on 2026-08-26:

- Active default route: `0.0.0.0/0` via `10.16.0.1` on interface index `20` (`Wi-Fi 2`).
- Active local subnet: `10.16.0.0/21` on interface index `20`.
- TEST-NET exact prefixes `192.0.2.0/24`, `198.51.100.0/24`, and `203.0.113.0/24` were absent from `Get-NetRoute`.
- Local address inspection identified interface `20` as `10.16.7.113/21`.
- `Get-NetAdapter` was unavailable in the current shell, and `Get-NetIPConfiguration` returned no usable formatted records.

## Candidate Decision

**NO SAFE LIVE TEST CANDIDATE**

The TEST-NET prefixes are absent, but interface `20` is the only interface with the active default route and current local Internet path. The controlled validator therefore rejects it as an unsafe interface. Adapter state and a safe isolated interface cannot be established from this preflight, so no next hop or interface is selected.

No route should be added until a later read-only preflight establishes a known, non-default, non-management interface and repeats all absence and safety checks.

## Validator

`ControlledLiveRouteTestValidator` is an internal pure gate. It requires:

- a valid internal controlled-test capability
- one prepared, authorized baseline route request
- matching active session
- no denied, manual-review, unsupported, or unverifiable entries
- an absent destination
- one of the three reserved TEST-NET prefixes
- a non-default prefix
- a known interface that owns no active default route and is not marked unsafe
- a next hop that is not a gateway or DNS server
- no overlap with a current local subnet

It rejects default routes, existing routes, local subnets, gateway/DNS targets, unknown or unsafe interfaces, wrong sessions, invalid capabilities, and multi-operation batches.

## Future Read-Only Report

A future preflight report must print:

- active adapters and interface indexes
- active default routes
- gateways and DNS servers
- selected TEST-NET prefix and exact-prefix absence in both Windows and LibertyRoute snapshots
- proposed interface, next hop, and metric
- safety rationale
- the exact authorized route identity that the provider would later remove

The rollback path is the same authorized route command passed to the provider's delete operation after successful creation and verification. No manual `netsh`, `route`, PowerShell mutation, or equivalent command is part of this procedure.

## Future Controlled Sequence

A. Capture the baseline snapshot.
B. Verify the exact test prefix is absent from `Get-NetRoute` and the LibertyRoute snapshot.
C. Create one test transaction and session.
D. Persist ownership evidence for that transaction.
E. Prepare exactly one authorized request.
F. Pass the Phase 3E4 capability and live-test eligibility gates.
G. Invoke the add operation once.
H. Verify the exact route appears by read-only observation.
I. Invoke the delete operation once using the owned route identity.
J. Verify the exact route disappears.
K. Compare the final snapshot with the baseline.
L. Retain the journal and evidence on any mismatch.

The sequence must stop before step G unless a separate deliberate, test-only confirmation point is supplied outside normal startup, service, desktop, recovery, and connection flows.

## Abort Conditions

Abort before mutation if any of these changes or remains uncertain:

- default or Internet route changes
- active interface changes
- a VPN or new management path appears
- gateway or DNS changes
- the selected TEST-NET prefix appears unexpectedly
- more than one operation is prepared
- the provider query reports a conflict
- authorization, session, capability, or interface validation fails
- adapter inventory or route absence cannot be established

## Emergency Recovery Design

If a future controlled test is interrupted:

1. Identify the exact TEST-NET destination, prefix, next hop, interface index, metric, transaction ID, and ownership evidence from the journal.
2. Confirm the route is the LibertyRoute-owned test route and is not a default, local, gateway, DNS, or management route.
3. Remove only that exact owned route through the controlled provider delete path.
4. Verify all default routes and the active local subnet remain unchanged.
5. Capture a final read-only snapshot and retain the journal on any mismatch or uncertainty.

Do not use manual route mutation commands as emergency recovery for this phase. Escalate an uncertain state for review and preserve evidence.
