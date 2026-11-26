package com.cathildros.swiftbeam.data

import android.content.Context
import com.cathildros.swiftbeam.protocol.ProtocolJson
import kotlinx.serialization.Serializable

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
)

@Serializable
data class SentTextRecord(
  val id: String,
  val text: String,
  val peerName: String,
  val sentAtMs: Long,
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

  private fun <T> decodeList(key: String, serializer: kotlinx.serialization.KSerializer<T>): List<T> {
    val raw = prefs.getString(key, null) ?: return emptyList()
    return runCatching {
      ProtocolJson.decodeFromString(kotlinx.serialization.builtins.ListSerializer(serializer), raw)
    }.getOrDefault(emptyList())
  }

  private fun <T> encodeList(key: String, list: List<T>, serializer: kotlinx.serialization.KSerializer<T>) {
    val raw = ProtocolJson.encodeToString(kotlinx.serialization.builtins.ListSerializer(serializer), list)
    prefs.edit().putString(key, raw).apply()
  }

  private companion object {
    const val KEY_PEERS = "saved_peers_json"
    const val KEY_HISTORY = "transfer_history_json"
    const val KEY_TEXTS = "sent_texts_json"
  }
}
