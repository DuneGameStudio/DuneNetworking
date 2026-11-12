# DunePresentation.Tests - Test Manifest

**Project:** `tests/DunePresentation.Tests/DunePresentation.Tests.csproj`\
**Target Framework:** net8.0\
**Dependencies:** DunePresentation, DuneSession, DuneTransport, DemoPackets, xUnit

***

## Test Categories

### ✅ RUN ALWAYS (55 tests)

All tests use real implementations. Some use mocks for unit tests, some use real sockets for integration.

| Test Class          | Tests | Description                                                                                |
| ------------------- | ----- | ------------------------------------------------------------------------------------------ |
| `PresentationTests` | 49    | PacketRegistry, Peer (unit + integration), PeerClient, PeerServer, PresentationHeader, Concurrency |

***

### 🔧 Unit Tests (Mocks) - Run Fast

| Test                                                            | Target                                                  | Type                  |
| --------------------------------------------------------------- | ------------------------------------------------------- | --------------------- |
| `RegisterHandler_*` (3)                                         | PacketRegistry registration                             | Unit                  |
| `Peer_Construction_*` (4)                                       | Validation of null args                                 | Unit                  |
| `SerializeAndEncrypt_ReturnsSegment_WhenSuccessful`             | Mock transport success                                  | Unit                  |
| `SerializeAndEncrypt_ReturnsNullWithReason_WhenPoolExhausted`   | Mock pool exhaustion                                    | Unit                  |
| `SerializeAndEncrypt_EncryptorThrows_ReleasesSegment_ReturnsNullWithSerializationError` | Encryptor throws; segment released, reason in return | Unit        |
| `SerializeAndEncrypt_OnSerializeFalse_ReturnsNullWithSerializationError` | `OnSerialize` false; reason in return   | Unit                  |
| `Send_DelegatesToTransport`                                     | Mock transport delegation                               | Unit                  |
| `DisconnectAsync_DelegatesToConnection`                         | Mock connection                                         | Unit                  |
| `Dispose_IsIdempotent`                                          | Idempotent dispose                                      | Unit                  |
| `Peer_DecryptAndDeserialize_*` (5)                              | Success, RegistryError, DecryptThrows, DeserializeFails, ThrowingEncryptor | Unit (mock encryptor) |
| `Peer_EncryptorIntegration_SerializeAndDecrypt`                 | XOR encryptor with mock                                 | Unit                  |
| `Peer_DecryptAndDeserialize_SegmentTooSmall_SerializationError` | Buffer validation                                       | Unit                  |
| `PeerClient_*` (6)                                              | Construction, TryConnect, events, dispose               | Unit (mock connector) |
| `PeerServer_*` (6)                                              | Construction, Start/Stop, events, dispose               | Unit (mock connector) |
| `PacketRegistry_ConcurrentRegistration_NoDuplicate`             | Thread-safe registration                                | Concurrency           |
| `Peer_ConcurrentSerializeAndDeserialize`                        | Thread-safe peer operations                             | Concurrency           |

***

### 🔌 Integration Tests (Real Sockets) - Run on Loopback

| Test                                                   | Target                             | Type        |
| ------------------------------------------------------ | ---------------------------------- | ----------- |
| `Peer_RealXorEncryptor_EndToEndEncryptDecrypt`         | Real XOR encryption + real sockets | Integration |
| `Peer_RealPassThroughEncryptor_EndToEndEncryptDecrypt` | PassThrough + real sockets         | Integration |
| `Peer_SerializeAndEncrypt_RealTransport_EndToEnd`      | Real Peer.SerializeAndEncrypt      | Integration |
| `Peer_RealXorEncryptor_MultiplePackets`                | 5 packets sequential               | Integration |
| `PeerClient_RealServer_EndToEnd`                       | Full PeerClient + PeerServer stack | Integration |
| `PeerClient_RealServer_FullStackHandshake`             | Full stack + XOR encryption        | Integration |

***

### 🧪 Encryption Tests (Always Run)

| Test                                            | Target               | Type |
| ----------------------------------------------- | -------------------- | ---- |
| `EncryptionTests.PassThroughEncryptorTests` (3) | PassThroughEncryptor | Unit |
| `EncryptionTests.XorEncryptorTests` (4)         | XorEncryptor         | Unit |

***

### ✅ PresentationHeader Tests (Done)

| Test                                             | Target                            | Type | Notes |
| ------------------------------------------------ | --------------------------------- | ---- | ----- |
| `PresentationHeader_Write_BufferTooSmall_Throws` | `Write` ArgumentException (lines 28-29) | Unit | Real static method; boundary case confirms exact-size does NOT throw; round-trips via Read |
| `PresentationHeader_Read_BufferTooSmall_Throws`  | `Read` ArgumentException (lines 42-43)  | Unit | Real static method; boundary case + round-trip via Write+Read |

***

### 📋 PLANNED (Not Yet Implemented)

_None._

***

## Run Commands

```Shell
# Run all tests
dotnet test tests/DunePresentation.Tests

# Run only unit tests (exclude integration)
dotnet test tests/DunePresentation.Tests --filter "FullyQualifiedName!~Real"

# Run only integration tests
dotnet test tests/DunePresentation.Tests --filter "FullyQualifiedName~Real"

# Run encryption tests only
dotnet test tests/DunePresentation.Tests --filter "FullyQualifiedName~Encryption"

# Run with coverage
dotnet test tests/DunePresentation.Tests --collect:"XPlat Code Coverage"
```

***

## Coverage Targets

| Metric          | Current | Target |
| --------------- | ------- | ------ |
| Line Coverage   | 98%     | ≥98%   |
| Branch Coverage | \~95%   | ≥95%   |

**Minor gaps:** PresentationHeader edge cases (buffer validation)
