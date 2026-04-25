using System;
using System.Threading;
using System.Threading.Tasks;
using System.Collections.Concurrent;
using Epic.OnlineServices;
using Epic.OnlineServices.P2P;
using FishNet.Utility.Performance;
using UnityEngine;

namespace FishNet.Transporting.EpicNetPlugin
{
    internal sealed class ClientPeer : CommonPeer
    {
        SocketId _socketId;
        ProductUserId _localUserId, _remoteUserId;
        ulong? _establishedHandle, _closedHandle, _interruptedHandle;
        volatile bool _isShuttingDown;
        CancellationTokenSource _authCts;
        bool _isInterrupted;
        volatile bool _deferredStopRequested;

        bool _autoReconnect;
        int _maxReconnectAttempts = 5;
        float _reconnectDelayBase = 1f, _reconnectDelayMax = 30f;
        int _reconnectAttempt;
        float _reconnectTimer;
        bool _manuallyStarted;

        public void SetReconnectSettings(bool auto, int maxAttempts, float baseDelay, float maxDelay)
        {
            _autoReconnect = auto;
            _maxReconnectAttempts = maxAttempts;
            _reconnectDelayBase = baseDelay;
            _reconnectDelayMax = maxDelay;
        }

        internal void StartConnection()
        {
            if (GetLocalConnectionState() != LocalConnectionState.Stopped) return;
            _isShuttingDown = false;
            _isInterrupted = false;
            _deferredStopRequested = false;
            _manuallyStarted = true;
            _reconnectAttempt = 0;
            _reconnectTimer = 0f;
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
                    {
                        _transport.LogErr($"[Client] Auth failed: {r}");
                        SetLocalConnectionState(LocalConnectionState.Stopped, false);
                        return;
                    }
                }
                if (ct.IsCancellationRequested)
                {
                    SetLocalConnectionState(LocalConnectionState.Stopped, false);
                    return;
                }

                _localUserId = EOS.LocalProductUserId;
                if (_localUserId == null || !_localUserId.IsValid())
                {
                    _transport.LogErr("[Client] Invalid local user.");
                    SetLocalConnectionState(LocalConnectionState.Stopped, false);
                    return;
                }
                _remoteUserId = ProductUserId.FromString(_transport.RemoteProductUserId);
                if (_remoteUserId == null || !_remoteUserId.IsValid())
                {
                    _transport.LogErr("[Client] Invalid RemoteProductUserId.");
                    SetLocalConnectionState(LocalConnectionState.Stopped, false);
                    return;
                }

                _socketId = new SocketId { SocketName = _transport.SocketName };
                var p2p = EOS.GetP2PInterface();
                if (p2p is null) { SetLocalConnectionState(LocalConnectionState.Stopped, false); return; }

                p2p.SetRelayControl(new SetRelayControlOptions { RelayControl = (RelayControl)_transport.RelayPolicyValue });

                CleanupHandles();

                var establishedOptions = new AddNotifyPeerConnectionEstablishedOptions { LocalUserId = _localUserId, SocketId = _socketId };
                _establishedHandle = p2p.AddNotifyPeerConnectionEstablished(ref establishedOptions, null, OnEstablished);

                var closedOptions = new AddNotifyPeerConnectionClosedOptions { LocalUserId = _localUserId, SocketId = _socketId };
                _closedHandle = p2p.AddNotifyPeerConnectionClosed(ref closedOptions, null, OnClosed);

                var interruptedOptions = new AddNotifyPeerConnectionInterruptedOptions { LocalUserId = _localUserId, SocketId = _socketId };
                _interruptedHandle = p2p.AddNotifyPeerConnectionInterrupted(ref interruptedOptions, null, OnInterrupted);

                var aOpt = new AcceptConnectionOptions { LocalUserId = _localUserId, RemoteUserId = _remoteUserId, SocketId = _socketId };
                var aR = p2p.AcceptConnection(ref aOpt);
                if (aR != Result.Success)
                {
                    _transport.LogErr($"[Client] Accept failed: {aR}");
                    CleanupHandles();
                    SetLocalConnectionState(LocalConnectionState.Stopped, false);
                    return;
                }

                _transport.LogDebug("[Client] Connected to server");
            }
            catch (OperationCanceledException) { SetLocalConnectionState(LocalConnectionState.Stopped, false); }
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

        internal void CheckDeferredStop()
        {
            if (_deferredStopRequested && GetLocalConnectionState() == LocalConnectionState.Started)
            {
                _deferredStopRequested = false;
                StopConnection();
            }
        }

        internal void IterateOutgoing()
        {
            ProcessReconnect();
            if (GetLocalConnectionState() != LocalConnectionState.Started || _isInterrupted) return;
            ProcessRetryQueue();
        }

        void ProcessReconnect()
        {
            if (!_autoReconnect || !_isInterrupted) return;
            if (!_manuallyStarted)
            {
                StopConnection();
                return;
            }
            _reconnectTimer += Time.unscaledDeltaTime;
            float delay = Mathf.Min(_reconnectDelayBase * Mathf.Pow(2, _reconnectAttempt), _reconnectDelayMax);
            if (_reconnectTimer < delay) return;
            _reconnectTimer = 0f;
            _reconnectAttempt++;

            if (_reconnectAttempt >= _maxReconnectAttempts)
            {
                _transport.LogErr($"[Client] Reconnect timeout after {_maxReconnectAttempts} attempts.");
                _isInterrupted = false;
                StopConnection();
            }
            else
            {
                _transport.LogDebug($"[Client] Awaiting EOS reconnect... attempt {_reconnectAttempt}/{_maxReconnectAttempts}");
            }
        }

        void OnEstablished(ref OnPeerConnectionEstablishedInfo data)
        {
            try
            {
                if (_isShuttingDown) return;
                if (data.ConnectionType == ConnectionEstablishedType.Reconnection)
                {
                    _isInterrupted = false;
                    _reconnectAttempt = 0;
                    _reconnectTimer = 0f;
                    _transport.LogDebug("[Client] Reconnected");
                    return;
                }
                _isInterrupted = false;
                _reconnectAttempt = 0;
                _reconnectTimer = 0f;
                SetLocalConnectionState(LocalConnectionState.Started, false);
            }
            catch (Exception e) { Debug.LogError($"[Client] OnEstablished: {e.Message}"); }
        }

        void OnClosed(ref OnRemoteConnectionClosedInfo data)
        {
            try
            {
                if (_isShuttingDown) return;
                _transport.LogDebug($"[Client] Disconnected: {data.Reason}");
                _deferredStopRequested = true;
            }
            catch (Exception e) { Debug.LogError($"[Client] OnClosed: {e.Message}"); }
        }

        void OnInterrupted(ref OnPeerConnectionInterruptedInfo data)
        {
            try
            {
                if (!_isShuttingDown)
                {
                    _isInterrupted = true;
                    _transport.LogWarn("[Client] Interrupted, awaiting reconnect...");
                }
            }
            catch (Exception e) { Debug.LogError($"[Client] OnInterrupted: {e.Message}"); }
        }

        internal bool StopConnection()
        {
            if (GetLocalConnectionState() is LocalConnectionState.Stopped or LocalConnectionState.Stopping) return false;
            _isShuttingDown = true;
            _manuallyStarted = false;
            SetLocalConnectionState(LocalConnectionState.Stopping, false);
            var oldCts = Interlocked.Exchange(ref _authCts, null);
            oldCts?.Cancel();
            oldCts?.Dispose();
            CleanupHandles();
            DrainRetryQueue();
            if (_localUserId is not null && _remoteUserId is not null)
            {
                var p2p = EOS.GetP2PInterface();
                if (p2p is not null)
                {
                    var co = new CloseConnectionOptions { SocketId = _socketId, LocalUserId = _localUserId, RemoteUserId = _remoteUserId };
                    p2p.CloseConnection(ref co);
                }
            }
            SetLocalConnectionState(LocalConnectionState.Stopped, false);
            return true;
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
                queue.Enqueue(new ThreadedPacket(ch, new ArraySegment<byte>(buf, 0, len), -1));
                ByteArrayPool.Store(buf);
            }
        }

        internal void SendToServer(byte channelId, ArraySegment<byte> segment, ChannelPriority priority = ChannelPriority.Default)
        {
            if (GetLocalConnectionState() != LocalConnectionState.Started || _isInterrupted) return;
            SendWithPriority(_localUserId, _remoteUserId, _socketId, channelId, segment, priority);
        }
    }
}