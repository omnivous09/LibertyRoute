# Phase 4N-0a exact native route read-back design

## Boundary

Phase 4N-0a is read-only. It adds an immutable semantic representation of a Windows
`MIB_IPFORWARD_ROW2`, bounded complete enumeration, and pure cardinality comparison.
It has no route mutation API or mutation-capable dependency. Its only native imports remain
`GetIpForwardTable2` and `FreeMibTable`.

This phase does not establish mutation ABI correctness, authorize elevation, or authorize any
networking change. Phase 4N-1 remains **BLOCKED**.

## Semantic identity and expected profile

The durable semantic key is address family, canonical destination address and prefix length,
canonical next hop (including the relevant IPv6 scope identifier), and interface LUID. The LUID
is the durable interface component. Interface index is retained only as immediate read-back
corroboration and index equality alone never proves interface identity.

The separately supplied expected normalized profile compares interface index, site prefix
length, lifetime expectations, route metric offset, protocol, and the Loopback,
AutoconfigureAddress, Publish, and Immortal flags. Protocol is only one profile constraint and
never establishes ownership by itself. Metric is the route metric offset, not the effective sum
of interface and route metrics.

Age and Origin are observational metadata and are excluded from caller-controlled equality.
Origin never establishes ownership. Infinite lifetimes must remain infinite; finite lifetimes may
count down and therefore match only when the observed value does not exceed the initial value.
Padding and reserved bytes are not semantic state. Raw native memory is never persisted or used
as semantic equality.

Expected profiles are rejected unless preferred lifetime is less than or equal to valid lifetime.
Both zero and both infinite are valid; finite valid plus infinite preferred is invalid; infinite
valid plus finite preferred is deliberate and valid. A verification result is ordinary immutable
descriptive data containing status, counts, and a reason. It is not transferable authority and a
separately supplied result must never be treated as proof that fresh read-back occurred.

## Native validation and ABI

Only AF_INET and AF_INET6 are accepted. IPv4 prefixes are limited to 0 through 32 and IPv6
prefixes to 0 through 128. Destination and next-hop socket families must match the requested
family. IPv4 next-hop scope must be zero. IPv6 scope must be in the unsigned 32-bit domain from
zero through `uint.MaxValue`; this is checked for both expected and observed keys. IPv6 address
bytes and next-hop scope are preserved. Unsupported families, invalid
prefixes, invalid lifetimes, unavailable LUID/index, malformed socket storage, and noncanonical
native BOOLEAN values fail closed.

The managed ABI uses a 64-bit NET_LUID, 28-byte SOCKADDR_INET, 32-byte IP_ADDRESS_PREFIX,
104-byte MIB_IPFORWARD_ROW2, an 8-byte ABI-aligned table-to-first-row offset, one-byte BOOLEAN
fields, and fixed 32-bit native integer/enum storage. Tests assert sizes, important offsets, row
stride, and independently specified raw-byte IPv4/IPv6 row fixtures decoded through the same
pointer-marshaling path as production. Raw native bytes remain test input, never durable identity.
Pointer arithmetic is checked. Every successfully returned native
table is released in a `finally` block.

## Complete bounded enumeration

IPv4 and IPv6 are independently enumerated. Complete family enumeration is the authoritative
cardinality source. The combined limit is 4,096 native rows. The native count is checked before
managed row materialization; overflow marks the observation truncated and incomplete. A family
failure, null table, malformed row, or truncation makes verification impossible.

## Cardinality and read-back descriptions

Verified presence requires exactly one full semantic-key row satisfying the expected profile and
exactly one row under the conservative destination-prefix reduced identity. Duplicate full-key
rows and any reduced-identity collision fail closed. The verifier never selects a first match.

Verified absence requires zero full-key rows and zero reduced-identity conflicts. A conflicting row
prevents an unambiguous absence claim.

4N-0a deliberately has no operation-decision API. In particular, it has no composition of native
success plus caller-supplied verification or status into authoritative success.

## Future trust boundary

Unrestricted full-trust .NET reflection can access private process state. Private and internal
members provide encapsulation, not a same-process security boundary, so 4N-0a does not attempt to
create an in-process unforgeable token or capability. Security must come from control-flow
ownership at the future mutation boundary, not secrecy of a managed object.

A future mutation-capable component must not accept an `ExactRouteVerifier.Verification`,
`ExactRouteVerificationStatus`, verified boolean, serialized verification result, or proof token
from an external caller as authority to publish Applied or Reverted state. The mutation-capable
control path must itself own the sequence: native operation, fresh noncancellable read-back, direct
exact-verifier invocation, and lifecycle decision. That mutation boundary is not implemented in
4N-0a.

## TOCTOU and reproducibility limits

The reader and verifier do not make the Windows route table transactional and never assume
exclusive ownership. They cannot prevent external changes before a read, after a read, or between
a hypothetical mutation and its read-back. They detect only inconsistencies visible in the fresh
observation.

The semantic model does not make arbitrary pre-existing routes exactly reproducible. Age and
Origin are stack-controlled, finite lifetimes change with time, and other values may be normalized
by Windows. Future durable ownership work must bind a versioned semantic key and accepted profile
before any live experiment can be considered.

4N-0a establishes only the read ABI and exact comparison semantics. It does not establish a
mutation ABI or durable mutation orchestration, and 4N-1 remains **BLOCKED**.
