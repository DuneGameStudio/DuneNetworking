# DuneSession.Tests - Test Manifest

**Project:** `tests/DuneSession.Tests/DuneSession.Tests.csproj`  
**Target Framework:** net8.0  
**Dependencies:** DuneSession, DuneTransport, xUnit

---

## Test Categories

### ✅ RUN ALWAYS (31 tests)
All tests use real sockets on loopback. No external dependencies.

| Test Class | Tests | Description |
|------------|-------|-------------|
| `SessionTests` | 31 | Connection lifecycle, connector validation, server/client integration, concurrency |

**Key Tests (Always Run):**
- `Connection_*` - Construction, IsConnected, Transport access, Dispose
- `Connection_DisconnectAsync_*` - Idempotent, after remote FIN, racing with Dispose
- `ClientConnector_*` - Construction, invalid address, refused connection, valid server
- `ServerConnector_*` - Construction, start/stop listening, accept connection, dispose
- `ServerConnector_AcceptConnection_*` - Real client, multiple clients, with data
- `ServerConnector_AcceptConnection_ObjectDisposed_FiresAcceptFailed` - Error injection via reflection
- `ServerConnector_ProcessAccept_ErrorCompletion_FiresAcceptFailed` - Real `ProcessAccept` else branch via reflection (see below)
- `ClientConnector_ConnectAsync_*` - Invalid address (FormatException), refused (SocketException)
- `ClientConnector_Dispose_*` - Idempotent, unsubscribes from connector
- Concurrency tests: `Connection_DisconnectAsync_RacingWithDispose`, `ServerConnector_AcceptConnection_RacingWithStopListening`

---

### ✅ Error Injection Tests (Done)

| Test | Target | How it reaches the branch (no cheating) |
|------|--------|-----------------------------------------|
| `ServerConnector_AcceptConnection_ObjectDisposed_FiresAcceptFailed` | `AcceptConnection` ObjectDisposed catch (lines 115-119) | Reflection closes the private listening socket while `IsListening` stays true; the real `AcceptAsync` throws `ObjectDisposedException`; the real catch fires the real `OnAcceptFailed`. |
| `ServerConnector_ProcessAccept_ErrorCompletion_FiresAcceptFailed` | `ProcessAccept` else branch (lines 149-152) | Reflection sets the real private `acceptEventArgs.SocketError=ConnectionReset` + `AcceptSocket=null`, then invokes the real private `ProcessAccept`. Its real `else` branch runs and fires the real `OnAcceptFailed` with the injected error (would time out if the branch never ran — no false path). |

### 📋 PLANNED / DOCUMENTED GAPS
_None._ The `ProcessAccept` else branch is now covered by the reflection test above.

---

## Run Commands

```bash
# Run all tests
dotnet test tests/DuneSession.Tests

# Run with coverage
dotnet test tests/DuneSession.Tests --collect:"XPlat Code Coverage"
```

---

## Coverage Targets

| Metric | Current | Target |
|--------|---------|--------|
| Line Coverage | ~80% (ProcessAccept else branch covered) | ≥90% |
| Branch Coverage | ~72% | ≥90% |

**Main gaps:** Connector aside from the covered error branches; remaining line coverage is in accept/connection helper paths exercised indirectly.