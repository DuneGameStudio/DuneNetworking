# DuneNetworking — Design Decision Log

The record of the Phase-3 architectural review: the issues we worked through, each with its code
evidence, the decision we made, and how it was resolved. This is the **change history** — the
"what we found, what we did, and why" — kept separate from the architecture description
([`Architecture.md`](Architecture.md)) and the functional "how it works" reference
([`Project-Spec.md`](Project-Spec.md)).

**Companion files:** `Architecture.md` (structure + design rationale) · `Project-Spec.md` (how the
library works) · `api.md` (signatures).

**Evidence tags** used below:
- **CONFIRMED** — verified directly against the current source (file:line cited).
- **PLAUSIBLE** — inferred from structure; likely but not yet traced end-to-end.
- **UNVERIFIED** — needs a runtime/benchmark/profile to confirm.

**Status: all eight items are resolved** — P4 and P3 by code change; P2, P1, P6, P5, P7, and P8 by
documentation / design rationale. Nothing under `src/` changed without explicit approval. Entries
are listed numerically (P1–P8); they were *addressed* in the fix-loop order
P4 → P2 → P1 → P3 → P6 → P5 → P7 → P8.

***

## How the review ran (fix-loop protocol)

For each problem, in priority order (**P4 → P2 → P1 → P3 → P6 → P5 → P7 → P8**):
1. Present the finding + a concrete proposed change with its risk.
2. Approve / adjust / reject.
3. On approval, make the `src/` change, run the full suite (`DOTNET_ROLL_FORWARD=LatestMajor dotnet test`), and confirm green.
4. Update the docs (mark the item resolved, adjust the model if the design changed).
5. Repeat until the map is empty or deferred items are explicitly parked.

Nothing under `src/` changed without explicit go-ahead. Docs, tests, and this log updated freely.

***

## P1 — `IPacket : ISegmentManager` crosses a layer boundary  MEDIUM
- **Layer:** Presentation ↔ Transport
- **Status:** CONFIRMED
- **Evidence:** `IPacket` (DunePresentation) extends `ISegmentManager` (DuneTransport.BufferManager.Interface) ([IPacket.cs:20](../../src/DunePresentation/src/Packet/Interfaces/IPacket.cs)). `ISegmentManager`'s default methods take an `ITransport` and call `transport.TryReserveSendPacket` ([ISegmentManager.cs:119-133](../../src/DuneTransport/src/BufferManager/Interface/ISegmentManager.cs)).
- **Impact:** A presentation-layer type *is-a* a transport-layer concept, and the segment-lifecycle contract (whose default methods reach into `ITransport`) lives in the lower layer but is implemented by the higher layer's types. It's legal (Presentation→Transport) and the `Segment`/pool genuinely belong to Transport, but it (a) couples every app packet to the transport's `Segment` type and `Serialize(ITransport)` shape, and (b) means "serialize a packet" is tied to a specific live transport (no serialization without one). Any change to the transport's reservation model ripples into the packet contract.
- **Recommended action:** Two options — (a) **Keep and document** the deliberate dependency (lowest effort; the segment type really is a transport concept), or (b) **Move the lifecycle out of the packet**: make `IPacket` just `PacketId` + field R/W, and let `Peer` own reserve→header→encrypt→release. Option (b) is the cleaner long-term design (packets = pure data; `Peer` = the pipeline) but is a larger refactor.
- **Resolution (2026-11-02):** **Option (a) — keep and document.** Deliberate: the dependency is legal (Presentation→Transport, a downward reference) and load-bearing — `SerializeAndEncrypt` sets `packet.PacketSize`/`packet.segment`, and the app reads `packet.PacketSize` to size every `Send` (DuneClient, RequestCorrelator, SerializationStage, DuneManagedPeer, and the demo). The cleanup (option b′: drop the `ISegmentManager` inheritance, keep the app-facing members, move the segment lifecycle into `Peer`) is scoped but **deferred** — revisit only if standalone/offline packet serialization (isolated unit tests) becomes a real need. Accepted trade-off: app packet types carry transport machinery (`Segment`/`PacketSize`); a packet can only be serialized via a live transport.

## P2 — Teardown (close/disconnect/dispose) protocol is highly complex  HIGH (complexity)
- **Layer:** Transport + Session (+ connectors)
- **Status:** CONFIRMED (behavior); correctness is well-covered by tests, but the *complexity* is the concern.
- **Evidence:** Three-state (`0/1/2`) `Interlocked` + `Volatile` protocol with `SpinWait` holds and mutual-confirmation re-reads across `Transport` (`_sendOpState`, `_receiveOpState`, `CloseSocket` [Transport.cs:660-666](../../src/DuneTransport/src/Transport/Transport.cs)), `Connection` (`disconnectingState`, `ReportDisconnected` [Connection.cs:188-198](../../src/DuneSession/src/SocketConnectors/Connection.cs)), `ClientConnector` (`connectingState`), `ServerConnector` (`acceptingState` + `startingState`).
- **Impact:** It is correct (the race/close-protocol tests pin it) but it carries the highest cognitive load and the highest regression risk in the library. Any new I/O operation or teardown change must correctly preserve "a close never outruns a claimed-not-issued starter; a close runs over in-flight ops." Future contributors are likely to break it.
- **Recommended action:** (a) Write a short **invariants note** (a "teardown protocol" section, possibly a state diagram) so the rule is explicit and reviewable; (b) evaluate whether the two non-zero states can be merged or whether a simpler protocol (e.g. an `AsyncLock`/`SemaphoreSlim`-based claim) would preserve safety with less subtlety — only if the simpler form is clearly safer; (c) keep/expand the race tests as the regression net.
- **Core change:** Possibly (only if simplifying). **Priority:** Document now; simplify only with confidence.
- **Resolution (Tier 1, 2026-11-01):** Invariants documented in the teardown-protocol section below; pinned by new connection-level race tests in `SessionTests` (disconnect×dispose ×100, in-flight receive, double-report guard). Open: (b) evaluate a simpler claim primitive (Tier 2/3) — deferred, needs separate approval.

## P3 — No named "success" value for `PacketError`; success == `Error == 0` (enum default)  LOW
- **Layer:** Presentation
- **Status:** CONFIRMED
- **Evidence:** `PacketError` has no `None`/`Success` member; success is returned as `(seg, default)` / `((packet, handler), default)` ([PacketError.cs](../../src/DunePresentation/src/Packet/PacketError.cs), [Peer.cs:87](../../src/DunePresentation/src/Peer/Peer.cs), [Peer.cs:150](../../src/DunePresentation/src/Packet/Peer.cs)).
- **Impact:** Callers must know that `0`/`default` means success — a magic value. Minor clarity/robustness cost; easy to misuse.
- **Recommended action:** Add `None = 0` (or `Success = 0`) to `PacketError` and return it explicitly. Trivial, low-risk.
- **Core change:** Yes (one-line enum + return sites). **Priority:** Low, cheap win.
- **Resolution (2026-11-03):** **Fixed.** Added `PacketError.None = 0` and made the two `Peer` success returns use `PacketError.None` explicitly; `PoolExhausted` and the other codes shifted up by one. The shift is transparent — every usage goes through a named constant or `default(…)` (still `None`), and the app detects failure by the null segment, not by `error == PoolExhausted`. Verified across all three solutions: core 191, app-infrastructure 284, demo E2E 22 — **497/497 green**. The `default(…)` mock setups needed no change.

## P4 — Segment pool size (and thus max packet size) is hardcoded and not configurable  HIGH
- **Layer:** Transport
- **Status:** CONFIRMED
- **Evidence:** `Transport(Socket)` does `sendBuffer = new SegmentedBuffer(); receiveBuffer = new SegmentedBuffer();` ([Transport.cs:145-149](../../src/DuneTransport/src/Transport/Transport.cs)), and `SegmentedBuffer()` defaults to `8192 / 32 = 256`-byte segments ([SegmentedBuffer.cs:49](../../src/DuneTransport/src/BufferManager/SegmentedBuffer.cs)). `Connection(Socket)` likewise calls `new Transport(socket)` with no pool parameter ([Connection.cs:88](../../src/DuneSession/src/SocketConnectors/Connection.cs)).
- **Impact:** The largest packet is bounded by one segment: ~254 usable bytes on send (the send segment is pre-sliced past the 2-byte length header). The receive side accepts up to `SegmentSize` bytes (the pipeline rejects `payloadLength > SegmentSize`, [Transport.cs:419](../../src/DuneTransport/src/Transport/Transport.cs)), but that 256-byte receive ceiling is only headroom — the effective protocol maximum is 254 bytes, because the sender's own segment caps what it can put on the wire. There is **no public way to enlarge the pool or the segment size**, so a "versatile" backend that the user explicitly wants is capped at tiny packets. Any protocol needing larger messages cannot use the library today.
- **Recommended action:** Add a constructor path that accepts pool configuration — e.g. `Transport(Socket, SegmentedBuffer? sendPool = null, SegmentedBuffer? recvPool = null)` (or a small options type) and thread it through `Connection`/`Peer` factories, or at minimum expose the segment size/count. Then verify max-packet behavior with a test.
- **Core change:** Yes. **Effort:** Small–medium. **Risk:** Low (additive; defaults preserve current behavior).
- **Resolution (2026-11-01):** **Fixed.** Added `TransportOptions { SegmentSize = 256, SegmentCount = 32 }` with a `CreatePool()` factory, threaded through `Transport` → `Connection` → `ClientConnector`/`ServerConnector` → `PeerClient`/`PeerServer` via optional constructor parameters. Default constructors are unchanged (256×32 pool), so existing behavior is preserved and larger pools are now available. Verified green (+1 test).

## P5 — `ServerConnector` can leak one accepted socket on Dispose mid-accept  LOW
- **Layer:** Session
- **Status:** CONFIRMED (documented in-code)
- **Evidence:** The `IsAcceptPending` dispose note + `ProcessAccept`/`CloseOrphanedAccept` orphan path ([ServerConnector.cs:22-30](../../src/DuneSession/src/SocketConnectors/ServerConnector.cs), [ServerConnector.cs:361-367](../../src/DuneSession/src/SocketConnectors/ServerConnector.cs)).
- **Impact:** If `Dispose()` runs after an accept completed but before delivery, that accepted socket stays open until finalization. The window is narrow — `Dispose()` must land between the kernel accepting a connection and the completion callback running — and it opens only if a caller skips the protocol. The app already ships the path: `DuneServer.StopListening` → `PeerServer` → connector, and the demo calls `StopListening`.
- **Recommended action:** Keep the documented protocol. The robustness alternative — making `Dispose` safely drain a completed-but-undelivered accept — was evaluated and declined: it needs a small ownership handoff with `ProcessAccept` (a lock or an atomic "take the orphan" claim) to avoid double-closing a socket already delivered, i.e. the same delicate lock-free territory P2 left alone, for a low-priority edge that is already protocol-mitigated.
- **Resolution (2026-11-03):** **Resolved by documentation.** No `src/` change. The shutdown contract is now a first-class `Dispose protocol (shutdown)` section in `api.md` (mechanism + the `StopListening` → bounded `IsAcceptPending` wait → `Dispose` sequence). The existing N11–N14/F2 test net (accepted-socket leak + `IsAcceptPending` dispose protocol) already pins the behavior.
- **Core change:** No. **Priority:** Low. **Status:** CLOSED.

## P6 — Asymmetric error model (events at transport, tuples at presentation)  LOW/MEDIUM
- **Layer:** Transport + Presentation
- **Status:** CONFIRMED
- **Evidence:** `ITransport` reports success **and** failure via events (`OnPacketReceived`, `OnPacketSent`, `OnPacketSendFailed`, `OnPacketReceiveFailed`); `IPeer` has **no events** and returns result tuples ([ITransport.cs](../../src/DuneTransport/src/Transport/Interface/ITransport.cs), [IPeer.cs](../../src/DunePresentation/src/Peer/Interfaces/IPeer.cs)).
- **Impact:** Defensible split (I/O is inherently async/evented; per-call serialize/deserialize is a pure function), but the app must hold two different error idioms and stitch "I got a packet" (transport event) with "did it deserialize" (tuple). A minor coherence/ergonomics cost.
- **Recommended action:** Document the two-tier model explicitly (done in `api.md` / this doc). Optionally evaluate exposing a higher-level "received packet" convenience on the peer, but do not change the model unless it buys real simplicity.
- **Core change:** No (doc/coherence).
- **Resolution (2026-11-03):** **Resolved by documentation.** The two-tier error model is deliberate and is documented in `api.md` (transport event signatures vs. peer tuple returns) and this entry. Rationale: I/O completion is inherently asynchronous and event-driven — a packet arriving is not the result of a call you made — so the transport tier reports via events; serialize/deserialize are pure synchronous operations, so the peer tier returns value tuples. The app stitches them: a transport `OnPacketReceived` event hands a `Segment` to `Peer.DecryptAndDeserialize`, whose tuple reports whether it parsed. Optional future (deferred): a higher-level "received packet" convenience on the peer, only if it buys real simplicity. No core change.

## P7 — `Serialize(ITransport)` couples packet serialization to a live transport  LOW
- **Layer:** Transport (contract) / Presentation
- **Status:** CONFIRMED
- **Evidence:** `ISegmentManager.Serialize(ITransport transport)` reserves from that transport's pool ([ISegmentManager.cs:119](../../src/DuneTransport/src/BufferManager/Interface/ISegmentManager.cs)).
- **Impact:** You cannot serialize a packet into a buffer without a live transport/pool. In a networking library you are effectively always serializing to send over a specific transport, so "offline" is a rare need; testing is served by driving `OnSerialize()` directly against a mock segment. Overlaps P1.
- **Recommended action:** Document the coupling as deliberate (zero-copy) and fold into the P1 decision. The "fix" — decoupling (standalone-pool reserve + copy/re-reserve, or a parallel serialize-to-buffer API) — is real API work for a rare case and defeats zero-copy; declined, same call as P1.
- **Resolution (2026-11-03):** **Resolved by documentation; folded into P1 (option a).** No `src/` change. `api.md` (`ISegmentManager`) now states *why* `Serialize` takes an `ITransport` — the send buffer is the destination transport's pooled buffer, rented via `TryReserveSendPacket`, so the transmitted bytes are never copied — and points to `OnSerialize()` for transport-free unit tests. This is the same deliberate `ISegmentManager`/transport coupling kept in P1, viewed from the transport side.
- **Core change:** No. **Priority:** Low. **Status:** CLOSED.

## P8 — Single-flight, pool-buffered I/O; no application-level queue (by design)  LOW (by design)
- **Layer:** All
- **Status:** CONFIRMED (design intent) — CLOSED
- **Evidence:** Single-flight `SendAsync`/`ReceiveAsync` (one op in flight per direction; a concurrent op is rejected with `SendAlreadyPending`/`ReceiveAlreadyPending`, not queued); the segment pools (32 slots by default) are the only buffering; `ReceiveArmed`/`SendArmed` expose op state rather than a queue. Backpressure is provided by the OS socket buffer + Transmission Control Protocol (TCP) window, not the library.
- **Impact:** No app-level queue. Send is reject-not-queue; receive is app-driven (re-arm per packet), which doubles as natural app-side backpressure. The pool size is the coarse upper bound (exhaustion → `PoolExhausted`/`SendAlreadyPending`). Deliberate trade-off favoring simplicity + natural backpressure + steady-state latency + zero-allocation over throughput buffering; the cost is burst latency (a per-packet re-arm between delivered packets).
- **Resolution (2026-11-03):** **By design; documented.** No `src/` change. `api.md` gained a `Flow control (no application-level queue)` section stating the model accurately: the pools are the bounded buffer, send is single-flight/reject, receive is single-flight/app-driven, the OS socket buffer is the real backpressure, and the zero-copy scope (no app-level copy; the OS still copies user→kernel). A continuous/draining receive is recorded as the natural *future* option (lower latency + less CPU under bursts, at the cost of the natural-backpressure property and teardown-protocol reconciliation), not a defect to fix.
- **Core change:** No. **Priority:** Low. **Status:** CLOSED (by design).

***

## Teardown protocol — state machine & safety invariants (P2 spec)

The close/disconnect/dispose logic is **lock-free** and shared by four classes: `Transport` (send + receive), `Connection`, `ClientConnector`, and `ServerConnector`. This section pins the invariants it relies on, so the rule is reviewable without re-deriving it from code. The race tests in `SessionTests` / `TransportTests` are the regression net.

### The single rule

> **A close never outruns a starter that has claimed but not yet issued; a close runs over operations that are already in flight.**

- **Claimed-not-issued** = the window between a starter winning its claim (state `1`) and its socket call returning (state `2`). Closing the socket *inside* that window would make the starter's issue throw on a dead socket, so a close **waits** for it.
- **In flight** = the socket call has been issued and is pending in the kernel (state `2`). Aborting one is safe (it settles through its own completion), and for a receive against a live peer the close is precisely what settles it, so a close **does not** wait for it.

### The states

Every operation direction uses a 3-value slot, touched only via `Interlocked`/`Volatile`:

| Value | Name | Meaning |
|:-----:|:-----|:--------|
| `0`   | idle | no operation is claimed or in flight |
| `1`   | claimed | a starter won the claim and is about to issue; **contains no blocking call** other than the socket call itself |
| `2`   | in flight | the socket call has been issued; pending in the kernel until its completion runs |

`Transport` has two such slots (`_sendOpState`, `_receiveOpState`); `Connection` has `disconnectingState`; the connectors have `connectingState` / `acceptingState` (+ `startingState`).

### How a close waits — and why it cannot deadlock

`Transport.CloseSocket()` spins while **any** op slot is `1`, then closes:

```csharp
while (Volatile.Read(ref _receiveOpState) == 1 || Volatile.Read(ref _sendOpState) == 1)
    spin.SpinOnce();
socket.Close();
```

This spin is safe (cannot hang) because:
- it waits **only** on `claimed` (`1`), never on `in flight` (`2`) — so it never waits on a completion that is itself waiting on the close;
- a `claimed` window holds no blocking call, so its holder always runs to `2` (issue) or `0` (bail) as soon as it gets a slice of CPU;
- therefore the slot leaves `1` without the closer doing anything — the hold always resolves.

`Connection.Dispose()` uses the same shape on `disconnectingState`: it holds while a disconnect is `claimed` (the `DisconnectAsync` starter is between its re-read and its issue), then closes, running over a disconnect already `in flight` (whose aborted completion settles it).

### The mutual-confirmation handshake

The dangerous interleaving is **a starter about to issue** vs **a closer about to close**. The protocol guarantees one side always observes the other:

1. A starter **claims** (`0 → 1`), then **re-reads** the dead/disposed flag. If it sees the mark, it **bails** (clears to `0`, closes on its own) — it never issues on a dead socket.
2. A closer **spins** on the claim slot, so it holds for a starter currently in its `claimed` window.

Because the starter's re-read happens *after* its claim and the closer's check happens *after* its mark, at least one side is guaranteed to see the other — so a close can never land between a starter's re-read and its issue. This is the invariant the tests below exercise.

### Invariants a future change must preserve (checklist)

A change to any teardown path is safe **iff** it preserves all of these:

- **[I1]** Every starter that will issue a socket call claims first (`0 → 1`) and re-reads the dead/disposed flag *after* claiming and *before* issuing; if the mark is set it bails (no issue).
- **[I2]** The `claimed` window contains no blocking call other than the socket call itself, and that call cannot block (it runs on a socket that is not yet closed).
- **[I3]** Every closer spins **only** on `claimed` (`1`), never on `in flight` (`2`).
- **[I4]** A closer marks the owner dead **before** it closes, and that mark precedes every close.
- **[I5]** The `Disconnected`/completion report is exactly-once (a compare-and-swap (CAS) /exchange guard), so racing the natural (FIN — the 'finish' flag) path and the force (`Dispose`) path never double-fires.
- **[I6]** Closing an already-closed socket and releasing an already-free segment are idempotent (no-ops), so racing closers/releasers are harmless.

### Regression net

`SessionTests` (connection-level, real loopback sockets):
- `Connection_DisconnectAndDispose_Raced_ReportsExactlyOnce` — 100× disconnect×dispose race with an in-flight receive; asserts exactly-once report, settled dispose, no deadlock.
- `Connection_DisconnectAsync_WithInFlightReceive_SettlesOnce`, `Connection_Dispose_WithInFlightReceive_SettlesOnce`, `Connection_DisconnectThenDispose_NeverDoubleReports`.

`TransportTests` cover the single-flight / reserve / oversize edges the close protocol assumes.
