using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;
using Epic.OnlineServices;
using Epic.OnlineServices.P2P;
using FishNet.Utility.Performance;
using UnityEngine;

namespace FishNet.Transporting.EpicNetPlugin
{
    internal abstract class CommonPeer
    {
        LocalConnectionState _connectionState = LocalConnectionState.Stopped;
        protected EpicNet _transport;
        protected int _mainThreadId;
        readonly Queue<PendingPacket> _retryQueue = new Queue<PendingPacket>(32);
        internal const int MAX_RETRY_QUEUE_SIZE = 1024;
        const int MAX_RETRY_FRAMES = 120;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal LocalConnectionState GetLocalConnectionState() => _connectionState;

        protected virtual void SetLocalConnectionState(LocalConnectionState connectionState, bool server)
        {
            if (connectionState == _connectionState) return;
            _connectionState = connectionState;

            if (server)
                _transport.HandleServerConnectionState(new ServerConnectionStateArgs(connectionState, _transport.Index));
            else
                _transport.HandleClientConnectionState(new ClientConnectionStateArgs(connectionState, _transport.Index));
        }

        internal void Initialize(EpicNet transport)
        {
            _transport = transport;
            _mainThreadId = Thread.CurrentThread.ManagedThreadId;
        }

        internal void ClearQueue(ref Queue<LocalPacket> queue)
        {
            while (queue.Count > 0) queue.Dequeue().ReturnToPool();
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        protected bool CheckMainThread()
        {
            if (Thread.CurrentThread.ManagedThreadId != _mainThreadId)
            {
                _transport.LogErr("[EpicNet] EOS SDK calls from non-main thread! Disable FishNet Multithreading or enable Threaded Mode.");
                return false;
            }
            return true;
        }

        internal Result Send(ProductUserId localUserId, ProductUserId remoteUserId, SocketId socketId,
            byte channelId, ArraySegment<byte> segment)
        {
            if (GetLocalConnectionState() != LocalConnectionState.Started)
                return Result.InvalidState;

            if (!CheckMainThread())
                return Result.InvalidState;

            bool isReliable = (channelId % 2 == 0);
            var reliability = isReliable ? PacketReliability.ReliableOrdered : PacketReliability.UnreliableUnordered;

            var sendOptions = new SendPacketOptions
            {
                LocalUserId = localUserId,
                RemoteUserId = remoteUserId,
                SocketId = socketId,
                Channel = channelId,
                Data = segment,
                Reliability = reliability,
                AllowDelayedDelivery = isReliable
            };

            var p2p = EOS.GetP2PInterface();
            if (p2p is null) return Result.InvalidState;

            var result = p2p.SendPacket(ref sendOptions);

            if (result == Result.LimitExceeded && isReliable)
            {
                if (_retryQueue.Count >= MAX_RETRY_QUEUE_SIZE)
                {
                    Interlocked.Increment(ref _transport.Stats.DroppedPackets);
                    _transport.LogErr("[EpicNet] Retry queue full — dropping reliable packet!");
                    return Result.LimitExceeded;
                }

                var buf = ByteArrayPool.Retrieve(segment.Count);
                Buffer.BlockCopy(segment.Array, segment.Offset, buf, 0, segment.Count);
                _retryQueue.Enqueue(new PendingPacket
                {
                    LocalUserId = localUserId, RemoteUserId = remoteUserId,
                    SocketId = socketId, ChannelId = channelId,
                    Data = buf, Length = segment.Count, RetryCount = 0
                });
                return Result.Success;
            }

            if (result != Result.Success)
                _transport.LogWarn($"[EpicNet] Send failed → {remoteUserId} size={segment.Count} err={result}");

            return result;
        }

        internal void ProcessRetryQueue()
        {
            int count = _retryQueue.Count;
            if (count == 0) return;

            _transport.Stats.RetryQueueSize = count;

            var p2p = EOS.GetP2PInterface();
            if (p2p is null) { DrainRetryQueue(); return; }

            for (int i = 0; i < count; i++)
            {
                var pending = _retryQueue.Dequeue();
                bool isReliable = (pending.ChannelId % 2 == 0);

                var sendOptions = new SendPacketOptions
                {
                    LocalUserId = pending.LocalUserId,
                    RemoteUserId = pending.RemoteUserId,
                    SocketId = pending.SocketId,
                    Channel = pending.ChannelId,
                    Data = new ArraySegment<byte>(pending.Data, 0, pending.Length),
                    Reliability = isReliable ? PacketReliability.ReliableOrdered : PacketReliability.UnreliableUnordered,
                    AllowDelayedDelivery = true
                };

                var result = p2p.SendPacket(ref sendOptions);

                if (result == Result.LimitExceeded && pending.RetryCount < MAX_RETRY_FRAMES)
                {
                    pending.RetryCount++;
                    _retryQueue.Enqueue(pending);
                }
                else
                {
                    if (result != Result.Success)
                    {
                        _transport.LogWarn($"[EpicNet] Retry #{pending.RetryCount} failed: {result}");
                        
                        if (isReliable)
                        {
                            _transport.LogErr($"[EpicNet] CRITICAL: Reliable packet dropped! Disconnecting {pending.RemoteUserId} to prevent state desync.");
                            var co = new CloseConnectionOptions { SocketId = pending.SocketId, LocalUserId = pending.LocalUserId, RemoteUserId = pending.RemoteUserId };
                            p2p.CloseConnection(ref co);
                        }
                    }
                    pending.ReturnToPool();
                }
            }
        }

        protected void DrainRetryQueue()
        {
            while (_retryQueue.Count > 0) _retryQueue.Dequeue().ReturnToPool();
            _transport.Stats.RetryQueueSize = 0;
        }

        protected bool Receive(ProductUserId localUserId, out ProductUserId remoteUserId,
            out byte[] buffer, out int length, out byte channelId)
        {
            remoteUserId = null;
            buffer = null;
            length = 0;
            channelId = 0;

            if (localUserId is null) return false;
            if (!CheckMainThread()) return false;

            var p2p = EOS.GetP2PInterface();
            if (p2p is null) return false;

            var getSizeOpt = new GetNextReceivedPacketSizeOptions { LocalUserId = localUserId };
            var sizeResult = p2p.GetNextReceivedPacketSize(ref getSizeOpt, out var packetSize);

            if (sizeResult == Result.NotFound) return false;
            if (sizeResult != Result.Success)
            {
                _transport.LogErr($"[EpicNet] GetNextReceivedPacketSize: {sizeResult}");
                return false;
            }

            if (packetSize > P2PInterface.MAX_PACKET_SIZE)
            {
                var trash = ByteArrayPool.Retrieve((int)packetSize);
                var trashSeg = new ArraySegment<byte>(trash, 0, (int)packetSize);
                var trashOpt = new ReceivePacketOptions { LocalUserId = localUserId, MaxDataSizeBytes = packetSize };
                SocketId s = default; ProductUserId u = null;
                p2p.ReceivePacket(ref trashOpt, ref u, ref s, out _, trashSeg, out _);
                ByteArrayPool.Store(trash);
                Interlocked.Increment(ref _transport.Stats.DroppedPackets);
                _transport.LogWarn($"[EpicNet] SECURITY: Dropped oversized packet ({packetSize}b)");
                return false;
            }

            buffer = ByteArrayPool.Retrieve((int)packetSize);
            var data = new ArraySegment<byte>(buffer, 0, (int)packetSize);
            var recvOpt = new ReceivePacketOptions { LocalUserId = localUserId, MaxDataSizeBytes = packetSize };
            SocketId sid = default;
            try
            {
                var recvResult = p2p.ReceivePacket(ref recvOpt, ref remoteUserId, ref sid, out channelId, data, out var bytesWritten);
                if (recvResult != Result.Success)
                {
                    ByteArrayPool.Store(buffer);
                    buffer = null; length = 0;
                    _transport.LogErr($"[EpicNet] ReceivePacket: {recvResult}");
                    return false;
                }
                length = (int)bytesWritten;
            }
            catch
            {
                ByteArrayPool.Store(buffer);
                buffer = null; length = 0;
                throw;
            }

            Interlocked.Increment(ref _transport.Stats.PacketsReceived);
            Interlocked.Add(ref _transport.Stats.BytesReceived, length);
            return true;
        }
    }
}