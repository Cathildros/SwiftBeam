package com.cathildros.swiftbeam.discovery

import android.content.Context
import android.net.wifi.WifiManager
import android.os.Build
import com.cathildros.swiftbeam.protocol.ProtocolConstants
import com.cathildros.swiftbeam.protocol.ProtocolJson
import java.net.DatagramPacket
import java.net.DatagramSocket
import java.net.Inet4Address
import java.net.InetAddress
import java.net.InetSocketAddress
import java.net.NetworkInterface
import java.net.SocketTimeoutException
import java.util.UUID
import java.util.concurrent.ConcurrentHashMap
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.Job
import kotlinx.coroutines.delay
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.asStateFlow
import kotlinx.coroutines.isActive
import kotlinx.coroutines.launch
import kotlinx.coroutines.withContext
import kotlinx.serialization.Serializable

@Serializable
data class DiscoveryBeacon(
  val magic: String,
  val kind: String,
  val serverId: String,
  val deviceName: String,
  val osPlatform: String,
  val transferPort: Int,
  val requiresPin: Boolean,
  val allowUnencrypted: Boolean,
)

data class DiscoveredPeer(
  val serverId: String,
  val deviceName: String,
  val osPlatform: String,
  val host: String,
  val port: Int,
  val requiresPin: Boolean,
  val allowUnencrypted: Boolean,
  val lastSeenMs: Long,
)

class LanPeerDiscovery(context: Context) {
  private val appContext = context.applicationContext
  private val localId = "and-${UUID.randomUUID().toString().replace("-", "").take(10)}"
  private val peersMap = ConcurrentHashMap<String, DiscoveredPeer>()

  private val _peers = MutableStateFlow<List<DiscoveredPeer>>(emptyList())
  val peers: StateFlow<List<DiscoveredPeer>> = _peers.asStateFlow()

  private val _isScanning = MutableStateFlow(false)
  val isScanning: StateFlow<Boolean> = _isScanning.asStateFlow()

  private var listenJob: Job? = null
  private var probeJob: Job? = null
  private var listenSocket: DatagramSocket? = null
  private var multicastLock: WifiManager.MulticastLock? = null

  fun start(scope: CoroutineScope) {
    if (listenJob?.isActive == true) return
    _isScanning.value = true

    acquireMulticastLock()

    listenJob = scope.launch(Dispatchers.IO) {
      var sock: DatagramSocket? = null
      try {
        sock = DatagramSocket(null).apply {
          reuseAddress = true
          broadcast = true
          soTimeout = 1500
          bind(InetSocketAddress(InetAddress.getByName("0.0.0.0"), ProtocolConstants.DEFAULT_DISCOVERY_PORT))
        }
        listenSocket = sock

        val buf = ByteArray(4096)
        while (isActive) {
          val packet = DatagramPacket(buf, buf.size)
          try {
            sock.receive(packet)
            if (packet.length > 0) {
              val jsonText = String(packet.data, packet.offset, packet.length, Charsets.UTF_8)
              val beacon = runCatching { ProtocolJson.decodeFromString(DiscoveryBeacon.serializer(), jsonText) }.getOrNull()
              if (beacon != null &&
                beacon.magic == "SWB1" &&
                !beacon.serverId.equals(localId, ignoreCase = true) &&
                beacon.transferPort > 0
              ) {
                val remoteIp = packet.address?.hostAddress?.substringBefore('%')
                if (!remoteIp.isNullOrBlank()) {
                  val updated = DiscoveredPeer(
                    serverId = beacon.serverId,
                    deviceName = beacon.deviceName.ifBlank { remoteIp },
                    osPlatform = beacon.osPlatform.ifBlank { "PC" },
                    host = remoteIp,
                    port = beacon.transferPort,
                    requiresPin = beacon.requiresPin,
                    allowUnencrypted = beacon.allowUnencrypted,
                    lastSeenMs = System.currentTimeMillis(),
                  )
                  peersMap[beacon.serverId] = updated
                  emitSortedPeers()
                }
              }
            }
          } catch (_: SocketTimeoutException) {
            pruneStalePeers()
          }
        }
      } catch (_: Exception) {
        // Fallback: probe-only or wait until restarted
      } finally {
        runCatching { sock?.close() }
        listenSocket = null
      }
    }

    probeJob = scope.launch(Dispatchers.IO) {
      while (isActive) {
        sendProbeOnce()
        delay(2800L)
        pruneStalePeers()
      }
    }
  }

  suspend fun probeNow() = withContext(Dispatchers.IO) {
    sendProbeOnce()
  }

  fun stop() {
    _isScanning.value = false
    probeJob?.cancel()
    listenJob?.cancel()
    probeJob = null
    listenJob = null
    runCatching { listenSocket?.close() }
    listenSocket = null
    releaseMulticastLock()
  }

  private fun emitSortedPeers() {
    _peers.value = peersMap.values
      .sortedWith(compareBy<DiscoveredPeer> { it.deviceName.lowercase() }.thenBy { it.host })
  }

  private fun pruneStalePeers() {
    val cutoff = System.currentTimeMillis() - 12_000L
    var removed = false
    val iterator = peersMap.entries.iterator()
    while (iterator.hasNext()) {
      val entry = iterator.next()
      if (entry.value.lastSeenMs < cutoff) {
        iterator.remove()
        removed = true
      }
    }
    if (removed) {
      emitSortedPeers()
    }
  }

  private fun sendProbeOnce() {
    val deviceName = Build.MODEL?.takeIf { it.isNotBlank() } ?: "Android Phone"
    val beacon = DiscoveryBeacon(
      magic = "SWB1",
      kind = "PROBE",
      serverId = localId,
      deviceName = deviceName,
      osPlatform = "Android",
      transferPort = 0,
      requiresPin = false,
      allowUnencrypted = false,
    )
    val bytes = ProtocolJson.encodeToString(DiscoveryBeacon.serializer(), beacon).toByteArray(Charsets.UTF_8)

    val targets = LinkedHashSet<InetAddress>()
    runCatching {
      val interfaces = NetworkInterface.getNetworkInterfaces() ?: return@runCatching
      while (interfaces.hasMoreElements()) {
        val iface = interfaces.nextElement()
        if (!iface.isUp || iface.isLoopback) continue
        for (addr in iface.interfaceAddresses) {
          val inet = addr.address
          val bcast = addr.broadcast
          if (inet is Inet4Address && bcast != null) {
            targets.add(bcast)
          }
        }
      }
    }
    runCatching { targets.add(InetAddress.getByName("255.255.255.255")) }

    runCatching {
      DatagramSocket().use { sender ->
        sender.broadcast = true
        for (addr in targets) {
          runCatching {
            sender.send(DatagramPacket(bytes, bytes.size, addr, ProtocolConstants.DEFAULT_DISCOVERY_PORT))
          }
        }
      }
    }
  }

  private fun acquireMulticastLock() {
    runCatching {
      val wifi = appContext.getSystemService(Context.WIFI_SERVICE) as? WifiManager ?: return
      if (multicastLock == null) {
        multicastLock = wifi.createMulticastLock("SwiftBeamDiscovery").apply {
          setReferenceCounted(false)
        }
      }
      if (multicastLock?.isHeld == false) {
        multicastLock?.acquire()
      }
    }
  }

  private fun releaseMulticastLock() {
    runCatching {
      if (multicastLock?.isHeld == true) {
        multicastLock?.release()
      }
    }
  }
}
