# EpicNet Transport for FishNet

**EpicNet** is an enterprise-grade, high-performance Peer-to-Peer (P2P) transport plugin bridging [FishNet Networking](https://fish-net.org/) and the [Epic Online Services (EOS) SDK](https://dev.epicgames.com/).

Engineered specifically for Unity 6 and FishNet 4.7+, EpicNet completely replaces legacy coroutine-based networking with a robust, zero-allocation, multithreaded architecture. It is designed to handle high-concurrency multiplayer sessions while completely bypassing the single-thread limitations inherent to the native EOS C++ SDK.

![version](https://img.shields.io/badge/version-0.4.5-blue)
![unity](https://img.shields.io/badge/Unity-6000.0+-brightgreen)
![fishnet](https://img.shields.io/badge/FishNet-4.7.2+-orange)

---

## Core Advantages

### ⚡ Absolute Zero Allocation (0 GC Spikes)
In fast-paced multiplayer games, garbage collection spikes are unacceptable. EpicNet is built entirely on `readonly struct` types and integrates directly with FishNet's `ByteArrayPool`.
- Hot paths produce zero garbage.
- Payload buffers are pooled and recycled without reallocation.
- Memory footprint remains flat regardless of packet volume or server uptime.

### 🧵 True Multithreading & Concurrency
The native EOS SDK strictly forbids cross-thread calls. EpicNet circumvents this using a highly optimized Producer-Consumer architecture.
- Serialization and core FishNet logic run entirely on background threads.
- Native C++ EOS API calls are safely marshaled to the main thread via lock-free `ConcurrentQueue` structures.
- Yields up to 30–40% CPU savings on large 64+ player dedicated servers.

### 🛡️ Enterprise-Level Security & DDoS Mitigation
Public P2P networks are vulnerable to spam and buffer overflow attacks. EpicNet implements strict edge-validation before data reaches your game code.
- Strict `SocketId` verification – mismatched requests are rejected immediately.
- Rate-limit and burst-limit for incoming connections to prevent floods.
- Oversized packets (> MTU) are dropped before allocation.
- Pending connections timeout and are forcibly closed if not established in time.

### ⚙️ Fine-Grained Reliability Control
When the EOS send queue is saturated, reliable packets are not dropped instantly. They enter a retry queue managed entirely by the transport.
- Configure maximum queue size, max retry attempts per packet, and processing budget per frame.
- If a reliable packet still fails after the allowed retries, the transport disconnects the client to prevent state desynchronization.

### 🏗️ Clean, Maintainable Architecture
Client-host functionality is isolated in a dedicated `ClientHostBridge`, avoiding the messy entanglement of server and local client logic.
- All shared collections are protected with `lock` sections.
- EOS SDK interaction remains strictly on the main thread.
- Runtime SDK liveness checks prevent crashes during platform shutdown.

---

## Quick Start

1. Install **FishNet 4.7.2+** and the **EOS SDK** (PlayEveryWare EOS Plugin or Epic's official plugin).
2. Add the **EpicNet** component to the same GameObject that holds your `NetworkManager`.
3. Configure in the Inspector:
   - **Socket Name** – must be identical on server and all clients.
   - If this is a client, enter the **Remote Server Product User ID** of the server.
   - Enable **Auto Authenticate** and fill in the **Auth Connect Data** (login type, token, timeout).
4. Play – the transport will automatically authenticate and establish the connection.

```csharp
// Minimal code setup
var transport = GetComponent<EpicNet>();
transport.SocketName = "MyGame";
transport.RemoteProductUserId = "0002...aabbcc";
transport.AutoAuthenticate = true;
transport.AuthConnectData = new AuthData
{
    loginCredentialType = LoginCredentialType.DeviceCode,
    displayName = "Player"
};
```

---

Inspector Overview

Connection
Max Clients – maximum number of simultaneously connected players.
Socket Name – shared identifier for the P2P socket.
Remote Server Product User Id – the server's EOS ProductUserId (used by clients).

Authentication
Auto Authenticate – enables automatic EOS login when the transport starts.
Auth Connect Data – credentials, token, timeout, and account creation options.

Security
Max Connections Per Second – rate limit for new connections.
Burst Connections – additional connections allowed above the per-second limit.
Max Pending Connections – maximum unconfirmed connections before requests are rejected.
Pending Timeout – time in seconds before a pending connection is dropped.

Reliability
Max Retry Queue Size – maximum number of reliable packets waiting for re-send.
Max Retry Frames – maximum retry attempts before forcing disconnection.
Max Retries Processed Per Frame – how many retry packets are processed each frame.

Performance
Max Incoming Packets Per Frame – budget to prevent frame time spikes.
MTU Safety Margin – effective MTU becomes 1170 minus this value.
Threaded Mode – enables lock-free queueing for FishNet's multithreading support.

Debug
Debug Level – controls verbosity (None, Errors, Warnings, Verbose).
Show Stats – displays real-time network statistics in the Inspector during Play Mode.

---

Architecture & Data Flow

```
Background Threads (Serialization)
        │
        ▼
Lock‑Free ConcurrentQueue
        │
        ▼
Main Thread (EOS P2P API) ────── P2P Network
        │
        ▼
Game Logic (FishNet)
```

· Main Thread Only: All EOS SDK calls happen on the main thread, preventing native C++ crashes.
· Parallel Processing: Packet serialization and FishNet event handling run on worker threads.
· Client-Host: A dedicated ClientHostBridge relays data between the server and the local client without extra allocations.

Connection Lifecycle:

1. Incoming request is validated: SocketId match, rate limits, pending limits.
2. If accepted, a unique connection ID is assigned (derived from ProductUserId hash).
3. On OnEstablished, the connection moves from pending to active.
4. On timeout or disconnect, all associated resources are released.

Reliability Flow:

· When SendPacket returns LimitExceeded, a reliable packet is enqueued for retry.
· Each frame, up to Max Retries Processed Per Frame packets are re-attempted.
· If a packet exceeds Max Retry Frames, the connection is terminated to avoid state corruption.

---

Real-Time Statistics

When Show Stats is enabled, you can monitor live metrics in the Inspector:

· 📨 Packets Sent / Packets Received
· 📊 Bytes Sent / Bytes Received (auto-formatted: B, KB, MB)
· ⚡ Send Rate / Receive Rate (packets per second)
· 🔄 Retry Queue Size – current number of packets awaiting re-send
· 👥 Active Connections – total connected clients
· ❌ Dropped Packets – packets discarded due to queue overflow or oversized payload

All counters update smoothly regardless of frame rate.

---

Installation & Requirements

· Unity 6000.0+
· FishNet 4.7.2+
· Epic Online Services SDK (PlayEveryWare EOS Plugin or Epic's EOS SDK for Unity)

Place the EpicNet folder inside Assets/Plugins/FishNet/Transports/.
The transport will appear automatically in the NetworkManager's transport selector.

---

Author

vovawe — design, optimization, and security.
For questions and contributions: GitHub Issues

---

EpicNet – unleashing the full potential of EOS P2P with zero compromises on performance, safety, and clean code.
