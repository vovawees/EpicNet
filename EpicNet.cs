using System;
using System.Collections.Concurrent;
using System.Threading;
using Epic.OnlineServices;
using Epic.OnlineServices.P2P;
using FishNet.Managing;
using FishNet.Utility.Performance;
using UnityEngine;

namespace FishNet.Transporting.EpicNetPlugin
{
    [AddComponentMenu("FishNet/Transport/EpicNet")]
    public sealed class EpicNet : Transport
    {
        [Header("Connection")]
        [Tooltip("Maximum number of simultaneously connected players.")]
        [Range(1, 65535)]
        [SerializeField] int _maximumClients = 4095;

        [Tooltip("Shared identifier for the P2P socket. Must be identical on server and all clients.")]
        [SerializeField] string socketName = "EpicNet";

        [Tooltip("Server's EOS ProductUserId. Required for clients to connect.")]
        [SerializeField] string remoteServerProductUserId;

        [Space(5)]
        [Header("Authentication")]
        [Tooltip("Enable automatic EOS login when the transport starts.")]
        [SerializeField] bool autoAuthenticate = true;

        [Tooltip("Credentials, token, timeout, and account creation options.")]
        [SerializeField] AuthData authConnectData = new AuthData();

        [Space(5)]
        [Header("Security")]
        [Tooltip("Maximum new connections allowed per second.")]
        [Range(1, 1000)]
        [SerializeField] int maxConnectionsPerSecond = 50;

        [Tooltip("Additional connections allowed above the per-second limit during bursts.")]
        [Range(0, 100)]
        [SerializeField] int maxBurstConnections = 10;

        [Tooltip("Maximum unconfirmed connections before new requests are rejected.")]
        [Range(1, 10000)]
        [SerializeField] int maxPendingConnections = 256;

        [Tooltip("Time in seconds before a pending connection is dropped if not established.")]
        [Range(1f, 120f)]
        [SerializeField] float pendingConnectionTimeout = 10f;

        [Space(5)]
        [Header("Reliability")]
        [Tooltip("Maximum number of reliable packets waiting for resend.")]
        [Range(1, 100000)]
        [SerializeField] int maxRetryQueueSize = 1024;

        [Tooltip("Maximum retry attempts per packet before forcing disconnection.")]
        [Range(1, 1000)]
        [SerializeField] int maxRetryFrames = 120;

        [Tooltip("How many retry packets are processed per frame.")]
        [Range(1, 500)]
        [SerializeField] int maxRetryProcessPerFrame = 32;

        [Space(5)]
        [Header("Lobbies")]
        [Tooltip("Enable EOS lobby creation. The server will advertise itself publicly.")]
        [SerializeField] bool enableLobbies = false;

        [Tooltip("Name of the lobby. Used if lobbies are enabled.")]
        [SerializeField] string lobbyName = "EpicNetGame";

        [Space(5)]
        [Header("Auto-Reconnect")]
        [Tooltip("Enable automatic reconnection on connection interruptions.")]
        [SerializeField] bool autoReconnect = true;

        [Tooltip("Maximum reconnection attempts before giving up.")]
        [Range(1, 100)]
        [SerializeField] int maxReconnectAttempts = 5;

        [Tooltip("Base delay in seconds before the first reconnection attempt.")]
        [Range(0.1f, 10f)]
        [SerializeField] float reconnectDelayBase = 1f;

        [Tooltip("Maximum delay in seconds between reconnection attempts.")]
        [Range(1f, 120f)]
        [SerializeField] float reconnectDelayMax = 30f;

        [Space(5)]
        [Header("Keep-Alive")]
        [Tooltip("Enable keep-alive checks. Idle clients will be disconnected after timeout.")]
        [SerializeField] bool enableKeepAlive = false;

        [Tooltip("Interval in seconds between keep-alive checks.")]
        [Range(0.1f, 60f)]
        [SerializeField] float keepAliveInterval = 2f;

        [Tooltip("Time in seconds without any packet before a client is considered timed out.")]
        [Range(1f, 300f)]
        [SerializeField] float keepAliveTimeout = 10f;

        [Space(5)]
        [Header("Relay")]
        [Tooltip("Relay policy: NoRelays = direct only, AllowRelays = try direct then relay, ForceRelays = always relay.")]
        [SerializeField] RelayPolicy relayPolicy = RelayPolicy.AllowRelays;

        public enum RelayPolicy { NoRelays, AllowRelays, ForceRelays }
        public RelayPolicy RelayPolicyValue => relayPolicy;

        [Space(5)]
        [Header("Performance")]
        [Tooltip("Maximum number of incoming packets processed per frame. Increase for high-load servers.")]
        [Range(1, 10000)]
        [SerializeField] int maxIncomingPacketsPerFrame = 100;

        [Tooltip("Effective MTU becomes P2P interface max size minus this safety margin.")]
        [Range(0, 500)]
        [SerializeField] int mtuSafetyMargin = 20;

        [Tooltip("Enable lock-free queueing for FishNet's multithreaded transport.")]
        [SerializeField] bool enableThreadedMode;

        [Space(5)]
        [Header("LAN Discovery")]
        [Tooltip("Enable LAN server discovery via UDP broadcast.")]
        [SerializeField] bool enableLanDiscovery = false;

        [Space(10)]
        [Header("Debug")]
        [Tooltip("Controls the verbosity of log messages.")]
        [SerializeField] EpicNetDebugLevel debugLevel = EpicNetDebugLevel.Errors;

        [Tooltip("Display real-time network statistics in the Inspector during Play Mode.")]
        [SerializeField] bool showStats;

        readonly ServerPeer _server = new ServerPeer();
        readonly ClientPeer _client = new ClientPeer();
        readonly ClientHostPeer _clientHost = new ClientHostPeer();
        readonly ClientHostBridge _clientHostBridge = new ClientHostBridge();

        internal const int CLIENT_HOST_ID = short.MaxValue;

        ConcurrentQueue<ThreadedPacket> _threadedServerOut;
        ConcurrentQueue<ThreadedPacket> _threadedClientOut;
        ConcurrentQueue<ThreadedPacket> _threadedServerIn;
        ConcurrentQueue<ThreadedPacket> _threadedClientIn;

        public EpicNetStatistics Stats = new EpicNetStatistics();
        public bool AutoAuthenticate => autoAuthenticate;
        public AuthData AuthConnectData => authConnectData;
        public string SocketName { get => socketName; set => socketName = value; }
        public bool IsThreadedMode => enableThreadedMode;
        public EpicNetDebugLevel DebugLevel => debugLevel;
        public bool EnableLobbies => enableLobbies;
        public string RemoteProductUserId
        {
            get => remoteServerProductUserId;
            set => remoteServerProductUserId = value;
        }

        public string LocalProductUserId =>
            EOS.GetPlatformInterface()?.GetConnectInterface()?.GetLoggedInUserByIndex(0)?.ToString() ?? "";

        public override void Initialize(NetworkManager networkManager, int transportIndex)
        {
            base.Initialize(networkManager, transportIndex);
            _clientHost.Bind(_clientHostBridge);
            _server.SetClientHostBridge(_clientHostBridge);
            _client.Initialize(this);
            _clientHost.Initialize(this);
            _server.Initialize(this);
            _server.SetMaximumClients(_maximumClients);
            _server.SetSecurityLimits(maxConnectionsPerSecond, maxPendingConnections, pendingConnectionTimeout, maxBurstConnections);
            _server.SetRetrySettings(maxRetryQueueSize, maxRetryFrames, maxRetryProcessPerFrame, maxIncomingPacketsPerFrame);
            _server.SetLobbySettings(enableLobbies, lobbyName);
            _server.SetKeepAlive(enableKeepAlive, keepAliveInterval, keepAliveTimeout);
            _server.SetLanDiscovery(enableLanDiscovery);
            _client.SetRetrySettings(maxRetryQueueSize, maxRetryFrames, maxRetryProcessPerFrame, maxIncomingPacketsPerFrame);
            _client.SetReconnectSettings(autoReconnect, maxReconnectAttempts, reconnectDelayBase, reconnectDelayMax);
            if (enableThreadedMode)
            {
                _threadedServerOut = new ConcurrentQueue<ThreadedPacket>();
                _threadedClientOut = new ConcurrentQueue<ThreadedPacket>();
                _threadedServerIn = new ConcurrentQueue<ThreadedPacket>();
                _threadedClientIn = new ConcurrentQueue<ThreadedPacket>();
            }
        }

        void OnDestroy() => Shutdown();

        void Update()
        {
            if (!EOS.IsReady()) { Shutdown(); return; }
            _clientHost.PollServerReady();
            _client.CheckDeferredStop();

            if (enableThreadedMode)
            {
                ProcessThreadedSending();
                ProcessThreadedReceiving();
            }

            if (_server.GetLocalConnectionState() == LocalConnectionState.Started)
            {
                _server.ProcessRetryQueue();
                _server.CleanupPendingConnections();
                _clientHostBridge.ProcessServerIncoming();
            }
            if (_clientHost.GetLocalConnectionState() == LocalConnectionState.Started)
            {
                _clientHostBridge.ProcessClientHostIncoming();
                _clientHost.IterateIncoming();
            }

            if (showStats) Stats.Calculate();
        }

        void ProcessThreadedSending()
        {
            while (_threadedServerOut.TryDequeue(out var pkt))
            {
                _server.SendToClient(pkt.ChannelId, new ArraySegment<byte>(pkt.Data, 0, pkt.Length), pkt.ConnectionId, pkt.Priority);
                Interlocked.Increment(ref Stats.PacketsSent);
                Interlocked.Add(ref Stats.BytesSent, pkt.Length);
                pkt.ReturnToPool();
            }
            while (_threadedClientOut.TryDequeue(out var pkt))
            {
                var seg = new ArraySegment<byte>(pkt.Data, 0, pkt.Length);
                if (_clientHost.GetLocalConnectionState() == LocalConnectionState.Started)
                    _clientHost.SendToServer(pkt.ChannelId, seg, pkt.Priority);
                else
                    _client.SendToServer(pkt.ChannelId, seg, pkt.Priority);
                Interlocked.Increment(ref Stats.PacketsSent);
                Interlocked.Add(ref Stats.BytesSent, pkt.Length);
                pkt.ReturnToPool();
            }
        }

        void ProcessThreadedReceiving()
        {
            if (_server.GetLocalConnectionState() == LocalConnectionState.Started)
                _server.ReceiveToQueue(_threadedServerIn);
            if (_client.GetLocalConnectionState() == LocalConnectionState.Started)
                _client.ReceiveToQueue(_threadedClientIn);
        }

        public override string GetConnectionAddress(int connectionId) =>
            connectionId == CLIENT_HOST_ID ? LocalProductUserId : _server.GetConnectionAddress(connectionId);

        public override event Action<ClientConnectionStateArgs> OnClientConnectionState;
        public override event Action<ServerConnectionStateArgs> OnServerConnectionState;
        public override event Action<RemoteConnectionStateArgs> OnRemoteConnectionState;

        public override LocalConnectionState GetConnectionState(bool server) =>
            server ? _server.GetLocalConnectionState() : _client.GetLocalConnectionState();

        public override RemoteConnectionState GetConnectionState(int connectionId) =>
            _server.GetConnectionState(connectionId);

        public override void HandleClientConnectionState(ClientConnectionStateArgs a) =>
            OnClientConnectionState?.Invoke(a);

        public override void HandleServerConnectionState(ServerConnectionStateArgs a) =>
            OnServerConnectionState?.Invoke(a);

        public override void HandleRemoteConnectionState(RemoteConnectionStateArgs a) =>
            OnRemoteConnectionState?.Invoke(a);

        public override void IterateIncoming(bool server)
        {
            if (enableThreadedMode)
            {
                if (server)
                    DrainIncomingQueue(_threadedServerIn, true);
                else
                    DrainIncomingQueue(_threadedClientIn, false);
            }
            else
            {
                if (server)
                    _server.IterateIncoming();
                else
                {
                    _client.IterateIncoming();
                    _clientHost.IterateIncoming();
                }
            }
        }

        void DrainIncomingQueue(ConcurrentQueue<ThreadedPacket> queue, bool server)
        {
            while (queue.TryDequeue(out var pkt))
            {
                var seg = new ArraySegment<byte>(pkt.Data, 0, pkt.Length);
                if (server)
                    HandleServerReceivedDataArgs(new ServerReceivedDataArgs(seg, (Channel)pkt.ChannelId, pkt.ConnectionId, Index));
                else
                    HandleClientReceivedDataArgs(new ClientReceivedDataArgs(seg, (Channel)pkt.ChannelId, Index));
                pkt.ReturnToPool();
            }
        }

        public override void IterateOutgoing(bool server)
        {
            if (enableThreadedMode) return;
            if (server) _server.IterateOutgoing();
            else _client.IterateOutgoing();
        }

        public override event Action<ClientReceivedDataArgs> OnClientReceivedData;
        public override event Action<ServerReceivedDataArgs> OnServerReceivedData;

        public override void HandleClientReceivedDataArgs(ClientReceivedDataArgs a) =>
            OnClientReceivedData?.Invoke(a);

        public override void HandleServerReceivedDataArgs(ServerReceivedDataArgs a) =>
            OnServerReceivedData?.Invoke(a);

        public override void SendToServer(byte channelId, ArraySegment<byte> segment)
        {
            if (enableThreadedMode)
                _threadedClientOut?.Enqueue(new ThreadedPacket(channelId, segment));
            else
            {
                if (_clientHost.GetLocalConnectionState() == LocalConnectionState.Started)
                    _clientHost.SendToServer(channelId, segment);
                else
                    _client.SendToServer(channelId, segment);
                Interlocked.Increment(ref Stats.PacketsSent);
                Interlocked.Add(ref Stats.BytesSent, segment.Count);
            }
        }

        public override void SendToClient(byte channelId, ArraySegment<byte> segment, int connectionId)
        {
            if (enableThreadedMode)
                _threadedServerOut?.Enqueue(new ThreadedPacket(channelId, segment, connectionId));
            else
            {
                _server.SendToClient(channelId, segment, connectionId);
                Interlocked.Increment(ref Stats.PacketsSent);
                Interlocked.Add(ref Stats.BytesSent, segment.Count);
            }
        }

        public override bool IsLocalTransport(int connectionId) => connectionId == CLIENT_HOST_ID;
        public override int GetMaximumClients() => _maximumClients;
        public override void SetMaximumClients(int value) { _maximumClients = value; _server.SetMaximumClients(value); }
        public override void SetClientAddress(string address) { }
        public override void SetServerBindAddress(string address, IPAddressType addressType) { }
        public override void SetPort(ushort port) { }

        public override bool StartConnection(bool server) => server ? StartServer() : StartClient();
        public override bool StopConnection(bool server) => server ? StopServer() : StopClient();
        public override bool StopConnection(int connectionId, bool immediately) => _server.StopConnection(connectionId);

        public override void Shutdown()
        {
            StopConnection(false);
            StopConnection(true);
            DrainAllQueues();
            _clientHostBridge.Reset();
            Stats.Reset();
        }

        void DrainAllQueues()
        {
            if (_threadedServerOut is null) return;
            while (_threadedServerOut.TryDequeue(out var p)) p.ReturnToPool();
            while (_threadedClientOut.TryDequeue(out var p)) p.ReturnToPool();
            while (_threadedServerIn.TryDequeue(out var p)) p.ReturnToPool();
            while (_threadedClientIn.TryDequeue(out var p)) p.ReturnToPool();
        }

        bool StartServer()
        {
            if (_server.GetLocalConnectionState() != LocalConnectionState.Stopped)
            {
                NetworkManager.LogError("Server is already running.");
                return false;
            }
            bool clientWasRunning = _client.GetLocalConnectionState() != LocalConnectionState.Stopped;
            if (clientWasRunning) _client.StopConnection();
            bool result = _server.StartConnection();
            if (result && clientWasRunning) StartConnection(false);
            return result;
        }

        bool StopServer() => _server.StopConnection();

        bool StartClient()
        {
            if (_server.GetLocalConnectionState() == LocalConnectionState.Stopped)
            {
                if (_client.GetLocalConnectionState() != LocalConnectionState.Stopped)
                {
                    NetworkManager.LogError("Client is already running.");
                    return false;
                }
                if (string.IsNullOrWhiteSpace(remoteServerProductUserId))
                {
                    NetworkManager.LogError("[EpicNet] RemoteProductUserId is empty.");
                    return false;
                }
                var testId = ProductUserId.FromString(remoteServerProductUserId);
                if (testId == null || !testId.IsValid())
                {
                    NetworkManager.LogError($"[EpicNet] Invalid RemoteProductUserId: '{remoteServerProductUserId}'");
                    return false;
                }
                if (_clientHost.GetLocalConnectionState() != LocalConnectionState.Stopped)
                    _clientHost.StopConnection();
                _client.StartConnection();
            }
            else
            {
                _clientHost.StartConnection(_server);
            }
            return true;
        }

        bool StopClient()
        {
            bool r = _client.StopConnection();
            r |= _clientHost.StopConnection();
            return r;
        }

        public override int GetMTU(byte channel) => P2PInterface.MAX_PACKET_SIZE - mtuSafetyMargin;

        internal void LogDebug(string msg)
        { if (debugLevel >= EpicNetDebugLevel.Verbose) Debug.Log(msg); }
        internal void LogWarn(string msg)
        { if (debugLevel >= EpicNetDebugLevel.Warnings) Debug.LogWarning(msg); }
        internal void LogErr(string msg)
        { if (debugLevel >= EpicNetDebugLevel.Errors) Debug.LogError(msg); }
    }
}