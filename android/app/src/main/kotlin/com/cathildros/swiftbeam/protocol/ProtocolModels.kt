package com.cathildros.swiftbeam.protocol

import kotlinx.serialization.Serializable
import kotlinx.serialization.json.Json

object ProtocolConstants {
  const val DEFAULT_TRANSFER_PORT = 45890
  const val DEFAULT_DISCOVERY_PORT = 45891
  const val MAX_CONTROL_FRAME_BYTES = 16 * 1024 * 1024
  const val DATA_FRAME_CHUNK_BYTES = 512 * 1024
  const val DRIVES_VIRTUAL_PATH = "__DRIVES__"

  val MAGIC_PLAIN: ByteArray = "SBM1".toByteArray(Charsets.US_ASCII)
  val MAGIC_TLS: ByteArray = "SBT1".toByteArray(Charsets.US_ASCII)

  const val PREAMBLE_OK: Byte = 0x01
  const val PREAMBLE_TLS_REQUIRED: Byte = 0x02
  const val PREAMBLE_INVALID: Byte = 0x03
}

val ProtocolJson: Json = Json {
  ignoreUnknownKeys = true
  encodeDefaults = true
}

enum class FrameType(val code: Byte) {
  AuthChallenge(0x01),
  AuthResponse(0x02),
  AuthResult(0x03),

  ListDirRequest(0x10),
  ListDirResponse(0x11),
  FileInfoRequest(0x12),
  FileInfoResponse(0x13),
  CreateDirRequest(0x14),
  CreateDirResponse(0x15),
  DeletePathRequest(0x16),
  DeletePathResponse(0x17),
  RenamePathRequest(0x18),
  RenamePathResponse(0x19),

  PullRangeRequest(0x20),
  PullRangeHeader(0x21),
  DataChunk(0x22),
  DataEnd(0x23),
  PushChunkRequest(0x24),
  PushChunkAck(0x25),
  PushFinalizeRequest(0x26),
  PushFinalizeAck(0x27),
  SendTextRequest(0x28),
  SendTextAck(0x29),
  CompressedDataChunk(0x2A),

  Ping(0x30),
  Pong(0x31),

  Error(0xFF.toByte());

  companion object {
    private val byCode = entries.associateBy { it.code }
    fun fromCode(code: Byte): FrameType =
      byCode[code] ?: throw IllegalArgumentException("Unknown SwiftBeam frame type: 0x${(code.toInt() and 0xFF).toString(16)}")
  }
}

@Serializable
data class AuthChallengeMessage(
  val serverId: String,
  val deviceName: String,
  val osPlatform: String,
  val serverNonceB64: String,
  val requiresPin: Boolean,
  val tlsActive: Boolean,
  val certFingerprint: String? = null,
)

@Serializable
data class AuthResponseMessage(
  val clientName: String,
  val clientNonceB64: String,
  val proofB64: String,
  val reverseCallbackPort: Int = 0,
  val reverseCallbackToken: String? = null,
  val mutualAuthToken: String? = null,
)

@Serializable
data class AuthResultMessage(
  val success: Boolean,
  val error: String? = null,
  val sharedRootName: String? = null,
  val fullFilesystemAccess: Boolean = false,
  val homePath: String? = null,
  val desktopPath: String? = null,
  val downloadsPath: String? = null,
  val documentsPath: String? = null,
  val readOnly: Boolean = false,
)

@Serializable
data class CreateDirRequestMessage(
  val relativePath: String,
)

@Serializable
data class CreateDirResponseMessage(
  val success: Boolean,
  val error: String? = null,
)

@Serializable
data class DeletePathRequestMessage(
  val relativePath: String,
)

@Serializable
data class DeletePathResponseMessage(
  val success: Boolean,
  val error: String? = null,
)

@Serializable
data class RenamePathRequestMessage(
  val relativePath: String,
  val newName: String,
)

@Serializable
data class RenamePathResponseMessage(
  val success: Boolean,
  val newRelativePath: String? = null,
  val error: String? = null,
)

@Serializable
data class PushChunkRequestMessage(
  val relativePath: String,
  val totalFileSize: Long,
  val offset: Long,
  val length: Long,
  val lastModifiedUtc: String,
  val allowCompression: Boolean = false,
)

@Serializable
data class PushChunkAckMessage(
  val success: Boolean,
  val error: String? = null,
)

@Serializable
data class PushFinalizeRequestMessage(
  val relativePath: String,
  val totalFileSize: Long,
  val lastModifiedUtc: String,
  val expectedSha256Hex: String? = null,
)

@Serializable
data class PushFinalizeAckMessage(
  val success: Boolean,
  val error: String? = null,
)

@Serializable
data class ListDirRequestMessage(
  val relativePath: String,
  val recursive: Boolean = false,
  val includeHidden: Boolean = false,
)

@Serializable
data class RemoteEntry(
  val name: String,
  val relativePath: String,
  val isDirectory: Boolean,
  val size: Long = 0,
  val lastModifiedUtc: String = "",
  val isHidden: Boolean = false,
)

@Serializable
data class ListDirResponseMessage(
  val currentPath: String,
  val entries: List<RemoteEntry> = emptyList(),
  val error: String? = null,
  val freeDiskBytes: Long? = null,
  val totalDiskBytes: Long? = null,
)

@Serializable
data class FileInfoRequestMessage(
  val relativePath: String,
  val includeSha256: Boolean = false,
)

@Serializable
data class FileInfoResponseMessage(
  val exists: Boolean,
  val size: Long,
  val lastModifiedUtc: String = "",
  val sha256Hex: String? = null,
  val error: String? = null,
)

@Serializable
data class PullRangeRequestMessage(
  val relativePath: String,
  val offset: Long,
  val length: Long,
  val allowCompression: Boolean = false,
)

@Serializable
data class PullRangeHeaderMessage(
  val success: Boolean,
  val fileSize: Long,
  val lastModifiedUtc: String = "",
  val offset: Long = 0,
  val length: Long = 0,
  val error: String? = null,
)

@Serializable
data class SendTextRequestMessage(
  val text: String,
  val senderName: String,
)

@Serializable
data class SendTextAckMessage(
  val success: Boolean,
  val error: String? = null,
)

@Serializable
data class ErrorMessage(
  val message: String,
)

data class ConnectionTarget(
  val host: String,
  val port: Int,
  val pin: String,
  val useTls: Boolean,
  val clientName: String,
  val reverseCallbackPort: Int = 0,
  val reverseCallbackToken: String? = null,
)

data class ConnectedPeerMetadata(
  val serverId: String,
  val deviceName: String,
  val osPlatform: String,
  val sharedRootName: String,
  val tlsActive: Boolean,
  val certFingerprint: String?,
  val fullFilesystemAccess: Boolean = false,
  val homePath: String? = null,
  val desktopPath: String? = null,
  val downloadsPath: String? = null,
  val documentsPath: String? = null,
  val readOnly: Boolean = false,
)
