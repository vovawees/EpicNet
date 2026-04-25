using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Epic.OnlineServices;
using Epic.OnlineServices.P2P;
using Epic.OnlineServices.Lobby;
using FishNet.Utility.Performance;
using UnityEngine;

namespace FishNet.Transporting.EpicNetPlugin
{
    internal sealed class ServerPeer : CommonPeer
    {
        SocketId _socketId;
        ProductUserId _localUserId;
        volatile bool _isShuttingDown;
        CancellationTokenSource _authCts;
        ulong? _acceptHandle, _interruptedHandle, _establishHandle, _closeHandle;

        readonly object _lock = new object();
        readonly Dictionary<int, Connection> _clientsById = new Dictionary<int, Connection>(64);
        readonly Dictionary<ProductUserId, int> _userToId = new Dictionary<ProductUserId, int>(64);
        readonly HashSet<ProductUserId> _interruptedUsers = new HashSet<ProductUserId>();
        readonly Dictionary<ProductUserId, Connection> _pendingConnections = new Dictionary<ProductUserId, Connection>(16);
        readonly Dictionary<ProductUserId, float> _pendingTimestamps = new Dictionary<ProductUserId, float>(16);
        readonly List<ProductUserId> _expiredCache = new List<ProductUserId>(8);
        readonly Dictionary<int, float> _lastKeepAlive = new Dictionary<int, float>();

        int _maximumClients = short.MaxValue;
        int _connectionsThisSecond;
        float _lastRateLimitReset;
        int _maxConnPerSec = 50, _maxPending = 256, _maxBurst = 10, _connectionBurstTokens;
        float _pendingTimeout = 10f;

        bool _enableLobbies;
        string _lobbyName;
        LobbyInterface _lobby;

        bool _enableKeepAlive;
        float _keepAliveInterval = 2f;
        float _keepAliveTimeout = 10f;

        bool _enableLanDiscovery;
        UdpClient _lanUdp;
        const int LAN_PORT = 7776;

        ClientHostBridge _clientHostBridge;
        int _nextId = 1;

        public void SetSecurityLimits(int maxConnPerSec, int maxPending, float timeout, int maxBurst)
        {
            _maxConnPerSec = maxConnPerSec;
            _maxPending = maxPending;
            _pendingTimeout = timeout;
            _maxBurst = maxBurst;
        }

        public void SetClientHostBridge(ClientHostBridge bridge) => _clientHostBridge = bridge;

        public void SetLobbySettings(bool enable, string lobbyName)
        {
            _enableLobbies = enable;
            _lobbyName = lobbyName;
        }

        public void SetKeepAlive(bool enable, float interval, float timeout)
        {
            _enableKeepAlive = enable;
            _keepAliveInterval = interval;
            _keepAliveTimeout = timeout;
        }

        public void SetLanDiscovery(bool enable) => _enableLanDiscovery = enable;

        internal bool StartConnection()
        {
            if (GetLocalConnectionState() != LocalConnectionState.Stopped) return false;
            _isShuttingDown = false;
            _nextId = 1;
            SetLocalConnectionState(LocalConnectionState.Starting, true);
            _authCts = new CancellationTokenSource();
            _ = AuthAndListen(_authCts.Token);
            return true;
        }

        async Task AuthAndListen(CancellationToken ct)
        {
            try
            {
                if (_transport.AutoAuthenticate)
                {
                    var r = await EOSAuthenticator.Authenticate(_transport.AuthConnectData, ct);
                    if (r != Result.Success)
                    {
                        _transport.LogErr($"[Server] Auth failed: {r}");
                        SetLocalConnectionState(LocalConnectionState.Stopped, true);
                        return;
                    }
                }
                if (ct.IsCancellationRequested) { SetLocalConnectionState(LocalConnectionState.Stopped, true); return; }

                _localUserId = EOS.LocalProductUserId;
                if (_localUserId == null || !_localUserId.IsValid())
                {
                    _transport.LogErr("[Server] Invalid local user.");
                    SetLocalConnectionState(LocalConnectionState.Stopped, true);
                    return;
                }
                _socketId = new SocketId { SocketName = _transport.SocketName };
                var p2p = EOS.GetP2PInterface();
                if (p2p is null) { SetLocalConnectionState(LocalConnectionState.Stopped, true); return; }

                p2p.SetRelayControl(new SetRelayControlOptions { RelayControl = (RelayControl)_transport.RelayPolicy });

                var qOpt = new SetPacketQueueSizeOptions
                { IncomingPacketQueueMaxSizeBytes = 4 * 1024 * 1024, OutgoingPacketQueueMaxSizeBytes = 4 * 1024 * 1024 };
                p2p.SetPacketQueueSize(ref qOpt);

                _acceptHandle = p2p.AddNotifyPeerConnectionRequest(new AddNotifyPeerConnectionRequestOptions { SocketId = _socketId, LocalUserId = _localUserId }, null, OnRequest);
                _establishHandle = p2p.AddNotifyPeerConnectionEstablished(new AddNotifyPeerConnectionEstablishedOptions { SocketId = _socketId, LocalUserId = _localUserId }, null, OnEstablished);
                _closeHandle = p2p.AddNotifyPeerConnectionClosed(new AddNotifyPeerConnectionClosedOptions { SocketId = _socketId, LocalUserId = _localUserId }, null, OnClosed);
                _interruptedHandle = p2p.AddNotifyPeerConnectionInterrupted(new AddNotifyPeerConnectionInterruptedOptions { SocketId = _socketId, LocalUserId = _localUserId }, null, OnInterrupted);

                if (_enableLobbies)
                {
                    _lobby = EOS.GetLobbyInterface();
                    var createOpt = new CreateLobbyOptions { LocalUserId = _localUserId, MaxLobbyMembers = (uint)_maximumClients, PresencePermission = LobbyPermissionLevel.Publicadvertised };
                    _lobby.CreateLobby(ref createOpt, null, (ref CreateLobbyCallbackInfo info) =>
                    {
                        if (info.ResultCode != Result.Success)
                            _transport.LogWarn($"[Server] Lobby creation failed: {info.ResultCode}");
                        else
                            _transport.LogDebug("[Server] Lobby created.");
                    });
                }

                if (_enableLanDiscovery)
                {
                    try
                    {
                        _lanUdp = new UdpClient(LAN_PORT);
                        _ = Task.Run(async () =>
                        {
                            while (!_isShuttingDown)
                            {
                                try
                                {
                                    var recv = await _lanUdp.ReceiveAsync();
                                    if (Encoding.UTF8.GetString(recv.Buffer) == "EPICNET_LAN_QUERY")
                                    {
                                        var resp = Encoding.UTF8.GetBytes($"EPICNET_LAN_SERVER:{_transport.SocketName}:{_localUserId}");
                                        await _lanUdp.SendAsync(resp, resp.Length, recv.RemoteEndPoint);
                                    }
                                }
                                catch (ObjectDisposedException) { break; }
                                catch (Exception) { }
                            }
                        }, ct);
                    }
                    catch { _transport.LogWarn("[Server] LAN Discovery failed to start."); }
                }

                SetLocalConnectionState(LocalConnectionState.Started, true);
                _clientHostBridge?.OnServerState(LocalConnectionState.Started);
            }
            catch (OperationCanceledException) { SetLocalConnectionState(LocalConnectionState.Stopped, true); }
            catch (Exception e) { _transport.LogErr($"[Server] Start failed: {e.Message}"); SetLocalConnectionState(LocalConnectionState.Stopped, true); }
        }

        void OnRequest(ref OnIncomingConnectionRequestInfo data)
        {
            try
            {
                if (_isShuttingDown) return;
                if (data.RemoteUserId == null || !data.RemoteUserId.IsValid()) return;

                float now = Time.unscaledTime;
                lock (_lock)
                {
                    if (now - _lastRateLimitReset > 1f) { _connectionsThisSecond = 0; _lastRateLimitReset = now; _connectionBurstTokens = _maxBurst; }
                    bool rateLimited = _connectionsThisSecond >= _maxConnPerSec;
                    bool burstAvailable = _connectionBurstTokens > 0;
                    if (rateLimited && !burstAvailable) { RejectRequest(ref data); return; }
                    if (_clientsById.Count + _pendingConnections.Count >= _maximumClients) { RejectRequest(ref data); return; }
                    if (_pendingConnections.Count >= _maxPending) { RejectRequest(ref data); return; }
                    if (_userToId.ContainsKey(data.RemoteUserId)) { RejectRequest(ref data); return; }
                    if (_pendingConnections.ContainsKey(data.RemoteUserId)) { RejectRequest(ref data); return; }

                    string incomingSocketName = data.SocketId?.SocketName ?? "";
                    if (incomingSocketName != _socketId.SocketName)
                    {
                        _transport.LogWarn($"[Server] SECURITY: Rejected mismatched SocketId from {data.RemoteUserId}");
                        RejectRequest(ref data);
                        return;
                    }

                    var p2p = EOS.GetP2PInterface();
                    if (p2p is null) return;

                    int id = _nextId++;
                    if (id < 1) { _nextId = 1; id = _nextId++; }
                    if (id == EpicNet.CLIENT_HOST_ID) id = _nextId++;
                    int idAttempts = 0;
                    while (_clientsById.ContainsKey(id) || _pendingConnectionsContainsId(id))
                    {
                        id = _nextId++;
                        if (id < 1) { _nextId = 1; id = _nextId++; }
                        if (id == EpicNet.CLIENT_HOST_ID) id = _nextId++;
                        if (++idAttempts > 10000)
                        {
                            _transport.LogErr("[Server] Failed to generate unique connection id.");
                            return;
                        }
                    }

                    var conn = new Connection(id, data.LocalUserId, data.RemoteUserId, _socketId);
                    _pendingConnections[data.RemoteUserId] = conn;
                    _pendingTimestamps[data.RemoteUserId] = now;

                    var aOpt = new AcceptConnectionOptions { LocalUserId = _localUserId, RemoteUserId = data.RemoteUserId, SocketId = _socketId };
                    var aR = p2p.AcceptConnection(ref aOpt);
                    if (aR == Result.Success)
                    {
                        if (rateLimited) _connectionBurstTokens--;
                        _connectionsThisSecond++;
                        _transport.LogDebug($"[Server] Pending connection: {data.RemoteUserId} id={id}");
                    }
                    else
                    {
                        _pendingConnections.Remove(data.RemoteUserId);
                        _pendingTimestamps.Remove(data.RemoteUserId);
                        _transport.LogErr($"[Server] Accept failed: {data.RemoteUserId} → {aR}");
                    }
                }
            }
            catch (Exception e) { Debug.LogError($"[Server] OnRequest: {e.Message}"); }
        }

        bool _pendingConnectionsContainsId(int id)
        {
            foreach (var conn in _pendingConnections.Values)
                if (conn.Id == id) return true;
            return false;
        }

        void RejectRequest(ref OnIncomingConnectionRequestInfo reqData)
        {
            if (reqData.RemoteUserId == null) return;
            var p2p = EOS.GetP2PInterface();
            if (p2p is null) return;
            var co = new CloseConnectionOptions { SocketId = _socketId, LocalUserId = _localUserId, RemoteUserId = reqData.RemoteUserId };
            p2p.CloseConnection(ref co);
        }

        void OnEstablished(ref OnPeerConnectionEstablishedInfo data)
        {
            try
            {
                if (_isShuttingDown) return;
                if (data.RemoteUserId == null) return;
                lock (_lock)
                {
                    if (data.ConnectionType == ConnectionEstablishedType.Reconnection)
                    {
                        _interruptedUsers.Remove(data.RemoteUserId);
                        _transport.LogDebug($"[Server] Reconnected: {data.RemoteUserId}");
                        return;
                    }
                    if (!_pendingConnections.TryGetValue(data.RemoteUserId, out var conn))
                        return;
                    _pendingConnections.Remove(data.RemoteUserId);
                    _pendingTimestamps.Remove(data.RemoteUserId);
                    _clientsById[conn.Id] = conn;
                    _userToId[data.RemoteUserId] = conn.Id;
                    _transport.Stats.ActiveConnections = _clientsById.Count;
                    if (_enableKeepAlive)
                        _lastKeepAlive[conn.Id] = Time.unscaledTime;
                    _transport.HandleRemoteConnectionState(new RemoteConnectionStateArgs(RemoteConnectionState.Started, conn.Id, _transport.Index));
                    _transport.LogDebug($"[Server] Connected: {data.RemoteUserId} id={conn.Id}");
                }
            }
            catch (Exception e) { Debug.LogError($"[Server] OnEstablished: {e.Message}"); }
        }

        void OnClosed(ref OnRemoteConnectionClosedInfo data)
        {
            try
            {
                if (_isShuttingDown) return;
                if (data.RemoteUserId == null) return;
                int idToRemove = -1;
                lock (_lock)
                {
                    if (!_userToId.TryGetValue(data.RemoteUserId, out int id)) return;
                    if (!_clientsById.TryGetValue(id, out var conn)) return;
                    RemoveClient(conn);
                    idToRemove = id;
                }
                if (idToRemove >= 0)
                    _transport.HandleRemoteConnectionState(new RemoteConnectionStateArgs(RemoteConnectionState.Stopped, idToRemove, _transport.Index));
            }
            catch (Exception e) { Debug.LogError($"[Server] OnClosed: {e.Message}"); }
        }

        void OnInterrupted(ref OnPeerConnectionInterruptedInfo data)
        {
            try
            {
                if (_isShuttingDown) return;
                if (data.RemoteUserId == null) return;
                lock (_lock) _interruptedUsers.Add(data.RemoteUserId);
                _transport.LogWarn($"[Server] Interrupted: {data.RemoteUserId}");
            }
            catch (Exception e) { Debug.LogError($"[Server] OnInterrupted: {e.Message}"); }
        }

        void RemoveClient(Connection conn)
        {
            _clientsById.Remove(conn.Id);
            _userToId.Remove(conn.RemoteUserId);
            _interruptedUsers.Remove(conn.RemoteUserId);
            _lastKeepAlive.Remove(conn.Id);
            _transport.Stats.ActiveConnections = _clientsById.Count;
        }

        void RemovePending(ProductUserId userId)
        {
            _pendingConnections.Remove(userId);
            _pendingTimestamps.Remove(userId);
        }

        internal bool StopConnection()
        {
            if (GetLocalConnectionState() is LocalConnectionState.Stopped or LocalConnectionState.Stopping) return false;
            SetLocalConnectionState(LocalConnectionState.Stopping, true);
            try
            {
                _isShuttingDown = true;
                var oldCts = Interlocked.Exchange(ref _authCts, null);
                oldCts?.Cancel();
                oldCts?.Dispose();

                var p2p = EOS.GetP2PInterface();
                if (_closeHandle.HasValue) { p2p?.RemoveNotifyPeerConnectionClosed(_closeHandle.Value); _closeHandle = null; }
                if (_establishHandle.HasValue) { p2p?.RemoveNotifyPeerConnectionEstablished(_establishHandle.Value); _establishHandle = null; }
                if (_acceptHandle.HasValue) { p2p?.RemoveNotifyPeerConnectionRequest(_acceptHandle.Value); _acceptHandle = null; }
                if (_interruptedHandle.HasValue) { p2p?.RemoveNotifyPeerConnectionInterrupted(_interruptedHandle.Value); _interruptedHandle = null; }

                List<Connection> clientsToStop;
                lock (_lock)
                {
                    clientsToStop = new List<Connection>(_clientsById.Values);
                    _clientsById.Clear(); _userToId.Clear();
                    _pendingTimestamps.Clear(); _interruptedUsers.Clear();
                    _pendingConnections.Clear(); _lastKeepAlive.Clear();
                }

                foreach (var conn in clientsToStop)
                    _transport.HandleRemoteConnectionState(new RemoteConnectionStateArgs(RemoteConnectionState.Stopped, conn.Id, _transport.Index));

                _clientHostBridge?.Stop();
                DrainRetryQueue();

                if (_localUserId is not null && p2p is not null)
                {
                    var co = new CloseConnectionsOptions { SocketId = _socketId, LocalUserId = _localUserId };
                    p2p.CloseConnections(ref co);
                }
                _lanUdp?.Dispose();
                _lanUdp = null;
                _transport.Stats.ActiveConnections = 0;
            }
            catch (Exception e) { _transport.LogErr($"[Server] Stop error: {e.Message}"); }
            SetLocalConnectionState(LocalConnectionState.Stopped, true);
            return true;
        }

        internal bool StopConnection(int connectionId)
        {
            if (connectionId == EpicNet.CLIENT_HOST_ID)
            {
                _clientHostBridge?.OnClientHostState(LocalConnectionState.Stopped);
                return true;
            }
            int idToRemove = -1;
            ProductUserId remoteUserIdToRemove = null;
            lock (_lock)
            {
                if (_clientsById.TryGetValue(connectionId, out var conn))
                {
                    remoteUserIdToRemove = conn.RemoteUserId;
                    RemoveClient(conn);
                    idToRemove = connectionId;
                }
            }
            if (idToRemove >= 0 && remoteUserIdToRemove != null)
            {
                var p2p = EOS.GetP2PInterface();
                if (p2p is not null)
                {
                    var co = new CloseConnectionOptions { SocketId = _socketId, LocalUserId = _localUserId, RemoteUserId = remoteUserIdToRemove };
                    p2p.CloseConnection(ref co);
                }
                _transport.HandleRemoteConnectionState(new RemoteConnectionStateArgs(RemoteConnectionState.Stopped, idToRemove, _transport.Index));
                return true;
            }
            return false;
        }

        internal RemoteConnectionState GetConnectionState(int id)
        {
            lock (_lock) return _clientsById.ContainsKey(id) ? RemoteConnectionState.Started : RemoteConnectionState.Stopped;
        }

        internal void IterateOutgoing()
        {
            if (GetLocalConnectionState() != LocalConnectionState.Started) return;
            ProcessRetryQueue();
            CleanupPendingConnections();
            _clientHostBridge?.ProcessServerIncoming();
            ProcessKeepAlive();
        }

        void ProcessKeepAlive()
        {
            if (!_enableKeepAlive) return;
            float now = Time.unscaledTime;
            List<int> timedOut = null;
            lock (_lock)
            {
                foreach (var kv in _lastKeepAlive)
                {
                    if (now - kv.Value > _keepAliveTimeout)
                    {
                        timedOut = timedOut ?? new List<int>();
                        timedOut.Add(kv.Key);
                    }
                }
            }
            if (timedOut != null)
                foreach (var id in timedOut)
                    StopConnection(id);
        }

        internal void CleanupPendingConnections()
        {
            lock (_lock)
            {
                if (_pendingTimestamps.Count == 0) return;
                float now = Time.unscaledTime;
                _expiredCache.Clear();
                foreach (var e in _pendingTimestamps)
                    if (now - e.Value > _pendingTimeout)
                        _expiredCache.Add(e.Key);
                if (_expiredCache.Count == 0) return;

                var p2p = EOS.GetP2PInterface();
                foreach (var uid in _expiredCache)
                {
                    if (_pendingConnections.TryGetValue(uid, out var conn) && p2p is not null)
                    {
                        var co = new CloseConnectionOptions { SocketId = conn.SocketId, LocalUserId = conn.LocalUserId, RemoteUserId = conn.RemoteUserId };
                        p2p.CloseConnection(ref co);
                    }
                    RemovePending(uid);
                }
            }
        }

        internal void IterateIncoming()
        {
            if (GetLocalConnectionState() != LocalConnectionState.Started) return;
            int processed = 0;
            while (processed < _maxIncomingPacketsPerFrame && Receive(_localUserId, out var remoteUserId, out var buf, out int len, out byte ch))
            {
                processed++;
                if (remoteUserId == null)
                {
                    ByteArrayPool.Store(buf);
                    continue;
                }
                var seg = new ArraySegment<byte>(buf, 0, len);
                int connId;
                lock (_lock)
                {
                    if (!_userToId.TryGetValue(remoteUserId, out connId))
                    { ByteArrayPool.Store(buf); continue; }
                    if (_enableKeepAlive)
                        _lastKeepAlive[connId] = Time.unscaledTime;
                }
                _transport.HandleServerReceivedDataArgs(new ServerReceivedDataArgs(seg, (Channel)ch, connId, _transport.Index));
                ByteArrayPool.Store(buf);
            }
        }

        internal void ReceiveToQueue(ConcurrentQueue<ThreadedPacket> queue)
        {
            if (GetLocalConnectionState() != LocalConnectionState.Started) return;
            int processed = 0;
            while (processed < _maxIncomingPacketsPerFrame && Receive(_localUserId, out var remoteUserId, out var buf, out int len, out byte ch))
            {
                processed++;
                if (remoteUserId == null)
                {
                    ByteArrayPool.Store(buf);
                    continue;
                }
                int connId;
                lock (_lock)
                {
                    if (!_userToId.TryGetValue(remoteUserId, out connId))
                    { ByteArrayPool.Store(buf); continue; }
                    if (_enableKeepAlive)
                        _lastKeepAlive[connId] = Time.unscaledTime;
                }
                queue.Enqueue(new ThreadedPacket(ch, new ArraySegment<byte>(buf, 0, len), connId));
                ByteArrayPool.Store(buf);
            }
        }

        internal void SendToClient(byte channelId, ArraySegment<byte> segment, int connectionId, ChannelPriority priority = ChannelPriority.Default)
        {
            if (GetLocalConnectionState() != LocalConnectionState.Started) return;
            if (connectionId == EpicNet.CLIENT_HOST_ID)
            {
                _clientHostBridge?.ServerSendToClient(new LocalPacket(segment, channelId, priority));
                return;
            }
            Connection conn = default;
            lock (_lock)
            {
                if (!_clientsById.TryGetValue(connectionId, out conn)) return;
            }
            SendWithPriority(_localUserId, conn.RemoteUserId, _socketId, channelId, segment, priority);
        }

        public int GetMaximumClients() => _maximumClients;
        public void SetMaximumClients(int v) => _maximumClients = v;

        internal void InternalReceiveFromClientHost(LocalPacket pkt)
        {
            var seg = new ArraySegment<byte>(pkt.Data, 0, pkt.Length);
            _transport.HandleServerReceivedDataArgs(new ServerReceivedDataArgs(seg, pkt.Channel, EpicNet.CLIENT_HOST_ID, _transport.Index));
        }

        internal string GetConnectionAddress(int id)
        {
            lock (_lock) return _clientsById.TryGetValue(id, out var c) ? c.RemoteUserId?.ToString() ?? string.Empty : string.Empty;
        }

        internal void HandleClientHostConnectionStateChange(LocalConnectionState state)
        {
            if (state is LocalConnectionState.Started)
                _transport.HandleRemoteConnectionState(new RemoteConnectionStateArgs(RemoteConnectionState.Started, EpicNet.CLIENT_HOST_ID, _transport.Index));
            else if (state is LocalConnectionState.Stopped)
                _transport.HandleRemoteConnectionState(new RemoteConnectionStateArgs(RemoteConnectionState.Stopped, EpicNet.CLIENT_HOST_ID, _transport.Index));
        }
    }
}