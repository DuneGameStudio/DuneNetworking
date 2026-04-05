using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using DuneNetworking.SocketConnectors.Interface;
using DuneTransport.Transport;
using DuneTransport.Transport.Interface;

namespace DuneNetworking.SocketConnectors
{
    public class ServerConnector : IServer
    {
        private readonly Socket _listenSocket;
        private readonly SocketAsyncEventArgs _acceptEventArgs;
        
        // Thread-safe registry for active sessions. Using a boolean as a dummy value.
        private readonly ConcurrentDictionary<ITransport, bool> _activeSessions;
        
        private bool _isListening;
        
        public bool IsListening => _isListening;

        public int ActiveConnectionCount => _activeSessions.Count;

        public event Action<ITransport>? OnClientConnected;

        public ServerConnector()
        {
            _listenSocket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            
            _acceptEventArgs = new SocketAsyncEventArgs();
            _acceptEventArgs.Completed += OnAcceptCompleted;

            _activeSessions = new ConcurrentDictionary<ITransport, bool>();
        }

        public void StartListening(string address, int port)
        {
            if (_isListening)
            {
                Debug.WriteLine("StartListening | Server is already running.", "Error");
                return;
            }

            try
            {
                IPEndPoint endPoint = new IPEndPoint(IPAddress.Parse(address), port);
                
                _listenSocket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, 1);
                _listenSocket.Bind(endPoint);
                _listenSocket.Listen((int)SocketOptionName.MaxConnections);
                
                _isListening = true;
                
                AcceptConnection();
                
                Debug.WriteLine($"Server started listening on {address}:{port}", "log");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"StartListening | Failed to start server: {ex.Message}", "Error");
                _isListening = false;
            }
        }

        private void AcceptConnection()
        {
            if (!_isListening) return;

            _acceptEventArgs.AcceptSocket = null;

            try
            {
                if (!_listenSocket.AcceptAsync(_acceptEventArgs))
                {
                    ProcessAccept(_acceptEventArgs);
                }
            }
            catch (ObjectDisposedException)
            {
                // Normal during teardown
            }
            catch (SocketException ex)
            {
                Debug.WriteLine($"AcceptConnection | SocketException: {ex.Message}", "Error");
            }
        }

        private void OnAcceptCompleted(object? sender, SocketAsyncEventArgs e)
        {
            ProcessAccept(e);
        }

        private void ProcessAccept(SocketAsyncEventArgs e)
        {
            if (!_isListening) return;
            
            if (e.SocketError == SocketError.Success && e.AcceptSocket != null)
            {
                ITransport newSession = new Transport(e.AcceptSocket);
                
                // Track the session and hook into its lifecycle
                if (_activeSessions.TryAdd(newSession, true))
                {
                    newSession.EventArgsOnDisconnected += HandleSessionDisconnected;
                    OnClientConnected?.Invoke(newSession);
                }
            }
            else
            {
                Debug.WriteLine($"ProcessAccept | Accept failed: {e.SocketError}", "Error");
            }

            AcceptConnection();
        }

        private void HandleSessionDisconnected(object? sender, EventArgs e)
        {
            if (sender is ITransport disconnectedSession)
            {
                // Unhook the event to prevent memory leaks
                disconnectedSession.EventArgsOnDisconnected -= HandleSessionDisconnected;
                
                // Remove from registry
                _activeSessions.TryRemove(disconnectedSession, out _);
            }
        }

        public void StopListening()
        {
            if (!_isListening) return;

            _isListening = false;

            try
            {
                _listenSocket.Close();
                Debug.WriteLine("Server stopped listening for new connections.", "log");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"StopListening | Error during shutdown: {ex.Message}", "Error");
            }
        }

        public void StopServer()
        {
            // 1. Stop accepting new connections
            StopListening();

            // 2. Terminate all active sessions
            foreach (var session in _activeSessions.Keys)
            {
                session.DisconnectAsync();
            }
            
            _activeSessions.Clear();
            Debug.WriteLine("Server completely stopped. All active sessions terminated.", "log");
        }

        #region IDisposable
        
        private bool _disposedValue;

        protected virtual void Dispose(bool disposing)
        {
            if (!_disposedValue)
            {
                if (disposing)
                {
                    StopServer();
                    
                    _listenSocket.Dispose();
                    _acceptEventArgs.Dispose();
                }
                _disposedValue = true;
            }
        }

        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }
        
        #endregion
    }
}