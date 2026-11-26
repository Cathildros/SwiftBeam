package com.cathildros.swiftbeam.ui

import android.app.Application
import android.graphics.Bitmap
import android.graphics.BitmapFactory
import android.net.Uri
import androidx.lifecycle.AndroidViewModel
import androidx.lifecycle.viewModelScope
import com.cathildros.swiftbeam.BuildConfig
import com.cathildros.swiftbeam.SwiftBeamApplication
import com.cathildros.swiftbeam.data.PhoneHostPrefs
import com.cathildros.swiftbeam.data.ReceivedTextRecord
import com.cathildros.swiftbeam.data.SavedPeerRecord
import com.cathildros.swiftbeam.data.SentTextRecord
import com.cathildros.swiftbeam.data.SyncPairRecord
import com.cathildros.swiftbeam.data.TransferHistoryRecord
import com.cathildros.swiftbeam.data.UpdateChecker
import com.cathildros.swiftbeam.data.UpdateInfo
import com.cathildros.swiftbeam.discovery.DiscoveredPeer
import com.cathildros.swiftbeam.discovery.LanPeerDiscovery
import com.cathildros.swiftbeam.protocol.PathUtils
import com.cathildros.swiftbeam.protocol.ProtocolConstants
import com.cathildros.swiftbeam.protocol.RemoteEntry
import com.cathildros.swiftbeam.service.ActiveTransferItem
import com.cathildros.swiftbeam.service.ConnectionUiState
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.flow.MutableSharedFlow
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.SharedFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.asSharedFlow
import kotlinx.coroutines.flow.asStateFlow
import kotlinx.coroutines.flow.update
import kotlinx.coroutines.launch
import kotlinx.coroutines.withContext

enum class AppTab(val label: String) {
  Connect("Connect"),
  Files("Files"),
  Sync("Sync"),
  Transfers("Transfers"),
  QuickText("Text"),
}

sealed interface PreviewModalState {
  data object None : PreviewModalState
  data class Loading(val entry: RemoteEntry) : PreviewModalState
  data class ImagePreview(val entry: RemoteEntry, val bitmap: Bitmap) : PreviewModalState
  data class TextPreview(val entry: RemoteEntry, val text: String, val truncated: Boolean) : PreviewModalState
  data class Error(val entry: RemoteEntry, val error: String) : PreviewModalState
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
  val phoneHostPrefs: PhoneHostPrefs = PhoneHostPrefs(true, null, "App Shared Folder"),
  val latestUpdate: UpdateInfo? = null,
  val isCheckingUpdate: Boolean = false,
  val currentPath: String = "",
  val entries: List<RemoteEntry> = emptyList(),
  val searchQuery: String = "",
  val showHidden: Boolean = false,
  val selectedPaths: Set<String> = emptySet(),
  val freeDiskBytes: Long? = null,
  val totalDiskBytes: Long? = null,
  val isLoadingDir: Boolean = false,
  val dirError: String? = null,
  val previewState: PreviewModalState = PreviewModalState.None,
  val syncPairs: List<SyncPairRecord> = emptyList(),
  val activeTransfers: List<ActiveTransferItem> = emptyList(),
  val transferHistory: List<TransferHistoryRecord> = emptyList(),
  val quickTextInput: String = "",
  val isSendingText: Boolean = false,
  val sentTexts: List<SentTextRecord> = emptyList(),
  val receivedTexts: List<ReceivedTextRecord> = emptyList(),
  val pendingSharedUris: List<Uri> = emptyList(),
  val pendingSharedText: String? = null,
  val nearbyPermissionGranted: Boolean = true,
)

class MainViewModel(application: Application) : AndroidViewModel(application) {
  private val coordinator = (application as SwiftBeamApplication).coordinator
  private val client = coordinator.client
  private val discovery = LanPeerDiscovery(application)

  private val _uiState = MutableStateFlow(
    MainUiState(
      savedPeers = coordinator.savedPeers.value,
      phoneHostPrefs = coordinator.phoneHostPrefs.value,
      syncPairs = coordinator.syncPairs.value,
      activeTransfers = coordinator.activeTransfers.value,
      transferHistory = coordinator.transferHistory.value,
      sentTexts = coordinator.sentTexts.value,
      receivedTexts = coordinator.receivedTexts.value,
    ),
  )
  val uiState: StateFlow<MainUiState> = _uiState.asStateFlow()

  private val _messages = MutableSharedFlow<String>(extraBufferCapacity = 24)
  val messages: SharedFlow<String> = _messages.asSharedFlow()

  init {
    viewModelScope.launch {
      discovery.peers.collect { peers -> _uiState.update { it.copy(discoveredPeers = peers) } }
    }
    viewModelScope.launch {
      discovery.isScanning.collect { scanning -> _uiState.update { it.copy(isScanning = scanning) } }
    }
    viewModelScope.launch {
      coordinator.connection.collect { conn -> _uiState.update { it.copy(connection = conn) } }
    }
    viewModelScope.launch {
      coordinator.activeTransfers.collect { list -> _uiState.update { it.copy(activeTransfers = list) } }
    }
    viewModelScope.launch {
      coordinator.transferHistory.collect { hist -> _uiState.update { it.copy(transferHistory = hist) } }
    }
    viewModelScope.launch {
      coordinator.savedPeers.collect { peers -> _uiState.update { it.copy(savedPeers = peers) } }
    }
    viewModelScope.launch {
      coordinator.sentTexts.collect { texts -> _uiState.update { it.copy(sentTexts = texts) } }
    }
    viewModelScope.launch {
      coordinator.receivedTexts.collect { texts -> _uiState.update { it.copy(receivedTexts = texts) } }
    }
    viewModelScope.launch {
      coordinator.syncPairs.collect { pairs -> _uiState.update { it.copy(syncPairs = pairs) } }
    }
    viewModelScope.launch {
      coordinator.phoneHostPrefs.collect { prefs -> _uiState.update { it.copy(phoneHostPrefs = prefs) } }
    }
    viewModelScope.launch {
      coordinator.messages.collect { msg -> _messages.tryEmit(msg) }
    }
    viewModelScope.launch {
      coordinator.directoryRefreshSignal.collect {
        if (_uiState.value.connection is ConnectionUiState.Connected) {
          loadDirectory()
        }
      }
    }

    discovery.start(viewModelScope)
    checkForUpdates(silent = true)
  }

  fun setNearbyPermissionGranted(granted: Boolean) {
    _uiState.update { it.copy(nearbyPermissionGranted = granted) }
    if (granted) refreshDiscovery()
  }

  fun selectTab(tab: AppTab) {
    _uiState.update { it.copy(activeTab = tab) }
  }

  fun updateHostInput(value: String) {
    val parsedQr = PathUtils.parseQrPayload(value)
    if (parsedQr != null && (value.contains("://") || value.contains("sbport=") || value.contains("pin="))) {
      _uiState.update { state ->
        state.copy(
          hostInput = parsedQr.host,
          portInput = parsedQr.port.toString(),
          pinInput = parsedQr.pin ?: state.pinInput,
          connectionError = null,
        )
      }
      return
    }
    val (parsedHost, parsedPort) = PathUtils.parseHostInput(value)
    _uiState.update { state ->
      val looksLikePort = parsedPort != null
      state.copy(
        hostInput = if (looksLikePort) parsedHost else value,
        portInput = parsedPort?.toString() ?: state.portInput,
        connectionError = null,
      )
    }
  }

  fun updatePortInput(value: String) {
    _uiState.update { it.copy(portInput = value.filter { c -> c.isDigit() }.take(5), connectionError = null) }
  }

  fun updatePinInput(value: String) {
    _uiState.update { it.copy(pinInput = value.filter { c -> c.isLetterOrDigit() }.take(12), connectionError = null) }
  }

  fun updateUseTls(enabled: Boolean) {
    _uiState.update { it.copy(useTls = enabled, connectionError = null) }
  }

  fun refreshDiscovery() {
    discovery.start(viewModelScope)
    viewModelScope.launch { discovery.probeNow() }
  }

  fun checkForUpdates(silent: Boolean = false) {
    viewModelScope.launch {
      _uiState.update { it.copy(isCheckingUpdate = true) }
      val info = UpdateChecker.checkLatestRelease(BuildConfig.VERSION_NAME)
      _uiState.update { it.copy(isCheckingUpdate = false, latestUpdate = info) }
      if (!silent) {
        if (info != null) _messages.tryEmit("SwiftBeam v${info.version} is available!")
        else _messages.tryEmit("You're on the latest version (${BuildConfig.VERSION_NAME})")
      }
    }
  }

  fun togglePhoneHostEnabled(enabled: Boolean) {
    coordinator.updatePhoneHostSettings(enabled = enabled)
  }

  fun setPhoneHostFolder(treeUri: Uri) {
    val label = coordinator.fileHelper.resolveTreeLabel(treeUri)
    coordinator.updatePhoneHostSettings(enabled = true, treeUri = treeUri.toString(), label = label)
    _messages.tryEmit("Phone shared folder set to: $label")
  }

  fun handleScannedQr(rawPayload: String) {
    val parsed = PathUtils.parseQrPayload(rawPayload)
    if (parsed == null) {
      _messages.tryEmit("QR code doesn't look like a SwiftBeam host URL.")
      return
    }
    val pinToUse = parsed.pin ?: _uiState.value.pinInput
    _uiState.update {
      it.copy(
        hostInput = parsed.host,
        portInput = parsed.port.toString(),
        pinInput = pinToUse,
        connectionError = null,
      )
    }
    if (!parsed.pin.isNullOrBlank()) {
      connectTo(parsed.host, parsed.port, parsed.pin, true, parsed.host)
    } else {
      _messages.tryEmit("QR scanned (${parsed.host}:${parsed.port}). Enter PIN to connect.")
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
    coordinator.removeSavedPeer(peer.host, peer.port)
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
          hostInput = host,
          portInput = port.toString(),
          pinInput = pin,
          useTls = useTls,
          connectionError = null,
        )
      }
      try {
        val metadata = coordinator.connect(host, port, pin, useTls, displayLabel)
        val initialPath = when {
          metadata.fullFilesystemAccess && !metadata.desktopPath.isNullOrBlank() -> metadata.desktopPath
          metadata.fullFilesystemAccess && !metadata.homePath.isNullOrBlank() -> metadata.homePath
          metadata.fullFilesystemAccess -> ProtocolConstants.DRIVES_VIRTUAL_PATH
          else -> ""
        }

        val pendingUris = _uiState.value.pendingSharedUris
        val pendingText = _uiState.value.pendingSharedText

        _uiState.update { state ->
          state.copy(
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
        loadDirectory(initialPath)
        _messages.tryEmit("Connected to ${metadata.deviceName}")
        if (pendingUris.isNotEmpty()) uploadUris(pendingUris)
      } catch (t: Throwable) {
        _uiState.update { it.copy(connectionError = t.message ?: "Could not connect to $host:$port") }
      }
    }
  }

  fun disconnect() {
    viewModelScope.launch {
      coordinator.disconnect()
      _uiState.update {
        it.copy(
          entries = emptyList(),
          selectedPaths = emptySet(),
          dirError = null,
          previewState = PreviewModalState.None,
          activeTab = AppTab.Connect,
        )
      }
      refreshDiscovery()
      _messages.tryEmit("Disconnected")
    }
  }

  fun loadDirectory(path: String = _uiState.value.currentPath) {
    val conn = _uiState.value.connection as? ConnectionUiState.Connected ?: return
    viewModelScope.launch {
      _uiState.update { it.copy(isLoadingDir = true, dirError = null) }
      try {
        val resp = client.listDirectory(path, recursive = false, includeHidden = _uiState.value.showHidden)
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
      }
    }
  }

  fun openEntry(entry: RemoteEntry) {
    if (entry.isDirectory) {
      loadDirectory(entry.relativePath)
    } else if (PathUtils.isPreviewableImage(entry.name) || PathUtils.isPreviewableText(entry.name)) {
      previewEntry(entry)
    } else {
      downloadEntries(listOf(entry))
    }
  }

  fun previewEntry(entry: RemoteEntry) {
    if (entry.isDirectory) return
    val isImg = PathUtils.isPreviewableImage(entry.name)
    val isTxt = PathUtils.isPreviewableText(entry.name)
    if (!isImg && !isTxt) {
      _messages.tryEmit("Preview not supported for this file type — downloading instead.")
      downloadEntries(listOf(entry))
      return
    }
    viewModelScope.launch {
      _uiState.update { it.copy(previewState = PreviewModalState.Loading(entry)) }
      try {
        val maxBytes = if (isImg) 6 * 1024 * 1024 else 256 * 1024
        val (bytes, totalSize) = client.pullRangeBytes(entry.relativePath, maxBytes)
        if (isImg) {
          val bmp = withContext(Dispatchers.Default) { BitmapFactory.decodeByteArray(bytes, 0, bytes.size) }
          if (bmp != null) {
            _uiState.update { it.copy(previewState = PreviewModalState.ImagePreview(entry, bmp)) }
          } else {
            _uiState.update { it.copy(previewState = PreviewModalState.Error(entry, "Could not decode image preview.")) }
          }
        } else {
          val text = bytes.toString(Charsets.UTF_8)
          _uiState.update { it.copy(previewState = PreviewModalState.TextPreview(entry, text, truncated = totalSize > bytes.size)) }
        }
      } catch (t: Throwable) {
        _uiState.update { it.copy(previewState = PreviewModalState.Error(entry, t.message ?: "Preview failed.")) }
      }
    }
  }

  fun dismissPreview() {
    _uiState.update { it.copy(previewState = PreviewModalState.None) }
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
      runCatching {
        val target = PathUtils.joinRemotePath(_uiState.value.currentPath, trimmed, conn.metadata.fullFilesystemAccess)
        val resp = client.createDirectory(target)
        if (resp.success) {
          _messages.tryEmit("Created folder '$trimmed'")
          loadDirectory()
        } else {
          _messages.tryEmit(resp.error ?: "Could not create folder.")
        }
      }.onFailure { _messages.tryEmit(it.message ?: "Create folder failed.") }
    }
  }

  fun renameEntry(entry: RemoteEntry, newName: String) {
    val trimmed = newName.trim()
    if (trimmed.isEmpty() || trimmed == entry.name) return
    viewModelScope.launch {
      runCatching {
        val resp = client.renamePath(entry.relativePath, trimmed)
        if (resp.success) {
          _messages.tryEmit("Renamed to '$trimmed'")
          loadDirectory()
        } else {
          _messages.tryEmit(resp.error ?: "Rename failed.")
        }
      }.onFailure { _messages.tryEmit(it.message ?: "Rename failed.") }
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
          if (resp.success) deleted++ else if (firstErr == null) firstErr = resp.error
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
      if (uris.isNotEmpty()) uploadUris(uris)
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
        it.copy(pendingSharedUris = uris, pendingSharedText = sharedText, activeTab = AppTab.Connect)
      }
      if (uris.isNotEmpty()) _messages.tryEmit("Connect to your PC to send ${uris.size} shared item(s)")
      else if (!sharedText.isNullOrBlank()) _messages.tryEmit("Connect to your PC to send shared text")
    }
  }

  fun clearPendingShare() {
    _uiState.update { it.copy(pendingSharedUris = emptyList(), pendingSharedText = null) }
  }

  fun uploadUris(uris: List<Uri>) {
    val conn = _uiState.value.connection as? ConnectionUiState.Connected ?: run {
      _messages.tryEmit("Connect to a PC first.")
      return
    }
    if (conn.metadata.readOnly) {
      _messages.tryEmit("Connected PC is in Read-Only mode.")
      return
    }
    val targetDir = resolveWritableDir(conn) ?: run {
      _messages.tryEmit("Open a folder or drive on your PC before uploading.")
      return
    }
    coordinator.enqueueUploads(uris, targetDir, conn.metadata.fullFilesystemAccess)
  }

  fun uploadFolderTree(treeUri: Uri) {
    val conn = _uiState.value.connection as? ConnectionUiState.Connected ?: run {
      _messages.tryEmit("Connect to a PC first.")
      return
    }
    if (conn.metadata.readOnly) {
      _messages.tryEmit("Connected PC is in Read-Only mode.")
      return
    }
    val targetDir = resolveWritableDir(conn) ?: run {
      _messages.tryEmit("Open a folder or drive on your PC before uploading.")
      return
    }
    coordinator.enqueueFolderTreeUpload(treeUri, targetDir, conn.metadata.fullFilesystemAccess)
  }

  private fun resolveWritableDir(conn: ConnectionUiState.Connected): String? {
    val cur = _uiState.value.currentPath
    val dir = if (conn.metadata.fullFilesystemAccess && cur == ProtocolConstants.DRIVES_VIRTUAL_PATH) {
      conn.metadata.downloadsPath ?: conn.metadata.desktopPath ?: conn.metadata.homePath ?: ""
    } else {
      cur
    }
    return if (conn.metadata.fullFilesystemAccess && dir.isBlank()) null else dir
  }

  fun downloadEntries(entries: List<RemoteEntry>) {
    if (_uiState.value.connection !is ConnectionUiState.Connected) {
      _messages.tryEmit("Connect to a PC first.")
      return
    }
    clearSelection()
    coordinator.enqueueDownloads(entries, _uiState.value.showHidden)
  }

  fun openDownloadedUri(uriString: String, mimeType: String?) {
    if (!coordinator.fileHelper.openDownloadedFile(uriString, mimeType)) {
      _messages.tryEmit("No installed app could open that file type.")
    }
  }

  fun resolveSyncFolderLabel(treeUri: Uri): String = coordinator.fileHelper.resolveTreeLabel(treeUri)

  fun addSyncPair(treeUri: Uri, remotePath: String, twoWay: Boolean, autoOnConnect: Boolean) {
    coordinator.addSyncPair(treeUri, remotePath, twoWay, autoOnConnect)
    _messages.tryEmit("Added Auto-Sync folder")
  }

  fun syncPairNow(id: String) = coordinator.syncPairNow(id)
  fun toggleSyncPairAuto(id: String, enabled: Boolean) = coordinator.toggleSyncPairAuto(id, enabled)
  fun removeSyncPair(id: String) = coordinator.removeSyncPair(id)

  fun cancelTransfer(id: String) = coordinator.cancelTransfer(id)
  fun clearFinishedTransfers() = coordinator.clearFinishedTransfers()
  fun clearHistory() = coordinator.clearHistory()

  fun updateQuickTextInput(text: String) {
    _uiState.update { it.copy(quickTextInput = text) }
  }

  fun sendQuickText() {
    val text = _uiState.value.quickTextInput.trim()
    val conn = _uiState.value.connection as? ConnectionUiState.Connected ?: run {
      _messages.tryEmit("Connect to a PC first.")
      return
    }
    if (text.isEmpty()) return
    viewModelScope.launch {
      _uiState.update { it.copy(isSendingText = true) }
      try {
        val ok = coordinator.sendText(text)
        _uiState.update {
          it.copy(isSendingText = false, quickTextInput = if (ok) "" else it.quickTextInput)
        }
        if (ok) _messages.tryEmit("Sent text to ${conn.metadata.deviceName}")
      } catch (t: Throwable) {
        _uiState.update { it.copy(isSendingText = false) }
        _messages.tryEmit(t.message ?: "Failed to send text.")
      }
    }
  }
}
