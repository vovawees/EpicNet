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
        [Tooltip("Maximum number of players which may be connected at once.")]
        [Range(1, 9999)]
        [SerializeField] int _maximumClients = 4095;

        [Header("EOS")]
        [Tooltip("Socket ID [Must be the same on all clients and server].")]
        [SerializeField] string socketName = "EpicNet";

        [Tooltip("Server Product User ID. Must be set for remote clients.")]
        [SerializeField] string remoteServerProductUserId;

        [Tooltip("Automatically Authenticate/Login to EOS Connect.")]
        [SerializeField] bool autoAuthenticate = true;

        [Tooltip("Auth Connect Data.")]
        [SerializeField] AuthData authConnectData = new AuthData();

        [Header("Security")]
        [Tooltip("Maximum connection requests accepted per second to prevent DDoS.")]
        [SerializeField] int maxConnectionsPerSecond = 50;

        [Tooltip("Maximum simultaneous pending connections before dropping new requests.")]
        [SerializeField] int maxPendingConnections = 256;

        [Tooltip("Seconds before a pending connection is dropped.")]
        [SerializeField] float pendingConnectionTimeout = 10f;

        [Header("Performance")]
        [Tooltip("MTU safety margin in bytes.")]
        [Range(0, 100)]
        [SerializeField] int mtuSafetyMargin = 20;

        [Tooltip("Enable thread-safe mode for FishNet Multithreading support.")]
        [SerializeField] bool enableThreadedMode;

        [Header("Debug")]
        [Tooltip("Debug logging level.")]
        [SerializeField] EpicNetDebugLevel debugLevel = EpicNetDebugLevel.Errors;

        [Tooltip("Show runtime statistics in Inspector.")]
        [SerializeField] bool showStats;

        readonly ServerPeer _server = new ServerPeer();
        readonly ClientPeer _client = new ClientPeer();
        readonly ClientHostPeer _clientHost = new ClientHostPeer();

        internal const int CLIENT_HOST_ID = short.MaxValue;
        int _mainThreadId;

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

        public string LocalProductUserId =>
            EOS.GetPlatformInterface()?.GetConnectInterface()?.GetLoggedInUserByIndex(0)?.ToString() ?? string.Empty;

        public string RemoteProductUserId
        {
            get => remoteServerProductUserId;
            set => remoteServerProductUserId = value;
        }

        public override void Initialize(NetworkManager networkManager, int transportIndex)
        {
            base.Initialize(networkManager, transportIndex);
            _mainThreadId = Thread.CurrentThread.ManagedThreadId;
            _client.Initialize(this);
            _clientHost.Initialize(this);
            _server.Initialize(this);
            _server.SetMaximumClients(_maximumClients);
            _server.SetSecurityLimits(maxConnectionsPerSecond, maxPendingConnections, pendingConnectionTimeout);

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
            _clientHost.PollServerReady();

            if (enableThreadedMode)
            {
                ProcessThreadedSending();
                ProcessThreadedReceiving();

                if (_server.GetLocalConnectionState() == LocalConnectionState.Started)
                {
                    _server.ProcessRetryQueue();
                    _server.CleanupPendingConnections();
                }
                if (_client.GetLocalConnectionState() == LocalConnectionState.Started)
                    _client.ProcessRetryQueue();
            }

            if (showStats) Stats.Calculate();
        }

        void ProcessThreadedSending()
        {
            while (_threadedServerOut.TryDequeue(out var pkt))
            {
                var seg = new ArraySegment<byte>(pkt.Data, 0, pkt.Length);
                _server.SendToClient(pkt.ChannelId, seg, pkt.ConnectionId);
                Interlocked.Increment(ref Stats.PacketsSent);
                Interlocked.Add(ref Stats.BytesSent, pkt.Length);
                pkt.ReturnToPool();
            }

            while (_threadedClientOut.TryDequeue(out var pkt))
            {
                var seg = new ArraySegment<byte>(pkt.Data, 0, pkt.Length);
                if (_clientHost.GetLocalConnectionState() == LocalConnectionState.Started)
                    _clientHost.SendToServer(pkt.ChannelId, seg);
                else
                    _client.SendToServer(pkt.ChannelId, seg);
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
                {
                    _server.ProcessClientHostIncoming();
                    DrainIncomingQueue(_threadedServerIn, true);
                }
                else
                {
                    _clientHost.IterateIncoming();
                    DrainIncomingQueue(_threadedClientIn, false);
                }
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
                    HandleServerReceivedDataArgs(new ServerReceivedDataArgs(
                        seg, (Channel)pkt.ChannelId, pkt.ConnectionId, Index));
                else
                    HandleClientReceivedDataArgs(new ClientReceivedDataArgs(
                        seg, (Channel)pkt.ChannelId, Index));
                pkt.ReturnToPool();
            }
        }

        public override void IterateOutgoing(bool server)
        {
            if (enableThreadedMode) return;

            if (server)
                _server.IterateOutgoing();
            else
                _client.IterateOutgoing();
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
            {
                _threadedClientOut.Enqueue(new ThreadedPacket(channelId, segment));
            }
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
            {
                _threadedServerOut.Enqueue(new ThreadedPacket(channelId, segment, connectionId));
            }
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
        {
            if (debugLevel >= EpicNetDebugLevel.Verbose) Debug.Log(msg);
        }

        internal void LogWarn(string msg)
        {
            if (debugLevel >= EpicNetDebugLevel.Warnings) Debug.LogWarning(msg);
        }

        internal void LogErr(string msg)
        {
            if (debugLevel >= EpicNetDebugLevel.Errors) Debug.LogError(msg);
        }
    }
}

#if UNITY_EDITOR
namespace FishNet.Transporting.EpicNetPlugin.EditorOnly
{
    using UnityEditor;

    [CustomEditor(typeof(EpicNet))]
    public class EpicNetEditor : Editor
    {
        SerializedProperty _maxClients, _socketName, _remoteUserId, _autoAuth, _authData;
        SerializedProperty _maxConnPerSec, _maxPending, _pendingTimeout;
        SerializedProperty _mtuMargin, _threadedMode, _debugLevel, _showStats;

        void OnEnable()
        {
            _maxClients = serializedObject.FindProperty("_maximumClients");
            _socketName = serializedObject.FindProperty("socketName");
            _remoteUserId = serializedObject.FindProperty("remoteServerProductUserId");
            _autoAuth = serializedObject.FindProperty("autoAuthenticate");
            _authData = serializedObject.FindProperty("authConnectData");

            _maxConnPerSec = serializedObject.FindProperty("maxConnectionsPerSecond");
            _maxPending = serializedObject.FindProperty("maxPendingConnections");
            _pendingTimeout = serializedObject.FindProperty("pendingConnectionTimeout");

            _mtuMargin = serializedObject.FindProperty("mtuSafetyMargin");
            _threadedMode = serializedObject.FindProperty("enableThreadedMode");
            _debugLevel = serializedObject.FindProperty("debugLevel");
            _showStats = serializedObject.FindProperty("showStats");
        }

        public override void OnInspectorGUI()
        {
            serializedObject.Update();
            var t = (EpicNet)target;

            EditorGUILayout.LabelField("EpicNet v0.4.0", EditorStyles.boldLabel);
            EditorGUILayout.Space(4);

            DrawSection("Connection", () =>
            {
                EditorGUILayout.PropertyField(_maxClients);
                EditorGUILayout.PropertyField(_socketName);

                bool isServer = t.GetConnectionState(true) != LocalConnectionState.Stopped;
                if (!isServer)
                    EditorGUILayout.PropertyField(_remoteUserId);
                else
                    EditorGUILayout.LabelField("Server ProductUserId", t.LocalProductUserId);
            });

            DrawSection("Authentication", () =>
            {
                EditorGUILayout.PropertyField(_autoAuth);
                if (_autoAuth.boolValue)
                    EditorGUILayout.PropertyField(_authData, true);
            });

            DrawSection("Security (DDoS Mitigation)", () =>
            {
                EditorGUILayout.PropertyField(_maxConnPerSec);
                EditorGUILayout.PropertyField(_maxPending);
                EditorGUILayout.PropertyField(_pendingTimeout);
            });

            DrawSection("Performance", () =>
            {
                EditorGUILayout.PropertyField(_mtuMargin);
                EditorGUILayout.LabelField("Effective MTU", $"{1170 - _mtuMargin.intValue} bytes");
                EditorGUILayout.Space(2);
                EditorGUILayout.PropertyField(_threadedMode);
                if (_threadedMode.boolValue)
                    EditorGUILayout.HelpBox(
                        "Threaded mode enables FishNet Multithreading support. " +
                        "EOS calls are marshalled to main thread via ConcurrentQueue.",
                        MessageType.Info);
            });

            DrawSection("Debug", () =>
            {
                EditorGUILayout.PropertyField(_debugLevel);
                EditorGUILayout.PropertyField(_showStats);
            });

            if (Application.isPlaying && _showStats.boolValue)
            {
                DrawSection("Runtime Statistics", () =>
                {
                    var s = t.Stats;
                    EditorGUILayout.LabelField("Packets Sent", s.PacketsSent.ToString());
                    EditorGUILayout.LabelField("Packets Received", s.PacketsReceived.ToString());
                    EditorGUILayout.LabelField("Bytes Sent", FormatBytes(s.BytesSent));
                    EditorGUILayout.LabelField("Bytes Received", FormatBytes(s.BytesReceived));
                    EditorGUILayout.LabelField("Send Rate", $"{s.SendRate:F0} pkt/s");
                    EditorGUILayout.LabelField("Receive Rate", $"{s.ReceiveRate:F0} pkt/s");
                    EditorGUILayout.LabelField("Retry Queue", s.RetryQueueSize.ToString());
                    EditorGUILayout.LabelField("Active Connections", s.ActiveConnections.ToString());
                    EditorGUILayout.LabelField("Dropped Packets", s.DroppedPackets.ToString());

                    EditorGUILayout.Space(4);
                    string serverState = t.GetConnectionState(true).ToString();
                    string clientState = t.GetConnectionState(false).ToString();
                    EditorGUILayout.LabelField("Server", serverState);
                    EditorGUILayout.LabelField("Client", clientState);

                    Repaint();
                });
            }

            serializedObject.ApplyModifiedProperties();
        }

        void DrawSection(string title, Action content)
        {
            EditorGUILayout.Space(6);
            EditorGUILayout.LabelField(title, EditorStyles.boldLabel);
            EditorGUI.indentLevel++;
            content();
            EditorGUI.indentLevel--;
        }

        string FormatBytes(long bytes)
        {
            if (bytes < 1024) return $"{bytes} B";
            if (bytes < 1048576) return $"{bytes / 1024f:F1} KB";
            return $"{bytes / 1048576f:F1} MB";
        }
    }
}
#endif
