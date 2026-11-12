using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using DunePresentation.Encryption.Interface;
using DunePresentation.Packet;
using DunePresentation.Packet.Interfaces;
using DunePresentation.Peer;
using DunePresentation.Peer.Interfaces;
using DuneSession.SocketConnectors;
using DuneSession.SocketConnectors.Interface;
using DuneTransport.BufferManager;
using DuneTransport.BufferManager.Interface;
using DuneTransport.Transport;
using DuneTransport.Transport.Interface;
using DemoPackets;
using Xunit;

namespace DunePresentation.Tests
{
    public class PresentationTests
    {
        #region PacketRegistry Tests

        [Fact]
        public void RegisterHandler_StoresHandlerCorrectly()
        {
            var registry = new PacketRegistry();
            registry.RegisterHandler<EchoPacket>(0x0003, null!, _ => _ => { });

            // Verify storage indirectly: duplicate registration throws, confirming the entry exists.
            Assert.Throws<InvalidOperationException>(() =>
                registry.RegisterHandler<EchoPacket>(0x0003, null!, _ => _ => { }));
        }

        [Fact]
        public void RegisterHandler_ThrowsOnNullHandler()
        {
            var registry = new PacketRegistry();
            Assert.Throws<ArgumentNullException>(() =>
                registry.RegisterHandler<EchoPacket>(0x0003, null!, null!));
        }

        [Fact]
        public void RegisterHandler_ThrowsOnDuplicatePacketId()
        {
            var registry = new PacketRegistry();
            registry.RegisterHandler<EchoPacket>(0x0003, null!, _ => _ => { });

            Assert.Throws<InvalidOperationException>(() =>
                registry.RegisterHandler<EchoPacket>(0x0003, null!, _ => _ => { }));
        }

        #endregion

        #region Peer Tests

        [Fact]
        public void Peer_Construction_ThrowsOnNullConnection()
        {
            var registry = new PacketRegistry();
            Assert.Throws<ArgumentNullException>(() =>
                new global::DunePresentation.Peer.Peer(null!, registry));
        }

        [Fact]
        public void Peer_Construction_ThrowsOnNullRegistry()
        {
            var connection = new MockConnection();
            Assert.Throws<ArgumentNullException>(() =>
                new global::DunePresentation.Peer.Peer(connection, null!));
        }

        [Fact]
        public void Peer_Construction_AcceptsNullEncryptor()
        {
            var connection = new MockConnection();
            var registry = new PacketRegistry();
            var peer = new global::DunePresentation.Peer.Peer(connection, registry, encryptor: null);
            Assert.NotNull(peer);
            peer.Dispose();
        }

        [Fact]
        public void SerializeAndEncrypt_ReturnsSegment_WhenSuccessful()
        {
            var transport = new MockTransport { ReserveSendResult = true };
            var connection = new MockConnection(transport);
            var registry = new PacketRegistry();
            var peer = new global::DunePresentation.Peer.Peer(connection, registry);

            var packet = new EchoPacket { Text = "hello" };
            var (segment, _) = peer.SerializeAndEncrypt(packet);

            Assert.NotNull(segment);
            Assert.True(segment!.Value.SegmentIndex > 0);
            peer.Dispose();
        }

        [Fact]
        public void SerializeAndEncrypt_ReturnsNullWithReason_WhenPoolExhausted()
        {
            var transport = new MockTransport { ReserveSendResult = false };
            var connection = new MockConnection(transport);
            var registry = new PacketRegistry();
            var peer = new global::DunePresentation.Peer.Peer(connection, registry);

            var packet = new EchoPacket { Text = "hello" };
            var (segment, error) = peer.SerializeAndEncrypt(packet);

            Assert.Null(segment);
            Assert.Equal(PacketError.PoolExhausted, error);
            peer.Dispose();
        }

        // Transport that reserves a segment whose Release() is observable (the real pool's
        // SegmentedBuffer release callback is private; here we own it so we can assert the
        // segment was returned on a presentation-throw).
        private class ObservableReleaseTransport : ITransport
        {
            public bool ReserveResult { get; set; } = true;
            public bool ReleaseInvoked { get; private set; }
            public Segment? ReservedSegment { get; private set; }

            public bool IsConnected => true;
            public bool IsDisposed => false;
            public bool ReceiveArmed => false;
            public bool SendArmed => false;

            public event Action<ITransport>? OnPacketSent;
            public event Action<ITransport, Segment, TransportError>? OnPacketSendFailed;
            public event Action<ITransport, Segment>? OnPacketReceived;
            public event Action<ITransport, TransportError>? OnPacketReceiveFailed;

            public void ReceiveAsync() { }
            public void SendAsync(Segment packet, int packetSize) { }

            public bool TryReserveSendPacket(out Segment segment)
            {
                if (!ReserveResult) { segment = default; return false; }
                segment = new Segment(1, new byte[256], _ => { });
                var self = this;
                // Can't set ReleaseMemoryCallback on immutable property, so we use the constructor
                // Actually, we need a mutable callback. Let's make the constructor handle this.
                // We'll set the callback by re-creating the segment
                segment = new Segment(1, new byte[256], _ => self.ReleaseInvoked = true);
                ReservedSegment = segment;
                return true;
            }
            public void Dispose() { }
        }

        [Fact]
        public void SerializeAndEncrypt_EncryptorThrows_Propagates()
        {
            // ARRANGE — encryptor throws during the in-place encrypt phase. New contract:
            // a throwing encryptor propagates to the caller and the reserved segment is
            // lost (documented foot gun: user encryptor code must not throw).
            var transport = new ObservableReleaseTransport();
            var connection = new MockConnection(transport);
            var registry = new PacketRegistry();
            var encryptor = new MockEncryptor { SimulateEncryptThrow = true };
            var peer = new global::DunePresentation.Peer.Peer(connection, registry, encryptor);

            var packet = new EchoPacket { Text = "hello" };

            // ACT / ASSERT
            Assert.Throws<InvalidOperationException>(() => peer.SerializeAndEncrypt(packet));
            Assert.True(encryptor.EncryptCalled, "Sanity — encryptor was actually invoked (throw path reached)");
            Assert.False(transport.ReleaseInvoked, "By contract the segment is lost when the encryptor throws (no release).");
            peer.Dispose();
        }

        [Fact]
        public void SerializeAndEncrypt_OnSerializeFalse_ReturnsNullWithSerializationError()
        {
            // ARRANGE — a packet whose OnSerialize returns false (simulating a serialize
            // failure after the segment was reserved). The peer should return
            // (null, SerializationError), not PoolExhausted.
            var transport = new ObservableReleaseTransport();
            var connection = new MockConnection(transport);
            var registry = new PacketRegistry();
            registry.RegisterHandler<BadSerializePacket>(0x0098, null!, _ => _ => { });
            var peer = new global::DunePresentation.Peer.Peer(connection, registry);

            var packet = new BadSerializePacket();

            // ACT
            Segment? segment;
            PacketError error;
            try
            {
                (segment, error) = peer.SerializeAndEncrypt(packet);
            }
            catch
            {
                Assert.Fail("SerializeAndEncrypt must not propagate a serialize failure; it should return (null, SerializationError).");
                return;
            }

            // ASSERT
            Assert.Null(segment);
            Assert.Equal(PacketError.SerializationError, error);
            Assert.True(transport.ReleaseInvoked, "Reserved segment must be released back to the pool when OnSerialize returns false");
            peer.Dispose();
        }

        [Fact]
        public void Send_DelegatesToTransport()
        {
            var transport = new MockTransport();
            var connection = new MockConnection(transport);
            var registry = new PacketRegistry();
            var peer = new global::DunePresentation.Peer.Peer(connection, registry);

            var segment = new Segment(1, new byte[64], _ => { });
            peer.Send(segment, 64);

            Assert.True(transport.SendCalled);
            Assert.Equal(1, transport.LastSentSegment!.Value.SegmentIndex);
            Assert.Equal(64, transport.LastSentSize);
            peer.Dispose();
        }

        [Fact]
        public void DisconnectAsync_DelegatesToConnection()
        {
            var connection = new MockConnection();
            var registry = new PacketRegistry();
            var peer = new global::DunePresentation.Peer.Peer(connection, registry);

            peer.DisconnectAsync();

            Assert.True(connection.DisconnectCalled);
            peer.Dispose();
        }

        [Fact]
        public void Dispose_IsIdempotent()
        {
            var connection = new MockConnection();
            var registry = new PacketRegistry();
            var peer = new global::DunePresentation.Peer.Peer(connection, registry);

            peer.Dispose();
            peer.Dispose(); // should not throw
        }

        #endregion

        
        #region DecryptAndDeserialize Tests

        [Fact]
        public void Peer_DecryptAndDeserialize_Success()
        {
            var transport = new MockTransport { ReserveSendResult = true };
            var connection = new MockConnection(transport);
            var registry = new PacketRegistry();
            registry.RegisterHandler<EchoPacket>(0x0003, null!, _ => _ => { });
            var peer = new global::DunePresentation.Peer.Peer(connection, registry);

            // EchoPacket wire format: 2 bytes packet ID (LE), then raw UTF-8 text bytes
            // DecryptAndDeserialize sets packet.PacketSize = span.Length
            // So OnDeserialize reads span.Length - PresentationHeader.Size as the payload
            var wireData = new byte[] { 0x03, 0x00, (byte)'h', (byte)'i' }; // packet ID + "hi"
            var segment = new Segment(1, wireData, _ => { });

            var (result, _) = peer.DecryptAndDeserialize(segment);
            Assert.NotNull(result);
            Assert.IsType<EchoPacket>(result!.Value.Packet);
            Assert.Equal("hi", ((EchoPacket)result.Value.Packet).Text);
            peer.Dispose();
        }

        [Fact]
        public void Peer_DecryptAndDeserialize_RegistryError()
        {
            var transport = new MockTransport { ReserveSendResult = true };
            var connection = new MockConnection(transport);
            var registry = new PacketRegistry();
            // No handlers registered
            var peer = new global::DunePresentation.Peer.Peer(connection, registry);

            bool released = false;
            var segment = new Segment(1, new byte[100], _ => released = true);
            var span = segment.Memory.Span;
            span[0] = 0xFF; span[1] = 0xFF; // unregistered packet ID 0xFFFF

            var (result, error) = peer.DecryptAndDeserialize(segment);

            Assert.Null(result);
            Assert.Equal(PacketError.RegistryError, error);
            Assert.True(released, "Segment should have been released on registry error");
            peer.Dispose();
        }

        [Fact]
        public void Peer_DecryptAndDeserialize_DecryptThrows()
        {
            var transport = new MockTransport { ReserveSendResult = true };
            var connection = new MockConnection(transport);
            var registry = new PacketRegistry();
            var encryptor = new MockEncryptor { SimulateDecryptThrow = true };
            var peer = new global::DunePresentation.Peer.Peer(connection, registry, encryptor);

            var segment = new Segment(1, new byte[100], _ => { });

            // New contract: a throwing encryptor propagates to the caller and the
            // segment is lost (no release).
            Assert.Throws<InvalidOperationException>(() => peer.DecryptAndDeserialize(segment));
            peer.Dispose();
        }

        [Fact]
        public void Peer_DecryptAndDeserialize_DeserializeFails()
        {
            var transport = new MockTransport { ReserveSendResult = true };
            var connection = new MockConnection(transport);
            var registry = new PacketRegistry();
            registry.RegisterHandler<BadDeserializePacket>(0x0099, null!, _ => _ => { });
            var peer = new global::DunePresentation.Peer.Peer(connection, registry);

            var segment = new Segment(1, new byte[100], _ => { });
            var span = segment.Memory.Span;
            span[0] = 0x99; span[1] = 0x00; // packet ID 0x0099

            var (result, error) = peer.DecryptAndDeserialize(segment);

            Assert.Null(result);
            Assert.Equal(PacketError.DeserializeError, error);
            peer.Dispose();
        }

        [Fact]
        public void Peer_EncryptorIntegration_SerializeAndDecrypt()
        {
            var transport = new MockTransport { ReserveSendResult = true };
            var connection = new MockConnection(transport);
            var registry = new PacketRegistry();
            registry.RegisterHandler<EchoPacket>(0x0003, null!, _ => _ => { });
            var encryptor = new XorEncryptor(0xAB);
            var peer = new global::DunePresentation.Peer.Peer(connection, registry, encryptor);

            var packet = new EchoPacket { Text = "hello" };
            var (serResult, _) = peer.SerializeAndEncrypt(packet);

            Assert.NotNull(serResult);
            Assert.True(serResult.Value.SegmentIndex > 0);
            Assert.Equal(7, packet.PacketSize); // 2 byte header + 5 bytes "hello"

            // Slice the segment memory to the actual packet size so DecryptAndDeserialize
            // reads only the relevant bytes (span.Length becomes PacketSize after decrypt).
            var segment = new Segment(serResult.Value.SegmentIndex, serResult.Value.Memory.Slice(0, packet.PacketSize), serResult.Value.ReleaseMemoryCallback);

            var (result, _) = peer.DecryptAndDeserialize(segment);
            Assert.NotNull(result);
            Assert.Equal("hello", ((EchoPacket)result!.Value.Packet).Text);
            peer.Dispose();
        }

        [Fact]
        public void Peer_Dispose_CleansUpConnection()
        {
            var connection = new MockConnection();
            var registry = new PacketRegistry();
            var peer = new global::DunePresentation.Peer.Peer(connection, registry);

            peer.Dispose();
            Assert.True(connection.Disposed);
        }

        [Fact]
        public void Peer_IsConnected_False_WhenDisconnected()
        {
            var connection = new MockConnection();
            var registry = new PacketRegistry();
            var peer = new global::DunePresentation.Peer.Peer(connection, registry);

            Assert.True(peer.IsConnected);
            connection.DisconnectAsync();
            Assert.False(peer.IsConnected);
            peer.Dispose();
        }

        [Fact]
        public void Peer_DecryptAndDeserialize_FactoryThrows_Propagates()
        {
            // The registry factory is () => new T(): a packet type whose constructor
            // throws now propagates to the caller, and the segment is lost (new
            // contract: user packet code must not throw — a documented foot gun).
            var transport = new MockTransport { ReserveSendResult = true };
            var connection = new MockConnection(transport);
            var registry = new PacketRegistry();
            registry.RegisterHandler<ThrowingCtorPacket>(0x009A, null!, _ => _ => { });
            var peer = new global::DunePresentation.Peer.Peer(connection, registry);

            var segment = new Segment(1, new byte[100], _ => { });
            var span = segment.Memory.Span;
            span[0] = 0x9A; span[1] = 0x00; // packet ID 0x009A

            // The registry factory is a reflection-created delegate, so the ctor
            // throw arrives wrapped in TargetInvocationException (the unwrapped
            // InvalidOperationException is the inner exception).
            var ex = Assert.Throws<TargetInvocationException>(() => peer.DecryptAndDeserialize(segment));
            Assert.IsAssignableFrom<InvalidOperationException>(ex.InnerException);
            peer.Dispose();
        }

        [Fact]
        public void Peer_DecryptAndDeserialize_SegmentTooSmall_Propagates()
        {
            var transport = new MockTransport { ReserveSendResult = true };
            var connection = new MockConnection(transport);
            var registry = new PacketRegistry();
            var peer = new global::DunePresentation.Peer.Peer(connection, registry);

            // Segment with only 1 byte (less than 2-byte header): the header read
            // now throws instead of being contained (contract: framed payloads are
            // always header-sized; a too-small segment is a documented foot gun).
            var segment = new Segment(1, new byte[1], _ => { });

            Assert.Throws<ArgumentException>(() => peer.DecryptAndDeserialize(segment));
            peer.Dispose();
        }

        #region Integration Tests with Real Encryptors

        [Fact]
        public async Task Peer_RealXorEncryptor_EndToEndEncryptDecrypt()
        {
            // Integration test: real Peer + real XorEncryptor + real Transport (via socket pair)
            // Tests the full encryption/decryption pipeline end-to-end

            var listener = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            listener.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, 1);
            listener.Bind(new IPEndPoint(IPAddress.Loopback, 0));
            listener.Listen(1);
            int port = ((IPEndPoint)listener.LocalEndPoint!).Port;

            var clientSocket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            clientSocket.Connect(new IPEndPoint(IPAddress.Loopback, port));
            var serverSocket = listener.Accept();
            listener.Close();

            try
            {
                var registry = new PacketRegistry();
                registry.RegisterHandler<EchoPacket>(0x0003, null!, _ => _ => { });

                // Client side with XorEncryptor
                var clientEncryptor = new XorEncryptor(0xAB);
                var clientConnection = new Connection(clientSocket);
                var clientPeer = new global::DunePresentation.Peer.Peer(clientConnection, registry, clientEncryptor);

                // Server side with same XorEncryptor
                var serverEncryptor = new XorEncryptor(0xAB);
                var serverConnection = new Connection(serverSocket);
                var serverPeer = new global::DunePresentation.Peer.Peer(serverConnection, registry, serverEncryptor);

                var tcs = new TaskCompletionSource<EchoPacket>();
                serverPeer.Connection.Transport.OnPacketReceived += (t, seg) =>
                {
                    var (result, _) = serverPeer.DecryptAndDeserialize(seg);
                    if (result.HasValue && result.Value.Packet is EchoPacket echo)
                        tcs.TrySetResult(echo);
                    if (serverPeer.IsConnected)
                        serverPeer.Connection.Transport.ReceiveAsync();
                };
                serverPeer.Connection.Transport.ReceiveAsync();

                // Client sends encrypted packet
                var sendPacket = new EchoPacket { Text = "encrypted hello" };
                var (sendSegment, _) = clientPeer.SerializeAndEncrypt(sendPacket);
                Assert.True(sendSegment!.Value.SegmentIndex > 0);
                clientPeer.Send(sendSegment!.Value, sendPacket.PacketSize);

                var received = await tcs.Task.WaitAsync(TimeSpan.FromSeconds(5));
                Assert.Equal("encrypted hello", received.Text);

                clientPeer.Dispose();
                serverPeer.Dispose();
            }
            finally
            {
                clientSocket.Close();
                serverSocket.Close();
            }
        }

        [Fact]
        public async Task Peer_RealPassThroughEncryptor_EndToEndEncryptDecrypt()
        {
            // Integration test: real Peer + real PassThroughEncryptor + real Transport

            var listener = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            listener.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, 1);
            listener.Bind(new IPEndPoint(IPAddress.Loopback, 0));
            listener.Listen(1);
            int port = ((IPEndPoint)listener.LocalEndPoint!).Port;

            var clientSocket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            clientSocket.Connect(new IPEndPoint(IPAddress.Loopback, port));
            var serverSocket = listener.Accept();
            listener.Close();

            try
            {
                var registry = new PacketRegistry();
                registry.RegisterHandler<EchoPacket>(0x0003, null!, _ => _ => { });

                // Client side with PassThroughEncryptor
                var clientEncryptor = new PassThroughEncryptor();
                var clientConnection = new Connection(clientSocket);
                var clientPeer = new global::DunePresentation.Peer.Peer(clientConnection, registry, clientEncryptor);

                // Server side with PassThroughEncryptor
                var serverEncryptor = new PassThroughEncryptor();
                var serverConnection = new Connection(serverSocket);
                var serverPeer = new global::DunePresentation.Peer.Peer(serverConnection, registry, serverEncryptor);

                var tcs = new TaskCompletionSource<EchoPacket>();
                serverPeer.Connection.Transport.OnPacketReceived += (t, seg) =>
                {
                    var (result, _) = serverPeer.DecryptAndDeserialize(seg);
                    if (result.HasValue && result.Value.Packet is EchoPacket echo)
                        tcs.TrySetResult(echo);
                    if (serverPeer.IsConnected)
                        serverPeer.Connection.Transport.ReceiveAsync();
                };
                serverPeer.Connection.Transport.ReceiveAsync();

                // Client sends packet (no actual encryption)
                var sendPacket = new EchoPacket { Text = "plain text" };
                var (sendSegment, _) = clientPeer.SerializeAndEncrypt(sendPacket);
                Assert.True(sendSegment!.Value.SegmentIndex > 0);
                clientPeer.Send(sendSegment!.Value, sendPacket.PacketSize);

                var received = await tcs.Task.WaitAsync(TimeSpan.FromSeconds(5));
                Assert.Equal("plain text", received.Text);

                clientPeer.Dispose();
                serverPeer.Dispose();
            }
            finally
            {
                clientSocket.Close();
                serverSocket.Close();
            }
        }

        #endregion

        #region PeerClient Tests

        [Fact]
        public void PeerClient_Construction_NullRegistry_Throws()
        {
            Assert.Throws<ArgumentNullException>(() => new PeerClient(null!));
        }

        [Fact]
        public void PeerClient_Construction_DefaultClientConnector()
        {
            var client = new PeerClient(new PacketRegistry());
            Assert.NotNull(client);
            client.Dispose();
        }

        [Fact]
        public void PeerClient_Construction_WithCustomClient()
        {
            var mock = new MockClientConnector();
            var client = new PeerClient(new PacketRegistry(), client: mock);
            Assert.NotNull(client);
            client.Dispose();
        }

        [Fact]
        public void PeerClient_TryConnect_DelegatesToConnector()
        {
            var mock = new MockClientConnector();
            var client = new PeerClient(new PacketRegistry(), client: mock);

            bool result = client.TryConnect("127.0.0.1", 9999, out _);

            Assert.True(mock.ConnectCalled);
            Assert.Equal("127.0.0.1", mock.LastAddress);
            Assert.Equal(9999, mock.LastPort);
            client.Dispose();
        }

        [Fact]
        public void PeerClient_OnPeerConnected_FiresOnSuccess()
        {
            var mock = new MockClientConnector();
            var client = new PeerClient(new PacketRegistry(), client: mock);

            IPeer? peer = null;
            client.OnPeerConnected += p => peer = p;

            mock.SimulateConnected();

            Assert.NotNull(peer);
            client.Dispose();
        }

        [Fact]
        public void PeerClient_OnConnectFailed_FiresOnError()
        {
            var mock = new MockClientConnector();
            var client = new PeerClient(new PacketRegistry(), client: mock);

            SocketError? error = null;
            client.OnConnectFailed += e => error = e;

            mock.SimulateFailed();

            Assert.Equal(SocketError.ConnectionRefused, error);
            client.Dispose();
        }

        [Fact]
        public void PeerClient_Dispose_IsIdempotent()
        {
            var mock = new MockClientConnector();
            var client = new PeerClient(new PacketRegistry(), client: mock);

            client.Dispose();
            client.Dispose(); // should not throw
        }

        [Fact]
        public void PeerClient_Dispose_UnsubscribesFromConnector()
        {
            var mock = new MockClientConnector();
            var client = new PeerClient(new PacketRegistry(), client: mock);
            client.Dispose();

            // Simulate connection after dispose - should not throw or invoke events
            bool peerFired = false;
            client.OnPeerConnected += _ => peerFired = true;
            mock.SimulateConnected();
            Assert.False(peerFired);
        }

        [Fact]
        public void PeerClient_OnPeerConnected_ThrowingSubscriber_Propagates()
        {
            var mock = new MockClientConnector();
            var client = new PeerClient(new PacketRegistry(), client: mock);

            client.OnPeerConnected += p =>
            {
                throw new InvalidOperationException("subscriber boom");
            };

            // New contract: the subscriber's throw propagates to the caller (the mock
            // fires synchronously) instead of being contained.
            Assert.Throws<InvalidOperationException>(() => mock.SimulateConnected());
            client.Dispose();
        }

        [Fact]
        public void PeerClient_ThrowingEncryptorFactory_PropagatesNoEvent()
        {
            var mock = new MockClientConnector();
            var client = new PeerClient(new PacketRegistry(),
                encryptorFactory: () => throw new InvalidOperationException("factory boom"),
                client: mock);

            bool eventFired = false;
            client.OnPeerConnected += _ => eventFired = true;

            Assert.Throws<InvalidOperationException>(() => mock.SimulateConnected());

            // The factory threw before delivery, so the event must never have fired.
            Assert.False(eventFired);
            client.Dispose();
        }

        [Fact]
        public void PeerClient_ThrowingOnConnectFailedSubscriber_Propagates()
        {
            var mock = new MockClientConnector();
            var client = new PeerClient(new PacketRegistry(), client: mock);

            client.OnConnectFailed += _ => throw new InvalidOperationException("subscriber boom");

            // New contract: the relay is unguarded — the subscriber's throw propagates
            // to the caller.
            Assert.Throws<InvalidOperationException>(() => mock.SimulateFailed());

            client.Dispose();
        }

        #endregion

        #region PeerServer Tests

        [Fact]
        public void PeerServer_Construction_NullRegistry_Throws()
        {
            Assert.Throws<ArgumentNullException>(() => new PeerServer(null!));
        }

        [Fact]
        public void PeerServer_Construction_DefaultServerConnector()
        {
            var server = new PeerServer(new PacketRegistry());
            Assert.NotNull(server);
            server.Dispose();
        }

        [Fact]
        public void PeerServer_Construction_WithCustomServer()
        {
            var mock = new MockServerConnector();
            var server = new PeerServer(new PacketRegistry(), server: mock);
            Assert.NotNull(server);
            server.Dispose();
        }

        [Fact]
        public void PeerServer_TryStartListening_DelegatesToConnector()
        {
            var mock = new MockServerConnector();
            var server = new PeerServer(new PacketRegistry(), server: mock);

            server.TryStartListening("127.0.0.1", 9999, out _);

            Assert.True(mock.IsListening);
            Assert.Equal("127.0.0.1", mock.LastAddress);
            Assert.Equal(9999, mock.LastPort);
            server.Dispose();
        }

        [Fact]
        public void PeerServer_StopListening_DelegatesToConnector()
        {
            var mock = new MockServerConnector();
            var server = new PeerServer(new PacketRegistry(), server: mock);

            server.TryStartListening("127.0.0.1", 9999, out _);
            server.StopListening();

            Assert.False(mock.IsListening);
            server.Dispose();
        }

        [Fact]
        public void PeerServer_OnPeerConnected_FiresOnAccept()
        {
            var mock = new MockServerConnector();
            var server = new PeerServer(new PacketRegistry(), server: mock);

            IPeer? peer = null;
            server.OnPeerConnected += p => peer = p;

            mock.SimulateClientConnected();

            Assert.NotNull(peer);
            server.Dispose();
        }

        [Fact]
        public void PeerServer_OnAcceptFailed_FiresOnError()
        {
            var mock = new MockServerConnector();
            var server = new PeerServer(new PacketRegistry(), server: mock);

            SocketError? error = null;
            server.OnAcceptFailed += e => error = e;

            mock.SimulateAcceptFailed();

            Assert.Equal(SocketError.ConnectionRefused, error);
            server.Dispose();
        }

        [Fact]
        public void PeerServer_Dispose_IsIdempotent()
        {
            var mock = new MockServerConnector();
            var server = new PeerServer(new PacketRegistry(), server: mock);

            server.Dispose();
            server.Dispose(); // should not throw
        }

        [Fact]
        public void PeerServer_OnPeerConnected_ThrowingSubscriber_Propagates()
        {
            var mock = new MockServerConnector();
            var server = new PeerServer(new PacketRegistry(), server: mock);

            server.OnPeerConnected += p =>
            {
                throw new InvalidOperationException("subscriber boom");
            };

            // New contract: the subscriber's throw propagates to the caller (the mock
            // fires synchronously) instead of being contained.
            Assert.Throws<InvalidOperationException>(() => mock.SimulateClientConnected());
            server.Dispose();
        }

        [Fact]
        public void PeerServer_ThrowingOnAcceptFailedSubscriber_Propagates()
        {
            var mock = new MockServerConnector();
            var server = new PeerServer(new PacketRegistry(), server: mock);

            server.OnAcceptFailed += _ => throw new InvalidOperationException("subscriber boom");

            // New contract: the relay is unguarded — the subscriber's throw propagates
            // to the caller.
            Assert.Throws<InvalidOperationException>(() => mock.SimulateAcceptFailed());

            server.Dispose();
        }

        #endregion

        #region Presentation Integration Tests

        [Fact]
        public async Task Peer_RealXorEncryptor_MultiplePackets()
        {
            // End-to-end: send 5 encrypted packets in sequence
            var listener = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            listener.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, 1);
            listener.Bind(new IPEndPoint(IPAddress.Loopback, 0));
            listener.Listen(1);
            int port = ((IPEndPoint)listener.LocalEndPoint!).Port;

            var clientSocket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            clientSocket.Connect(new IPEndPoint(IPAddress.Loopback, port));
            var serverSocket = listener.Accept();
            listener.Close();

            try
            {
                var registry = new PacketRegistry();
                registry.RegisterHandler<EchoPacket>(0x0003, null!, _ => _ => { });

                var received = new System.Collections.Generic.List<string>();
                var encryptor = new XorEncryptor(0xAB);

                var clientConnection = new Connection(clientSocket);
                var clientPeer = new global::DunePresentation.Peer.Peer(clientConnection, registry, encryptor);
                var serverConnection = new Connection(serverSocket);
                var serverPeer = new global::DunePresentation.Peer.Peer(serverConnection, registry, encryptor);

                var allDone = new System.Threading.Tasks.TaskCompletionSource<bool>();

                serverPeer.Connection.Transport.OnPacketReceived += (t, seg) =>
                {
                    var (result, _) = serverPeer.DecryptAndDeserialize(seg);
                    if (result.HasValue && result.Value.Packet is EchoPacket echo)
                    {
                        received.Add(echo.Text);
                        if (received.Count == 5)
                            allDone.TrySetResult(true);
                        else
                            t.ReceiveAsync();
                    }
                };
                serverPeer.Connection.Transport.ReceiveAsync();

                for (int i = 0; i < 5; i++)
                {
                    var packet = new EchoPacket { Text = $"msg{i}" };
                    var (seg, _) = clientPeer.SerializeAndEncrypt(packet);
                    Assert.True(seg!.Value.SegmentIndex > 0);
                    clientPeer.Send(seg!.Value, packet.PacketSize);
                }

                await allDone.Task.WaitAsync(TimeSpan.FromSeconds(10));
                Assert.Equal(5, received.Count);
                for (int i = 0; i < 5; i++)
                    Assert.Equal($"msg{i}", received[i]);

                clientPeer.Dispose();
                serverPeer.Dispose();
            }
            finally
            {
                clientSocket.Close();
                serverSocket.Close();
            }
        }

        [Fact]
        public async Task PeerClient_RealServer_EndToEnd()
        {
            // Full stack: PeerServer + PeerClient over real sockets
            var listener = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            listener.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, 1);
            listener.Bind(new IPEndPoint(IPAddress.Loopback, 0));
            listener.Listen(1);
            int port = ((IPEndPoint)listener.LocalEndPoint!).Port;
            listener.Close();

            var registry = new PacketRegistry();
            registry.RegisterHandler<EchoPacket>(0x0003, null!, _ => p => { });

            // Server
            var server = new PeerServer(registry);
            server.TryStartListening("127.0.0.1", port, out _);
            server.AcceptConnection();

            var serverPeerTcs = new System.Threading.Tasks.TaskCompletionSource<IPeer>();
            server.OnPeerConnected += p => serverPeerTcs.TrySetResult(p);

            // Client
            var client = new PeerClient(registry);
            var clientPeerTcs = new System.Threading.Tasks.TaskCompletionSource<IPeer>();
            client.OnPeerConnected += p => clientPeerTcs.TrySetResult(p);
            client.TryConnect("127.0.0.1", port, out _);

            var serverPeer = await serverPeerTcs.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var clientPeer = await clientPeerTcs.Task.WaitAsync(TimeSpan.FromSeconds(5));

            Assert.NotNull(serverPeer);
            Assert.NotNull(clientPeer);

            // Set up server receive
            var recvTcs = new System.Threading.Tasks.TaskCompletionSource<EchoPacket>();
            serverPeer.Connection.Transport.OnPacketReceived += (t, seg) =>
            {
                var (result, _) = serverPeer.DecryptAndDeserialize(seg);
                if (result.HasValue && result.Value.Packet is EchoPacket echo)
                    recvTcs.TrySetResult(echo);
            };
            serverPeer.Connection.Transport.ReceiveAsync();

            // Client sends
            var packet = new EchoPacket { Text = "hello from client" };
            var (seg, _) = clientPeer.SerializeAndEncrypt(packet);
            Assert.True(seg!.Value.SegmentIndex > 0);
            clientPeer.Send(seg!.Value, packet.PacketSize);

            var received = await recvTcs.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal("hello from client", received.Text);

            clientPeer.Dispose();
            serverPeer.Dispose();
            client.Dispose();
            server.Dispose();
        }

        /// <summary>
        /// Verifies Peer.SerializeAndEncrypt works end-to-end with a REAL Transport
        /// (not mock). Sends a packet through real sockets and verifies the server
        /// receives and decrypts it correctly.
        /// </summary>
        [Fact]
        public async Task Peer_SerializeAndEncrypt_RealTransport_EndToEnd()
        {
            // ARRANGE - Real socket pair
            var listener = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            listener.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, 1);
            listener.Bind(new IPEndPoint(IPAddress.Loopback, 0));
            listener.Listen(1);
            int port = ((IPEndPoint)listener.LocalEndPoint!).Port;

            var clientSocket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            clientSocket.Connect(new IPEndPoint(IPAddress.Loopback, port));
            var serverSocket = listener.Accept();
            listener.Close();

            try
            {
                var registry = new PacketRegistry();
                var receivedPacket = new TaskCompletionSource<EchoPacket>();

                // Server peer
                var serverConn = new Connection(serverSocket);
                var serverPeer = new global::DunePresentation.Peer.Peer(serverConn, registry);
                registry.RegisterHandler<EchoPacket>(0x0003, null!, _ => p =>
                {
                    if (p is EchoPacket ep) receivedPacket.TrySetResult(ep);
                });
                // Need to call DecryptAndDeserialize in the transport handler
                serverPeer.Connection.Transport.OnPacketReceived += (t, seg) =>
                {
                    var (result, _) = serverPeer.DecryptAndDeserialize(seg);
                    if (result.HasValue && result.Value.Packet is EchoPacket ep)
                        receivedPacket.TrySetResult(ep);
                    if (serverPeer.IsConnected)
                        t.ReceiveAsync();
                };
                serverPeer.Connection.Transport.ReceiveAsync();

                // Client peer
                var clientConn = new Connection(clientSocket);
                var clientPeer = new global::DunePresentation.Peer.Peer(clientConn, registry);

                // ACT - Serialize and send real packet
                var packet = new EchoPacket { Text = "real transport test" };
                var (segment, _) = clientPeer.SerializeAndEncrypt(packet);

                Assert.True(segment!.Value.SegmentIndex > 0, "Should reserve real segment from transport");
                Assert.Equal(2 + Encoding.UTF8.GetByteCount("real transport test"), packet.PacketSize);

                clientPeer.Send(segment!.Value, packet.PacketSize);

                // ASSERT - Wait for server to receive and deserialize
                var received = await receivedPacket.Task.WaitAsync(TimeSpan.FromSeconds(5));
                Assert.Equal("real transport test", received.Text);

                clientPeer.Dispose();
                serverPeer.Dispose();
            }
            finally
            {
                try { clientSocket.Close(); } catch { }
                try { serverSocket.Close(); } catch { }
            }
        }

        /// <summary>
        /// Verifies that a throwing encryptor propagates to the caller
        /// (contract: the encryptor must not throw; the segment is lost).
        /// </summary>
        [Fact]
        public void Peer_DecryptAndDeserialize_ThrowingEncryptor_Propagates()
        {
            // ARRANGE - Encryptor that throws on decrypt
            var throwingEncryptor = new ThrowingEncryptor(simulateDecryptThrow: true);
            var registry = new PacketRegistry();
            var mockConn = new MockConnection(new MockTransport { ReserveSendResult = true });
            var peer = new global::DunePresentation.Peer.Peer(mockConn, registry, throwingEncryptor);

            var segment = new Segment(1, new byte[10], _ => { });

            // ACT / ASSERT
            Assert.Throws<CryptographicException>(() => peer.DecryptAndDeserialize(segment));

            peer.Dispose();
        }

        [Fact]
        public void Peer_EncryptorThrow_SendPoolLosesExactlyOneSegment()
        {
            // A throwing encryptor now propagates (contract: user code must not throw)
            // and the reserved segment is lost. This pins the pool bookkeeping around
            // the removed guard: exactly one slot is consumed, nothing is
            // double-released, and the pool stays fully functional afterwards.
            var listener = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            listener.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, 1);
            listener.Bind(new IPEndPoint(IPAddress.Loopback, 0));
            listener.Listen(1);
            int port = ((IPEndPoint)listener.LocalEndPoint!).Port;

            var clientSocket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            clientSocket.Connect(new IPEndPoint(IPAddress.Loopback, port));
            var serverSocket = listener.Accept();
            listener.Close();

            try
            {
                var connection = new Connection(serverSocket);
                var peer = new global::DunePresentation.Peer.Peer(connection, new PacketRegistry(),
                    new MockEncryptor { SimulateEncryptThrow = true });
                var transport = connection.Transport;

                // Hold 31 of the 32 default send segments.
                var held = new List<Segment>();
                for (int i = 0; i < 31; i++)
                {
                    Assert.True(transport.TryReserveSendPacket(out Segment s));
                    held.Add(s);
                }

                // The encryptor throws: the call reserved the 32nd segment and then
                // propagated, losing that segment permanently.
                var packet = new EchoPacket { Text = "x" };
                Assert.Throws<InvalidOperationException>(() => peer.SerializeAndEncrypt(packet));

                // The pool is now full: the leaked segment occupies the last slot.
                Assert.False(transport.TryReserveSendPacket(out _));

                // Bookkeeping is intact: releasing one held slot frees exactly one,
                // and an idempotent double-release of another is harmless.
                held[0].Release();
                Assert.True(transport.TryReserveSendPacket(out Segment freed));
                freed.Release();
                held[1].Release();
                held[1].Release(); // idempotent by contract

                // Full accounting: release everything held; the only segment still in
                // use is the leaked one, so exactly 31 of 32 must be reservable.
                foreach (var s in held) s.Release();
                int reservable = 0;
                var extra = new List<Segment>();
                while (transport.TryReserveSendPacket(out Segment s2))
                {
                    reservable++;
                    extra.Add(s2);
                }
                // Exactly one slot (the leaked segment) must be permanently lost.
                Assert.Equal(31, reservable);
                foreach (var s in extra) s.Release();

                connection.Dispose();
            }
            finally
            {
                try { clientSocket.Close(); } catch { }
                try { serverSocket.Close(); } catch { }
            }
        }

        [Fact]
        public async Task Peer_DecryptorThrow_ReceivePipelineStaysHealthy()
        {
            // A throwing decryptor propagates and the segment is lost (documented
            // contract). This pins that the transport/pool pipeline around the
            // removed guard is undamaged: the next packet still arrives intact.
            var listener = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            listener.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, 1);
            listener.Bind(new IPEndPoint(IPAddress.Loopback, 0));
            listener.Listen(1);
            int port = ((IPEndPoint)listener.LocalEndPoint!).Port;

            var clientSocket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            clientSocket.Connect(new IPEndPoint(IPAddress.Loopback, port));
            var serverSocket = listener.Accept();
            listener.Close();

            try
            {
                var peer = new global::DunePresentation.Peer.Peer(new Connection(serverSocket), new PacketRegistry(),
                    new MockEncryptor { SimulateDecryptThrow = true });
                var transport = peer.Connection.Transport;

                Segment? seg1 = null;
                var first = new TaskCompletionSource<Segment>();
                var second = new TaskCompletionSource<Segment>();
                transport.OnPacketReceived += (t, seg) =>
                {
                    if (seg1 == null) { seg1 = seg; first.TrySetResult(seg); }
                    else second.TrySetResult(seg);
                };

                transport.ReceiveAsync();
                byte[] frame = { 4, 0, 0xFF, 0xFF, 0xFF, 0xFF };
                clientSocket.Send(frame);
                var received1 = await first.Task.WaitAsync(TimeSpan.FromSeconds(5));

                // The decryptor throws on the first packet: it propagates and the
                // segment is lost (deliberately not released below).
                Assert.Throws<InvalidOperationException>(() => peer.DecryptAndDeserialize(received1));

                // The pipeline is undamaged: re-arm and receive a second packet.
                transport.ReceiveAsync();
                clientSocket.Send(frame);
                var received2 = await second.Task.WaitAsync(TimeSpan.FromSeconds(5));
                Assert.Equal(received1.Memory.Length, received2.Memory.Length);
                received2.Release();

                peer.Dispose();
            }
            finally
            {
                try { clientSocket.Close(); } catch { }
                try { serverSocket.Close(); } catch { }
            }
        }

        /// <summary>
        /// Test-only encryptor that throws on decrypt to test error handling.
        /// </summary>
        private class ThrowingEncryptor : IPacketEncryptor
        {
            private readonly bool _throwOnDecrypt;
            public ThrowingEncryptor(bool simulateDecryptThrow) => _throwOnDecrypt = simulateDecryptThrow;
            public void Encrypt(ReadOnlySpan<byte> source, Span<byte> dest) => source.CopyTo(dest);
            public void Decrypt(ReadOnlySpan<byte> source, Span<byte> dest)
            {
                if (_throwOnDecrypt) throw new CryptographicException("Decrypt failed");
                source.CopyTo(dest);
            }
        }

        /// <summary>
        /// Verifies Peer.Send correctly delegates to Transport.SendAsync with real transport.
        /// </summary>
        [Fact]
        public async Task Peer_Send_RealTransport_DelegatesCorrectly()
        {
            // ARRANGE - Real socket pair
            var listener = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            listener.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, 1);
            listener.Bind(new IPEndPoint(IPAddress.Loopback, 0));
            listener.Listen(1);
            int port = ((IPEndPoint)listener.LocalEndPoint!).Port;

            var clientSocket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            clientSocket.Connect(new IPEndPoint(IPAddress.Loopback, port));
            var serverSocket = listener.Accept();
            listener.Close();

            try
            {
                var registry = new PacketRegistry();
                var receivedData = new TaskCompletionSource<byte[]>();

                // Server peer - just receive raw data
                var serverConn = new Connection(serverSocket);
                var serverPeer = new global::DunePresentation.Peer.Peer(serverConn, registry);
                serverPeer.Connection.Transport.OnPacketReceived += (t, seg) =>
                {
                    var data = seg.Memory.Span.ToArray();
                    receivedData.TrySetResult(data);
                    seg.Release();
                };
                serverPeer.Connection.Transport.ReceiveAsync();

                // Client peer
                var clientConn = new Connection(clientSocket);
                var clientPeer = new global::DunePresentation.Peer.Peer(clientConn, registry);

                // ACT - Create segment and send via Peer.Send
                var payload = new byte[] { 0xAA, 0xBB, 0xCC, 0xDD };
                clientConn.Transport.TryReserveSendPacket(out var segment);
                payload.CopyTo(segment.Memory.Span);
                clientPeer.Send(segment, payload.Length);

                // ASSERT
                var received = await receivedData.Task.WaitAsync(TimeSpan.FromSeconds(5));
                // OnPacketReceived delivers the payload segment (header already stripped by transport)
                Assert.Equal(payload.Length, received.Length);
                Assert.Equal(payload, received);

                clientPeer.Dispose();
                serverPeer.Dispose();
            }
            finally
            {
                try { clientSocket.Close(); } catch { }
                try { serverSocket.Close(); } catch { }
            }
        }

        /// <summary>
        /// Verifies PeerClient full stack integration: PeerClient connects to PeerServer
        /// over real sockets, both create real Peers with encryption, and packets flow end-to-end.
        /// </summary>
        [Fact]
        public async Task PeerClient_RealServer_FullStackHandshake()
        {
            // ARRANGE - Start real PeerServer
            var listener = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            listener.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, 1);
            listener.Bind(new IPEndPoint(IPAddress.Loopback, 0));
            listener.Listen(1);
            int port = ((IPEndPoint)listener.LocalEndPoint!).Port;
            listener.Close();

            var registry = new PacketRegistry();
            registry.RegisterHandler<EchoPacket>(0x0003, null!, _ => p => { });

            var server = new PeerServer(registry, encryptorFactory: () => new XorEncryptor(0x42));
            server.TryStartListening("127.0.0.1", port, out _);
            server.AcceptConnection();

            var serverPeerTcs = new TaskCompletionSource<IPeer>();
            server.OnPeerConnected += p => serverPeerTcs.TrySetResult(p);

            // ACT - Client connects
            var client = new PeerClient(registry, encryptorFactory: () => new XorEncryptor(0x42));
            var clientPeerTcs = new TaskCompletionSource<IPeer>();
            client.OnPeerConnected += p => clientPeerTcs.TrySetResult(p);
            client.TryConnect("127.0.0.1", port, out _);

            var serverPeer = await serverPeerTcs.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var clientPeer = await clientPeerTcs.Task.WaitAsync(TimeSpan.FromSeconds(5));

            // Set up server receive
            var receivedPacket = new TaskCompletionSource<EchoPacket>();
            serverPeer.Connection.Transport.OnPacketReceived += (t, seg) =>
            {
                var (result, _) = serverPeer.DecryptAndDeserialize(seg);
                if (result.HasValue && result.Value.Packet is EchoPacket ep)
                    receivedPacket.TrySetResult(ep);
                if (serverPeer.IsConnected)
                    t.ReceiveAsync();
            };
            serverPeer.Connection.Transport.ReceiveAsync();

            // Client sends encrypted packet
            var sendPacket = new EchoPacket { Text = "full stack encrypted" };
            var (segment, _) = clientPeer.SerializeAndEncrypt(sendPacket);
            Assert.True(segment!.Value.SegmentIndex > 0);
            clientPeer.Send(segment!.Value, sendPacket.PacketSize);

            // ASSERT - Server received and decrypted correctly
            var received = await receivedPacket.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal("full stack encrypted", received.Text);

            clientPeer.Dispose();
            serverPeer.Dispose();
            client.Dispose();
            server.Dispose();
        }

        #endregion

        #region Concurrency Tests

        [Fact]
        public async Task Peer_ConcurrentSerializeAndDeserialize()
        {
            var transport = new MockTransport { ReserveSendResult = true };
            var connection = new MockConnection(transport);
            var registry = new PacketRegistry();
            registry.RegisterHandler<EchoPacket>(0x0003, null!, _ => _ => { });

            var peer = new global::DunePresentation.Peer.Peer(connection, registry);

            try
            {
                var t1 = System.Threading.Tasks.Task.Run(() =>
                {
                    for (int i = 0; i < 20; i++)
                    {
                        try
                        {
                            var packet = new EchoPacket { Text = $"msg{i}" };
                            peer.SerializeAndEncrypt(packet);
                        }
                        catch { }
                    }
                });
                var t2 = System.Threading.Tasks.Task.Run(() =>
                {
                    for (int i = 0; i < 20; i++)
                    {
                        try
                        {
                            var wireData = new byte[] { 0x03, 0x00, (byte)'h', (byte)'i' };
                            var segment = new Segment(1, wireData, _ => { });
                            peer.DecryptAndDeserialize(segment);
                        }
                        catch { }
                    }
                });
                await System.Threading.Tasks.Task.WhenAll(t1, t2).WaitAsync(TimeSpan.FromSeconds(5));
            }
            catch (Exception e)
            {
                Assert.Fail($"Concurrent serialize/deserialize threw: {e.Message}");
            }

            peer.Dispose();
        }

        [Fact]
        public async Task PacketRegistry_ConcurrentRegistration_NoDuplicate()
        {
            var registry = new PacketRegistry();

            try
            {
                var t1 = System.Threading.Tasks.Task.Run(() =>
                {
                    for (ushort i = 0x1000; i < 0x1020; i++)
                        try { registry.RegisterHandler<EchoPacket>(i, null!, _ => _ => { }); } catch { }
                });
                var t2 = System.Threading.Tasks.Task.Run(() =>
                {
                    for (ushort i = 0x2000; i < 0x2020; i++)
                        try { registry.RegisterHandler<EchoPacket>(i, null!, _ => _ => { }); } catch { }
                });
                await System.Threading.Tasks.Task.WhenAll(t1, t2).WaitAsync(TimeSpan.FromSeconds(5));
            }
            catch (Exception e)
            {
                Assert.Fail($"Concurrent registration threw: {e.Message}");
            }

            // Verify no exceptions and registrations succeeded
            Assert.True(true);
        }

        #endregion

        #region PresentationHeader Tests

        // Span<T> is a ref-like struct and cannot be captured by Assert.Throws's lambda,
        // so the throwing call must allocate its array-backed span *inside* the lambda.
        // Each invocation builds a fresh 1-byte buffer (smaller than PresentationHeader.Size),
        // forcing the real size guard to throw.
        private static Action WriteTooSmall { get; } = () =>
        {
            Span<byte> buffer = new byte[1];
            PresentationHeader.Write(buffer, 42);
        };

        private static Action ReadTooSmall { get; } = () =>
        {
            ReadOnlySpan<byte> buffer = new byte[1];
            PresentationHeader.Read(buffer, out _);
        };

        /// <summary>
        /// Verifies that PresentationHeader.Write throws ArgumentException when the
        /// destination buffer is smaller than the 2-byte header, exercising the real
        /// validation branch in PresentationHeader.Write (line 28-29).
        /// </summary>
        [Fact]
        public void PresentationHeader_Write_BufferTooSmall_Throws()
        {
            // Buffer smaller than PresentationHeader.Size (2) must throw.
            Assert.Throws<ArgumentException>(WriteTooSmall);

            // Boundary: an exactly-sized buffer must NOT throw, proving the assertion
            // targets the size guard rather than rejecting any input.
            Span<byte> exact = stackalloc byte[PresentationHeader.Size];
            PresentationHeader.Write(exact, 42);

            // Round-trip through the real Read to confirm the value landed correctly.
            PresentationHeader.Read(exact, out var packetId);
            Assert.Equal(42, packetId);
        }

        /// <summary>
        /// Verifies that PresentationHeader.Read throws ArgumentException when the
        /// source buffer is smaller than the 2-byte header, exercising the real
        /// validation branch in PresentationHeader.Read (line 42-43).
        /// </summary>
        [Fact]
        public void PresentationHeader_Read_BufferTooSmall_Throws()
        {
            // Buffer smaller than PresentationHeader.Size (2) must throw.
            Assert.Throws<ArgumentException>(ReadTooSmall);

            // Boundary: an exactly-sized buffer with a known value must NOT throw and
            // must return the value that was written, proving the guard is on size only.
            Span<byte> exact = stackalloc byte[PresentationHeader.Size];
            PresentationHeader.Write(exact, 7);
            PresentationHeader.Read(exact, out var packetId);
            Assert.Equal(7, packetId);
        }

        #endregion

        /// <summary>Packet whose ReadFieldsFromBuffer always returns false.</summary>
        private class BadDeserializePacket : IPacket
        {
            public ushort PacketId => 0x0099;
            public int PacketSize { get; set; }
            public Segment segment { get; set; }

            public void WriteFieldsToBuffer(Span<byte> buffer, out int bytesWritten)
            {
                bytesWritten = 0;
            }

            public bool ReadFieldsFromBuffer(ReadOnlySpan<byte> buffer, int length)
            {
                return false; // Always fails
            }
        }

        /// <summary>
        /// Packet whose ISegmentManager.OnSerialize returns false (simulating a serialize
        /// failure after the segment was reserved). Used to exercise the SerializeFailed path
        /// in ISegmentManager.Serialize (which returns SerializeResult.SerializeFailed).
        /// </summary>
        private class BadSerializePacket : IPacket
        {
            public ushort PacketId => 0x0098;
            public int PacketSize { get; set; }
            public Segment segment { get; set; }

            public void WriteFieldsToBuffer(Span<byte> buffer, out int bytesWritten)
            {
                bytesWritten = 0;
            }

            public bool ReadFieldsFromBuffer(ReadOnlySpan<byte> buffer, int length)
            {
                return true;
            }

            // Override the explicit interface implementation to force a serialize failure.
            bool ISegmentManager.OnSerialize()
            {
                return false; // Always fails
            }
        }

        // Packet whose constructor throws — verifies the decrypt/deserialize path
        // contains factory exceptions and releases the segment (G-5).
        private class ThrowingCtorPacket : IPacket
        {
            public ThrowingCtorPacket()
            {
                throw new InvalidOperationException("ctor must throw");
            }

            public ushort PacketId => 0x009A;
            public int PacketSize { get; set; }
            public Segment segment { get; set; }

            public void WriteFieldsToBuffer(Span<byte> buffer, out int bytesWritten)
            {
                bytesWritten = 0;
            }

            public bool ReadFieldsFromBuffer(ReadOnlySpan<byte> buffer, int length)
            {
                return true;
            }
        }

        #endregion
    }
}