# DuneTransport.Tests - Test Manifest

**Project:** `tests/DuneTransport.Tests/DuneTransport.Tests.csproj`  
**Target Framework:** net8.0  
**Dependencies:** DuneTransport, xUnit, coverlet.collector

---

## Test Categories

### ✅ RUN ALWAYS (Core Tests - 71 tests)
These tests use real sockets on loopback and cover synchronous paths, error handling, and concurrency.

| Test Class | Tests | Description |
|------------|-------|-------------|
| `SegmentedBufferTests` | 18 | Segment pool allocation, release, concurrency, idempotent release |
| `TransportTests` | 53 | Send/receive, error paths, pool exhaustion, dispose races, protocol validation |

**Key Tests (Always Run):**
- `Transport_Construction_*` - Constructor validation
- `Transport_ReceiveAsync_*` - Receive paths (sync completion)
- `Transport_SendAsync_*` - Send paths (sync completion on loopback)
- `Transport_Dispose_*` - Dispose idempotency, in-flight cleanup
- `Transport_Receive_*ProtocolError` - Zero-length, oversized payload rejection
- `Transport_OnPacketReceived_HandlerThrows_FiresHandlerFailed` - Handler exception safety
- `Transport_SendAsync_SocketDisconnected_AfterFIN` - FIN detection
- `Transport_ReceiveAsync_FromOnPacketReceived_Handler` - Re-arm pattern
- `Transport_Dispose_RacingWith*` - Concurrency safety
- `Transport_MultipleSequential_PacketsFast` - Rapid sequential packets
- `Transport_IssueReceive_SocketException_Handled` - Socket error handling
- `Transport_SendAsync_SocketException_Handled` - Send error handling

---

### 🗑️ REMOVED: Async Completion Tests (Formerly "Requires netem")

The following 3 tests were **fully implemented but have been removed** (deleted from `TransportTests.cs`):

| Former Test | Target Coverage | Removal Reason |
|-------------|-----------------|----------------|
| `Transport_SendAsync_AsyncCompletionPath_FiresOnPacketSentEventHandler` | `OnPacketSentEventHandler` (lines 430-432), `ProcessSend` async entry (445-470) | Requires `tc netem` on Linux to force async completion; no cross-platform way to trigger. Per project decision: no CI workflow with netem. Keeping fail-fast tests provides false "coverage" on CI; removing them avoids misleading results. |
| `Transport_Dispose_RacingWithAsyncSendCompletion` | Dispose race on async send (lines 448-453) | Same — only manifests with async completion (netem). |
| `Transport_SendAsync_AsyncCompletion_SocketError_FiresOnPacketSendFailed` | Socket error on async completion (lines 456-461) | Same — only reachable via async path. |

**Decision rationale:** These tests exercised the production async completion path (thread-pool `Completed` event → `OnPacketSentEventHandler` → `ProcessSend`), which is the **normal network path**. However, on loopback without artificial latency, `Socket.SendAsync` completes synchronously (returns `false`), so the path is never exercised. The only way to force it locally is `tc netem` (Linux-only, requires root). Since the project explicitly declined a CI workflow with netem, these tests would **always fail on any non-Linux or non-netem runner**, producing 0% coverage on those paths and failing the build. They were removed to avoid false negatives. The async path remains a **documented uncovered gap** — if netem CI is ever added, these tests should be re-implemented.

**Related helper also removed:** `CanForceAsyncCompletion()` heuristic and `Manual_ConfigureNetemForAsyncTests` doc test.

---

### 📋 PLANNED (Not Yet Implemented)
| Test | Target | Status |
|------|--------|--------|
| `Transport_IssueReceive_UnexpectedException_ReleasesSegment` | Lines 213-218 (bare catch) | **Cannot implement honestly** — `Transport` takes a concrete `Socket` (sealed); no injection seam exists to trigger an exception other than `SocketException`/`ObjectDisposedException` (already covered). The bare `catch` is a defensive belt-and-braces block; leaving it intentionally uncovered is a documented decision, not a gap to cheat. |
| `Transport_SendAsync_SendAlreadyPending_FiresError` | Lines 387-389 | Needs netem + implementation (true concurrent send requires async path) |

---

## Run Commands

```bash
# Run all tests (3 will fail without netem)
dotnet test tests/DuneTransport.Tests

# Run only core tests (exclude async completion)
dotnet test tests/DuneTransport.Tests --filter "FullyQualifiedName!~AsyncCompletion"

# Run only async completion tests (requires netem)
dotnet test tests/DuneTransport.Tests --filter "FullyQualifiedName~AsyncCompletion"

# Run with coverage
dotnet test tests/DuneTransport.Tests --collect:"XPlat Code Coverage"
```

---

## Coverage Targets

| Metric | Current | Target |
|--------|---------|--------|
| Line Coverage | 79.1% | ≥95% |
| Branch Coverage | 68.3% | ≥90% |
| With netem CI | ~92%+ | ≥95% |

**Critical gaps without netem:** `OnPacketSentEventHandler` (0%), `ProcessSend` async entry (71%)