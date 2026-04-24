using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Epic.OnlineServices;
using Epic.OnlineServices.P2P;
using FishNet.Utility.Performance;
using UnityEngine;

namespace FishNet.Transporting.EpicNetPlugin
{
    internal sealed class ServerPeer : CommonPeer
    {
        int _nextId = 1;

        SocketId _socketId;
        ProductUserId _localUserId;
        Queue<LocalPacket> _clientHostIncoming = new Queue<LocalPacket>(64);
        ClientHostPeer _clientHost;
        volatile bool _isShuttingDown;
        CancellationTokenSource _authCts;

        ulong? _acceptHandle;
        ulong? _interruptedHandle;

        readonly Dictionary<int, Connection> _clientsById = new Dictionary<int, Connection>(64);
        readonly Dictionary<ProductUserId, int> _userToId = new Dictionary<ProductUserId, int>(64);
        ulong? _establishHandle;
        ulong? _closeHandle;
        readonly HashSet<ProductUserId> _interruptedUsers = new HashSet<ProductUserId>();

        readonly Dictionary<ProductUserId, Connection> _pendingConnections = new Dictionary<ProductUserId, Connection>(16);
        readonly Dictionary<ProductUserId, float> _pendingTimestamps = new Dictionary<ProductUserId, float>(16);
        readonly List<ProductUserId> _expiredCache = new List<ProductUserId>(8);

        int _maximumClients = short.MaxValue;
        int _connectionsThisSecond;
        float _lastRateLimitReset;

        int _maxConnPerSec = 50;
        int _maxPending = 256;
        float _pendingTimeout = 10f;

        internal void SetSecurityLimits(int maxConnPerSec, int maxPending, float timeout)
        {
            _maxConnPerSec = maxConnPerSec;
            _maxPending = maxPending;
            _pendingTimeout = timeout;
        }

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
                if (ct.IsCancellationRequested)
                {
                    SetLocalConnectionState(LocalConnectionState.Stopped, true);
                    return;
                }

                _localUserId = EOS.LocalProductUserId;
                _socketId = new SocketId { SocketName = _transport.SocketName };

                var p2p = EOS.GetP2PInterface();
                if (p2p is null)
                {
                    _transport.LogErr("[Server] P2P interface unavailable");
                    SetLocalConnectionState(LocalConnectionState.Stopped, true);
                    return;
                }

                var qOpt = new SetPacketQueueSizeOptions
                { IncomingPacketQueueMaxSizeBytes = 4 * 1024 * 1024, OutgoingPacketQueueMaxSizeBytes = 4 * 1024 * 1024 };
                p2p.SetPacketQueueSize(ref qOpt);

                var rOpt = new AddNotifyPeerConnectionRequestOptions { SocketId = _socketId, LocalUserId = _localUserId };
                _acceptHandle = p2p.AddNotifyPeerConnectionRequest(ref rOpt, null, OnRequest);

                var eOpt = new AddNotifyPeerConnectionEstablishedOptions { SocketId = _socketId, LocalUserId = _localUserId };
                _establishHandle = p2p.AddNotifyPeerConnectionEstablished(ref eOpt, null, OnEstablished);

                var cOpt = new AddNotifyPeerConnectionClosedOptions { SocketId = _socketId, LocalUserId = _localUserId };
                _closeHandle = p2p.AddNotifyPeerConnectionClosed(ref cOpt, null, OnClosed);

                var iOpt = new AddNotifyPeerConnectionInterruptedOptions { SocketId = _socketId, LocalUserId = _localUserId };
                _interruptedHandle = p2p.AddNotifyPeerConnectionInterrupted(ref iOpt, null, OnInterrupted);

                _transport.LogDebug("[Server] Started listening");
                SetLocalConnectionState(LocalConnectionState.Started, true);
            }
            catch (OperationCanceledException)
            {
                SetLocalConnectionState(LocalConnectionState.Stopped, true);
            }
            catch (Exception e)
            {
                _transport.LogErr($"[Server] Start failed: {e.Message}");
                SetLocalConnectionState(LocalConnectionState.Stopped, true);
            }
        }

        void RejectRequest(ref OnIncomingConnectionRequestInfo reqData)
        {
            var co = new CloseConnectionOptions { SocketId = _socketId, LocalUserId = _localUserId, RemoteUserId = reqData.RemoteUserId };
            EOS.GetP2PInterface()?.CloseConnection(ref co);
        }

        void OnRequest(ref OnIncomingConnectionRequestInfo data)
        {
            try
            {
                if (_isShuttingDown) return;

                float now = Time.unscaledTime;
                if (now - _lastRateLimitReset > 1f) { _connectionsThisSecond = 0; _lastRateLimitReset = now; }
                if (_connectionsThisSecond >= _maxConnPerSec) { RejectRequest(ref data); return; }
                if (_clientsById.Count + _pendingConnections.Count >= _maximumClients) { RejectRequest(ref data); return; }
                if (_pendingConnections.Count >= _maxPending) { RejectRequest(ref data); return; }
                if (_userToId.ContainsKey(data.RemoteUserId)) { RejectRequest(ref data); return; }
                if (_pendingConnections.ContainsKey(data.RemoteUserId)) { RejectRequest(ref data); return; }

                // Security: force local socket after name verification
                if ((data.SocketId?.SocketName ?? _socketId.SocketName) != _socketId.SocketName)
                {
                    _transport.LogWarn($"[Server] SECURITY: Rejected mismatched SocketId from {data.RemoteUserId}");
                    RejectRequest(ref data);
                    return;
                }

                _connectionsThisSecond++;

                var p2p = EOS.GetP2PInterface();
                if (p2p is null) return;

                // ID assignment before accept to store in pending; worst case we waste an ID (not critical)
                int id = _nextId++;
                if (id == EpicNet.CLIENT_HOST_ID) id = _nextId++;

                var conn = new Connection(id, data.LocalUserId, data.RemoteUserId, _socketId);
                _pendingConnections[data.RemoteUserId] = conn;
                _pendingTimestamps[data.RemoteUserId] = now;

                var aOpt = new AcceptConnectionOptions { LocalUserId = _localUserId, RemoteUserId = data.RemoteUserId, SocketId = _socketId };
                var aR = p2p.AcceptConnection(ref aOpt);
                if (aR != Result.Success)
                {
                    _pendingConnections.Remove(data.RemoteUserId);
                    _pendingTimestamps.Remove(data.RemoteUserId);
                    _transport.LogErr($"[Server] Accept failed: {data.RemoteUserId} → {aR}");
                    return;
                }

                _transport.LogDebug($"[Server] Pending connection: {data.RemoteUserId} id={id}");
            }
            catch (Exception e) { Debug.LogError($"[Server] OnRequest: {e.Message}"); }
        }

        void OnEstablished(ref OnPeerConnectionEstablishedInfo data)
        {
            try
            {
                if (_isShuttingDown) return;

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

                _transport.HandleRemoteConnectionState(new RemoteConnectionStateArgs(
                    RemoteConnectionState.Started, conn.Id, _transport.Index));
                _transport.LogDebug($"[Server] Connected: {data.RemoteUserId} id={conn.Id}");
            }
            catch (Exception e) { Debug.LogError($"[Server] OnEstablished: {e.Message}"); }
        }

        void OnClosed(ref OnRemoteConnectionClosedInfo data)
        {
            try
            {
                if (_isShuttingDown) return;
                if (!_userToId.TryGetValue(data.RemoteUserId, out int id)) return;
                if (!_clientsById.TryGetValue(id, out var conn)) return;
                RemoveClient(conn);
                _transport.HandleRemoteConnectionState(new RemoteConnectionStateArgs(
                    RemoteConnectionState.Stopped, conn.Id, _transport.Index));
            }
            catch (Exception e) { Debug.LogError($"[Server] OnClosed: {e.Message}"); }
        }

        void OnInterrupted(ref OnPeerConnectionInterruptedInfo data)
        {
            try
            {
                if (_isShuttingDown) return;
                _interruptedUsers.Add(data.RemoteUserId);
                _transport.LogWarn($"[Server] Interrupted: {data.RemoteUserId}");
            }
            catch (Exception e) { Debug.LogError($"[Server] OnInterrupted: {e.Message}"); }
        }

        void RemoveClient(Connection conn)
        {
            _clientsById.Remove(conn.Id);
            _userToId.Remove(conn.RemoteUserId);
            _interruptedUsers.Remove(conn.RemoteUserId);
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
                // Cancel auth
                var oldCts = Interlocked.Exchange(ref _authCts, null);
                oldCts?.Cancel();
                oldCts?.Dispose();

                var p2p = EOS.GetP2PInterface();

                // 1. Remove notifications BEFORE raising shutdown flag
                if (_closeHandle.HasValue) { p2p?.RemoveNotifyPeerConnectionClosed(_closeHandle.Value); _closeHandle = null; }
                if (_establishHandle.HasValue) { p2p?.RemoveNotifyPeerConnectionEstablished(_establishHandle.Value); _establishHandle = null; }
                if (_acceptHandle.HasValue) { p2p?.RemoveNotifyPeerConnectionRequest(_acceptHandle.Value); _acceptHandle = null; }
                if (_interruptedHandle.HasValue) { p2p?.RemoveNotifyPeerConnectionInterrupted(_interruptedHandle.Value); _interruptedHandle = null; }

                // 2. Now safe to mark as shutting down
                _isShuttingDown = true;

                // Disconnect all clients
                var clientsToStop = new List<Connection>(_clientsById.Values);
                foreach (var conn in clientsToStop)
                    _transport.HandleRemoteConnectionState(new RemoteConnectionStateArgs(
                        RemoteConnectionState.Stopped, conn.Id, _transport.Index));

                _clientsById.Clear(); _userToId.Clear();
                _pendingTimestamps.Clear(); _interruptedUsers.Clear();
                _pendingConnections.Clear();
                ClearQueue(ref _clientHostIncoming); _clientHost?.StopConnection(); DrainRetryQueue();

                if (_localUserId is not null)
                {
                    var co = new CloseConnectionsOptions { SocketId = _socketId, LocalUserId = _localUserId };
                    p2p?.CloseConnections(ref co);
                }
                _transport.Stats.ActiveConnections = 0;
            }
            catch (Exception e) { _transport.LogErr($"[Server] Stop error: {e.Message}"); }
            SetLocalConnectionState(LocalConnectionState.Stopped, true);
            return true;
        }

        internal bool StopConnection(int connectionId)
        {
            if (connectionId == EpicNet.CLIENT_HOST_ID) { _clientHost?.StopConnection(); return true; }
            if (!_clientsById.TryGetValue(connectionId, out var conn)) return false;
            var p2p = EOS.GetP2PInterface();
            if (p2p is not null)
            {
                var co = new CloseConnectionOptions { SocketId = _socketId, LocalUserId = conn.LocalUserId, RemoteUserId = conn.RemoteUserId };
                p2p.CloseConnection(ref co);
            }
            RemoveClient(conn);
            _transport.HandleRemoteConnectionState(new RemoteConnectionStateArgs(
                RemoteConnectionState.Stopped, connectionId, _transport.Index));
            return true;
        }

        internal RemoteConnectionState GetConnectionState(int id) =>
            _clientsById.ContainsKey(id) ? RemoteConnectionState.Started : RemoteConnectionState.Stopped;

        internal void IterateOutgoing()
        {
            if (GetLocalConnectionState() != LocalConnectionState.Started) return;
            ProcessRetryQueue();
            CleanupPendingConnections();
        }

        internal void CleanupPendingConnections()
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
                _transport.LogWarn($"[Server] Pending timeout: {uid}");
            }
        }

        internal void ProcessClientHostIncoming()
        {
            if (_clientHost is null) return;
            while (_clientHostIncoming.Count > 0)
            {
                var pkt = _clientHostIncoming.Dequeue();
                var seg = new ArraySegment<byte>(pkt.Data, 0, pkt.Length);
                _transport.HandleServerReceivedDataArgs(new ServerReceivedDataArgs(
                    seg, pkt.Channel, EpicNet.CLIENT_HOST_ID, _transport.Index));
                pkt.ReturnToPool();
            }
        }

        internal void IterateIncoming()
        {
            if (GetLocalConnectionState() != LocalConnectionState.Started) return;
            ProcessClientHostIncoming();
            while (Receive(_localUserId, out var remoteUserId, out var buf, out int len, out byte ch))
            {
                var seg = new ArraySegment<byte>(buf, 0, len);
                if (!_userToId.TryGetValue(remoteUserId, out int connId))
                { ByteArrayPool.Store(buf); continue; }
                _transport.HandleServerReceivedDataArgs(new ServerReceivedDataArgs(
                    seg, (Channel)ch, connId, _transport.Index));
                ByteArrayPool.Store(buf);
            }
        }

        internal void ReceiveToQueue(ConcurrentQueue<ThreadedPacket> queue)
        {
            if (GetLocalConnectionState() != LocalConnectionState.Started) return;
            ProcessClientHostIncoming();
            while (Receive(_localUserId, out var remoteUserId, out var buf, out int len, out byte ch))
            {
                if (!_userToId.TryGetValue(remoteUserId, out int connId))
                { ByteArrayPool.Store(buf); continue; }
                queue.Enqueue(new ThreadedPacket { ChannelId = ch, Data = buf, Length = len, ConnectionId = connId });
            }
        }

        internal void SendToClient(byte channelId, ArraySegment<byte> segment, int connectionId)
        {
            if (GetLocalConnectionState() != LocalConnectionState.Started) return;
            if (connectionId == EpicNet.CLIENT_HOST_ID)
            { _clientHost?.ReceivedFromLocalServer(new LocalPacket(segment, channelId)); return; }
            if (!_clientsById.TryGetValue(connectionId, out var conn)) return;
            if (_interruptedUsers.Contains(conn.RemoteUserId)) return;
            var r = Send(_localUserId, conn.RemoteUserId, _socketId, channelId, segment);
            if (r is Result.NoConnection or Result.InvalidParameters) StopConnection(connectionId);
        }

        public int GetMaximumClients() => _maximumClients;
        public void SetMaximumClients(int v) => _maximumClients = v;
        internal void SetClientHostPeer(ClientHostPeer p) => _clientHost = p;

        internal void ReceivedFromClientHost(LocalPacket pkt)
        {
            if (_clientHost is null || _clientHost.GetLocalConnectionState() != LocalConnectionState.Started) return;
            _clientHostIncoming.Enqueue(pkt);
        }

        internal void HandleClientHostConnectionStateChange(LocalConnectionState state, bool server)
        {
            if (state is LocalConnectionState.Started)
                _transport.HandleRemoteConnectionState(new RemoteConnectionStateArgs(
                    RemoteConnectionState.Started, EpicNet.CLIENT_HOST_ID, _transport.Index));
            else if (state is LocalConnectionState.Stopped)
                _transport.HandleRemoteConnectionState(new RemoteConnectionStateArgs(
                    RemoteConnectionState.Stopped, EpicNet.CLIENT_HOST_ID, _transport.Index));
        }

        internal string GetConnectionAddress(int id) =>
            _clientsById.TryGetValue(id, out var c) ? c.RemoteUserId?.ToString() ?? string.Empty : string.Empty;
    }
}