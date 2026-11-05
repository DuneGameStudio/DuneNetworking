# DuneNetworking — Architecture Document

The design-level description of how the `DuneNetworking` library is built and why: its layering
(the C4 model: Context, Container, Component, and Code), its structural and concurrency views (4+1), its cross-cutting concerns, its quality goals and
scenarios, and the rationale behind its key design decisions.

**Companion files:**
- [`Project-Spec.md`](Project-Spec.md) — the *functional* "how the library works" reference, with the end-to-end flows (connect, send, receive, disconnect).
- [`api.md`](api.md) — the signature-level API reference (per type, property, and method).
- [`Decision-Log.md`](Decision-Log.md) — the Phase-3 change history: the issues reviewed, the decisions, and the detailed teardown protocol.

Ground truth is the source on `dev`. Where this document and `api.md` agree, they are both derived
from the same code; `api.md` is the signature-level reference, this document is the design-level one.

***

## 1. Purpose

`DuneNetworking` is a **library** (not an application). Its job is to be a low-latency, zero-copy,
versatile networking backend that the user's future apps and games build on. "Zero-copy" is application-level: a packet serializes directly into a pooled buffer with no intermediate allocation, though the OS (operating system) still performs the user-to-kernel copy on a standard TCP send. It deliberately handles
the "boring" parts — Transmission Control Protocol (TCP) connect/accept, byte framing, packet routing, pooled memory — and leaves
threading, protocol semantics, and application state to the host.

The design goal is a library that is correct, fast, zero-copy, and versatile. The issues raised
during the Phase-3 review and how each was resolved are recorded in
[`Decision-Log.md`](Decision-Log.md); this document describes the resulting architecture.

***

## 2. C4 Model

### 2.1 Level 1 — Context

```
                    ┌──────────────────────────────┐
   Host application /  │      DuneNetworking library     │
   game (consumer) ──▶ │  (in-process, netstandard2.1)   │
                    │                                │
                    │  DunePresentation               │
                    │    └─ DuneSession               │
                    │         └─ DuneTransport  ──────┼──▶ OS TCP sockets
                    └─────────────────────────────────┘
```

The consumer references **only** `DunePresentation`. It creates `PeerClient`/`PeerServer`, gets
`IPeer` handles, and drives send/receive. `DuneSession` and `DuneTransport` are implementation
detail. There is no external runtime dependency beyond the .NET Base Class Library (BCL) and `System.Net.Sockets`.

### 2.2 Level 2 — Containers (assemblies)

| Assembly | Target | Depends on | Responsibility |
| --- | --- | --- | --- |
| `DuneTransport` | netstandard2.1 | (BCL, sockets) | Raw framed byte I/O over a `Socket`; pooled segments; I/O error events. |
| `DuneSession` | netstandard2.1 | `DuneTransport` | Connection lifecycle: connect/accept factories, graceful disconnect, close protocol. |
| `DunePresentation` | netstandard2.1 | `DuneSession` | Packet dispatch, registry, encryption; the public entry point. |
| `DuneApplication` | netstandard2.1 | `DunePresentation` | (Demo/infra consumer.) |
| `DemoPackets` | netstandard2.1 | (presentation) | (Demo packet types.) |
| `*.Tests` (×3) | net8.0 | matching layer | Unit + race/stress + real-socket integration tests. |

The dependency chain is strictly one-way and each layer references only the one below it. **One
exception worth noting** (see [`Decision-Log.md`](Decision-Log.md), P1): the presentation-layer
`IPacket` interface *extends* the transport-layer `ISegmentManager` — the dependency is still legal
(Presentation→Transport) but the *contract ownership* crosses a layer boundary.

***

## 3. The 4+1 Views

### 3.1 Logical view (types and responsibilities)

- **Transport:** `Transport : ITransport`; `SegmentedBuffer` (pool); `Segment` (struct);
  `ISegmentManager` + `SerializeResult`/`DeserializeResult` (segment lifecycle contract);
  `TransportError`.
- **Session:** `Connection : IConnection`; `ClientConnector : IClientConnector`;
  `ServerConnector : IServerConnector`.
- **Presentation:** `Peer : IPeer`; `PeerClient : IPeerClient`; `PeerServer : IPeerServer`;
  `PacketRegistry`; `IPacket : ISegmentManager`; `PacketError`; `IPacketEncryptor`;
  `PresentationHeader` (internal, the 2-byte packet-ID header).

Two independent segment pools exist **per connection** (one send, one receive), created inside
`Transport`. Ownership of a `Segment` transfers across the transport's `OnPacketReceived` /
`OnPacketSendFailed` events and across the peer's tuple returns.

### 3.2 Process / concurrency view

- **No app-facing async/await or tasks.** All I/O is `SocketAsyncEventArgs` (SAEA) callback-driven.
  Completions arrive on thread-pool threads; the application owns all scheduling and must provide
  its own thread-safety for cross-thread hand-off.
- **Single-flight per direction** per connection: one send and one receive in flight. Reentrant
  starts are rejected with `SendAlreadyPending` / `ReceiveAlreadyPending`.
- **In-flight state machine (0/1/2):** each of `_sendOpState` / `_receiveOpState` (and the
  connectors' `connectingState` / `acceptingState` / `startingState`) uses a three-state protocol:
  `0` idle, `1` claimed (a start is between claiming and issuing), `2` in-flight. Teardown
  (close/dispose/disconnect) **holds** (a `SpinWait` read-spin) for any `1`-state starter but runs
  **over** `2`-state operations. This "a close must never outrun a claimed-not-issued starter, but
  may run over an in-flight op" rule is the core invariant of the lock-free teardown; the full
  protocol and invariants are in [`Decision-Log.md`](Decision-Log.md).
- **The receive re-arm** is the only self-driving behavior: after each SAEA completion, the internal
  handler re-issues the operation (header→payload). The *application* decides when to start the
  next top-level `ReceiveAsync()` (i.e. after it has consumed the delivered packet).

### 3.3 Development view

Three source projects + three test projects; two demo/infra projects. Source is `netstandard2.1`,
tests are `net8.0`. (Local runs need `DOTNET_ROLL_FORWARD=LatestMajor` because only the .NET 10
runtime is installed.)

### 3.4 Physical view

In-process library. "Deployment" is a NuGet/assembly reference. The only external resources are the
OS socket and the in-process pool arrays (one contiguous `byte[]` per pool). There is no network
topology, no persistence, and no configuration surface.

### 3.5 Scenarios

1. **Happy path:** `PeerClient.ConnectAsync` → `OnPeerConnected(peer)` → app wires
   `peer.Connection.Transport` events → `SerializeAndEncrypt` + `Send` out; `OnPacketReceived` in →
   `DecryptAndDeserialize` → handler.
2! **Server:** `PeerServer.StartListening` + `AcceptConnection` (one-shot) → a `Peer` per client.
3! **Graceful close:** `peer.DisconnectAsync()` (or the peer's `DisconnectAsync`) → FIN (the 'finish' flag) →
   `Connection.Disconnected` fires exactly once.
4! **Error handling:** pool exhausted, malformed frame, socket reset — surfaced as
   `TransportError` events (transport) and `PacketError` tuple returns (presentation).

***

## 4. Fitness Functions (what "good" is measurable as)

| Fitness | Target | How to measure |
| --- | --- | --- |
| **Zero-copy** | Zero heap allocations on the serialize/receive hot path | Allocation profiler (e.g. `PerfView`/dotnet-counters `gen-0` ≈ 0 during N send/receive cycles); pools return to baseline `FreeCount`. |
| **No pool leak** | `FreeCount` returns to pool size after N balanced reserve/release cycles | Existing stress tests (e.g. `SegmentedBuffer_ConcurrentReserveRelease_NoDoubleAllocate`); must hold under race conditions. |
| **Latency** | No task/await/allocation on the I/O completion path | Code inspection (SAEA only); microbenchmark send/recv round trip. |
| **Correctness** | 100% test pass; idempotent release; no double-release; exactly-once `Disconnected` | `dotnet test` baseline + the dedicated race/close-protocol tests. |
| **Teardown safety** | No deadlock or throw when dispose/disconnect race an in-flight op | The close-protocol tests; soak test (thousands of connect/dispose iterations). |
| **Versatility** | Configurable pool size & max packet size; optional encryption; dependency-injection (DI)-resolvable handlers | API surface — pool sizing is now met via `TransportOptions` (see [`Decision-Log.md`](Decision-Log.md), P4). |

***

## 5. Quality Attribute Scenarios (ATAM-style, where ATAM = Architecture Tradeoff Analysis Method)

| # | QA | Stimulus | Scenario | Target |
| --- | --- | --- | --- | --- |
| 1 | Performance | A client sends a 64-byte packet | Single round trip over loopback | No task creation, no GC on the path; completion via SAEA callback. |
| 2 | Scalability | 1000 concurrent peers | Each peer has its own socket + 2 pools | Per-connection isolation; no shared mutable state across peers. |
| 3 | Availability | A peer's connection resets mid-transfer | App observes `SocketError` + `IsConnected=false` | No hang; app can reconnect; exactly-once `Disconnected` on the next teardown. |
| 4 | Modifiability | Add a new packet type | Implement `IPacket`, `RegisterHandler<T>` | No change to transport/session; one registry line. |
| 5 | Modifiability | Swap the encryption scheme | Provide a new `IPacketEncryptor` | No change outside the presentation layer. |
| 6 | Security | Enable encryption | Supply an encryptor factory per connection | In-place encrypt/decrypt; app owns key/IV lifecycle. |
| 7 | Modifiability | Need packets larger than the default segment | Configurable via `TransportOptions` (see [`Decision-Log.md`](Decision-Log.md), P4) | Larger pool → larger max packet. |

***

## 6. Cross-Cutting Architectural Concerns

- **Segment ownership transfer.** The single most important invariant: exactly one owner at a time,
  release is idempotent, and every exit path (success, failure, dispose race, handler throw) returns
  the segment to its pool. This is why `Release()`/`ReleaseMemory` are idempotent and why the
  transport's dispose "sweeps" in-flight fields.
- **The close protocol.** The lock-free teardown (see §3.2 and [`Decision-Log.md`](Decision-Log.md))
  is the highest-complexity area and the main maintenance risk. Its guarantee is that teardown is
  exactly-once, cannot deadlock, and never leaves an awaiter hanging.
- **Two independent 2-byte headers.** The transport length prefix (in `Transport`) and the
  presentation packet-ID header (in `PresentationHeader`, written after the transport one) are
  separate by design. The transport is deliberately ignorant of the presentation header.
- **Asymmetric error model.** The *transport* reports both success and failure via **events**; the
  *presentation* layer reports outcomes via **return tuples** and has **no events**. The app stitches
  the two idioms together (see [`Decision-Log.md`](Decision-Log.md), P6).

***

## 7. Design decisions & rationale

The library makes a small number of deliberate choices that trade one quality for another. Each is
stated here as *why*; the full evidence and change history is in [`Decision-Log.md`](Decision-Log.md).

- **Segment ownership is a first-class invariant.** Every `Segment` has exactly one owner at a
  time; `Release()` is idempotent; every exit path — success, failure, dispose race, a throwing
  handler — returns the segment to its pool. This is what makes the pooling leak-free and is the
  single most important rule to preserve in any change.
- **A packet *is* a segment owner (`IPacket : ISegmentManager`).** The coupling is deliberate, not
  accidental: the segment/pool are genuinely a transport concern, and serialization is zero-copy
  precisely because a packet writes into the destination transport's own pooled buffer. The cost — a
  packet can only be serialized via a live transport, and app packet types carry `Segment` /
  `PacketSize` machinery — is accepted as the price of zero-copy. (Decision-Log, P1 and P7.)
- **Two-tier error reporting.** The *transport* reports both success and failure via **events** (I/O
  completion is inherently asynchronous — a packet arriving is not the result of a call you made);
  the *presentation* layer reports outcomes via **return tuples** (serialize/deserialize are pure,
  synchronous operations). The app stitches the two idioms. (Decision-Log, P6.)
- **Single-flight, pool-buffered I/O; no application-level queue.** One send and one receive in
  flight per direction; a concurrent call is rejected, not queued. The segment pools are the
  buffering, and the OS socket buffer + TCP window are the real backpressure. A deliberate trade-off
  favoring simplicity, steady-state latency, and zero-allocation over throughput buffering, paying
  in burst latency (a per-packet re-arm between delivered packets). (Decision-Log, P8.)
- **Configurable pools.** `TransportOptions` (segment size × count, threaded through every factory)
  makes the pool — and thus the maximum packet size — configurable, with the original 256×32 as the
  default. (Decision-Log, P4.)
- **A named success value.** `PacketError.None` (== 0) makes "success" explicit rather than a magic
  `0` / `default`. (Decision-Log, P3.)
- **A lock-free, exactly-once teardown protocol.** A connection tears down safely even when
  disconnect and dispose race an in-flight operation: a close never outruns a starter that has
  claimed but not yet issued, and runs over operations already in flight. The `Disconnected` report
  fires exactly once and teardown cannot deadlock. The full state machine and invariants live in
  [`Decision-Log.md`](Decision-Log.md). (Decision-Log, P2.)
- **The server's dispose protocol.** `ServerConnector` is one-shot; the narrow window where a
  completed-but-undelivered accept could leak one socket is documented, and the safe shutdown
  (`StopListening` → bounded `IsAcceptPending` wait → `Dispose`) is the contract. (Decision-Log, P5.)
