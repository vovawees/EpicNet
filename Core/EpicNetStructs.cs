using System;
using System.Runtime.CompilerServices;
using Epic.OnlineServices;
using Epic.OnlineServices.Auth;
using Epic.OnlineServices.Connect;
using Epic.OnlineServices.P2P;
using Epic.OnlineServices.Platform;
using FishNet.Utility.Performance;
using PlayEveryWare.EpicOnlineServices;
using UnityEngine;

namespace FishNet.Transporting.EpicNetPlugin
{
    internal static class EOS
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal static PlatformInterface GetPlatformInterface() =>
            EOSManager.Instance?.GetEOSPlatformInterface();

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal static P2PInterface GetP2PInterface() =>
            GetPlatformInterface()?.GetP2PInterface();

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal static LobbyInterface GetLobbyInterface() =>
            GetPlatformInterface()?.GetLobbyInterface();

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal static VoiceInterface GetVoiceInterface() =>
            GetPlatformInterface()?.GetVoiceInterface();

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal static ConnectInterface GetConnectInterface() =>
            GetPlatformInterface()?.GetConnectInterface();

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal static AuthInterface GetAuthInterface() =>
            GetPlatformInterface()?.GetAuthInterface();

        internal static ProductUserId LocalProductUserId =>
            GetConnectInterface()?.GetLoggedInUserByIndex(0);

        internal static bool IsReady() => EOSManager.Instance != null;
    }

    public enum ChannelPriority : byte
    {
        Default = 0,
        High = 1,
        Low = 2
    }

    public enum RelayControl : byte
    {
        NoRelays = 0,
        AllowRelays = 1,
        ForceRelays = 2
    }

    internal readonly struct LocalPacket
    {
        public readonly byte[] Data;
        public readonly int Length;
        public readonly Channel Channel;
        public readonly ChannelPriority Priority;

        public LocalPacket(ArraySegment<byte> data, byte channelId, ChannelPriority priority = ChannelPriority.Default)
        {
            Length = data.Count;
            Data = ByteArrayPool.Retrieve(Length);
            Buffer.BlockCopy(data.Array, data.Offset, Data, 0, Length);
            Channel = (Channel)channelId;
            Priority = priority;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void ReturnToPool()
        {
            if (Data is not null) ByteArrayPool.Store(Data);
        }
    }

    internal readonly struct Connection
    {
        public readonly int Id;
        public readonly ProductUserId LocalUserId;
        public readonly ProductUserId RemoteUserId;
        public readonly SocketId SocketId;

        public Connection(int id, ProductUserId local, ProductUserId remote, SocketId socket)
        {
            Id = id;
            LocalUserId = local;
            RemoteUserId = remote;
            SocketId = socket;
        }
    }

    [Serializable]
    public sealed class AuthData
    {
        public LoginCredentialType loginCredentialType = LoginCredentialType.DeviceCode;
        public ExternalCredentialType externalCredentialType = ExternalCredentialType.DeviceidAccessToken;
        [NonSerialized] public string id;
        [NonSerialized] public string token;
        public string displayName = "EpicNet";
        public bool automaticallyCreateDeviceId = true;
        public bool automaticallyCreateConnectAccount = true;
        public AuthScopeFlags authScopeFlags = AuthScopeFlags.NoFlags;
        public float timeout = 30f;
    }

    [Serializable]
    public sealed class ChannelSettings
    {
        public byte channelId;
        public bool isReliable;
        public ChannelPriority priority = ChannelPriority.Default;
    }

    internal struct PendingPacket
    {
        public ProductUserId LocalUserId;
        public ProductUserId RemoteUserId;
        public SocketId SocketId;
        public byte ChannelId;
        public byte[] Data;
        public int Length;
        public int RetryCount;
        public ChannelPriority Priority;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void ReturnToPool()
        {
            if (Data is not null) ByteArrayPool.Store(Data);
            Data = null;
        }
    }

    internal struct ThreadedPacket
    {
        public byte[] Data;
        public int Length;
        public byte ChannelId;
        public int ConnectionId;
        public ChannelPriority Priority;

        public ThreadedPacket(byte channelId, ArraySegment<byte> segment, int connectionId = -1, ChannelPriority priority = ChannelPriority.Default)
        {
            ChannelId = channelId;
            Length = segment.Count;
            ConnectionId = connectionId;
            Priority = priority;
            Data = ByteArrayPool.Retrieve(Length);
            Buffer.BlockCopy(segment.Array, segment.Offset, Data, 0, Length);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void ReturnToPool()
        {
            if (Data is not null) ByteArrayPool.Store(Data);
            Data = null;
        }
    }

    public enum EpicNetDebugLevel : byte { None = 0, Errors = 1, Warnings = 2, Verbose = 3 }

    [Serializable]
    public sealed class EpicNetStatistics
    {
        public long PacketsSent;
        public long PacketsReceived;
        public long BytesSent;
        public long BytesReceived;
        public int RetryQueueSize;
        public int ActiveConnections;
        public int DroppedPackets;
        public float SendRate;
        public float ReceiveRate;

        long _lastSent;
        long _lastReceived;
        float _lastCalcTime;

        public void Calculate()
        {
            float now = Time.unscaledTime;
            float dt = now - _lastCalcTime;
            if (dt < 0.5f) return;
            SendRate = (PacketsSent - _lastSent) / dt;
            ReceiveRate = (PacketsReceived - _lastReceived) / dt;
            _lastSent = PacketsSent;
            _lastReceived = PacketsReceived;
            _lastCalcTime = now;
        }

        public void Reset()
        {
            PacketsSent = 0; PacketsReceived = 0;
            BytesSent = 0; BytesReceived = 0;
            RetryQueueSize = 0; ActiveConnections = 0;
            DroppedPackets = 0; SendRate = 0; ReceiveRate = 0;
            _lastSent = 0; _lastReceived = 0; _lastCalcTime = 0;
        }
    }
}