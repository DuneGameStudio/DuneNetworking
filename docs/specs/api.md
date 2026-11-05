# DuneNetworking — API Reference

A layered networking library for games and real-time applications. Three stacked layers, each referencing only the one below it:

```
DunePresentation → DuneSession → DuneTransport
```

Consumers depend on `DunePresentation`. Lower layers are implementation detail.

> This reference is generated from the current source on `dev`. If a signature here disagrees
> with your checkout, the source is the source of truth.

***

## Contents

### DuneTransport

* [`ITransport`](#itransport) / [`Transport`](#transport)
* [`TransportOptions`](#transportoptions)
* [`TransportError`](#transporterror)
* [`ISegmentManager`](#isegmentmanager)
* [`SerializeResult`](#serializeresult) / [`DeserializeResult`](#deserializeresult)
* [`Segment`](#segment)
* [`SegmentedBuffer`](#segmentedbuffer)

### DuneSession

* [`IConnection`](#iconnection) / [`Connection`](#connection)
* [`IClientConnector`](#iconnectclient) / [`ClientConnector`](#clientconnector)
* [`IServerConnector`](#iconnectserver) / [`ServerConnector`](#serverconnector)

### DunePresentation

* [`IPeer`](#ipeer) / [`Peer`](#peer)
* [`IPeerClient`](#ipeerclient) / [`PeerClient`](#peerclient)
* [`IPeerServer`](#ipeerserver) / [`PeerServer`](#peerserver)
* [`PacketError`](#packeterror)
* [`IPacket`](#ipacket)
* [`PacketRegistry`](#packetregistry)
* [`IPacketEncryptor`](#ipacketencryptor)

### Data Flow

* [Send path](#send-path)
* [Receive path](#receive-path)

***

## DuneTransport

Raw byte send and receive with I/O error notification. Length-prefixed framing (2-byte
little-endian). Pooled segment memory. No socket lifecycle management — the owning layer
provides the `Socket` and owns it.

The library is entirely callback-driven via `SocketAsyncEventArgs`. There are no
task/async-await APIs at this layer; the application owns all scheduling.

***

### `ITransport` / `Transport`

Asynchronous socket transport with two-phase (header → payload) receive and length-prefixed send.

**Namespace:** `DuneTransport.Transport.Interface` (interface), `DuneTransport.Transport` (class)

```C#
interface ITransport : IDisposable
class Transport : ITransport
```

#### Constructor

```C#
new Transport(Socket socket)
new Transport(Socket socket, TransportOptions options)
```

| Parameter | Type     | Description                                      |
| --------- | -------- | ------------------------------------------------ |
| `socket`  | `Socket` | An already-connected TCP socket. Must not be null. |

**Throws:** `ArgumentNullException` if `socket` is null.

Two independent segment pools are created internally (one for sending, one for receiving),
a default [`SegmentedBuffer`](#segmentedbuffer) each (256 bytes × 32 segments), or a differently-sized pool when the overload with [`TransportOptions`](#transportoptions) is used.

#### Properties

| Property        | Type   | Description                                                                                                                  |
| --------------- | ------ | ---------------------------------------------------------------------------------------------------------------------------- |
| `IsConnected`   | `bool` | `true` while the socket is alive. Set to `false` on FIN (peer closed) or on a hard socket error. `Dispose()` also clears it. |
| `IsDisposed`    | `bool` | `true` after `Dispose()` is called.                                                                                          |
| `ReceiveArmed`  | `bool` | `true` while a `ReceiveAsync()` operation is in flight. Use this instead of tracking your own "receive armed" flag.          |
| `SendArmed`     | `bool` | `true` while a `SendAsync()` operation is in flight. Use this instead of tracking your own "send armed" flag.                |

#### Methods

***

#### `TryReserveSendPacket`

Rents a segment from the send pool for serializing a packet.

```C#
bool TryReserveSendPacket(out Segment segment)
```

| Parameter | Type          | Description                                                                                                                                                                     |
| --------- | ------------- | ------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| `segment` | `out Segment` | On success, the rented segment. `segment.Memory` is pre-sliced from byte 2 (after the 2-byte length header). Write payload starting at offset 0 of the span. On failure, `default`. |

**Returns:** `true` if a segment was rented; `false` if disposed or the pool is exhausted.

> Note: the presentation-layer [`Peer`](#peer) calls this for you through
> [`ISegmentManager.Serialize`](#isegmentmanager). You only call it directly if you drive
> a [`Transport`](#transport) without the presentation layer.

***

#### `SendAsync`

Begins an asynchronous send. Prepends a 2-byte little-endian length header and initiates the socket send.

```C#
void SendAsync(Segment packet, int packetSize)
```

| Parameter    | Type      | Description                                                   |
| ------------ | --------- | ------------------------------------------------------------- |
| `packet`     | `Segment` | A segment obtained from `TryReserveSendPacket`.               |
| `packetSize` | `int`     | The number of payload bytes to send (excluding the 2-byte header). |

**Behavior:**

* One send in flight at a time. If another send is pending, fires `OnPacketSendFailed` with `SendAlreadyPending`.
* On success: segment is released by Transport, `OnPacketSent` fires.
* On failure: segment ownership **transfers** to the subscriber via `OnPacketSendFailed`. The subscriber must retry with `SendAsync()` or call `Release()`.
* Calling on a disposed or disconnected socket fires `OnPacketSendFailed` with `ObjectDisposed` / `SocketDisconnected`.

> **Retry contract:** When `OnPacketSendFailed` fires, the send slot is cleared and the transport
> can accept a new `SendAsync()`. The application must not send a *different* packet until the
> failed one is resolved — either retried or released. The library provides no ordering or queuing guarantees.

**Example:**

```C#
transport.OnPacketSendFailed += (t, seg, error) =>
{
    if (error == TransportError.SocketError)
        t.SendAsync(seg, segSize);  // retry
    else
        seg.Release();               // terminal error, give up
};

transport.SendAsync(segment, size);
```

***

#### `ReceiveAsync`

Begins an asynchronous receive. Arms the receive pipeline (header phase → payload phase → deliver).

```C#
void ReceiveAsync()
```

**Behavior:**

* One receive in flight at a time. If another receive is pending, fires `OnPacketReceiveFailed` with `ReceiveAlreadyPending`.
* On success: fires `OnPacketReceived` with segment ownership transferred to the subscriber.
* On failure: fires `OnPacketReceiveFailed`. No segment is passed — any rented segment has already been released.
* Does **not** re-arm after completion. The application must call `ReceiveAsync()` again.

**Example:**

```C#
transport.OnPacketReceived += (t, seg) =>
{
    // process seg.Memory.Span
    seg.Release();
    t.ReceiveAsync();  // re-arm
};

transport.ReceiveAsync();  // initial arm
```

#### Flow control (no application-level queue)

There is no application-level queue or internal packet buffer — the **segment pools** are the buffering:

* **The pools are a bounded buffer.** Each direction owns a pool (32 segments by default, configurable via `TransportOptions`). You can therefore stage multiple serialized packets ready to send, and multiple raw received `Segment`s ready to deserialize — up to one per free pool slot. This is not an app-level queue; it is the pre-allocated, zero-allocation buffer the whole design is built on.
* **Send is single-flight and reject-not-queue.** Only one `SendAsync` can be in flight; a concurrent second call fails with `SendAlreadyPending` rather than being queued. The app drains its staged packets one at a time, gated on `OnPacketSent` / `SendArmed`. The library provides no ordering or queuing.
* **Receive is single-flight, app-driven.** One `ReceiveAsync` delivers one complete packet; you re-arm per packet, and the raw `Segment`s can be staged in the pool before you deserialize them.
* **Where the backpressure actually is.** The OS socket buffer + the TCP window are the real backpressure, not the library. Re-arming per packet doubles as a natural, app-side throttle (the socket is only read as fast as you process). The pool size is the coarse upper bound — once exhausted you get `PoolExhausted` (reserve) / `SendAlreadyPending` (send), i.e. a "slow down or drop" signal rather than a smooth throttle.
* **Zero-copy scope.** The packet serializes directly into the send segment (a pre-registered region), so there is no application-level copy between serialization and the socket call. The OS still performs the user→kernel copy into the socket buffer on a standard TCP send; true cross-socket zero-copy would require shared-memory / mmap, which this library does not use.
* **Future option (not implemented):** a continuous / draining receive — keep a receive armed at all times and drain all currently-available packets into the pool in one pass — would remove the per-packet re-arm round-trip (lower latency and less CPU under bursts) at the cost of the natural-backpressure property above and a reconciliation with the teardown protocol. It is the natural next step *if* a bursty or bandwidth-heavy use case appears; it is not required for small, frequent, latency-sensitive traffic.

***

#### `Dispose`

Cleans up Transport resources.

```C#
void Dispose()
```

**Behavior:**

* Idempotent — safe to call multiple times.
* Releases whatever segment the in-flight fields still hold (release is idempotent).
* Unwires and disposes each direction's `SocketAsyncEventArgs` only when that direction has no pending operation.
* Sets `IsConnected` to `false` and `IsDisposed` to `true`.
* Does **not** close the socket — the owning layer does that.
* No events fire after `Dispose()` completes.

***

#### Events

| Event                   | Signature                                                 | Description                                                                                                                                     |
| ----------------------- | --------------------------------------------------------- | ----------------------------------------------------------------------------------------------------------------------------------------------- |
| `OnPacketReceived`      | `Action<ITransport, Segment>?`                           | Fired when a complete framed packet is received. Segment ownership **transfers** to the subscriber. Subscriber must call `Release()` when done. |
| `OnPacketSent`          | `Action<ITransport>?`                                    | Fired when a send completes successfully. Segment already released.                                                                             |
| `OnPacketSendFailed`    | `Action<ITransport, Segment, TransportError>?`           | Fired when a send fails. Segment ownership **transfers** to the subscriber. Subscriber must retry or release.                                   |
| `OnPacketReceiveFailed` | `Action<ITransport, TransportError>?`                    | Fired when a receive fails. No segment passed.                                                                                                  |

**See also:** [`TransportError`](#transporterror) for error codes.

***

### `TransportError`

Reason codes surfaced with Transport failure events.

**Namespace:** `DuneTransport.Transport`

```C#
enum TransportError
```

| Value                   | Meaning                                                                             |
| ----------------------- | ----------------------------------------------------------------------------------- |
| `SocketError`           | OS-level socket failure (exception, or a bad `SocketError` in the SAEA completion). |
| `PoolExhausted`         | Segment pool had no free segment.                                                   |
| `ProtocolError`         | Peer sent a malformed frame (zero-length or oversized payload).                     |
| `InvalidSegment`        | Caller passed a segment the send buffer cannot resolve.                             |
| `HandlerFailed`         | An `OnPacketReceived` subscriber threw. The segment is released on their behalf.    |
| `ObjectDisposed`        | Operation called on a disposed Transport.                                           |
| `SocketDisconnected`    | FIN received, or the socket is no longer connected.                                 |
| `ReceiveAlreadyPending` | Reentrant `ReceiveAsync()` call.                                                    |
| `SendAlreadyPending`    | Reentrant `SendAsync()` call.                                                        |

***

### `ISegmentManager`

The segment lifecycle contract: reserve → serialize → release, or deserialize → release.
Implemented by [`IPacket`](#ipacket). This is a **default interface method (DIM)** interface —
`Serialize` and `Deserialize` are provided by the interface and orchestrate the abstract hooks
`OnSerialize` / `OnDeserialize`.

**Namespace:** `DuneTransport.BufferManager.Interface`

```C#
interface ISegmentManager
```

#### Properties

| Property     | Type      | Description                                                            |
| ------------ | --------- | ---------------------------------------------------------------------- |
| `segment`    | `Segment` | The rented segment. Set by `Serialize()`, released by `Deserialize()`. |
| `PacketSize` | `int`     | Total packet size in bytes (including the framing the implementor owns). |

#### Abstract methods (implement in your packet)

| Method          | Returns | Description |
| --------------- | ------- | ----------- |
| `OnSerialize()` | `bool`  | Writes the packet's own fields into `segment` and sets `PacketSize`. **Must not throw** — report failure by returning `false`. |
| `OnDeserialize()` | `bool` | Reads the packet's own fields from `segment`. **Must not throw** — report failure by returning `false`. |

#### Default methods (provided by the interface)

| Method                                                                          | Returns           | Description                                                                                                                       |
| ------------------------------------------------------------------------------- | ----------------- | --------------------------------------------------------------------------------------------------------------------------------- |
| `Serialize(ITransport transport)`                                              | `SerializeResult` | Reserves a segment from `transport`, calls `OnSerialize()`, and returns `Ok` (segment now owned by caller), `PoolExhausted`, or `SerializeFailed`. |
| `Deserialize(Action<Segment, int>? beforeDeserialize = null)`                   | `DeserializeResult` | Invokes the optional `beforeDeserialize` callback, calls `OnDeserialize()`, releases the segment, and returns `Ok` or `DeserializeFailed`. |

> Framing headers and encryption are **not** part of this contract — they are the caller's
> concern (handled by the presentation layer after `Serialize` returns `SerializeResult.Ok`).

> **Why `Serialize` takes an `ITransport`:** this is the zero-copy design, not an oversight.
> `Serialize` rents the send buffer *from the destination transport's pool*
> (`TryReserveSendPacket`) and the packet writes its fields directly into it, so the transmitted
> bytes are never copied. It follows that you cannot serialize a packet "offline" into an
> arbitrary buffer — there is no buffer to write into until a transport's pool provides one. To
> unit-test a packet's field serialization without a transport, drive `OnSerialize()` directly
> against a mock `segment`.

**See also:** [`SerializeResult`](#serializeresult), [`DeserializeResult`](#deserializeresult), [`Segment`](#segment)

***

### `SerializeResult` / `DeserializeResult`

Outcome codes returned by the [`ISegmentManager`](#isegmentmanager) lifecycle methods. They let the
caller report the correct [`PacketError`](#packeterror) instead of a bare `bool`.

**Namespace:** `DuneTransport.BufferManager.Interface`

```C#
enum SerializeResult
{
    Ok,             // segment reserved + serialized; ownership transfers to caller
    PoolExhausted,  // no segment available; OnSerialize not called
    SerializeFailed // segment reserved but OnSerialize returned false; segment released
}

enum DeserializeResult
{
    Ok,             // OnDeserialize returned true; segment released
    DeserializeFailed // OnDeserialize returned false; segment released
}
```

***

### `Segment`

A rented slice of the pooled buffer. Value type — copies cheaply, but `Memory` always points to the same backing array.

**Namespace:** `DuneTransport.BufferManager`

```C#
struct Segment
```

#### Properties

| Property                | Type                | Description                                |
| ----------------------- | ------------------- | ------------------------------------------ |
| `SegmentIndex`          | `int`               | 1-based index into the pool (readable; set internally). |
| `Memory`                | `Memory<byte>`      | The rented slice.                          |
| `ReleaseMemoryCallback` | `Action<int>`       | Points to `SegmentedBuffer.ReleaseMemory`. |

#### Methods

| Method      | Description                                                                |
| ----------- | -------------------------------------------------------------------------- |
| `Release()` | Returns the segment to the pool. Idempotent — safe to call multiple times. |

> On a failed [`Peer`](#peer) call, the returned segment is `null` (a `Segment?`), **not**
> `default(Segment)`.

***

### `SegmentedBuffer`

Fixed-size memory pool carved from a single contiguous array. Zero-allocation segment rental. Thread-safe.

**Namespace:** `DuneTransport.BufferManager`

```C#
new SegmentedBuffer(int arrayLength = 8192, int segmentCount = 32)
```

**Throws:** `ArgumentOutOfRangeException` if `segmentCount <= 0`; `ArgumentException` if `arrayLength <= 0` or `arrayLength < segmentCount`.

#### Properties

| Property       | Type  | Description                                                   |
| -------------- | ----- | ------------------------------------------------------------- |
| `SegmentSize`  | `int` | Size of each segment in bytes (`arrayLength / segmentCount`). |
| `SegmentCount` | `int` | Total number of segments.                                     |
| `FreeCount`    | `int` | Number of free segments.                                      |

#### Methods

| Method                                                                       | Returns | Description                                                                                             |
| ---------------------------------------------------------------------------- | ------- | ------------------------------------------------------------------------------------------------------- |
| `TryReserveSegment(out Segment)`                                             | `bool`  | Rents a segment. Returns `false` if the pool is exhausted.                                              |
| `ReleaseMemory(int segmentNumber)`                                           | `void`  | Returns a segment to the pool. Idempotent. `ArgumentOutOfRangeException` if out of range.               |
| `GetRegisteredMemory(int segmentNumber, int length, out Memory<byte>)`       | `bool`  | Validates bounds and returns a `Memory<byte>` view (no ownership transfer). Used to locate backing memory for framing. |

***

### `TransportOptions`

Segment pool sizing. Controls the size of each segment (which caps the maximum packet size) and
the number of segments in each send and receive pool. Thread it through the connectors and peer
factories to override the default pool (256-byte × 32 segments).

**Namespace:** `DuneTransport.Transport`

```C#
sealed class TransportOptions
```

#### Properties

| Property       | Type  | Default | Description                                                    |
| -------------- | ----- | ------- | --------------------------------------------------------------- |
| `SegmentSize`  | `int` | `256`   | Size of each segment in bytes. Larger values allow larger packets. |
| `SegmentCount` | `int` | `32`    | Number of segments in each send and receive pool.              |

> `TransportOptions` is how you enlarge the default pool. It is threaded through
> `ClientConnector` / `ServerConnector` / `PeerClient` / `PeerServer` (and, for the lower layers,
> `Transport` / `Connection`). Omitting it (or passing `null`) keeps the 256 × 32 default, whose
> ~252-byte field cap is the default maximum packet payload. A connector you supply yourself
> (`client` / `server` param) governs its own pool and ignores `options`.

***

## DuneSession

Connection lifecycle management. Wraps a socket with a [`Transport`](#transport), exposes
connection state, and runs the disconnect/close protocol.

***

### `IConnection` / `Connection`

Core connection handle. Owns a socket and an embedded transport, and manages graceful disconnect.

**Namespace:** `DuneSession.SocketConnectors.Interface` (interface), `DuneSession.SocketConnectors` (class)

```C#
interface IConnection : IDisposable
class Connection : IConnection
```

#### Constructor

```C#
new Connection(Socket socket)
new Connection(Socket socket, TransportOptions options)
```

| Parameter | Type     | Description                       |
| --------- | -------- | --------------------------------- |
| `socket`  | `Socket` | An already-connected socket. Must not be null. |

**Throws:** `ArgumentNullException` if `socket` is null.

#### Properties

| Property      | Type         | Description                                     |
| ------------- | ------------ | ----------------------------------------------- |
| `IsConnected` | `bool`       | `true` while the connection and its transport are both connected. |
| `Transport`   | `ITransport` | The underlying transport. Use this for all I/O. |

#### Methods

| Method              | Description                                                                                         |
| ------------------- | --------------------------------------------------------------------------------------------------- |
| `DisconnectAsync()` | Graceful socket disconnect (TCP FIN). Single-shot — guarded against disposed and already-disconnected states. |
| `Dispose()`         | Force-completes the connection, disposes the transport, closes the socket. Idempotent. Always raises `Disconnected` exactly once. |

#### Events

| Event            | Signature | Description                                                                                 |
| ---------------- | --------- | ------------------------------------------------------------------------------------------- |
| `Disconnected`   | `Action?` | Raised **exactly once** when the connection is definitively over: graceful disconnect completed, or the connection was disposed (a force completion so an awaiter can never hang). It is a report, not a wait — the connection owns no threads, timers, or tasks. |

**Example:**

```C#
connection.Disconnected += () => { /* cleanup */ };
connection.Transport.SendAsync(segment, size);
// ...
connection.DisconnectAsync();
```

**See also:** [`Transport`](#transport)

***

### `IClientConnector` / `ClientConnector` {#iconnectclient}

Factory for client connections. Handles the TCP handshake and produces an `IConnection` on success.

**Namespace:** `DuneSession.SocketConnectors.Interface` (interface), `DuneSession.SocketConnectors` (class)

```C#
interface IClientConnector : IDisposable
class ClientConnector : IClientConnector
```

#### Constructor

```C#
new ClientConnector()
new ClientConnector(TransportOptions options)
```

#### Properties

| Property      | Type     | Description                                                |
| ------------- | -------- | ---------------------------------------------------------- |
| `IsConnected` | `bool`   | `true` if a connection is currently active.               |

#### Methods

| Method                                   | Returns | Description                                                                                 |
| ---------------------------------------- | ------- | ------------------------------------------------------------------------------------------- |
| `ConnectAsync(string address, int port)` | `bool`  | Begins an async TCP connect. Returns `false` if already connected, a connect is in progress, or the connector is disposed. |

#### Events

| Event             | Signature                   | Description                                               |
| ----------------- | --------------------------- | --------------------------------------------------------- |
| `OnConnected`     | `Action<IConnection>?`     | Fires when connect succeeds. Produces a new `Connection`. A successful reconnect disposes the connection it replaces. |
| `OnConnectFailed` | `Action<SocketError>?`     | Fires when connect fails. Socket disposed.                |

**Example:**

```C#
var connector = new ClientConnector();
connector.OnConnected += conn => { /* use conn.Transport */ };
connector.OnConnectFailed += err => { /* handle */ };
connector.ConnectAsync("127.0.0.1", 5000);
```

***

### `IServerConnector` / `ServerConnector` {#iconnectserver}

Factory for server connections. Binds a listening socket and produces an `IConnection` per accepted client. One-shot accept model.

**Namespace:** `DuneSession.SocketConnectors.Interface` (interface), `DuneSession.SocketConnectors` (class)

```C#
interface IServerConnector : IDisposable
class ServerConnector : IServerConnector
```

#### Constructor

```C#
new ServerConnector()            // listen backlog 128
new ServerConnector(int backlog) // custom listen backlog (must be > 0)
new ServerConnector(int backlog, TransportOptions options) // custom pool
```

**Throws:** `ArgumentOutOfRangeException` if `backlog < 1` (parameterized constructor only).

#### Properties

| Property           | Type  | Description                                                   |
| ------------------ | ----- | ------------------------------------------------------------- |
| `IsListening`      | `bool`| `true` if the server is currently listening.                 |
| `IsAcceptPending`  | `bool`| `true` while an accept is armed, or has completed but not yet been delivered. See the dispose note below. |

#### Methods

| Method                                     | Description                                                                                   |
| ------------------------------------------ | --------------------------------------------------------------------------------------------- |
| `StartListening(string address, int port)` | Binds the socket and starts listening. Single-shot (calling while already listening has no effect). Throws on a bad address or bind failure. |
| `AcceptConnection()`                       | Begins an async accept. One-shot — call again after each accept to continue.                   |
| `StopListening()`                          | Closes the listening socket. Existing accepted connections are unaffected.                     |

#### Events

| Event               | Signature                   | Description                                              |
| ------------------- | --------------------------- | -------------------------------------------------------- |
| `OnClientConnected` | `Action<IConnection>?`     | Fires when accept succeeds. Produces a new `Connection`. If no subscriber is attached, the accepted socket is closed. |
| `OnAcceptFailed`    | `Action<SocketError>?`     | Fires when accept fails.                                 |

**Example:**

```C#
var server = new ServerConnector();
server.OnClientConnected += conn => { /* handle client */ };
server.StartListening("0.0.0.0", 5000);
server.AcceptConnection();
```

#### Dispose protocol (shutdown)

`Dispose()` never waits on or drains an in-flight accept — it reports accept state via `IsAcceptPending` and leaves the shutdown decision to the caller. If it runs while an accept is armed, an accepted socket that completed but was not yet delivered can stay open until finalization: the connector unwires the accept's completion callback and disposes its `SocketAsyncEventArgs` (which does not close `AcceptSocket`), and closing the *listener* socket does not close an already-accepted socket.

The window is narrow — it requires `Dispose()` to land between the kernel accepting a connection and the completion callback running — and it only opens if the caller skips this protocol. Intended shutdown:

```C#
server.StopListening();              // stop accepting; closes the listener
while (server.IsAcceptPending) { /* bounded wait — spin or await, time it out */ }
server.Dispose();                    // safe: no armed or undelivered accept
```

Once `StopListening()` has run and `IsAcceptPending` has been driven to false (bounded wait), every inbound connection is either already delivered to `OnClientConnected` or closed by the connector, so `Dispose()` cannot leak.

***

## DunePresentation

Packet dispatch, registration, and encryption. The application's primary entry point — it exposes the lower layers in the way the presentation layer sees fit.

***

### `IPeer` / `Peer`

Main handle for a single connection. Provides pure methods for serialization and deserialization — **no internal event subscriptions and no events of its own**. Failure is reported synchronously through the returned `PacketError`, not through events. The application wires `peer.Connection.Transport` events and calls Peer methods on its own threads.

**Namespace:** `DunePresentation.Peer.Interfaces` (interface), `DunePresentation.Peer` (class)

```C#
interface IPeer : IDisposable
class Peer : IPeer
```

#### Constructor

```C#
new Peer(IConnection connection, PacketRegistry packetRegistry, IPacketEncryptor? encryptor = null)
```

| Parameter        | Type                | Description                                             |
| ---------------- | ------------------- | ------------------------------------------------------- |
| `connection`     | `IConnection`       | An active connection to wrap. Must not be null.         |
| `packetRegistry` | `PacketRegistry`    | Registry for incoming packet handler lookup. Must not be null. |
| `encryptor`      | `IPacketEncryptor?` | Optional encryptor for bidirectional encryption. `null` disables encryption. |

**Throws:** `ArgumentNullException` if `connection` or `packetRegistry` is null.

#### Properties

| Property      | Type         | Description                                                        |
| ------------- | ------------ | ------------------------------------------------------------------ |
| `Connection`  | `IConnection` | The underlying connection. Access `Transport` for events and I/O. |
| `IsConnected` | `bool`       | `true` while the underlying connection is active.                  |

#### Methods

| Method                                                  | Returns                                        | Description                                                                                                                           |
| ------------------------------------------------------- | ---------------------------------------------- | ------------------------------------------------------------------------------------------------------------------------------------- |
| `SerializeAndEncrypt<T>(T packet)` where `T : IPacket` | `(Segment? Segment, PacketError Error)`       | Reserves a segment, serializes the packet's fields, writes the presentation header, and encrypts (if configured). On success returns the framed segment with `Error == 0`; on failure returns a `null` segment and the `PacketError` naming the step that failed. The segment is released internally on failure. |
| `DecryptAndDeserialize(Segment segment)`                | `((IPacket Packet, Action<IPacket> Handler)? Result, PacketError Error)` | Decrypts (if configured), reads the packet ID, looks up the registry, deserializes, and releases the segment. On success returns the packet with its handler; on failure returns a `null` result and the `PacketError`. The segment is released in all cases. |
| `Send(Segment segment, int packetSize)`                 | `void`                                         | Delegates to `Transport.SendAsync()`. |
| `DisconnectAsync()`                                     | `void`                                         | Graceful disconnect. Delegates to `Connection.DisconnectAsync()`. |
| `Dispose()`                                             | `void`                                         | Disposes the connection. Idempotent. No event unsubscribes — the Peer subscribes to nothing. |

> **No events.** Unlike the transport (which reports I/O through events), the Peer reports
> serialization/deserialization outcomes **synchronously** via the `PacketError` in each return
> tuple. Check the `Error` member (or the null segment / null result) to detect failure.

**Example — threading model:**

```C#
// Wire up once per peer (transport events, not peer events)
peer.Connection.Transport.OnPacketReceived += (t, seg) =>
{
    serializationQueue.Enqueue((peer, seg));
    t.ReceiveAsync();  // re-arm
};
peer.Connection.Transport.ReceiveAsync();  // initial arm

// Serialization thread (receive side)
var (result, error) = peer.DecryptAndDeserialize(segment);
if (result != null)
    processingQueue.Enqueue((result.Value.Packet, result.Value.Handler));

// Serialization thread (send side)
var (seg, sendError) = peer.SerializeAndEncrypt(new ChatMessage { Text = "hello" });
if (seg != null)
    sendQueue.Enqueue((peer, seg.Value, packet.PacketSize));

// Send thread
peer.Send(segment, packetSize);
```

> **Segment ownership note:** `SerializeAndEncrypt<T>` returns a `null` segment on failure —
> do not enqueue it. Check the `Segment` (or `Error`) before calling `Send`. Sending a
> `default(Segment)` will surface as `TransportError.InvalidSegment` on the send side with no
> traceable cause.

**See also:** [`IPacket`](#ipacket), [`PacketError`](#packeterror), [`IConnection`](#iconnection)

***

### `PacketError`

Reason codes returned synchronously by [`Peer`](#peer) serialize/deserialize calls. Each layer
handles its own domain of errors — Transport handles I/O, Presentation handles serialization.

**Namespace:** `DunePresentation.Packet`

```C#
enum PacketError
```

| Value                   | Meaning                                                                             |
| ----------------------- | ----------------------------------------------------------------------------------- |
| `PoolExhausted`         | Underlying segment pool had no free segment when one was needed during serialization. |
| `SerializationError`    | Serialization failed (field write or encryptor exception during `SerializeAndEncrypt`). |
| `DecryptError`          | Decrypt failed (encryptor threw during `DecryptAndDeserialize`).                    |
| `DeserializeError`      | Deserialize failed (`ReadFieldsFromBuffer` returned false during `DecryptAndDeserialize`). |
| `RegistryError`         | The packet registry failed to resolve the received packet ID.                        |

> `Error == 0` (the enum default) on a returned tuple means **success**.

***

### `IPeerClient` / `PeerClient`

Factory for client peers. Wraps `IClientConnector` with the packet registry and optional encryption.

**Namespace:** `DunePresentation.Peer.Interfaces` (interface), `DunePresentation.Peer` (class)

```C#
interface IPeerClient : IDisposable
sealed class PeerClient : IPeerClient
```

#### Constructor

```C#
new PeerClient(PacketRegistry registry, Func<IPacketEncryptor>? encryptorFactory = null, IClientConnector? client = null, TransportOptions? options = null)
```

| Parameter        | Type                       | Description                                                             |
| ---------------- | -------------------------- | ----------------------------------------------------------------------- |
| `registry`       | `PacketRegistry`           | Shared packet registry. Must not be null.                               |
| `encryptorFactory` | `Func<IPacketEncryptor>?` | Optional factory invoked per-connection to create encryptors. `null` disables encryption. |
| `client`         | `IClientConnector?`        | Optional connector. A new `ClientConnector` is created if not provided. |
| `options`        | `TransportOptions?`        | Optional pool sizing for the internally-created connector. Ignored when `client` is supplied. |

**Throws:** `ArgumentNullException` if `registry` is null.

#### Properties

| Property      | Type     | Description                                            |
| ------------- | -------- | ------------------------------------------------------ |
| `IsConnected` | `bool`   | `true` if a peer connection is currently active.      |

#### Methods

| Method                                   | Returns | Description               |
| ---------------------------------------- | ------- | ------------------------- |
| `ConnectAsync(string address, int port)` | `bool`  | Begins an async TCP connect. |

#### Events

| Event             | Signature                   | Description                                     |
| ----------------- | --------------------------- | ----------------------------------------------- |
| `OnPeerConnected` | `Action<IPeer>?`           | Fires when connect succeeds. Produces a `Peer`. |
| `OnConnectFailed` | `Action<SocketError>?`     | Fires when connect fails.                       |

**Example:**

```C#
var registry = new PacketRegistry();
registry.RegisterHandler<ChatMessage>(0x0001, sp => (ChatMessage msg) =>
{
    Console.WriteLine($"[{msg.PacketId}] {msg.Text}");
});

var client = new PeerClient(registry);
client.OnPeerConnected += peer =>
{
    // Wire up transport events
    peer.Connection.Transport.OnPacketReceived += (t, seg) =>
    {
        var (result, error) = peer.DecryptAndDeserialize(seg);
        if (result != null)
        {
            var (packet, handler) = result.Value;
            handler(packet);  // or enqueue for a processing thread
        }
        t.ReceiveAsync();  // re-arm
    };
    peer.Connection.Transport.ReceiveAsync();  // initial arm
};
client.ConnectAsync("127.0.0.1", 5000);
```

***

### `IPeerServer` / `PeerServer`

Factory for server peers. Wraps `IServerConnector` with the packet registry and optional encryption.

**Namespace:** `DunePresentation.Peer.Interfaces` (interface), `DunePresentation.Peer` (class)

```C#
interface IPeerServer : IDisposable
sealed class PeerServer : IPeerServer
```

#### Constructor

```C#
new PeerServer(PacketRegistry registry, Func<IPacketEncryptor>? encryptorFactory = null, IServerConnector? server = null, TransportOptions? options = null)
```

| Parameter        | Type                       | Description                                                             |
| ---------------- | -------------------------- | ----------------------------------------------------------------------- |
| `registry`       | `PacketRegistry`           | Shared packet registry. Must not be null.                               |
| `encryptorFactory` | `Func<IPacketEncryptor>?` | Optional factory invoked per-connection to create encryptors. `null` disables encryption. |
| `server`         | `IServerConnector?`        | Optional connector. A new `ServerConnector` is created if not provided. |
| `options`        | `TransportOptions?`        | Optional pool sizing for the internally-created connector. Ignored when `server` is supplied. |

**Throws:** `ArgumentNullException` if `registry` is null.

#### Properties

| Property      | Type     | Description                                            |
| ------------- | -------- | ------------------------------------------------------ |
| `IsListening` | `bool`   | `true` if the server is currently listening.          |

#### Methods

| Method                                     | Description                  |
| ------------------------------------------ | ---------------------------- |
| `StartListening(string address, int port)` | Binds and starts listening.  |
| `AcceptConnection()`                       | Begins an async accept.      |
| `StopListening()`                          | Closes the listening socket. |

#### Events

| Event             | Signature                   | Description                                      |
| ----------------- | --------------------------- | ------------------------------------------------ |
| `OnPeerConnected` | `Action<IPeer>?`           | Fires when a client connects. Produces a `Peer`. |
| `OnAcceptFailed`  | `Action<SocketError>?`     | Fires when accept fails.                         |

***

### `IPacket`

Application packet abstraction. The application implements field-level serialization; the library
handles the presentation header and buffer slicing.

**Namespace:** `DunePresentation.Packet.Interfaces`

```C#
interface IPacket : ISegmentManager
```

#### Properties to implement

| Property   | Type     | Description                                                                    |
| ---------- | -------- | ------------------------------------------------------------------------------ |
| `PacketId` | `ushort` | Unique identifier for this packet type. Used by [`PacketRegistry`](#packetregistry) for dispatch. |
| `segment`  | `Segment` | Inherited from [`ISegmentManager`](#isegmentmanager). Rented segment.          |
| `PacketSize` | `int`   | Inherited from [`ISegmentManager`](#isegmentmanager). Total packet size.       |

#### Methods to implement

| Method                                                               | Description                                                 |
| -------------------------------------------------------------------- | ----------------------------------------------------------- |
| `WriteFieldsToBuffer(Span<byte> buffer, out int bytesWritten)`       | Write packet fields into the span (positioned after the presentation header). Report bytes written. |
| `ReadFieldsFromBuffer(ReadOnlySpan<byte> buffer, int length)`        | Read packet fields from the span. Return `true` on success.  |

`OnSerialize()` / `OnDeserialize()` (from [`ISegmentManager`](#isegmentmanager)) are provided by
`IPacket`'s default explicit implementations — you only write the two field methods above.

**Example:**

```C#
public class ChatMessage : IPacket
{
    public ushort PacketId => 0x0001;
    public Segment segment { get; set; }
    public int PacketSize { get; set; }
    public string Text { get; set; }

    public void WriteFieldsToBuffer(Span<byte> buffer, out int bytesWritten)
    {
        var bytes = Encoding.UTF8.GetBytes(Text);
        bytesWritten = bytes.Length;
        bytes.CopyTo(buffer);
    }

    public bool ReadFieldsFromBuffer(ReadOnlySpan<byte> buffer, int length)
    {
        Text = Encoding.UTF8.GetString(buffer.Slice(0, length));
        return true;
    }
}
```

**Inherited from** **[`ISegmentManager`](#isegmentmanager):** `Serialize`, `Deserialize`, `segment`, `PacketSize`.

***

### `PacketRegistry`

Thread-safe map from packet IDs to (factory, handler) pairs. Used by the Peer to dispatch received packets.

**Namespace:** `DunePresentation.Packet`

```C#
sealed class PacketRegistry
```

#### Methods

| Method                                                                                                    | Returns | Description                                                                                                                     |
| --------------------------------------------------------------------------------------------------------- | ------- | ------------------------------------------------------------------------------------------------------------------------------- |
| `RegisterHandler<T>(ushort packetId, IServiceProvider serviceProvider, Func<IServiceProvider, Action<T>> handlerFactory)` where `T : IPacket, new()` | `void`  | Registers a handler. `handlerFactory` is invoked immediately with `serviceProvider` to produce the `Action<T>` handler. `T` must have a parameterless constructor. |
| `UnregisterHandler(ushort packetId)`                                                                      | `bool`  | Removes a previously registered handler. Returns `true` if it was found and removed.                                          |

**Throws:**

* `ArgumentNullException` if `handlerFactory` is null.
* `InvalidOperationException` if `packetId` is already registered.

> The `serviceProvider` / `handlerFactory` indirection lets a handler close over resolved
> services (e.g. an injected dependency) at registration time. If you don't need a service
> provider, pass `null` and ignore the parameter:
> `registry.RegisterHandler<ChatMessage>(0x0001, null, _ => msg => { ... });`

**Example:**

```C#
var registry = new PacketRegistry();
registry.RegisterHandler<ChatMessage>(0x0001, serviceProvider, sp =>
{
    var logger = sp.GetRequiredService<ILogger>();
    return (ChatMessage msg) => logger.Log($"[{msg.PacketId}] {msg.Text}");
});
```

***

### `IPacketEncryptor`

Optional symmetric encryption hook. Applied in-place to the framed payload (presentation header +
fields) on both send and receive. The application supplies the implementation.

**Namespace:** `DunePresentation.Encryption.Interface`

```C#
interface IPacketEncryptor
```

#### Methods

| Method                                                                  | Description                                                     |
| ----------------------------------------------------------------------- | --------------------------------------------------------------- |
| `Encrypt(ReadOnlySpan<byte> source, Span<byte> destination)`            | Encrypts in place. Source and destination may be the same span. |
| `Decrypt(ReadOnlySpan<byte> source, Span<byte> destination)`            | Decrypts in place. Source and destination may be the same span. |

***

## Data Flow

### Send Path

```
Serialization thread
  var (seg, err) = peer.SerializeAndEncrypt<T>(packet)
    → packet.Serialize(transport)         // ISegmentManager: reserve segment, call OnSerialize
      → TryReserveSendPacket(out seg)     // rent segment (Slice past the 2-byte length header)
      → OnSerialize() → WriteFieldsToBuffer()
      → returns SerializeResult.Ok / PoolExhausted / SerializeFailed
    → PresentationHeader.Write(span, packetId)   // write the 2-byte presentation header
    → encryptor?.Encrypt(span[0..PacketSize], span)  // optional, in place
    → on any throw: segment released, (null, SerializationError) returned
  → app enqueues (peer, seg.Value, packet.PacketSize)   // only if seg != null

Send thread
  peer.Send(segment, packetSize)
    → Transport.SendAsync(seg, size)
      → prepend 2-byte length header
      → socket.SendAsync()
        → success: Release(), OnPacketSent
        → failure: OnPacketSendFailed(segment ownership → app)

> On failure, SerializeAndEncrypt returns (null, PacketError). Check the result before enqueueing.
```

### Receive Path

```
Socket data arrives
  → Transport.ReceiveAsync
    → rent header segment
    → read 2-byte header (payload length) → release header segment
    → rent payload segment
    → read payload bytes
    → OnPacketReceived(transport, segment)   // segment ownership → app
  → app enqueues (peer, segment)

Serialization thread
  var (result, err) = peer.DecryptAndDeserialize(segment)
    → encryptor?.Decrypt(span, span)         // on throw: (null, DecryptError), segment released
    → PresentationHeader.Read(span, out id)  // on throw: (null, SerializationError), segment released
    → registry.TryGetEntry(id, out entry)    // on miss: (null, RegistryError), segment released
    → packet = entry.Factory()
    → packet.Deserialize()                   // on false: (null, DeserializeError); segment released
    → on success: ((packet, entry.Invoke), 0)  // segment already released by Deserialize
  → app enqueues (packet, handler)

Processing thread
  handler(packet)  // invoke the registered handler
```
