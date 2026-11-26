package com.cathildros.swiftbeam.service

import android.app.NotificationManager
import android.content.Context
import android.content.Intent
import android.net.Uri
import android.net.wifi.WifiManager
import android.os.Build
import android.os.PowerManager
import androidx.core.app.NotificationCompat
import com.cathildros.swiftbeam.MainActivity
import com.cathildros.swiftbeam.data.AndroidFileHelper
import com.cathildros.swiftbeam.data.PhoneHostPrefs
import com.cathildros.swiftbeam.data.ReceivedTextRecord
import com.cathildros.swiftbeam.data.SavedPeerRecord
import com.cathildros.swiftbeam.data.SavedPeerStore
import com.cathildros.swiftbeam.data.SentTextRecord
import com.cathildros.swiftbeam.data.SyncPairRecord
import com.cathildros.swiftbeam.data.TransferHistoryRecord
import com.cathildros.swiftbeam.protocol.ConnectedPeerMetadata
import com.cathildros.swiftbeam.protocol.ConnectionTarget
import com.cathildros.swiftbeam.protocol.PathUtils
import com.cathildros.swiftbeam.protocol.RemoteEntry
import com.cathildros.swiftbeam.protocol.SwiftBeamClient
import com.cathildros.swiftbeam.server.DirectoryStorageProvider
import com.cathildros.swiftbeam.server.PhoneFileShareServer
import com.cathildros.swiftbeam.server.SafTreeStorageProvider
import com.cathildros.swiftbeam.sync.PhoneAutoSyncEngine
import java.io.BufferedInputStream
import java.io.File
import java.io.FileOutputStream
import java.io.IOException
import java.security.MessageDigest
import java.time.Instant
import java.util.UUID
import java.util.concurrent.ConcurrentHashMap
import kotlinx.coroutines.CancellationException
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.Job
import kotlinx.coroutines.SupervisorJob
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
  val savedUri: String? = null,
  val mimeType: String? = null,
)

sealed interface ConnectionUiState {
  data object Disconnected : ConnectionUiState
  data class Connecting(val host: String, val label: String) : ConnectionUiState
  data class Connected(
    val host: String,
    val port: Int,
    val metadata: ConnectedPeerMetadata,
  ) : ConnectionUiState
}

private sealed interface QueuedTransferJob {
  val id: String
  val fileName: String

  data class UploadUri(
    override val id: String,
    override val fileName: String,
    val uri: Uri,
    val remoteRelativePath: String,
    val sizeHint: Long = -1L,
    val lastModIso: String = "",
  ) : QueuedTransferJob

  data class DownloadFile(
    override val id: String,
    override val fileName: String,
    val remoteRelativePath: String,
    val expectedSize: Long,
    val downloadsSubfolder: String,
  ) : QueuedTransferJob
}

class TransferCoordinator(private val appContext: Context) {
  private val scope = CoroutineScope(SupervisorJob() + Dispatchers.IO)
  val client = SwiftBeamClient()
  val store = SavedPeerStore(appContext)
  val fileHelper = AndroidFileHelper(appContext)

  private val transferChannel = Channel<QueuedTransferJob>(Channel.UNLIMITED)
  private val cancelledIds = ConcurrentHashMap.newKeySet<String>()
  @Volatile
  private var currentWorkerJob: Job? = null
  @Volatile
  private var lastConnectionTarget: ConnectionTarget? = null
  private var keepAliveJob: Job? = null
  private var phoneServer: PhoneFileShareServer? = null

  private val _connection = MutableStateFlow<ConnectionUiState>(ConnectionUiState.Disconnected)
  val connection: StateFlow<ConnectionUiState> = _connection.asStateFlow()

  private val _activeTransfers = MutableStateFlow<List<ActiveTransferItem>>(emptyList())
  val activeTransfers: StateFlow<List<ActiveTransferItem>> = _activeTransfers.asStateFlow()

  private val _transferHistory = MutableStateFlow(store.loadTransferHistory())
  val transferHistory: StateFlow<List<TransferHistoryRecord>> = _transferHistory.asStateFlow()

  private val _savedPeers = MutableStateFlow(store.loadSavedPeers())
  val savedPeers: StateFlow<List<SavedPeerRecord>> = _savedPeers.asStateFlow()

  private val _sentTexts = MutableStateFlow(store.loadSentTexts())
  val sentTexts: StateFlow<List<SentTextRecord>> = _sentTexts.asStateFlow()

  private val _receivedTexts = MutableStateFlow(store.loadReceivedTexts())
  val receivedTexts: StateFlow<List<ReceivedTextRecord>> = _receivedTexts.asStateFlow()

  private val _syncPairs = MutableStateFlow(store.loadSyncPairs())
  val syncPairs: StateFlow<List<SyncPairRecord>> = _syncPairs.asStateFlow()

  private val _phoneHostPrefs = MutableStateFlow(store.loadPhoneHostPrefs())
  val phoneHostPrefs: StateFlow<PhoneHostPrefs> = _phoneHostPrefs.asStateFlow()

  private val _messages = MutableSharedFlow<String>(extraBufferCapacity = 24)
  val messages: SharedFlow<String> = _messages.asSharedFlow()

  private val _directoryRefreshSignal = MutableSharedFlow<Unit>(extraBufferCapacity = 8)
  val directoryRefreshSignal: SharedFlow<Unit> = _directoryRefreshSignal.asSharedFlow()

  init {
    startTransferWorker()
  }

  fun updatePhoneHostSettings(enabled: Boolean, treeUri: String? = _phoneHostPrefs.value.treeUri, label: String = _phoneHostPrefs.value.label) {
    store.savePhoneHostPrefs(enabled, treeUri, label)
    _phoneHostPrefs.value = PhoneHostPrefs(enabled, treeUri, label)
    if (!enabled) {
      phoneServer?.stop()
      phoneServer = null
    }
  }

  private fun ensurePhoneHostServerStarted(pin: String): Pair<Int, String> {
    val prefs = _phoneHostPrefs.value
    if (!prefs.enabled) return 0 to ""

    if (phoneServer?.isRunning != true) {
      val server = PhoneFileShareServer(
        storageProvider = {
          val cur = _phoneHostPrefs.value
          val uriStr = cur.treeUri
          if (!uriStr.isNullOrBlank()) {
            SafTreeStorageProvider(appContext, Uri.parse(uriStr), cur.label)
          } else {
            DirectoryStorageProvider(File(appContext.filesDir, "Shared"), "Phone Shared")
          }
        },
        pin = pin,
        requirePin = true,
        onTextReceived = { text, sender ->
          handleIncomingPcText(text, sender)
        },
      )
      server.start()
      phoneServer = server
    }
    val active = phoneServer ?: return 0 to ""
    return active.listeningPort to active.registerMutualToken()
  }

  private fun handleIncomingPcText(text: String, sender: String) {
    val record = ReceivedTextRecord(
      id = UUID.randomUUID().toString(),
      text = text,
      senderName = sender.ifBlank { "PC" },
      receivedAtMs = System.currentTimeMillis(),
    )
    store.appendReceivedText(record)
    _receivedTexts.value = store.loadReceivedTexts()
    _messages.tryEmit("Text received from ${record.senderName}")

    runCatching {
      TransferForegroundService.ensureNotificationChannels(appContext)
      val openIntent = android.app.PendingIntent.getActivity(
        appContext,
        10,
        Intent(appContext, MainActivity::class.java),
        android.app.PendingIntent.FLAG_UPDATE_CURRENT or android.app.PendingIntent.FLAG_IMMUTABLE,
      )
      val notif = NotificationCompat.Builder(appContext, TransferForegroundService.CHANNEL_TEXT)
        .setSmallIcon(android.R.drawable.stat_notify_chat)
        .setContentTitle("Text from ${record.senderName}")
        .setContentText(text)
        .setStyle(NotificationCompat.BigTextStyle().bigText(text))
        .setAutoCancel(true)
        .setContentIntent(openIntent)
        .build()
      val mgr = appContext.getSystemService(Context.NOTIFICATION_SERVICE) as NotificationManager
      mgr.notify((System.currentTimeMillis() % 10000).toInt() + 5000, notif)
    }
  }

  suspend fun connect(
    host: String,
    port: Int,
    pin: String,
    useTls: Boolean,
    displayLabel: String,
  ): ConnectedPeerMetadata {
    _connection.value = ConnectionUiState.Connecting(host, displayLabel)
    val phoneName = Build.MODEL?.takeIf { it.isNotBlank() } ?: "Android Phone"
    val (revPort, revToken) = ensurePhoneHostServerStarted(pin)

    val target = ConnectionTarget(
      host = host.trim(),
      port = port,
      pin = pin,
      useTls = useTls,
      clientName = phoneName,
      reverseCallbackPort = revPort,
      reverseCallbackToken = revToken.ifBlank { null },
    )
    lastConnectionTarget = target

    return try {
      val metadata = client.connect(target)
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
      _savedPeers.value = store.loadSavedPeers()
      _connection.value = ConnectionUiState.Connected(target.host, target.port, metadata)
      startKeepAliveLoop()
      triggerAutoSyncForHost(target.host, metadata)
      metadata
    } catch (t: Throwable) {
      _connection.value = ConnectionUiState.Disconnected
      throw t
    }
  }

  suspend fun disconnect() {
    keepAliveJob?.cancel()
    keepAliveJob = null
    lastConnectionTarget = null
    phoneServer?.stop()
    phoneServer = null
    client.disconnect()
    _connection.value = ConnectionUiState.Disconnected
  }

  private fun startKeepAliveLoop() {
    keepAliveJob?.cancel()
    keepAliveJob = scope.launch {
      while (isActive) {
        delay(20_000L)
        if (_connection.value !is ConnectionUiState.Connected) break
        if (_activeTransfers.value.any { it.status == TransferStatus.Running }) continue
        val ok = client.ping()
        if (!ok) {
          client.disconnect()
          _connection.value = ConnectionUiState.Disconnected
          _messages.tryEmit("Connection to PC was lost.")
          break
        }
      }
    }
  }

  fun removeSavedPeer(host: String, port: Int) {
    store.removeSavedPeer(host, port)
    _savedPeers.value = store.loadSavedPeers()
  }

  fun enqueueUploads(
    uris: List<Uri>,
    remoteDir: String,
    fullFilesystem: Boolean,
  ) {
    if (uris.isEmpty()) return
    scope.launch {
      val added = ArrayList<ActiveTransferItem>(uris.size)
      for (uri in uris) {
        val meta = fileHelper.inspectUri(uri)
        val id = UUID.randomUUID().toString()
        val remoteTarget = PathUtils.joinRemotePath(remoteDir, meta.displayName, fullFilesystem)
        added.add(
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
            remoteRelativePath = remoteTarget,
            sizeHint = meta.sizeBytes,
            lastModIso = meta.lastModifiedUtcIso,
          ),
        )
      }
      _activeTransfers.update { it + added }
      TransferForegroundService.start(appContext)
      _messages.tryEmit("Queued ${added.size} file(s) for upload")
    }
  }

  fun enqueueFolderTreeUpload(
    treeUri: Uri,
    remoteDir: String,
    fullFilesystem: Boolean,
  ) {
    scope.launch {
      val rootName = fileHelper.resolveTreeLabel(treeUri)
      val files = fileHelper.walkTree(treeUri)
      if (files.isEmpty()) {
        _messages.tryEmit("Selected folder '$rootName' has no files.")
        return@launch
      }
      val added = ArrayList<ActiveTransferItem>(files.size)
      for (entry in files) {
        val id = UUID.randomUUID().toString()
        val relInsideTree = "$rootName/${entry.relativePath}"
        val remoteTarget = PathUtils.joinRemotePath(remoteDir, relInsideTree, fullFilesystem)
        added.add(
          ActiveTransferItem(
            id = id,
            fileName = entry.displayName,
            direction = TransferDirection.Upload,
            totalBytes = maxOf(0L, entry.sizeBytes),
            status = TransferStatus.Queued,
          ),
        )
        transferChannel.send(
          QueuedTransferJob.UploadUri(
            id = id,
            fileName = entry.displayName,
            uri = entry.uri,
            remoteRelativePath = remoteTarget,
            sizeHint = entry.sizeBytes,
            lastModIso = Instant.ofEpochMilli(entry.lastModifiedMs).toString(),
          ),
        )
      }
      _activeTransfers.update { it + added }
      TransferForegroundService.start(appContext)
      _messages.tryEmit("Queued ${added.size} files from '$rootName'")
    }
  }

  fun enqueueDownloads(
    entries: List<RemoteEntry>,
    showHidden: Boolean,
  ) {
    if (entries.isEmpty()) return
    scope.launch {
      val jobs = ArrayList<Pair<ActiveTransferItem, QueuedTransferJob>>()
      for (entry in entries) {
        if (!entry.isDirectory) {
          val id = UUID.randomUUID().toString()
          jobs.add(
            ActiveTransferItem(id, entry.name, TransferDirection.Download, entry.size, status = TransferStatus.Queued) to
              QueuedTransferJob.DownloadFile(id, entry.name, entry.relativePath, entry.size, ""),
          )
        } else {
          try {
            val listing = client.listDirectory(entry.relativePath, recursive = true, includeHidden = showHidden)
            val baseNorm = entry.relativePath.replace('\\', '/').trimEnd('/')
            for (child in listing.entries.filter { !it.isDirectory }) {
              val childNorm = child.relativePath.replace('\\', '/')
              val rel = childNorm.removePrefix(baseNorm).trimStart('/')
              val subParent = rel.substringBeforeLast('/', "")
              val subfolder = if (subParent.isEmpty()) entry.name else "${entry.name}/$subParent"
              val id = UUID.randomUUID().toString()
              jobs.add(
                ActiveTransferItem(id, child.name, TransferDirection.Download, child.size, status = TransferStatus.Queued) to
                  QueuedTransferJob.DownloadFile(id, child.name, child.relativePath, child.size, subfolder),
              )
            }
          } catch (t: Throwable) {
            _messages.tryEmit("Could not list '${entry.name}': ${t.message}")
          }
        }
      }
      if (jobs.isEmpty()) {
        _messages.tryEmit("Selected folder has no files.")
        return@launch
      }
      for ((_, job) in jobs) transferChannel.send(job)
      _activeTransfers.update { it + jobs.map { it.first } }
      TransferForegroundService.start(appContext)
      _messages.tryEmit("Queued ${jobs.size} download(s) -> Downloads/SwiftBeam")
    }
  }

  fun addSyncPair(treeUri: Uri, remotePath: String, twoWay: Boolean, autoOnConnect: Boolean) {
    val conn = _connection.value as? ConnectionUiState.Connected
    val label = fileHelper.resolveTreeLabel(treeUri)
    val pair = SyncPairRecord(
      id = UUID.randomUUID().toString(),
      localTreeUri = treeUri.toString(),
      localLabel = label,
      remotePath = remotePath.trim(),
      peerHost = conn?.host ?: "",
      twoWay = twoWay,
      autoSyncOnConnect = autoOnConnect,
    )
    val next = listOf(pair) + _syncPairs.value
    store.saveSyncPairs(next)
    _syncPairs.value = next
  }

  fun toggleSyncPairAuto(id: String, enabled: Boolean) {
    val next = _syncPairs.value.map { if (it.id == id) it.copy(autoSyncOnConnect = enabled) else it }
    store.saveSyncPairs(next)
    _syncPairs.value = next
  }

  fun removeSyncPair(id: String) {
    val next = _syncPairs.value.filterNot { it.id == id }
    store.saveSyncPairs(next)
    _syncPairs.value = next
  }

  private fun triggerAutoSyncForHost(host: String, meta: ConnectedPeerMetadata) {
    val matching = _syncPairs.value.filter {
      it.autoSyncOnConnect && (it.peerHost.isBlank() || it.peerHost.equals(host, ignoreCase = true))
    }
    for (pair in matching) {
      syncPairNow(pair.id, meta)
    }
  }

  fun syncPairNow(pairId: String, connectedMeta: ConnectedPeerMetadata? = null) {
    val pair = _syncPairs.value.firstOrNull { it.id == pairId } ?: return
    val meta = connectedMeta ?: (_connection.value as? ConnectionUiState.Connected)?.metadata ?: run {
      _messages.tryEmit("Connect to your PC first to sync folders.")
      return
    }
    scope.launch {
      try {
        val localFiles = fileHelper.walkTree(Uri.parse(pair.localTreeUri))
        val targetRemote = pair.remotePath.ifBlank {
          if (meta.fullFilesystemAccess) (meta.desktopPath ?: meta.homePath ?: "") else ""
        }
        val remoteListing = client.listDirectory(targetRemote, recursive = true, includeHidden = false)
        val plan = PhoneAutoSyncEngine.buildPlan(localFiles, remoteListing.entries, targetRemote, pair.localLabel, pair.twoWay)

        var queued = 0
        for (up in plan.toUpload) {
          val id = UUID.randomUUID().toString()
          val item = ActiveTransferItem(id, up.localFile.displayName, TransferDirection.Upload, maxOf(0L, up.localFile.sizeBytes))
          _activeTransfers.update { it + item }
          transferChannel.send(
            QueuedTransferJob.UploadUri(
              id = id,
              fileName = up.localFile.displayName,
              uri = up.localFile.uri,
              remoteRelativePath = up.remoteRelativePath,
              sizeHint = up.localFile.sizeBytes,
              lastModIso = Instant.ofEpochMilli(up.localFile.lastModifiedMs).toString(),
            ),
          )
          queued++
        }
        for (dl in plan.toDownload) {
          val id = UUID.randomUUID().toString()
          val item = ActiveTransferItem(id, dl.remoteEntry.name, TransferDirection.Download, dl.remoteEntry.size)
          _activeTransfers.update { it + item }
          transferChannel.send(
            QueuedTransferJob.DownloadFile(id, dl.remoteEntry.name, dl.remoteEntry.relativePath, dl.remoteEntry.size, dl.downloadsSubfolder),
          )
          queued++
        }

        if (queued > 0) TransferForegroundService.start(appContext)
        val summary = if (queued == 0) "Up to date (${plan.alreadyInSyncCount} files)"
        else "Queued ${plan.toUpload.size} up, ${plan.toDownload.size} down"
        updateSyncPairResult(pair.id, summary)
        _messages.tryEmit("Sync '${pair.localLabel}': $summary")
      } catch (t: Throwable) {
        updateSyncPairResult(pair.id, "Sync failed: ${t.message}")
        _messages.tryEmit("Sync error: ${t.message}")
      }
    }
  }

  private fun updateSyncPairResult(id: String, summary: String) {
    val now = System.currentTimeMillis()
    val next = _syncPairs.value.map {
      if (it.id == id) it.copy(lastSyncedMs = now, lastSummary = summary) else it
    }
    store.saveSyncPairs(next)
    _syncPairs.value = next
  }

  fun cancelTransfer(id: String) {
    cancelledIds.add(id)
    val currentRunningId = _activeTransfers.value.firstOrNull { it.status == TransferStatus.Running }?.id
    if (id == currentRunningId) {
      currentWorkerJob?.cancel()
    } else {
      _activeTransfers.update { list -> list.filterNot { it.id == id } }
    }
  }

  fun cancelAllTransfers() {
    for (item in _activeTransfers.value) {
      if (item.status == TransferStatus.Queued || item.status == TransferStatus.Running) {
        cancelledIds.add(item.id)
      }
    }
    currentWorkerJob?.cancel()
    _activeTransfers.update { list ->
      list.filter { it.status == TransferStatus.Completed || it.status == TransferStatus.Failed || it.status == TransferStatus.Cancelled }
    }
  }

  fun clearFinishedTransfers() {
    _activeTransfers.update { list -> list.filter { it.status == TransferStatus.Queued || it.status == TransferStatus.Running } }
  }

  fun clearHistory() {
    store.clearTransferHistory()
    _transferHistory.value = emptyList()
  }

  suspend fun sendText(text: String): Boolean {
    val conn = _connection.value as? ConnectionUiState.Connected ?: return false
    val sender = Build.MODEL?.takeIf { it.isNotBlank() } ?: "Android Phone"
    val ack = client.sendText(text, sender)
    if (ack.success) {
      store.appendSentText(SentTextRecord(UUID.randomUUID().toString(), text, conn.metadata.deviceName, System.currentTimeMillis()))
      _sentTexts.value = store.loadSentTexts()
      return true
    }
    _messages.tryEmit(ack.error ?: "Failed to send text.")
    return false
  }

  private fun startTransferWorker() {
    scope.launch {
      for (job in transferChannel) {
        if (cancelledIds.remove(job.id)) continue
        val conn = _connection.value as? ConnectionUiState.Connected
        if (conn == null || !client.isConnected) {
          markFinished(job.id, job.fileName, jobDir(job), 0L, 0.0, TransferStatus.Failed, "Not connected to PC", "Disconnected")
          continue
        }
        val worker = launch { executeJobWithResume(job, conn) }
        currentWorkerJob = worker
        worker.join()
        currentWorkerJob = null
      }
    }
  }

  private suspend fun executeJobWithResume(job: QueuedTransferJob, conn: ConnectionUiState.Connected) {
    val powerMgr = appContext.getSystemService(Context.POWER_SERVICE) as? PowerManager
    val wakeLock = runCatching {
      powerMgr?.newWakeLock(PowerManager.PARTIAL_WAKE_LOCK, "SwiftBeam::TransferWakeLock")?.apply { acquire(15 * 60 * 1000L) }
    }.getOrNull()
    val wifiMgr = appContext.applicationContext.getSystemService(Context.WIFI_SERVICE) as? WifiManager
    @Suppress("DEPRECATION")
    val wifiLock = runCatching {
      val mode = if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.Q) WifiManager.WIFI_MODE_FULL_LOW_LATENCY else WifiManager.WIFI_MODE_FULL_HIGH_PERF
      wifiMgr?.createWifiLock(mode, "SwiftBeam::TransferWifiLock")?.apply { acquire() }
    }.getOrNull()

    val startNs = System.nanoTime()
    var lastUiNs = startNs
    var transferred = 0L

    updateProgress(job.id, TransferStatus.Running, 0L, null, 0.0)
    try {
      when (job) {
        is QueuedTransferJob.UploadUri -> {
          val meta = fileHelper.inspectUri(job.uri)
          val lastMod = job.lastModIso.ifBlank { meta.lastModifiedUtcIso }
          val exactSize = fileHelper.withUploadStream(meta) { _, measuredSize -> measuredSize }
          updateProgress(job.id, TransferStatus.Running, 0L, exactSize, 0.0)

          if (exactSize <= 4L * 1024L * 1024L) {
            fileHelper.withUploadStream(meta) { stream, _ ->
              client.pushFile(job.remoteRelativePath, stream, exactSize, lastMod) { sent, total ->
                transferred = sent
                val now = System.nanoTime()
                if (now - lastUiNs >= 120_000_000L || sent >= total) {
                  lastUiNs = now
                  val secs = maxOf(0.05, (now - startNs) / 1_000_000_000.0)
                  updateProgress(job.id, TransferStatus.Running, sent, total, sent / secs)
                }
              }
            }
            transferred = exactSize
          } else {
            val chunkBytes = 4L * 1024L * 1024L
            val digest = MessageDigest.getInstance("SHA-256")
            var digestIntact = true
            var offset = 0L
            var attempt = 0

            while (offset < exactSize) {
              try {
                fileHelper.withUploadStream(meta) { rawStream, _ ->
                  val buffered = BufferedInputStream(rawStream, 64 * 1024)
                  var toSkip = offset
                  while (toSkip > 0) {
                    val skipped = buffered.skip(toSkip)
                    if (skipped > 0) {
                      toSkip -= skipped
                    } else {
                      if (buffered.read() < 0) break
                      toSkip--
                    }
                  }

                  while (offset < exactSize) {
                    val len = minOf(chunkBytes, exactSize - offset)
                    val baseOffset = offset
                    var inChunk = 0L
                    client.pushChunk(
                      remoteRelativePath = job.remoteRelativePath,
                      totalFileSize = exactSize,
                      offset = offset,
                      length = len,
                      source = buffered,
                      lastModifiedUtcIso = lastMod,
                      digest = if (digestIntact) digest else null,
                    ) { b ->
                      inChunk += b
                      transferred = baseOffset + inChunk
                      val now = System.nanoTime()
                      if (now - lastUiNs >= 120_000_000L || transferred >= exactSize) {
                        lastUiNs = now
                        val secs = maxOf(0.05, (now - startNs) / 1_000_000_000.0)
                        updateProgress(job.id, TransferStatus.Running, transferred, exactSize, transferred / secs)
                      }
                    }
                    offset += len
                  }
                }
              } catch (io: IOException) {
                digestIntact = false
                attempt++
                if (attempt > 3 || !tryReconnect()) throw io
                delay(800L * attempt)
              }
            }
            val shaHex = if (digestIntact) digest.digest().joinToString("") { "%02x".format(it) } else null
            client.finalizePush(job.remoteRelativePath, exactSize, lastMod, shaHex)
          }
          val secs = maxOf(0.05, (System.nanoTime() - startNs) / 1_000_000_000.0)
          markFinished(job.id, job.fileName, TransferDirection.Upload, exactSize, exactSize / secs, TransferStatus.Completed, "Uploaded to PC", conn.metadata.deviceName)
          _directoryRefreshSignal.tryEmit(Unit)
        }

        is QueuedTransferJob.DownloadFile -> {
          val partFile = File(appContext.cacheDir, "dl_${job.id}.sbpart")
          try {
            var totalSize = job.expectedSize
            var attempt = 0
            while (true) {
              val offset = if (partFile.exists()) partFile.length() else 0L
              transferred = offset
              try {
                FileOutputStream(partFile, offset > 0L).use { fos ->
                  val header = client.pullFileRange(job.remoteRelativePath, offset, fos) { inRange, full ->
                    totalSize = full
                    transferred = offset + inRange
                    val now = System.nanoTime()
                    if (now - lastUiNs >= 120_000_000L || transferred >= full) {
                      lastUiNs = now
                      val secs = maxOf(0.05, (now - startNs) / 1_000_000_000.0)
                      updateProgress(job.id, TransferStatus.Running, transferred, full, transferred / secs)
                    }
                  }
                  if (!header.success) error(header.error ?: "Download failed.")
                  totalSize = header.fileSize
                }
                break
              } catch (io: IOException) {
                attempt++
                if (attempt > 3 || !tryReconnect()) throw io
                delay(800L * attempt)
              }
            }
            val saved = fileHelper.saveToDownloads(job.fileName, job.downloadsSubfolder) { destOut ->
              partFile.inputStream().use { inp -> inp.copyTo(destOut, 64 * 1024) }
            }
            val secs = maxOf(0.05, (System.nanoTime() - startNs) / 1_000_000_000.0)
            markFinished(
              id = job.id,
              fileName = job.fileName,
              direction = TransferDirection.Download,
              totalBytes = totalSize,
              avgBps = totalSize / secs,
              status = TransferStatus.Completed,
              message = saved.displayPath,
              peerName = conn.metadata.deviceName,
              savedUri = saved.contentUri?.toString(),
              mimeType = saved.mimeType,
            )
          } finally {
            partFile.delete()
          }
        }
      }
    } catch (ce: CancellationException) {
      markFinished(job.id, job.fileName, jobDir(job), transferred, 0.0, TransferStatus.Cancelled, "Cancelled", conn.metadata.deviceName)
      throw ce
    } catch (t: Throwable) {
      markFinished(job.id, job.fileName, jobDir(job), transferred, 0.0, TransferStatus.Failed, t.message ?: "Transfer failed.", conn.metadata.deviceName)
    } finally {
      runCatching { if (wifiLock?.isHeld == true) wifiLock.release() }
      runCatching { if (wakeLock?.isHeld == true) wakeLock.release() }
    }
  }

  private suspend fun tryReconnect(): Boolean {
    val target = lastConnectionTarget ?: return false
    return runCatching {
      val meta = client.connect(target)
      _connection.value = ConnectionUiState.Connected(target.host, target.port, meta)
      true
    }.getOrDefault(false)
  }

  private fun jobDir(job: QueuedTransferJob): TransferDirection = when (job) {
    is QueuedTransferJob.UploadUri -> TransferDirection.Upload
    is QueuedTransferJob.DownloadFile -> TransferDirection.Download
  }

  private fun updateProgress(id: String, status: TransferStatus, transferred: Long, total: Long?, bps: Double) {
    _activeTransfers.update { list ->
      list.map {
        if (it.id == id) it.copy(status = status, transferredBytes = transferred, totalBytes = total ?: it.totalBytes, bytesPerSec = bps)
        else it
      }
    }
  }

  private fun markFinished(
    id: String,
    fileName: String,
    direction: TransferDirection,
    totalBytes: Long,
    avgBps: Double,
    status: TransferStatus,
    message: String,
    peerName: String,
    savedUri: String? = null,
    mimeType: String? = null,
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
        savedUri = savedUri,
        mimeType = mimeType,
      ),
    )
    _transferHistory.value = store.loadTransferHistory()
    _activeTransfers.update { list ->
      list.map {
        if (it.id == id) {
          it.copy(
            status = status,
            transferredBytes = if (status == TransferStatus.Completed) totalBytes else it.transferredBytes,
            totalBytes = if (totalBytes > 0L) totalBytes else it.totalBytes,
            bytesPerSec = avgBps,
            message = message,
            savedUri = savedUri,
            mimeType = mimeType,
          )
        } else it
      }
    }
  }
}
