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

        protected int _maxRetryQueueSize = 1024;
        protected int _maxRetryFrames = 120;
        protected int _maxRetryProcessPerFrame = 32;
        protected int _maxIncomingPacketsPerFrame = 100;

        internal void SetRetrySettings(int queueSize, int maxFrames, int processPerFrame, int incomingPerFrame)
        {
            _maxRetryQueueSize = queueSize;
            _maxRetryFrames = maxFrames;
            _maxRetryProcessPerFrame = processPerFrame;
            _maxIncomingPacketsPerFrame = incomingPerFrame;
        }

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

        internal void SendWithPriority(ProductUserId localUserId, ProductUserId remoteUserId, SocketId socketId,
            byte channelId, ArraySegment<byte> segment, ChannelPriority priority)
        {
            if (GetLocalConnectionState() != LocalConnectionState.Started) return;
            if (!CheckMainThread()) return;

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
            if (p2p is null) return;

            var result = p2p.SendPacket(ref sendOptions);

            if (result == Result.LimitExceeded && isReliable)
            {
                if (_retryQueue.Count >= _maxRetryQueueSize)
                {
                    Interlocked.Increment(ref _transport.Stats.DroppedPackets);
                    _transport.LogErr("[EpicNet] Retry queue full — dropping reliable packet! Disconnecting remote peer.");
                    var co = new CloseConnectionOptions { SocketId = socketId, LocalUserId = localUserId, RemoteUserId = remoteUserId };
                    p2p.CloseConnection(ref co);
                    return;
                }

                var buf = ByteArrayPool.Retrieve(segment.Count);
                Buffer.BlockCopy(segment.Array, segment.Offset, buf, 0, segment.Count);
                _retryQueue.Enqueue(new PendingPacket
                {
                    LocalUserId = localUserId, RemoteUserId = remoteUserId,
                    SocketId = socketId, ChannelId = channelId,
                    Data = buf, Length = segment.Count, RetryCount = 0,
                    Priority = priority
                });
                return;
            }

            if (result != Result.Success)
                _transport.LogWarn($"[EpicNet] Send failed → {remoteUserId} size={segment.Count} err={result}");
        }

        internal void ProcessRetryQueue()
        {
            int count = _retryQueue.Count;
            if (count == 0) return;
            _transport.Stats.RetryQueueSize = count;

            var p2p = EOS.GetP2PInterface();
            if (p2p is null) { DrainRetryQueue(); return; }

            var sorted = new List<PendingPacket>(count);
            while (_retryQueue.Count > 0) sorted.Add(_retryQueue.Dequeue());
            sorted.Sort((a, b) => b.Priority.CompareTo(a.Priority));

            int processed = 0;
            for (int i = 0; i < sorted.Count && processed < _maxRetryProcessPerFrame; i++)
            {
                var pending = sorted[i];
                bool isReliable = (pending.ChannelId % 2 == 0);

                var sendOpt = new SendPacketOptions
                {
                    LocalUserId = pending.LocalUserId,
                    RemoteUserId = pending.RemoteUserId,
                    SocketId = pending.SocketId,
                    Channel = pending.ChannelId,
                    Data = new ArraySegment<byte>(pending.Data, 0, pending.Length),
                    Reliability = isReliable ? PacketReliability.ReliableOrdered : PacketReliability.UnreliableUnordered,
                    AllowDelayedDelivery = true
                };
                var result = p2p.SendPacket(ref sendOpt);
                processed++;

                if (result == Result.LimitExceeded && pending.RetryCount < _maxRetryFrames)
                {
                    pending.RetryCount++;
                    sorted[i] = pending;
                }
                else
                {
                    if (result != Result.Success)
                    {
                        _transport.LogWarn($"[EpicNet] Retry #{pending.RetryCount} failed: {result}");
                        if (isReliable)
                        {
                            _transport.LogErr($"[EpicNet] CRITICAL: Reliable packet dropped! Disconnecting {pending.RemoteUserId}");
                            var co = new CloseConnectionOptions { SocketId = pending.SocketId, LocalUserId = pending.LocalUserId, RemoteUserId = pending.RemoteUserId };
                            p2p.CloseConnection(ref co);
                        }
                    }
                    pending.ReturnToPool();
                    sorted[i] = default;
                }
            }

            for (int i = processed; i < sorted.Count; i++)
            {
                if (sorted[i].Data != null)
                    _retryQueue.Enqueue(sorted[i]);
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
            remoteUserId = null; buffer = null; length = 0; channelId = 0;
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