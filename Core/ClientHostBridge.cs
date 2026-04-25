using System.Collections.Generic;
using FishNet.Utility.Performance;

namespace FishNet.Transporting.EpicNetPlugin
{
    internal sealed class ClientHostBridge
    {
        ServerPeer _server;
        ClientHostPeer _clientHost;
        Queue<LocalPacket> _serverToClient = new Queue<LocalPacket>(64);
        Queue<LocalPacket> _clientToServer = new Queue<LocalPacket>(64);
        const int MaxQueueSize = 256;

        internal bool _serverStarted;
        internal bool _clientHostStarted;

        internal void Bind(ServerPeer server, ClientHostPeer clientHost)
        {
            _server = server;
            _clientHost = clientHost;
        }

        internal void OnServerState(LocalConnectionState state)
        {
            _serverStarted = state == LocalConnectionState.Started;
            if (state == LocalConnectionState.Stopped) ClearQueues();
        }

        internal void OnClientHostState(LocalConnectionState state)
        {
            _clientHostStarted = state == LocalConnectionState.Started;
            if (state == LocalConnectionState.Stopped) ClearQueues();
        }

        internal void ServerSendToClient(LocalPacket packet)
        {
            if (!_serverStarted || !_clientHostStarted) return;
            if (_serverToClient.Count >= MaxQueueSize)
            {
                packet.ReturnToPool();
                return;
            }
            _serverToClient.Enqueue(packet);
        }

        internal void ClientSendToServer(LocalPacket packet)
        {
            if (!_serverStarted || !_clientHostStarted) return;
            if (_clientToServer.Count >= MaxQueueSize)
            {
                packet.ReturnToPool();
                return;
            }
            _clientToServer.Enqueue(packet);
        }

        internal void ProcessServerIncoming()
        {
            while (_clientToServer.Count > 0)
            {
                var pkt = _clientToServer.Dequeue();
                _server?.InternalReceiveFromClientHost(pkt);
                pkt.ReturnToPool();
            }
        }

        internal void ProcessClientHostIncoming()
        {
            while (_serverToClient.Count > 0)
            {
                var pkt = _serverToClient.Dequeue();
                _clientHost?.InternalReceiveFromServer(pkt);
                pkt.ReturnToPool();
            }
        }

        void ClearQueues()
        {
            while (_serverToClient.Count > 0) _serverToClient.Dequeue().ReturnToPool();
            while (_clientToServer.Count > 0) _clientToServer.Dequeue().ReturnToPool();
        }

        internal void Stop()
        {
            ClearQueues();
            _serverStarted = false;
            _clientHostStarted = false;
        }

        internal void Reset()
        {
            Stop();
            _server = null;
            _clientHost = null;
        }
    }
}