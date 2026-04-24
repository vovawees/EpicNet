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
    public sealed class ServerPeer : CommonPeer
    {
        static int _nextId = 1;

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
        readonly Dictionary<ProductUserId, ulong> _closeHandles = new Dictionary<ProductUserId, ulong>(64);
        readonly Dictionary<int, ulong> _establishHandles = new Dictionary<int, ulong>(64);
        readonly Dictionary<ProductUserId, float> _pendingTimestamps = new Dictionary<ProductUserId, float>(16);
        readonly HashSet<ProductUserId> _interruptedUsers = new HashSet<ProductUserId>();

        int _maximumClients = short.MaxValue;
        int _connectionsThisSecond;
        float _lastRateLimitReset;
        const int MAX_CONN_PER_SEC = 10;
        const float PENDING_TIMEOUT = 10f;

        internal bool StartConnection()
        {
            if (GetLocalConnectionState() != LocalConnectionState.Stopped) return false;
            _isShuttingDown = false;
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
                if (ct.IsCancellationRequested) return;

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

                var iOpt = new AddNotifyPeerConnectionInterruptedOptions { SocketId = _socketId, LocalUserId = _localUserId };
                _interruptedHandle = p2p.AddNotifyPeerConnectionInterrupted(ref iOpt, null, OnInterrupted);

                _transport.LogDebug("[Server] Started listening");
                SetLocalConnectionState(LocalConnectionState.Started, true);
            }
            catch (OperationCanceledException) { }
            catch (Exception e)
            {
                _transport.LogErr($"[Server] Start failed: {e.Message}");
                SetLocalConnectionState(LocalConnectionState.Stopped, true);
            }
        }

        void OnRequest(ref OnIncomingConnectionRequestInfo data)
        {
            try
            {
                if (_isShuttingDown) return;
                float now = Time.unscaledTime;
                if (now - _lastRateLimitReset > 1f) { _connectionsThisSecond = 0; _lastRateLimitReset = now; }
                if (_connectionsThisSecond >= MAX_CONN_PER_SEC || _clientsById.Count >= _maximumClients) return;
                if (_userToId.ContainsKey(data.RemoteUserId)) return;
                _connectionsThisSecond++;

                var p2p = EOS.GetP2PInterface();
                if (p2p is null) return;

                int id = _nextId++;
                var socketId = data.SocketId ?? _socketId;
                var conn = new Connection(id, data.LocalUserId, data.RemoteUserId, socketId);
                _pendingTimestamps[data.RemoteUserId] = now;

                var eOpt = new AddNotifyPeerConnectionEstablishedOptions { SocketId = socketId, LocalUserId = data.LocalUserId };
                var eH = p2p.AddNotifyPeerConnectionEstablished(ref eOpt, conn, OnEstablished);
                _establishHandles[id] = eH;

                var aOpt = new AcceptConnectionOptions { LocalUserId = _localUserId, RemoteUserId = data.RemoteUserId, SocketId = socketId };
                var aR = p2p.AcceptConnection(ref aOpt);
                if (aR != Result.Success)
                {
                    _establishHandles.Remove(id);
                    _pendingTimestamps.Remove(data.RemoteUserId);
                    p2p.RemoveNotifyPeerConnectionEstablished(eH);
                    _transport.LogErr($"[Server] Accept failed: {data.RemoteUserId} → {aR}");
                    return;
                }
                _clientsById[id] = conn;
                _userToId[data.RemoteUserId] = id;
                _transport.Stats.ActiveConnections = _clientsById.Count;
            }
            catch (Exception e) { Debug.LogError($"[Server] OnRequest: {e.Message}"); }
        }

        void OnEstablished(ref OnPeerConnectionEstablishedInfo data)
        {
            try
            {
                if (_isShuttingDown) return;
                var conn = (Connection)data.ClientData;
                _pendingTimestamps.Remove(conn.RemoteUserId);

                if (_establishHandles.TryGetValue(conn.Id, out var eH))
                {
                    EOS.GetP2PInterface()?.RemoveNotifyPeerConnectionEstablished(eH);
                    _establishHandles.Remove(conn.Id);
                }

                if (data.ConnectionType == ConnectionEstablishedType.Reconnection)
                {
                    _interruptedUsers.Remove(conn.RemoteUserId);
                    _transport.LogDebug($"[Server] Reconnected: {conn.RemoteUserId}");
                    return;
                }

                var p2p = EOS.GetP2PInterface();
                if (p2p is not null)
                {
                    if (_closeHandles.TryGetValue(conn.RemoteUserId, out var old))
                    {
                        p2p.RemoveNotifyPeerConnectionClosed(old);
                        _closeHandles.Remove(conn.RemoteUserId);
                    }
                    var cOpt = new AddNotifyPeerConnectionClosedOptions { SocketId = conn.SocketId, LocalUserId = conn.LocalUserId };
                    _closeHandles[conn.RemoteUserId] = p2p.AddNotifyPeerConnectionClosed(ref cOpt, conn, OnClosed);
                }

                _transport.HandleRemoteConnectionState(new RemoteConnectionStateArgs(
                    RemoteConnectionState.Started, conn.Id, _transport.Index));
                _transport.LogDebug($"[Server] Connected: {conn.RemoteUserId} id={conn.Id}");
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
            _pendingTimestamps.Remove(conn.RemoteUserId);
            if (_closeHandles.TryGetValue(conn.RemoteUserId, out var ch))
            { EOS.GetP2PInterface()?.RemoveNotifyPeerConnectionClosed(ch); _closeHandles.Remove(conn.RemoteUserId); }
            if (_establishHandles.TryGetValue(conn.Id, out var eh))
            { EOS.GetP2PInterface()?.RemoveNotifyPeerConnectionEstablished(eh); _establishHandles.Remove(conn.Id); }
            _transport.Stats.ActiveConnections = _clientsById.Count;
        }

        internal bool StopConnection()
        {
            if (GetLocalConnectionState() is LocalConnectionState.Stopped or LocalConnectionState.Stopping) return false;
            _isShuttingDown = true;
            SetLocalConnectionState(LocalConnectionState.Stopping, true);
            try
            {
                _authCts?.Cancel(); _authCts?.Dispose(); _authCts = null;
                var p2p = EOS.GetP2PInterface();
                foreach (var kvp in _clientsById)
                    _transport.HandleRemoteConnectionState(new RemoteConnectionStateArgs(
                        RemoteConnectionState.Stopped, kvp.Value.Id, _transport.Index));
                foreach (var e in _closeHandles) p2p?.RemoveNotifyPeerConnectionClosed(e.Value);
                foreach (var e in _establishHandles) p2p?.RemoveNotifyPeerConnectionEstablished(e.Value);
                if (_acceptHandle.HasValue) { p2p?.RemoveNotifyPeerConnectionRequest(_acceptHandle.Value); _acceptHandle = null; }
                if (_interruptedHandle.HasValue) { p2p?.RemoveNotifyPeerConnectionInterrupted(_interruptedHandle.Value); _interruptedHandle = null; }
                _clientsById.Clear(); _userToId.Clear(); _closeHandles.Clear();
                _establishHandles.Clear(); _pendingTimestamps.Clear(); _interruptedUsers.Clear();
                ClearQueue(ref _clientHostIncoming); _clientHost?.StopConnection(); DrainRetryQueue();
                if (_localUserId is not null)
                { var co = new CloseConnectionsOptions { SocketId = _socketId, LocalUserId = _localUserId }; p2p?.CloseConnections(ref co); }
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
            { var co = new CloseConnectionOptions { SocketId = _socketId, LocalUserId = conn.LocalUserId, RemoteUserId = conn.RemoteUserId }; p2p.CloseConnection(ref co); }
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
            CleanupPending();
        }

        void CleanupPending()
        {
            if (_pendingTimestamps.Count == 0) return;
            float now = Time.unscaledTime;
            List<ProductUserId> expired = null;
            foreach (var e in _pendingTimestamps)
                if (now - e.Value > PENDING_TIMEOUT)
                    (expired ??= new List<ProductUserId>(4)).Add(e.Key);
            if (expired is null) return;
            foreach (var uid in expired)
            {
                _pendingTimestamps.Remove(uid);
                if (_userToId.TryGetValue(uid, out int id) && _clientsById.TryGetValue(id, out var c))
                { RemoveClient(c); _transport.LogWarn($"[Server] Pending timeout: {uid}"); }
            }
        }

        internal void ProcessClientHostIncoming()
        {
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
            ulong count = GetIncomingPacketQueueCurrentPacketCount();
            for (ulong i = 0; i < count; i++)
            {
                if (!Receive(_localUserId, out var remoteUserId, out var buf, out int len, out byte ch)) continue;
                var seg = new ArraySegment<byte>(buf, 0, len);
                if (!_userToId.TryGetValue(remoteUserId, out int connId))
                { ByteArrayPool.Store(buf); continue; }
                _transport.HandleServerReceivedDataArgs(new ServerReceivedDataArgs(
                    seg, (Channel)ch, connId, _transport.Index));
                ByteArrayPool.Store(buf);
            }
        }

        internal void ReceiveToQueue(ConcurrentQueue<ThreadedPacket> queue, ref EpicNetStatistics stats)
        {
            if (GetLocalConnectionState() != LocalConnectionState.Started) return;
            ProcessClientHostIncoming();
            ulong count = GetIncomingPacketQueueCurrentPacketCount();
            for (ulong i = 0; i < count; i++)
            {
                if (!Receive(_localUserId, out var remoteUserId, out var buf, out int len, out byte ch)) continue;
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
