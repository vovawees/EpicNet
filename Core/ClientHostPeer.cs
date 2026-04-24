using System;
using System.Collections.Generic;
using FishNet.Utility.Performance;
using UnityEngine;

namespace FishNet.Transporting.EpicNetPlugin
{
    internal sealed class ClientHostPeer : CommonPeer
    {
        ClientHostBridge _bridge;
        Queue<LocalPacket> _incoming = new Queue<LocalPacket>(64);

        internal void Bind(ClientHostBridge bridge)
        {
            _bridge = bridge;
        }

        internal bool StartConnection(ServerPeer serverPeer)
        {
            if (serverPeer is null || _bridge is null) return false;

            _bridge.Bind(serverPeer, this);
            serverPeer.SetClientHostBridge(_bridge);

            if (GetLocalConnectionState() != LocalConnectionState.Stopped) return false;

            SetLocalConnectionState(LocalConnectionState.Starting, false);

            if (serverPeer.GetLocalConnectionState() == LocalConnectionState.Started)
            {
                SetLocalConnectionState(LocalConnectionState.Started, false);
            }
            return true;
        }

        internal void PollServerReady()
        {
            if (GetLocalConnectionState() == LocalConnectionState.Starting &&
                _bridge != null && _bridge._serverStarted)
            {
                SetLocalConnectionState(LocalConnectionState.Started, false);
            }
        }

        protected override void SetLocalConnectionState(LocalConnectionState connectionState, bool server)
        {
            base.SetLocalConnectionState(connectionState, server);
            if (connectionState is LocalConnectionState.Started or LocalConnectionState.Stopped)
                _bridge?.OnClientHostState(connectionState);
        }

        internal bool StopConnection()
        {
            if (GetLocalConnectionState() is LocalConnectionState.Stopped or LocalConnectionState.Stopping)
                return false;

            ClearQueue(ref _incoming);
            SetLocalConnectionState(LocalConnectionState.Stopping, false);
            SetLocalConnectionState(LocalConnectionState.Stopped, false);
            _bridge?.Stop();
            return true;
        }

        internal void IterateIncoming()
        {
            if (GetLocalConnectionState() != LocalConnectionState.Started) return;
            _bridge?.ProcessClientHostIncoming();

            while (_incoming.Count > 0)
            {
                var packet = _incoming.Dequeue();
                var segment = new ArraySegment<byte>(packet.Data, 0, packet.Length);
                _transport.HandleClientReceivedDataArgs(
                    new ClientReceivedDataArgs(segment, packet.Channel, _transport.Index));
                packet.ReturnToPool();
            }
        }

        internal void InternalReceiveFromServer(LocalPacket packet)
        {
            _incoming.Enqueue(packet);
        }

        internal void SendToServer(byte channelId, ArraySegment<byte> segment)
        {
            if (GetLocalConnectionState() != LocalConnectionState.Started) return;
            _bridge?.ClientSendToServer(new LocalPacket(segment, channelId));
        }
    }
}