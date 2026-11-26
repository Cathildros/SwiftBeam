package com.cathildros.swiftbeam.data

import android.content.Context
import com.cathildros.swiftbeam.protocol.ProtocolJson
import kotlinx.serialization.Serializable
import kotlinx.serialization.builtins.ListSerializer

@Serializable
data class SavedPeerRecord(
  val deviceName: String,
  val host: String,
  val port: Int,
  val pin: String,
  val useTls: Boolean,
  val lastConnectedMs: Long,
)

@Serializable
data class TransferHistoryRecord(
  val id: String,
  val fileName: String,
  val direction: String, // "Upload" or "Download"
  val totalBytes: Long,
  val averageBytesPerSec: Double,
  val peerName: String,
  val status: String, // "Completed" or "Failed" or "Cancelled"
  val message: String = "",
  val completedAtMs: Long,
  val savedUri: String? = null,
  val mimeType: String? = null,
)

@Serializable
data class SentTextRecord(
  val id: String,
  val text: String,
  val peerName: String,
  val sentAtMs: Long,
)

@Serializable
data class ReceivedTextRecord(
  val id: String,
  val text: String,
  val senderName: String,
  val receivedAtMs: Long,
)

@Serializable
data class SyncPairRecord(
  val id: String,
  val localTreeUri: String,
  val localLabel: String,
  val remotePath: String,
  val peerHost: String,
  val twoWay: Boolean = false,
  val autoSyncOnConnect: Boolean = true,
  val lastSyncedMs: Long = 0L,
  val lastSummary: String = "Never synced",
)

data class PhoneHostPrefs(
  val enabled: Boolean,
  val treeUri: String?,
  val label: String,
)

class SavedPeerStore(context: Context) {
  private val prefs = context.applicationContext.getSharedPreferences("swiftbeam_prefs", Context.MODE_PRIVATE)

  fun loadSavedPeers(): List<SavedPeerRecord> = decodeList(KEY_PEERS, SavedPeerRecord.serializer())

  fun upsertSavedPeer(record: SavedPeerRecord) {
    val updated = loadSavedPeers()
      .filterNot { it.host.equals(record.host, ignoreCase = true) && it.port == record.port }
      .toMutableList()
    updated.add(0, record)
    encodeList(KEY_PEERS, updated.take(12), SavedPeerRecord.serializer())
  }

  fun removeSavedPeer(host: String, port: Int) {
    val updated = loadSavedPeers()
      .filterNot { it.host.equals(host, ignoreCase = true) && it.port == port }
    encodeList(KEY_PEERS, updated, SavedPeerRecord.serializer())
  }

  fun loadTransferHistory(): List<TransferHistoryRecord> =
    decodeList(KEY_HISTORY, TransferHistoryRecord.serializer())

  fun appendTransferHistory(record: TransferHistoryRecord) {
    val updated = loadTransferHistory().toMutableList()
    updated.add(0, record)
    encodeList(KEY_HISTORY, updated.take(80), TransferHistoryRecord.serializer())
  }

  fun clearTransferHistory() {
    prefs.edit().remove(KEY_HISTORY).apply()
  }

  fun loadSentTexts(): List<SentTextRecord> = decodeList(KEY_TEXTS, SentTextRecord.serializer())

  fun appendSentText(record: SentTextRecord) {
    val updated = loadSentTexts().toMutableList()
    updated.add(0, record)
    encodeList(KEY_TEXTS, updated.take(30), SentTextRecord.serializer())
  }

  fun loadReceivedTexts(): List<ReceivedTextRecord> = decodeList(KEY_RX_TEXTS, ReceivedTextRecord.serializer())

  fun appendReceivedText(record: ReceivedTextRecord) {
    val updated = loadReceivedTexts().toMutableList()
    updated.add(0, record)
    encodeList(KEY_RX_TEXTS, updated.take(30), ReceivedTextRecord.serializer())
  }

  fun loadSyncPairs(): List<SyncPairRecord> = decodeList(KEY_SYNC_PAIRS, SyncPairRecord.serializer())

  fun saveSyncPairs(pairs: List<SyncPairRecord>) {
    encodeList(KEY_SYNC_PAIRS, pairs, SyncPairRecord.serializer())
  }

  fun loadPhoneHostPrefs(): PhoneHostPrefs = PhoneHostPrefs(
    enabled = prefs.getBoolean(KEY_PHONE_HOST_ENABLED, true),
    treeUri = prefs.getString(KEY_PHONE_HOST_URI, null),
    label = prefs.getString(KEY_PHONE_HOST_LABEL, "App Shared Folder") ?: "App Shared Folder",
  )

  fun savePhoneHostPrefs(enabled: Boolean, treeUri: String?, label: String) {
    prefs.edit()
      .putBoolean(KEY_PHONE_HOST_ENABLED, enabled)
      .putString(KEY_PHONE_HOST_URI, treeUri)
      .putString(KEY_PHONE_HOST_LABEL, label)
      .apply()
  }

  private fun <T> decodeList(key: String, serializer: kotlinx.serialization.KSerializer<T>): List<T> {
    val raw = prefs.getString(key, null) ?: return emptyList()
    return runCatching {
      ProtocolJson.decodeFromString(ListSerializer(serializer), raw)
    }.getOrDefault(emptyList())
  }

  private fun <T> encodeList(key: String, list: List<T>, serializer: kotlinx.serialization.KSerializer<T>) {
    val raw = ProtocolJson.encodeToString(ListSerializer(serializer), list)
    prefs.edit().putString(key, raw).apply()
  }

  private companion object {
    const val KEY_PEERS = "saved_peers_json"
    const val KEY_HISTORY = "transfer_history_json"
    const val KEY_TEXTS = "sent_texts_json"
    const val KEY_RX_TEXTS = "received_texts_json"
    const val KEY_SYNC_PAIRS = "sync_pairs_json"
    const val KEY_PHONE_HOST_ENABLED = "phone_host_enabled"
    const val KEY_PHONE_HOST_URI = "phone_host_tree_uri"
    const val KEY_PHONE_HOST_LABEL = "phone_host_tree_label"
  }
}
