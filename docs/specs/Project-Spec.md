# DuneNetworking Library Specification

A layered networking library for games and real-time applications. Provides Transmission Control Protocol (TCP) connection management, length-prefixed packet framing, and packet dispatch on top of raw sockets.

## Purpose

Provide a minimal, composable networking foundation that handles the boring parts (TCP connect/accept, byte framing, packet routing) while leaving application-level decisions (disconnect logic, packet handling, threading) to the host application.

The library targets netstandard2.1 and is structured as three stacked layers, each referencing only the one below it:

```
DunePresentation → DuneSession → DuneTransport
```

Consumers depend on `DunePresentation` and treat the lower layers as implementation detail. The Presentation layer exposes lower-layer functionality in the way it sees fit — the application interacts with the library through this single entry point.

## Design Principles

1. **The application owns the threading model.** The library does not create background threads, dispatch queues, or receive pumps. The host application decides how and when to call into the library, and is free to structure its own threads, tasks, and loops however it sees fit.

2. **The library never auto-disconnects on error conditions.** Socket errors (connection reset, network unreachable) surface as events with reason codes; the application decides whether to retry, disconnect, or dispose. A TCP FIN (the 'finish' flag) — the peer's explicit intent to close — triggers an automatic disconnect signal, since there is nothing left to recover.

3. **No application-level loops.** The only self-driving behavior is the receive re-arm inside Transport's SocketAsyncEventArgs (SAEA) callback — after each completed phase of the two-phase receive (header → payload), the callback re-issues the socket operation to handle stream fragmentation. This is an internal I/O state machine, not an application dispatch loop. The application may run its own loops (e.g., a main loop iterating over connected sockets), but the library does not impose or require any.

4. **Single-flight per connection.** Each connection accepts one send and one receive operation at a time. Reentrant calls are rejected with a reason code. The application is responsible for serializing its own calls.

5. **Zero-copy serialization.** Packets serialize and deserialize directly into and from pooled byte buffers without intermediate allocations. The library uses a fixed-size segment pool — there is no per-packet garbage collection.

***

## How it works: the flows

The design principles above are the rules; this section walks through what actually happens, end to
end, in plain English. Back-ticked names are the library's public types; the byte-exact and
state-machine internals live in `api.md` and `Decision-Log.md`.

### Getting a connection

- **Client:** `PeerClient` wraps a `ClientConnector`. You call `ConnectAsync(address, port)`; on
  success the connector produces a `Connection` (and a `Peer`) and fires your `OnConnected` handler.
  Connect is **single-flight** — a second `ConnectAsync` while one is in progress is rejected. A
  successful reconnect disposes the connection it replaces.
- **Server:** `PeerServer` wraps a `ServerConnector`. You call `StartListening(address, port)`, then
  `AcceptConnection()` — a **one-shot** accept. Each accepted client produces a `Connection` (and a
  `Peer`) fired to your `OnClientConnected` handler; call `AcceptConnection()` again for the next.

In both cases the application owns the resulting `Peer` and is responsible for disposing it.

### Sending a packet (application → wire)

1. `peer.SerializeAndEncrypt(packet)` reserves a **send segment** from the connection's send pool,
   serializes the packet's own fields into it, writes the 2-byte **presentation packet-ID header**,
   and encrypts in place (if an encryptor is configured). It returns `(segment, PacketError)` — on
   success the framed segment, on failure `null` plus the reason.
2. You call `peer.Send(segment, packetSize)`. The transport prepends its 2-byte **length header** and
   issues `SendAsync`.
3. `SendAsync` is **single-flight**: only one send is in flight at a time; a concurrent send is
   rejected with `SendAlreadyPending`, not queued. On success the transport releases the segment and
   fires `OnPacketSent`; on failure the segment's ownership transfers to you to retry or release.

### Receiving a packet (wire → typed packet)

1. You arm a receive with `peer.Connection.Transport.ReceiveAsync()`. The transport reads in **two
   phases** — first the 2-byte length header, then the declared payload — reassembling fragmented
   reads, then fires `OnPacketReceived` with a **receive segment** (ownership transfers to you).
2. You call `peer.DecryptAndDeserialize(segment)`. It decrypts in place (if configured), reads the
   2-byte packet-ID, looks the ID up in the `PacketRegistry`, factory-creates the packet, and
   deserializes the fields. It returns `((packet, handler), PacketError)` — on success the packet plus
   its registered handler.
3. You invoke `handler(packet)`, then release the segment. The next receive is armed only when *you*
   call `ReceiveAsync()` again — the library does not re-arm on its own.

### Disconnecting

- **Graceful:** `peer.DisconnectAsync()` sends a TCP FIN; when the close completes, the connection's
  `Disconnected` event fires **exactly once**.
- **Force:** `peer.Dispose()` force-completes the connection so an awaiter can never hang;
  `Disconnected` still fires exactly once.
- Both are **idempotent** and **single-shot**, and disconnect/dispose can race an in-flight operation
  safely (no deadlock, no double-report). Socket *errors* do **not** auto-disconnect — they surface as
  transport error events for you to decide what to do (see Design Principle 2).

### Key mechanisms

- **The pool:** each direction owns a pool carved from a single contiguous `byte[]`, split into a
  fixed number of equal **segments**. A `Segment` is an index into that array plus a `Memory` view —
  there is no per-packet allocation. Reserving gives you a segment; releasing returns it (idempotent).
- **Two independent 2-byte headers:** the *transport* length prefix (splits a byte stream into
  messages) and the *presentation* packet-ID header (maps a message to a handler). They are separate
  by design; the transport never sees the packet-ID header.
- **Single-flight + OS backpressure:** one send and one receive in flight per direction; the pools
  bound how much can be staged, and the OS socket buffer + TCP window provide the actual backpressure.
  See `api.md`, "Flow control."

## Layer: Transport

Raw byte send and receive with I/O error notification.

* Manages a fixed-size memory pool carved from a single contiguous array. Memory is rented and returned explicitly. Releasing already-free memory is a no-op (idempotent release).
* Wraps each packet with a length prefix on the wire. The receive pipeline reassembles fragmented stream reads into complete packets.
* Surfaces I/O errors via events carrying a reason code (`TransportError`). Never touches the socket lifecycle — the owning layer manages sockets.
* Does not close the socket on error conditions. A hard socket error marks the connection dead (`IsConnected` → `false`) and reports it via `OnPacketReceiveFailed`/`OnPacketSendFailed`, but it does **not** close the socket (that is the owner's job) and does **not** fire the session-level `Disconnected` event — so the application can still observe state and decide whether to retry, disconnect, or dispose.

### Event Ownership

* **Packet sent:** segment released by Transport (it owns the buffer layout). The subscriber receives only a completion signal.
* **Packet received:** segment ownership transfers to the subscriber. The subscriber must release it.
* **Packet receive failed:** no segment is passed; any rented segment has already been released by Transport.
* **Packet send failed:** segment ownership transfers to the subscriber. The subscriber must either retry with `SendAsync()` or call `Release()`. All error types follow this same contract — the application decides based on the `TransportError` code.

### API Caveats

**Send retry contract:** When `OnPacketSendFailed` fires, the transport's send-operation state is cleared and it can technically accept a new `SendAsync()` call. However, the application must not send a different packet until the failed one is resolved — either retried or released. Sending a new packet while a previous failure is unresolved is an application-level error: the library provides no ordering or queuing guarantees, and packets may arrive out of intended sequence.

### Edge Cases

**Dispose race with in-flight send:** If `Dispose()` runs concurrently with a completing send operation, one of three outcomes occurs:

* **Dispose wins:** `Dispose()` sets its disposed flag, sweeps `currentSendingSegment` via its send-operation-state check, and releases it. When the send callback fires, it sees `_disposed == 1`, releases silently (idempotent — safe), and does **not** fire `OnPacketSendFailed`. The application never sees the failure.
* **Callback wins:** The send callback fires first, invokes the appropriate event (`OnPacketSent` or `OnPacketSendFailed`), and clears its send-operation state. When `Dispose()` runs, its send-operation-state sweep finds `0` and skips the send branch. If the callback transferred segment ownership to the app (failure case), the app must release it before the transport's `SegmentedBuffer` is garbage collected.
* **True simultaneous:** Both execute in overlapping windows. The idempotent `ReleaseMemory` in `SegmentedBuffer` prevents pool corruption from double-release. The `_disposed` check in the callback determines whether the event fires.

In all cases, no events fire after `Dispose()` completes. The application should not rely on receiving `OnPacketSendFailed` during shutdown.

## Layer: Session

Connection lifecycle management. Maps transport errors onto connection-level events.

* Provides client and server connection factories that produce a connection handle. Entry points are guarded against reentrant calls.
* The connection handle owns an underlying transport and exposes a `Disconnected` event that fires **exactly once** — when a graceful disconnect completes or the connection is disposed (a force completion so an awaiter can never hang). It is a report, not a wait: the connection owns no threads, timers, or tasks. Socket errors do not fire it; they bubble up via the transport's error events for the application to handle.
* Disconnect is single-shot — once initiated, it cannot be repeated. `DisconnectAsync` is guarded against disposed and already-disconnected states.
* Higher layers interact with connections through their transport interface, never touching sockets directly.

## Layer: Presentation

Packet dispatch, registration, and encryption. The application's primary entry point — it exposes the lower layers' functionality in the way the Presentation layer sees fit.

### Packet Interface

Implementers provide a packet ID and field-level serialization methods. The library handles header writing and slicing so user code only serializes its own fields.

### Packet Registry

Maps packet IDs to (factory, handler) pairs. Registration requires an explicit packet ID plus a factory/handler; duplicate IDs throw. An unknown packet ID at runtime does not raise an event — `DecryptAndDeserialize` reports it as `PacketError.RegistryError` in its return value.

### Peer

Wires transport, registry, and encryption together. Pure methods with no internal event subscriptions — the application wires `peer.Connection.Transport` events and calls Peer methods on its own threads.

`SerializeAndEncrypt<T>()` reserves a segment, serializes the packet's fields, writes the presentation header, and encrypts (if configured). It returns a `(Segment?, PacketError)` tuple: on success the framed segment with `Error == 0` (ready for the app to enqueue); on failure a `null` segment and the `PacketError` naming the step that failed. There is no failure event — the outcome is reported synchronously in the return value, and the segment is released internally on failure.

`DecryptAndDeserialize(Segment)` decrypts (if configured), reads the packet ID, looks up the registry, deserializes the packet, and releases the segment. It returns a `((IPacket, Action<IPacket>)?, PacketError)` tuple: on success the packet plus its registered handler with `Error == 0`; on failure a `null` result and the `PacketError` naming the step that failed. The segment is released in every case, and there is no failure event — the outcome is reported synchronously in the return value.

`Send(Segment, int)` delegates to `Transport.SendAsync()`.

`Dispose()` disposes the underlying connection. No event unsubscribes — Peer subscribes to nothing.

### Encryption

An optional symmetric encryption hook applied in-place to the framed payload (header + fields) on both send and receive. The application supplies the encryption implementation.

### Connection Helpers

PeerServer and PeerClient are factory helpers that wire up connectors, packet registry, and optional encryption, then produce ready-to-use peer instances. PeerServer accepts connections and creates a peer per client. PeerClient connects to a remote and creates a peer on success. The application owns each peer and is responsible for calling `Dispose()` when done.

## Threading Model

The library is designed to support an application-owned threading model where the host application structures its own threads and tasks around the library's callback-driven API. A reference pattern:

```
Application Side                          Library Side
────────────────                          ────────────
Task: listen + accept loop                │
  → produces Peer per connection          │
                                         │
Task: main loop over connected Peers      │
  → per-peer:                            │
      task calling ReceiveAsync()        │  ← SAEA Completion Callback
      task calling SendAsync()           │     (thread-pool thread)
      on receive callback →              │     ↓
        queue packet                     │     Transport-level: ProcessReceive /
      on send callback →                 │       ProcessSend (I/O state machine)
                                         │       ↓
                                         │     Pipeline-level: fire events
      main loop conditionally            │     (OnPacketReceived, etc.)
        re-arms send/receive             │  ← application event handlers
                                         │       (enqueue to channel, unwire, etc.)
  → only one send + one receive          │
     active at a time per peer           │
```

**Two tiers of SAEA callbacks:**

* **Transport-level:** The library's internal SAEA callbacks (`OnPacketReceivedEventHandler`, `OnPacketSentEventHandler`) manage the I/O state machine — two-phase receive (header → payload), segment reservation/release, in-flight flag management. This work is inherent to SAEA and cannot be offloaded.
* **Pipeline/application-level:** The events fired by Transport (`OnPacketReceived`, `OnPacketSent`, `OnPacketSendFailed`, `OnPacketReceiveFailed`) are handled by the application. These handlers should be signal-only — enqueue to channels, unwire temporary handlers, release segments on error. No business logic, no blocking.

Key properties:

* **The library provides no task-based APIs.** All I/O is callback-driven via `SocketAsyncEventArgs`. The application owns all scheduling — it wraps callbacks in tasks, queues, or threads as it sees fit.
* **Events fire on I/O completion threads.** SAEA callbacks execute on whatever thread-pool thread completed the socket operation. Event handlers must not assume they run on the caller's thread.
* **The application decides when to re-arm.** A socket being ready to send or receive is a necessary but not sufficient condition — the application may choose not to re-arm (e.g., during shutdown, backpressure, or resource constraints). The library never forces an operation.
* **Cross-thread data passing is the application's responsibility.** If the application queues packets from an I/O callback thread for consumption on a main loop thread, it must provide its own synchronization (e.g., `ConcurrentQueue<T>`). The library does not impose or provide this.

## What Is Not Included

* **Application-level protocols:** heartbeat, authentication, matchmaking
* **Message queues or buffering:** the library delivers packets as they arrive
* **Reliability or ordering guarantees:** TCP provides ordering; the library does not add retry or acknowledgment layers
* **Multi-connection management:** each peer/connection is independent; the application manages collections
