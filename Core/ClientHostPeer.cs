using System;
using System.Collections.Generic;
using FishNet.Utility.Performance;
using UnityEngine;

namespace FishNet.Transporting.EpicNetPlugin
{
    internal sealed class ClientHostPeer : CommonPeer
    {
        ServerPeer _server;
        Queue<LocalPacket> _incoming = new Queue<LocalPacket>(64);
        bool _waitingForServer;
        float _serverWaitDeadline;

        internal bool StartConnection(ServerPeer serverPeer)
        {
            if (serverPeer is null) return false;

            _server = serverPeer;
            _server.SetClientHostPeer(this);

            if (GetLocalConnectionState() != LocalConnectionState.Stopped) return false;
            if (_server.GetLocalConnectionState() is not (LocalConnectionState.Started or LocalConnectionState.Starting))
                return false;

            SetLocalConnectionState(LocalConnectionState.Starting, false);

            if (_server.GetLocalConnectionState() == LocalConnectionState.Started)
            {
                SetLocalConnectionState(LocalConnectionState.Started, false);
            }
            else
            {
                _waitingForServer = true;
                _serverWaitDeadline = Time.unscaledTime + 30f;
            }

            return true;
        }

        internal void PollServerReady()
        {
            if (!_waitingForServer) return;

            if (_server is not null && _server.GetLocalConnectionState() == LocalConnectionState.Started)
            {
                _waitingForServer = false;
                SetLocalConnectionState(LocalConnectionState.Started, false);
            }
            else if (Time.unscaledTime >= _serverWaitDeadline)
            {
                _waitingForServer = false;
                _transport.LogErr("[ClientHost] Timed out waiting for server.");
                SetLocalConnectionState(LocalConnectionState.Stopped, false);
            }
        }

        protected override void SetLocalConnectionState(LocalConnectionState connectionState, bool server)
        {
            base.SetLocalConnectionState(connectionState, server);
            if (connectionState is LocalConnectionState.Started or LocalConnectionState.Stopped)
                _server?.HandleClientHostConnectionStateChange(connectionState, server);
        }

        internal bool StopConnection()
        {
            if (GetLocalConnectionState() is LocalConnectionState.Stopped or LocalConnectionState.Stopping)
                return false;

            _waitingForServer = false;
            ClearQueue(ref _incoming);
            SetLocalConnectionState(LocalConnectionState.Stopping, false);
            SetLocalConnectionState(LocalConnectionState.Stopped, false);
            _server?.SetClientHostPeer(null);
            return true;
        }

        internal void IterateIncoming()
        {
            if (GetLocalConnectionState() != LocalConnectionState.Started) return;

            while (_incoming.Count > 0)
            {
                var packet = _incoming.Dequeue();
                var segment = new ArraySegment<byte>(packet.Data, 0, packet.Length);
                _transport.HandleClientReceivedDataArgs(
                    new ClientReceivedDataArgs(segment, packet.Channel, _transport.Index));
                packet.ReturnToPool();
            }
        }

        internal void ReceivedFromLocalServer(LocalPacket packet) => _incoming.Enqueue(packet);

        internal void SendToServer(byte channelId, ArraySegment<byte> segment)
        {
            if (GetLocalConnectionState() != LocalConnectionState.Started) return;
            if (_server?.GetLocalConnectionState() != LocalConnectionState.Started) return;
            _server.ReceivedFromClientHost(new LocalPacket(segment, channelId));
        }
    }
}
