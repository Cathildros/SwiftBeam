package com.cathildros.swiftbeam.ui

import android.app.Application
import android.content.Context
import android.net.Uri
import android.net.wifi.WifiManager
import android.os.Build
import android.os.PowerManager
import androidx.lifecycle.AndroidViewModel
import androidx.lifecycle.viewModelScope
import com.cathildros.swiftbeam.data.AndroidFileHelper
import com.cathildros.swiftbeam.data.SavedPeerRecord
import com.cathildros.swiftbeam.data.SavedPeerStore
import com.cathildros.swiftbeam.data.SentTextRecord
import com.cathildros.swiftbeam.data.TransferHistoryRecord
import com.cathildros.swiftbeam.discovery.DiscoveredPeer
import com.cathildros.swiftbeam.discovery.LanPeerDiscovery
import com.cathildros.swiftbeam.protocol.ConnectedPeerMetadata
import com.cathildros.swiftbeam.protocol.ConnectionTarget
import com.cathildros.swiftbeam.protocol.PathUtils
import com.cathildros.swiftbeam.protocol.ProtocolConstants
import com.cathildros.swiftbeam.protocol.RemoteEntry
import com.cathildros.swiftbeam.protocol.SwiftBeamClient
import java.util.UUID
import java.util.concurrent.ConcurrentHashMap
import kotlinx.coroutines.CancellationException
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.Job
import kotlinx.coroutines.channels.Channel
import kotlinx.coroutines.delay
import kotlinx.coroutines.flow.MutableSharedFlow
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.SharedFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.asSharedFlow
import kotlinx.coroutines.flow.asStateFlow
import kotlinx.coroutines.flow.update
import kotlinx.coroutines.isActive
import kotlinx.coroutines.launch

enum class AppTab(val label: String) {
  Connect("Connect"),
  Files("Files"),
  Transfers("Transfers"),
  QuickText("Text"),
}

sealed interface ConnectionUiState {
  data object Disconnected : ConnectionUiState
  data class Connecting(val host: String, val label: String) : ConnectionUiState
  data class Connected(
    val host: String,
    val port: Int,
    val metadata: ConnectedPeerMetadata,
  ) : ConnectionUiState
}

enum class TransferDirection {
  Upload,
  Download,
}

enum class TransferStatus {
  Queued,
  Running,
  Completed,
  Failed,
  Cancelled,
}

data class ActiveTransferItem(
  val id: String,
  val fileName: String,
  val direction: TransferDirection,
  val totalBytes: Long,
  val transferredBytes: Long = 0L,
  val bytesPerSec: Double = 0.0,
  val status: TransferStatus = TransferStatus.Queued,
  val message: String = "",
)

private sealed interface QueuedTransferJob {
  val id: String
  val fileName: String

  data class UploadUri(
    override val id: String,
    override val fileName: String,
    val uri: Uri,
    val remoteDir: String,
    val fullFilesystem: Boolean,
  ) : QueuedTransferJob

  data class DownloadFile(
    override val id: String,
    override val fileName: String,
    val remoteRelativePath: String,
    val expectedSize: Long,
    val downloadsSubfolder: String,
  ) : QueuedTransferJob
}

data class MainUiState(
  val activeTab: AppTab = AppTab.Connect,
  val connection: ConnectionUiState = ConnectionUiState.Disconnected,
  val hostInput: String = "",
  val portInput: String = ProtocolConstants.DEFAULT_TRANSFER_PORT.toString(),
  val pinInput: String = "",
  val useTls: Boolean = true,
  val connectionError: String? = null,
  val discoveredPeers: List<DiscoveredPeer> = emptyList(),
  val isScanning: Boolean = false,
  val savedPeers: List<SavedPeerRecord> = emptyList(),
  val currentPath: String = "",
  val entries: List<RemoteEntry> = emptyList(),
  val searchQuery: String = "",
  val showHidden: Boolean = false,
  val selectedPaths: Set<String> = emptySet(),
  val freeDiskBytes: Long? = null,
  val totalDiskBytes: Long? = null,
  val isLoadingDir: Boolean = false,
  val dirError: String? = null,
  val activeTransfers: List<ActiveTransferItem> = emptyList(),
  val transferHistory: List<TransferHistoryRecord> = emptyList(),
  val quickTextInput: String = "",
  val isSendingText: Boolean = false,
  val sentTexts: List<SentTextRecord> = emptyList(),
  val pendingSharedUris: List<Uri> = emptyList(),
  val pendingSharedText: String? = null,
  val nearbyPermissionGranted: Boolean = true,
)

class MainViewModel(application: Application) : AndroidViewModel(application) {
  private val client = SwiftBeamClient()
  private val discovery = LanPeerDiscovery(application)
  private val store = SavedPeerStore(application)
  private val fileHelper = AndroidFileHelper(application)

  private val _uiState = MutableStateFlow(
    MainUiState(
      savedPeers = store.loadSavedPeers(),
      transferHistory = store.loadTransferHistory(),
      sentTexts = store.loadSentTexts(),
    ),
  )
  val uiState: StateFlow<MainUiState> = _uiState.asStateFlow()

  private val _messages = MutableSharedFlow<String>(extraBufferCapacity = 16)
  val messages: SharedFlow<String> = _messages.asSharedFlow()

  private val transferChannel = Channel<QueuedTransferJob>(Channel.UNLIMITED)
  private val cancelledIds = ConcurrentHashMap.newKeySet<String>()
  @Volatile
  private var currentWorkerJob: Job? = null
  private var keepAliveJob: Job? = null

  init {
    viewModelScope.launch {
      discovery.peers.collect { peers ->
        _uiState.update { it.copy(discoveredPeers = peers) }
      }
    }
    viewModelScope.launch {
      discovery.isScanning.collect { scanning ->
        _uiState.update { it.copy(isScanning = scanning) }
      }
    }
    discovery.start(viewModelScope)
    startTransferWorker()
  }

  fun setNearbyPermissionGranted(granted: Boolean) {
    _uiState.update { it.copy(nearbyPermissionGranted = granted) }
    if (granted) {
      refreshDiscovery()
    }
  }

  fun selectTab(tab: AppTab) {
    _uiState.update { it.copy(activeTab = tab) }
  }

  fun updateHostInput(value: String) {
    val (parsedHost, parsedPort) = PathUtils.parseHostInput(value)
    _uiState.update { state ->
      val looksLikeUrlOrPort = value.contains("://") || parsedPort != null
      state.copy(
        hostInput = if (looksLikeUrlOrPort) parsedHost else value,
        portInput = parsedPort?.toString() ?: state.portInput,
        connectionError = null,
      )
    }
  }

  fun updatePortInput(value: String) {
    val digits = value.filter { it.isDigit() }.take(5)
    _uiState.update { it.copy(portInput = digits, connectionError = null) }
  }

  fun updatePinInput(value: String) {
    val cleaned = value.filter { it.isLetterOrDigit() }.take(12)
    _uiState.update { it.copy(pinInput = cleaned, connectionError = null) }
  }

  fun updateUseTls(enabled: Boolean) {
    _uiState.update { it.copy(useTls = enabled, connectionError = null) }
  }

  fun refreshDiscovery() {
    discovery.start(viewModelScope)
    viewModelScope.launch {
      discovery.probeNow()
    }
  }

  fun fillFromSavedPeer(peer: SavedPeerRecord) {
    _uiState.update {
      it.copy(
        hostInput = peer.host,
        portInput = peer.port.toString(),
        pinInput = peer.pin,
        useTls = peer.useTls,
        connectionError = null,
      )
    }
  }

  fun removeSavedPeer(peer: SavedPeerRecord) {
    store.removeSavedPeer(peer.host, peer.port)
    _uiState.update { it.copy(savedPeers = store.loadSavedPeers()) }
  }

  fun connectManual() {
    val s = _uiState.value
    val (parsedHost, parsedPort) = PathUtils.parseHostInput(s.hostInput)
    val host = parsedHost.trim()
    val port = s.portInput.toIntOrNull() ?: parsedPort ?: ProtocolConstants.DEFAULT_TRANSFER_PORT
    if (host.isEmpty()) {
      _uiState.update { it.copy(connectionError = "Enter a PC IP address or pick one from Nearby Computers.") }
      return
    }
    connectTo(host = host, port = port, pin = s.pinInput, useTls = s.useTls, displayLabel = host)
  }

  fun connectTo(
    host: String,
    port: Int,
    pin: String,
    useTls: Boolean,
    displayLabel: String = host,
  ) {
    viewModelScope.launch {
      _uiState.update {
        it.copy(
          connection = ConnectionUiState.Connecting(host, displayLabel),
          hostInput = host,
          portInput = port.toString(),
          pinInput = pin,
          useTls = useTls,
          connectionError = null,
        )
      }

      val phoneName = Build.MODEL?.takeIf { it.isNotBlank() } ?: "Android Phone"
      val target = ConnectionTarget(
        host = host.trim(),
        port = port,
        pin = pin,
        useTls = useTls,
        clientName = phoneName,
      )

      try {
        val metadata = client.connect(target)
        val initialPath = when {
          metadata.fullFilesystemAccess && !metadata.desktopPath.isNullOrBlank() -> metadata.desktopPath
          metadata.fullFilesystemAccess && !metadata.homePath.isNullOrBlank() -> metadata.homePath
          metadata.fullFilesystemAccess -> ProtocolConstants.DRIVES_VIRTUAL_PATH
          else -> ""
        }

        store.upsertSavedPeer(
          SavedPeerRecord(
            deviceName = metadata.deviceName,
            host = target.host,
            port = target.port,
            pin = target.pin,
            useTls = target.useTls,
            lastConnectedMs = System.currentTimeMillis(),
          ),
        )

        val pendingUris = _uiState.value.pendingSharedUris
        val pendingText = _uiState.value.pendingSharedText

        _uiState.update { state ->
          state.copy(
            connection = ConnectionUiState.Connected(target.host, target.port, metadata),
            savedPeers = store.loadSavedPeers(),
            currentPath = initialPath,
            selectedPaths = emptySet(),
            searchQuery = "",
            connectionError = null,
            activeTab = if (pendingText != null && pendingUris.isEmpty()) AppTab.QuickText else AppTab.Files,
            quickTextInput = if (pendingText != null && state.quickTextInput.isBlank()) pendingText else state.quickTextInput,
            pendingSharedUris = emptyList(),
            pendingSharedText = null,
          )
        }

        startKeepAliveLoop()
        loadDirectory(initialPath)
        _messages.tryEmit("Connected to ${metadata.deviceName}")

        if (pendingUris.isNotEmpty()) {
          uploadUris(pendingUris)
        }
      } catch (t: Throwable) {
        _uiState.update {
          it.copy(
            connection = ConnectionUiState.Disconnected,
            connectionError = t.message ?: "Could not connect to $host:$port",
          )
        }
      }
    }
  }

  fun disconnect() {
    keepAliveJob?.cancel()
    keepAliveJob = null
    viewModelScope.launch {
      client.disconnect()
      _uiState.update {
        it.copy(
          connection = ConnectionUiState.Disconnected,
          entries = emptyList(),
          selectedPaths = emptySet(),
          dirError = null,
          activeTab = AppTab.Connect,
        )
      }
      refreshDiscovery()
      _messages.tryEmit("Disconnected")
    }
  }

  private fun startKeepAliveLoop() {
    keepAliveJob?.cancel()
    keepAliveJob = viewModelScope.launch {
      while (isActive) {
        delay(20_000L)
        if (_uiState.value.connection !is ConnectionUiState.Connected) break
        if (_uiState.value.activeTransfers.any { it.status == TransferStatus.Running }) continue
        val ok = client.ping()
        if (!ok) {
          handleUnexpectedDisconnect("Connection to PC was lost.")
          break
        }
      }
    }
  }

  private fun handleUnexpectedDisconnect(reason: String) {
    keepAliveJob?.cancel()
    keepAliveJob = null
    viewModelScope.launch {
      client.disconnect()
      _uiState.update {
        it.copy(
          connection = ConnectionUiState.Disconnected,
          entries = emptyList(),
          selectedPaths = emptySet(),
          connectionError = reason,
          activeTab = AppTab.Connect,
        )
      }
      _messages.tryEmit(reason)
    }
  }

  fun loadDirectory(path: String = _uiState.value.currentPath) {
    val conn = _uiState.value.connection as? ConnectionUiState.Connected ?: return
    viewModelScope.launch {
      _uiState.update { it.copy(isLoadingDir = true, dirError = null) }
      try {
        val resp = client.listDirectory(
          relativePath = path,
          recursive = false,
          includeHidden = _uiState.value.showHidden,
        )
        _uiState.update {
          it.copy(
            currentPath = resp.currentPath.ifEmpty { if (conn.metadata.fullFilesystemAccess) ProtocolConstants.DRIVES_VIRTUAL_PATH else "" },
            entries = resp.entries,
            selectedPaths = emptySet(),
            freeDiskBytes = resp.freeDiskBytes,
            totalDiskBytes = resp.totalDiskBytes,
            isLoadingDir = false,
            dirError = resp.error,
          )
        }
      } catch (t: Throwable) {
        _uiState.update { it.copy(isLoadingDir = false, dirError = t.message ?: "Failed to load directory.") }
        if (!client.isConnected) {
          handleUnexpectedDisconnect(t.message ?: "Connection lost.")
        }
      }
    }
  }

  fun openEntry(entry: RemoteEntry) {
    if (entry.isDirectory) {
      loadDirectory(entry.relativePath)
    } else {
      downloadEntries(listOf(entry))
    }
  }

  fun navigateUp() {
    val conn = _uiState.value.connection as? ConnectionUiState.Connected ?: return
    val parent = PathUtils.parentRemotePath(_uiState.value.currentPath, conn.metadata.fullFilesystemAccess) ?: return
    loadDirectory(parent)
  }

  fun updateSearchQuery(query: String) {
    _uiState.update { it.copy(searchQuery = query) }
  }

  fun toggleShowHidden() {
    _uiState.update { it.copy(showHidden = !it.showHidden) }
    loadDirectory()
  }

  fun toggleSelection(relativePath: String) {
    _uiState.update { state ->
      val next = state.selectedPaths.toMutableSet()
      if (!next.add(relativePath)) next.remove(relativePath)
      state.copy(selectedPaths = next)
    }
  }

  fun clearSelection() {
    _uiState.update { it.copy(selectedPaths = emptySet()) }
  }

  fun createFolder(name: String) {
    val conn = _uiState.value.connection as? ConnectionUiState.Connected ?: return
    val trimmed = name.trim()
    if (trimmed.isEmpty()) return
    viewModelScope.launch {
      try {
        val target = PathUtils.joinRemotePath(_uiState.value.currentPath, trimmed, conn.metadata.fullFilesystemAccess)
        val resp = client.createDirectory(target)
        if (resp.success) {
          _messages.tryEmit("Created folder '$trimmed'")
          loadDirectory()
        } else {
          _messages.tryEmit(resp.error ?: "Could not create folder.")
        }
      } catch (t: Throwable) {
        _messages.tryEmit(t.message ?: "Create folder failed.")
      }
    }
  }

  fun renameEntry(entry: RemoteEntry, newName: String) {
    val trimmed = newName.trim()
    if (trimmed.isEmpty() || trimmed == entry.name) return
    viewModelScope.launch {
      try {
        val resp = client.renamePath(entry.relativePath, trimmed)
        if (resp.success) {
          _messages.tryEmit("Renamed to '$trimmed'")
          loadDirectory()
        } else {
          _messages.tryEmit(resp.error ?: "Rename failed.")
        }
      } catch (t: Throwable) {
        _messages.tryEmit(t.message ?: "Rename failed.")
      }
    }
  }

  fun deleteEntries(entries: List<RemoteEntry>) {
    if (entries.isEmpty()) return
    viewModelScope.launch {
      var deleted = 0
      var firstErr: String? = null
      for (entry in entries) {
        try {
          val resp = client.deletePath(entry.relativePath)
          if (resp.success) {
            deleted++
          } else if (firstErr == null) {
            firstErr = resp.error
          }
        } catch (t: Throwable) {
          if (firstErr == null) firstErr = t.message
        }
      }
      clearSelection()
      loadDirectory()
      _messages.tryEmit(
        if (firstErr != null && deleted == 0) firstErr
        else if (firstErr != null) "Deleted $deleted item(s); error: $firstErr"
        else "Deleted $deleted item(s)",
      )
    }
  }

  fun handleIncomingShare(uris: List<Uri>, sharedText: String?) {
    val conn = _uiState.value.connection as? ConnectionUiState.Connected
    if (conn != null) {
      if (uris.isNotEmpty()) {
        uploadUris(uris)
      }
      if (!sharedText.isNullOrBlank()) {
        _uiState.update {
          it.copy(
            quickTextInput = sharedText,
            activeTab = if (uris.isEmpty()) AppTab.QuickText else AppTab.Transfers,
          )
        }
      }
    } else {
      _uiState.update {
        it.copy(
          pendingSharedUris = uris,
          pendingSharedText = sharedText,
          activeTab = AppTab.Connect,
        )
      }
      if (uris.isNotEmpty()) {
        _messages.tryEmit("Connect to your PC to send ${uris.size} shared item(s)")
      } else if (!sharedText.isNullOrBlank()) {
        _messages.tryEmit("Connect to your PC to send shared text")
      }
    }
  }

  fun clearPendingShare() {
    _uiState.update { it.copy(pendingSharedUris = emptyList(), pendingSharedText = null) }
  }

  fun uploadUris(uris: List<Uri>) {
    if (uris.isEmpty()) return
    val conn = _uiState.value.connection as? ConnectionUiState.Connected ?: run {
      _messages.tryEmit("Connect to a PC first before sending files.")
      return
    }
    if (conn.metadata.readOnly) {
      _messages.tryEmit("Connected PC is in Read-Only mode.")
      return
    }

    val targetDir = _uiState.value.currentPath.let { cur ->
      if (conn.metadata.fullFilesystemAccess && cur == ProtocolConstants.DRIVES_VIRTUAL_PATH) {
        conn.metadata.downloadsPath ?: conn.metadata.desktopPath ?: conn.metadata.homePath ?: ""
      } else {
        cur
      }
    }
    if (conn.metadata.fullFilesystemAccess && targetDir.isBlank()) {
      _messages.tryEmit("Open a folder or drive on your PC before uploading.")
      return
    }

    viewModelScope.launch {
      val addedItems = mutableListOf<ActiveTransferItem>()
      for (uri in uris) {
        val meta = fileHelper.inspectUri(uri)
        val id = UUID.randomUUID().toString()
        addedItems.add(
          ActiveTransferItem(
            id = id,
            fileName = meta.displayName,
            direction = TransferDirection.Upload,
            totalBytes = maxOf(0L, meta.sizeBytes),
            status = TransferStatus.Queued,
          ),
        )
        transferChannel.send(
          QueuedTransferJob.UploadUri(
            id = id,
            fileName = meta.displayName,
            uri = uri,
            remoteDir = targetDir,
            fullFilesystem = conn.metadata.fullFilesystemAccess,
          ),
        )
      }
      _uiState.update { it.copy(activeTransfers = it.activeTransfers + addedItems) }
      _messages.tryEmit("Queued ${addedItems.size} file(s) for upload")
    }
  }

  fun downloadEntries(entries: List<RemoteEntry>) {
    if (entries.isEmpty()) return
    if (_uiState.value.connection !is ConnectionUiState.Connected) {
      _messages.tryEmit("Connect to a PC first.")
      return
    }

    viewModelScope.launch {
      val jobs = mutableListOf<Pair<ActiveTransferItem, QueuedTransferJob>>()
      for (entry in entries) {
        if (!entry.isDirectory) {
          val id = UUID.randomUUID().toString()
          jobs.add(
            ActiveTransferItem(
              id = id,
              fileName = entry.name,
              direction = TransferDirection.Download,
              totalBytes = entry.size,
              status = TransferStatus.Queued,
            ) to QueuedTransferJob.DownloadFile(
              id = id,
              fileName = entry.name,
              remoteRelativePath = entry.relativePath,
              expectedSize = entry.size,
              downloadsSubfolder = "",
            ),
          )
        } else {
          try {
            val listing = client.listDirectory(
              relativePath = entry.relativePath,
              recursive = true,
              includeHidden = _uiState.value.showHidden,
            )
            val baseNorm = entry.relativePath.replace('\\', '/').trimEnd('/')
            val files = listing.entries.filter { !it.isDirectory }
            for (child in files) {
              val childNorm = child.relativePath.replace('\\', '/')
              val relFromRoot = childNorm.removePrefix(baseNorm).trimStart('/')
              val subParent = relFromRoot.substringBeforeLast('/', "")
              val subfolder = if (subParent.isEmpty()) entry.name else "${entry.name}/$subParent"
              val id = UUID.randomUUID().toString()
              jobs.add(
                ActiveTransferItem(
                  id = id,
                  fileName = child.name,
                  direction = TransferDirection.Download,
                  totalBytes = child.size,
                  status = TransferStatus.Queued,
                ) to QueuedTransferJob.DownloadFile(
                  id = id,
                  fileName = child.name,
                  remoteRelativePath = child.relativePath,
                  expectedSize = child.size,
                  downloadsSubfolder = subfolder,
                ),
              )
            }
          } catch (t: Throwable) {
            _messages.tryEmit("Could not list '${entry.name}': ${t.message}")
          }
        }
      }

      if (jobs.isEmpty()) {
        _messages.tryEmit("Selected folder has no files to download.")
        return@launch
      }

      for ((_, job) in jobs) {
        transferChannel.send(job)
      }
      _uiState.update { it.copy(activeTransfers = it.activeTransfers + jobs.map { pair -> pair.first }) }
      clearSelection()
      _messages.tryEmit("Queued ${jobs.size} download(s) -> Downloads/SwiftBeam")
    }
  }

  fun cancelTransfer(id: String) {
    cancelledIds.add(id)
    val running = currentWorkerJob
    val currentRunningId = _uiState.value.activeTransfers.firstOrNull { it.status == TransferStatus.Running }?.id
    if (id == currentRunningId) {
      running?.cancel()
    } else {
      _uiState.update { state ->
        state.copy(activeTransfers = state.activeTransfers.filterNot { it.id == id })
      }
    }
  }

  fun clearFinishedTransfers() {
    _uiState.update { state ->
      state.copy(
        activeTransfers = state.activeTransfers.filter {
          it.status == TransferStatus.Queued || it.status == TransferStatus.Running
        },
      )
    }
  }

  fun clearHistory() {
    store.clearTransferHistory()
    _uiState.update { it.copy(transferHistory = emptyList()) }
  }

  fun updateQuickTextInput(text: String) {
    _uiState.update { it.copy(quickTextInput = text) }
  }

  fun sendQuickText() {
    val text = _uiState.value.quickTextInput.trim()
    val conn = _uiState.value.connection as? ConnectionUiState.Connected
    if (conn == null) {
      _messages.tryEmit("Connect to a PC first.")
      return
    }
    if (text.isEmpty()) return

    viewModelScope.launch {
      _uiState.update { it.copy(isSendingText = true) }
      try {
        val sender = Build.MODEL?.takeIf { it.isNotBlank() } ?: "Android Phone"
        val ack = client.sendText(text, sender)
        if (ack.success) {
          store.appendSentText(
            SentTextRecord(
              id = UUID.randomUUID().toString(),
              text = text,
              peerName = conn.metadata.deviceName,
              sentAtMs = System.currentTimeMillis(),
            ),
          )
          _uiState.update {
            it.copy(
              isSendingText = false,
              quickTextInput = "",
              sentTexts = store.loadSentTexts(),
            )
          }
          _messages.tryEmit("Sent text to ${conn.metadata.deviceName}")
        } else {
          _uiState.update { it.copy(isSendingText = false) }
          _messages.tryEmit(ack.error ?: "Failed to send text.")
        }
      } catch (t: Throwable) {
        _uiState.update { it.copy(isSendingText = false) }
        _messages.tryEmit(t.message ?: "Failed to send text.")
      }
    }
  }

  private fun startTransferWorker() {
    viewModelScope.launch(Dispatchers.IO) {
      for (job in transferChannel) {
        if (cancelledIds.remove(job.id)) continue

        val conn = _uiState.value.connection as? ConnectionUiState.Connected
        if (conn == null || !client.isConnected) {
          markTransferFinished(job.id, job.fileName, jobDirection(job), 0L, 0.0, TransferStatus.Failed, "Not connected to PC", "Disconnected")
          continue
        }

        val worker = launch(Dispatchers.IO) {
          executeTransferWithLocks(job, conn)
        }
        currentWorkerJob = worker
        worker.join()
        currentWorkerJob = null
      }
    }
  }

  private suspend fun executeTransferWithLocks(
    job: QueuedTransferJob,
    conn: ConnectionUiState.Connected,
  ) {
    val ctx = getApplication<Application>()
    val powerMgr = ctx.getSystemService(Context.POWER_SERVICE) as? PowerManager
    val wakeLock = runCatching {
      powerMgr?.newWakeLock(PowerManager.PARTIAL_WAKE_LOCK, "SwiftBeam::TransferWakeLock")?.apply {
        acquire(10 * 60 * 1000L)
      }
    }.getOrNull()

    val wifiMgr = ctx.applicationContext.getSystemService(Context.WIFI_SERVICE) as? WifiManager
    @Suppress("DEPRECATION")
    val wifiLock = runCatching {
      val mode = if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.Q) {
        WifiManager.WIFI_MODE_FULL_LOW_LATENCY
      } else {
        WifiManager.WIFI_MODE_FULL_HIGH_PERF
      }
      wifiMgr?.createWifiLock(mode, "SwiftBeam::TransferWifiLock")?.apply { acquire() }
    }.getOrNull()

    val startNs = System.nanoTime()
    var lastUiUpdateNs = startNs
    var totalTransferred = 0L

    updateTransferProgress(job.id, TransferStatus.Running, 0L, null, 0.0, "")

    try {
      when (job) {
        is QueuedTransferJob.UploadUri -> {
          val meta = fileHelper.inspectUri(job.uri)
          val remoteTarget = PathUtils.joinRemotePath(job.remoteDir, meta.displayName, job.fullFilesystem)
          fileHelper.withUploadStream(meta) { stream, exactSize ->
            updateTransferProgress(job.id, TransferStatus.Running, 0L, exactSize, 0.0, "")
            client.pushFile(
              remoteRelativePath = remoteTarget,
              source = stream,
              totalFileSize = exactSize,
              lastModifiedUtcIso = meta.lastModifiedUtcIso,
            ) { sent, total ->
              totalTransferred = sent
              val nowNs = System.nanoTime()
              if (nowNs - lastUiUpdateNs >= 120_000_000L || sent >= total) {
                lastUiUpdateNs = nowNs
                val secs = maxOf(0.05, (nowNs - startNs) / 1_000_000_000.0)
                updateTransferProgress(job.id, TransferStatus.Running, sent, total, sent / secs, "")
              }
            }
            totalTransferred = exactSize
          }
          val secs = maxOf(0.05, (System.nanoTime() - startNs) / 1_000_000_000.0)
          markTransferFinished(
            id = job.id,
            fileName = job.fileName,
            direction = TransferDirection.Upload,
            totalBytes = totalTransferred,
            avgBps = totalTransferred / secs,
            status = TransferStatus.Completed,
            message = "Uploaded to PC",
            peerName = conn.metadata.deviceName,
          )
          loadDirectory()
        }

        is QueuedTransferJob.DownloadFile -> {
          var finalSize = job.expectedSize
          val savedPath = fileHelper.saveToDownloads(job.fileName, job.downloadsSubfolder) { outStream ->
            val header = client.pullFile(job.remoteRelativePath, outStream) { received, total ->
              totalTransferred = received
              finalSize = total
              val nowNs = System.nanoTime()
              if (nowNs - lastUiUpdateNs >= 120_000_000L || received >= total) {
                lastUiUpdateNs = nowNs
                val secs = maxOf(0.05, (nowNs - startNs) / 1_000_000_000.0)
                updateTransferProgress(job.id, TransferStatus.Running, received, total, received / secs, "")
              }
            }
            if (!header.success) {
              throw IllegalStateException(header.error ?: "Download failed.")
            }
            totalTransferred = header.length
            finalSize = header.length
          }
          val secs = maxOf(0.05, (System.nanoTime() - startNs) / 1_000_000_000.0)
          markTransferFinished(
            id = job.id,
            fileName = job.fileName,
            direction = TransferDirection.Download,
            totalBytes = finalSize,
            avgBps = finalSize / secs,
            status = TransferStatus.Completed,
            message = savedPath,
            peerName = conn.metadata.deviceName,
          )
        }
      }
    } catch (ce: CancellationException) {
      markTransferFinished(
        id = job.id,
        fileName = job.fileName,
        direction = jobDirection(job),
        totalBytes = totalTransferred,
        avgBps = 0.0,
        status = TransferStatus.Cancelled,
        message = "Cancelled",
        peerName = conn.metadata.deviceName,
      )
      throw ce
    } catch (t: Throwable) {
      markTransferFinished(
        id = job.id,
        fileName = job.fileName,
        direction = jobDirection(job),
        totalBytes = totalTransferred,
        avgBps = 0.0,
        status = TransferStatus.Failed,
        message = t.message ?: "Transfer failed.",
        peerName = conn.metadata.deviceName,
      )
    } finally {
      runCatching { if (wifiLock?.isHeld == true) wifiLock.release() }
      runCatching { if (wakeLock?.isHeld == true) wakeLock.release() }
    }
  }

  private fun jobDirection(job: QueuedTransferJob): TransferDirection = when (job) {
    is QueuedTransferJob.UploadUri -> TransferDirection.Upload
    is QueuedTransferJob.DownloadFile -> TransferDirection.Download
  }

  private fun updateTransferProgress(
    id: String,
    status: TransferStatus,
    transferred: Long,
    total: Long?,
    bps: Double,
    message: String,
  ) {
    _uiState.update { state ->
      state.copy(
        activeTransfers = state.activeTransfers.map { item ->
          if (item.id == id) {
            item.copy(
              status = status,
              transferredBytes = transferred,
              totalBytes = total ?: item.totalBytes,
              bytesPerSec = bps,
              message = message,
            )
          } else {
            item
          }
        },
      )
    }
  }

  private fun markTransferFinished(
    id: String,
    fileName: String,
    direction: TransferDirection,
    totalBytes: Long,
    avgBps: Double,
    status: TransferStatus,
    message: String,
    peerName: String,
  ) {
    store.appendTransferHistory(
      TransferHistoryRecord(
        id = id,
        fileName = fileName,
        direction = direction.name,
        totalBytes = totalBytes,
        averageBytesPerSec = avgBps,
        peerName = peerName,
        status = status.name,
        message = message,
        completedAtMs = System.currentTimeMillis(),
      ),
    )

    _uiState.update { state ->
      state.copy(
        activeTransfers = state.activeTransfers.map { item ->
          if (item.id == id) {
            item.copy(
              status = status,
              transferredBytes = if (status == TransferStatus.Completed) totalBytes else item.transferredBytes,
              totalBytes = if (totalBytes > 0L) totalBytes else item.totalBytes,
              bytesPerSec = avgBps,
              message = message,
            )
          } else {
            item
          }
        },
        transferHistory = store.loadTransferHistory(),
      )
    }
  }

  override fun onCleared() {
    super.onCleared()
    keepAliveJob?.cancel()
    discovery.stop()
    viewModelScope.launch { client.disconnect() }
  }
}
