using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;
using Epic.OnlineServices;
using Epic.OnlineServices.Lobby;
using Epic.OnlineServices.P2P;
using FishNet.Utility.Performance;
using UnityEngine;

namespace FishNet.Transporting.EpicNetPlugin
{
    internal sealed class ClientPeer : CommonPeer
    {
        SocketId _socketId;
        ProductUserId _localUserId;
        ProductUserId _remoteUserId;
        ulong? _establishedHandle;
        ulong? _closedHandle;
        ulong? _interruptedHandle;
        volatile bool _isShuttingDown;
        CancellationTokenSource _authCts;
        bool _isInterrupted;
        volatile bool _deferredStopRequested;

        bool _autoReconnect;
        int _maxReconnectAttempts = 5;
        float _reconnectDelayBase = 1f, _reconnectDelayMax = 30f;
        int _reconnectAttempt;
        float _reconnectTimer;
        bool _reconnecting;

        public void SetReconnectSettings(bool auto, int maxAttempts, float baseDelay, float maxDelay)
        {
            _autoReconnect = auto;
            _maxReconnectAttempts = maxAttempts;
            _reconnectDelayBase = baseDelay;
            _reconnectDelayMax = maxDelay;
        }

        internal void StartConnection()
        {
            if (GetLocalConnectionState() != LocalConnectionState.Stopped)
            {
                _transport.LogErr("[Client] Already connecting or connected.");
                return;
            }
            _isShuttingDown = false;
            _isInterrupted = false;
            _deferredStopRequested = false;
            _reconnectAttempt = 0;
            _reconnecting = false;
            SetLocalConnectionState(LocalConnectionState.Starting, false);
            _authCts = new CancellationTokenSource();
            _ = StartAsync(_authCts.Token);
        }

        async Task StartAsync(CancellationToken ct)
        {
            try
            {
                if (_transport.AutoAuthenticate)
                {
                    var r = await EOSAuthenticator.Authenticate(_transport.AuthConnectData, ct);
                    if (r != Result.Success)
                    { _transport.LogErr($"[Client] Auth failed: {r}"); SetLocalConnectionState(LocalConnectionState.Stopped, false); return; }
                }
                if (ct.IsCancellationRequested)
                {
                    SetLocalConnectionState(LocalConnectionState.Stopped, false);
                    return;
                }

                _localUserId = EOS.LocalProductUserId;
                _remoteUserId = ProductUserId.FromString(_transport.RemoteProductUserId);

                var p2p = EOS.GetP2PInterface();
                if (p2p is null)
                { _transport.LogErr("[Client] P2P unavailable"); SetLocalConnectionState(LocalConnectionState.Stopped, false); return; }

#if UNITY_EDITOR || !UNITY_EDITOR
                try {
                    var relayProp = _transport.GetType().GetProperty("RelayPolicy");
                    var relayVal = relayProp != null ? (RelayControl)relayProp.GetValue(_transport) : RelayControl.NoRelays;
                    p2p.SetRelayControl(new SetRelayControlOptions { RelayControl = relayVal });
                } catch { }
#else
                p2p.SetRelayControl(new SetRelayControlOptions { RelayControl = (RelayControl)_transport.RelayPolicy });
#endif

#if UNITY_EDITOR || !UNITY_EDITOR
                try {
                    var enableProp = _transport.GetType().GetProperty("EnableLobbies");
                    bool enableLobbies = enableProp != null && (bool)enableProp.GetValue(_transport);
                    if ((_remoteUserId == null || !_remoteUserId.IsValid()) && enableLobbies)
                    {
                        var lobby = EOS.GetLobbyInterface();
                        var search = new CreateLobbySearchOptions { MaxResults = 10 };
                        lobby.CreateLobbySearch(ref search, out var handle);
                    }
                } catch { }
#else
                if ((_remoteUserId == null || !_remoteUserId.IsValid()) && _transport.EnableLobbies)
                {
                    var lobby = EOS.GetLobbyInterface();
                    var search = new CreateLobbySearchOptions { MaxResults = 10 };
                    lobby.CreateLobbySearch(ref search, out var handle);
                }
#endif

                if (_remoteUserId == null || !_remoteUserId.IsValid())
                { _transport.LogErr("[Client] Invalid RemoteProductUserId."); SetLocalConnectionState(LocalConnectionState.Stopped, false); return; }

                _socketId = new SocketId { SocketName = _transport.SocketName };

                CleanupHandles();

                var eOpt = new AddNotifyPeerConnectionEstablishedOptions { LocalUserId = _localUserId, SocketId = _socketId };
                _establishedHandle = p2p.AddNotifyPeerConnectionEstablished(ref eOpt, null, OnEstablished);

                var cOpt = new AddNotifyPeerConnectionClosedOptions { LocalUserId = _localUserId, SocketId = _socketId };
                _closedHandle = p2p.AddNotifyPeerConnectionClosed(ref cOpt, null, OnClosed);

                var iOpt = new AddNotifyPeerConnectionInterruptedOptions { LocalUserId = _localUserId, SocketId = _socketId };
                _interruptedHandle = p2p.AddNotifyPeerConnectionInterrupted(ref iOpt, null, OnInterrupted);

                var aOpt = new AcceptConnectionOptions { LocalUserId = _localUserId, RemoteUserId = _remoteUserId, SocketId = _socketId };
                var aR = p2p.AcceptConnection(ref aOpt);
                if (aR != Result.Success)
                { _transport.LogErr($"[Client] Accept failed: {aR}"); CleanupHandles(); SetLocalConnectionState(LocalConnectionState.Stopped, false); return; }

                var nOpt = new QueryNATTypeOptions();
                p2p.QueryNATType(ref nOpt, null, (ref OnQueryNATTypeCompleteInfo d) =>
                    _transport.LogDebug($"[Client] NAT: {d.NATType}"));
            }
            catch (OperationCanceledException)
            {
                SetLocalConnectionState(LocalConnectionState.Stopped, false);
            }
            catch (Exception e)
            {
                _transport.LogErr($"[Client] Start failed: {e.Message}");
                SetLocalConnectionState(LocalConnectionState.Stopped, false);
            }
        }

        void CleanupHandles()
        {
            var p2p = EOS.GetP2PInterface();
            if (p2p is null) return;
            if (_establishedHandle.HasValue) { p2p.RemoveNotifyPeerConnectionEstablished(_establishedHandle.Value); _establishedHandle = null; }
            if (_closedHandle.HasValue) { p2p.RemoveNotifyPeerConnectionClosed(_closedHandle.Value); _closedHandle = null; }
            if (_interruptedHandle.HasValue) { p2p.RemoveNotifyPeerConnectionInterrupted(_interruptedHandle.Value); _interruptedHandle = null; }
        }

        internal bool StopConnection()
        {
            if (GetLocalConnectionState() is LocalConnectionState.Stopped or LocalConnectionState.Stopping) return false;
            _isShuttingDown = true;
            SetLocalConnectionState(LocalConnectionState.Stopping, false);

            var oldCts = Interlocked.Exchange(ref _authCts, null);
            oldCts?.Cancel();
            oldCts?.Dispose();

            CleanupHandles();
            DrainRetryQueue();
            if (_localUserId is not null && _remoteUserId is not null)
            {
                var co = new CloseConnectionOptions { SocketId = _socketId, LocalUserId = _localUserId, RemoteUserId = _remoteUserId };
                EOS.GetP2PInterface()?.CloseConnection(ref co);
            }
            SetLocalConnectionState(LocalConnectionState.Stopped, false);
            return true;
        }

        internal void CheckDeferredStop()
        {
            if (_deferredStopRequested && GetLocalConnectionState() == LocalConnectionState.Started)
            {
                _deferredStopRequested = false;
                StopConnection();
            }
        }

        void OnEstablished(ref OnPeerConnectionEstablishedInfo data)
        {
            try
            {
                if (_isShuttingDown) return;
                if (data.ConnectionType == ConnectionEstablishedType.Reconnection)
                { _isInterrupted = false; _transport.LogDebug("[Client] Reconnected"); return; }
                _isInterrupted = false;
                SetLocalConnectionState(LocalConnectionState.Started, false);
                _transport.LogDebug("[Client] Connected to server");
            }
            catch (Exception e) { Debug.LogError($"[Client] OnEstablished: {e.Message}"); }
        }

        void OnClosed(ref OnRemoteConnectionClosedInfo data)
        {
            try
            {
                if (_isShuttingDown) return;
                _transport.LogDebug($"[Client] Disconnected: {data.Reason}");
                if (_autoReconnect)
                {
                    _isInterrupted = true;
                }
                else
                {
                    _deferredStopRequested = true;
                }
            }
            catch (Exception e) { Debug.LogError($"[Client] OnClosed: {e.Message}"); }
        }

        void OnInterrupted(ref OnPeerConnectionInterruptedInfo data)
        {
            try { if (!_isShuttingDown) { _isInterrupted = true; _transport.LogWarn("[Client] Interrupted, awaiting reconnect..."); } }
            catch (Exception e) { Debug.LogError($"[Client] OnInterrupted: {e.Message}"); }
        }

        internal void IterateOutgoing()
        {
            if (GetLocalConnectionState() != LocalConnectionState.Started || _isInterrupted) return;
            ProcessRetryQueue();
            ProcessReconnect();
        }

        void ProcessReconnect()
        {
            if (!_autoReconnect || !_isInterrupted || _reconnecting || _reconnectAttempt >= _maxReconnectAttempts) return;
            _reconnectTimer += Time.deltaTime;
            float delay = Mathf.Min(_reconnectDelayBase * Mathf.Pow(2, _reconnectAttempt - 1), _reconnectDelayMax);
            if (_reconnectTimer < delay) return;

            _reconnectAttempt++;
            _reconnecting = true;
            _reconnectTimer = 0f;
            _transport.LogDebug($"[Client] Reconnect attempt {_reconnectAttempt}/{_maxReconnectAttempts}");
            StartConnection(); 
        }

        internal void IterateIncoming()
        {
            if (GetLocalConnectionState() is LocalConnectionState.Stopped or LocalConnectionState.Stopping) return;
            int processed = 0;
            while (processed < _maxIncomingPacketsPerFrame && Receive(_localUserId, out _, out var buf, out int len, out byte ch))
            {
                processed++;
                var seg = new ArraySegment<byte>(buf, 0, len);
                _transport.HandleClientReceivedDataArgs(new ClientReceivedDataArgs(seg, (Channel)ch, _transport.Index));
                ByteArrayPool.Store(buf);
            }
        }

        internal void ReceiveToQueue(ConcurrentQueue<ThreadedPacket> queue)
        {
            if (GetLocalConnectionState() is LocalConnectionState.Stopped or LocalConnectionState.Stopping) return;
            int processed = 0;
            while (processed < _maxIncomingPacketsPerFrame && Receive(_localUserId, out _, out var buf, out int len, out byte ch))
            {
                processed++;
                queue.Enqueue(new ThreadedPacket { ChannelId = ch, Data = buf, Length = len, ConnectionId = -1 });
            }
        }

        internal void SendToServer(byte channelId, ArraySegment<byte> segment)
        {
            if (GetLocalConnectionState() != LocalConnectionState.Started || _isInterrupted) return;
            var r = Send(_localUserId, _remoteUserId, _socketId, channelId, segment);
            if (r is Result.NoConnection or Result.InvalidParameters) StopConnection();
        }
    }
}