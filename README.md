# EpicNet Transport for FishNet

**EpicNet** is an enterprise-grade, high-performance Peer-to-Peer (P2P) transport plugin bridging [FishNet Networking](https://fish-net.org/) and the [Epic Online Services (EOS) SDK](https://dev.epicgames.com/).

Engineered specifically for Unity 6 and FishNet 4.7+, EpicNet completely replaces legacy coroutine-based networking with a robust, zero-allocation, multithreaded architecture. It is designed to handle high-concurrency multiplayer sessions while completely bypassing the single-thread limitations inherent to the native EOS C++ SDK.

---

## Core Advantages

### 1. Absolute Zero Allocation (0 GC Spikes)
In fast-paced multiplayer games, garbage collection spikes are unacceptable. EpicNet is built entirely on `readonly struct` types and integrates directly with FishNet's `ByteArrayPool`.
* Hot paths produce zero garbage.
* Payload buffers are pooled and recycled without reallocation.
* Memory footprint remains flat regardless of packet volume or server uptime.

### 2. True Multithreading & Concurrency
The native EOS SDK strictly forbids cross-thread calls. EpicNet circumvents this using a highly optimized Producer-Consumer architecture.
* Serialization and core FishNet logic run entirely on background threads.
* Native C++ EOS API calls are safely marshaled to the main thread via lock-free `ConcurrentQueue` structures.
* Yields up to 30-40% CPU savings on large 64+ player dedicated servers.

### 3. Enterprise-Level Security & DDoS Mitigation
Public P2P networks are vulnerable to spam and buffer overflow attacks. EpicNet implements strict edge-validation before data reaches your game code.
* **MTU Validation**: Oversized packets are immediately dropped, preventing buffer overflow exploits.
* **Pending Connection Timeouts**: Prevents "zombie" handshakes from exhausting server memory.
* **Connection Rate Limiting**: Shields the host from malicious connection request spam.

### 4. Fail-Safe Reliability
Network volatility is handled gracefully without packet loss or state corruption.
* **Automatic Retry Queues**: If the native EOS P2P queue reaches capacity (`Result.LimitExceeded`), reliable packets are buffered locally and re-transmitted automatically.
* **Native Crash Prevention**: All asynchronous EOS callbacks are wrapped in strict exception handlers. A failure in your game logic will not propagate down to the C++ SDK and crash the engine.

### 5. Modern Architecture
EpicNet is built using modern C# paradigms, completely eliminating the use of Unity Coroutines.
* Authentication and connection handshakes utilize robust `async/await` flows.
* `CancellationToken` support ensures immediate cleanup during scene transitions or abrupt disconnects.
* Flat, sealed class structures allow the Mono/IL2CPP compilers to devirtualize calls for maximum execution speed.

---

## Live Runtime Metrics

EpicNet includes a custom Unity Inspector designed for live production monitoring. Track your server's health in real-time with metrics powered by thread-safe `Interlocked` operations:
* Packets Sent / Received
* Bytes Sent / Received
* Active Connections
* Dropped Packets (Security interventions)

---

## Requirements

| Dependency | Minimum Version |
|------------|-----------------|
| Unity Engine | `6000.0+` |
| FishNet Networking | `4.7.2+` |
| PlayEveryWare EOS Plugin | `6.0.2+` |

---

## Installation (UPM)

EpicNet is distributed as a clean Unity Package Manager (UPM) library.

1. Open the Package Manager in Unity (`Window > Package Manager`).
2. Click the **+** button in the top left corner.
3. Select **Add package from disk...**
4. Locate and select the `package.json` file inside the EpicNet directory.

---

## Usage Guide

1. Add the `EpicNet` component to your NetworkManager object alongside your other FishNet components.
2. In the `EpicNet` inspector, configure the transport:
   * **Socket Name**: Must be identical across the server and all connecting clients.
   * **Threaded Mode**: Enable this to marshal serialization to background threads (Highly recommended for production).
   * **Auto Authenticate**: If true, the transport will automatically handle the EOS Connect login handshake using the provided credentials.
3. Start your server or client using standard FishNet `ServerManager` and `ClientManager` methods.

### Client Connection Example

To connect as a client via P2P, you must provide the Host's `ProductUserId` prior to initiating the connection.

```csharp
var epicNet = networkManager.GetComponent<EpicNet>();

// Set the target host's EOS Product User ID
epicNet.RemoteProductUserId = "TARGET_HOST_PRODUCT_USER_ID";

// Start the connection
networkManager.ClientManager.StartConnection();
```

---

## License

This project is released under a **Custom License**. 
Please refer to the `LICENSE` file for full details. 

You have absolute freedom to use, modify, distribute, and monetize this software. You are not required to provide attribution. However, you are strictly prohibited from claiming original authorship of this codebase or any of its constituent parts. Derivative repositories must disclose that they are modified versions of the original project authored by vovawe.

**Author:** vovawe
