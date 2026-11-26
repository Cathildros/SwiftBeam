package com.cathildros.swiftbeam.protocol

import java.io.BufferedInputStream
import java.io.BufferedOutputStream
import java.io.EOFException
import java.io.IOException
import java.io.InputStream
import java.io.OutputStream
import java.net.InetSocketAddress
import java.net.Socket
import java.security.MessageDigest
import java.security.SecureRandom
import java.security.cert.X509Certificate
import java.util.Base64
import javax.net.ssl.SSLContext
import javax.net.ssl.SSLSocket
import javax.net.ssl.TrustManager
import javax.net.ssl.X509TrustManager
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.sync.Mutex
import kotlinx.coroutines.sync.withLock
import kotlinx.coroutines.withContext

class SwiftBeamClient {
  private val ioMutex = Mutex()
  private var socket: Socket? = null
  private var input: InputStream? = null
  private var output: OutputStream? = null

  @Volatile
  var peerMetadata: ConnectedPeerMetadata? = null
    private set

  val isConnected: Boolean
    get() = socket?.let { it.isConnected && !it.isClosed } == true && input != null && output != null

  suspend fun connect(target: ConnectionTarget): ConnectedPeerMetadata = ioMutex.withLock {
    withContext(Dispatchers.IO) {
      disconnectInternal()

      val rawSocket = Socket()
      try {
        rawSocket.tcpNoDelay = true
        rawSocket.receiveBufferSize = 1024 * 1024
        rawSocket.sendBufferSize = 1024 * 1024
        rawSocket.connect(InetSocketAddress(target.host.trim(), target.port), 10_000)
        rawSocket.soTimeout = 60_000

        val rawIn = BufferedInputStream(rawSocket.getInputStream(), 64 * 1024)
        val rawOut = BufferedOutputStream(rawSocket.getOutputStream(), 64 * 1024)

        val magic = if (target.useTls) ProtocolConstants.MAGIC_TLS else ProtocolConstants.MAGIC_PLAIN
        rawOut.write(magic)
        rawOut.flush()

        val ack = ByteArray(1)
        FrameProtocol.readExact(rawIn, ack)
        when (ack[0]) {
          ProtocolConstants.PREAMBLE_OK -> Unit
          ProtocolConstants.PREAMBLE_TLS_REQUIRED ->
            throw IOException("Remote PC requires TLS encryption. Enable Encrypt (TLS 1.3) and reconnect.")
          else ->
            throw IOException("Remote peer rejected protocol preamble (code 0x${(ack[0].toInt() and 0xFF).toString(16)}).")
        }

        var activeSocket: Socket = rawSocket
        var activeIn: InputStream = rawIn
        var activeOut: OutputStream = rawOut
        var remoteCertHash: ByteArray? = null

        if (target.useTls) {
          val sslContext = createTrustEphemeralSslContext()
          val sslSocket = sslContext.socketFactory.createSocket(
            rawSocket,
            target.host.ifBlank { "swiftbeam.local" },
            target.port,
            true,
          ) as SSLSocket
          sslSocket.useClientMode = true
          sslSocket.enabledProtocols = sslSocket.supportedProtocols
            .filter { it == "TLSv1.3" || it == "TLSv1.2" }
            .toTypedArray()
            .ifEmpty { sslSocket.enabledProtocols }
          sslSocket.soTimeout = 60_000
          sslSocket.startHandshake()

          val peerCert = sslSocket.session.peerCertificates.firstOrNull()
            ?: throw IOException("TLS handshake succeeded without a peer certificate.")
          remoteCertHash = MessageDigest.getInstance("SHA-256").digest(peerCert.encoded)

          activeSocket = sslSocket
          activeIn = BufferedInputStream(sslSocket.inputStream, 64 * 1024)
          activeOut = BufferedOutputStream(sslSocket.outputStream, 64 * 1024)
        }

        val challenge = FrameProtocol.readJsonFrame(
          activeIn,
          FrameType.AuthChallenge,
          AuthChallengeMessage.serializer(),
        )

        val serverNonce = Base64.getDecoder().decode(challenge.serverNonceB64)
        val clientNonce = PinAuth.generateNonce()
        val proof = if (challenge.requiresPin) {
          PinAuth.computeProof(
            pin = target.pin,
            serverNonce = serverNonce,
            clientNonce = clientNonce,
            tlsCertHash = if (target.useTls) remoteCertHash else null,
          )
        } else {
          ByteArray(0)
        }

        val response = AuthResponseMessage(
          clientName = target.clientName.ifBlank { "Android" },
          clientNonceB64 = Base64.getEncoder().encodeToString(clientNonce),
          proofB64 = Base64.getEncoder().encodeToString(proof),
          reverseCallbackPort = 0,
          reverseCallbackToken = null,
          mutualAuthToken = null,
        )
        FrameProtocol.writeJsonFrame(activeOut, FrameType.AuthResponse, AuthResponseMessage.serializer(), response)

        val authResult = FrameProtocol.readJsonFrame(
          activeIn,
          FrameType.AuthResult,
          AuthResultMessage.serializer(),
        )
        if (!authResult.success) {
          throw SecurityException(authResult.error ?: "Authentication failed.")
        }

        val metadata = ConnectedPeerMetadata(
          serverId = challenge.serverId,
          deviceName = challenge.deviceName,
          osPlatform = challenge.osPlatform,
          sharedRootName = authResult.sharedRootName ?: "Shared",
          tlsActive = challenge.tlsActive,
          certFingerprint = challenge.certFingerprint,
          fullFilesystemAccess = authResult.fullFilesystemAccess,
          homePath = authResult.homePath,
          desktopPath = authResult.desktopPath,
          downloadsPath = authResult.downloadsPath,
          documentsPath = authResult.documentsPath,
          readOnly = authResult.readOnly,
        )

        socket = activeSocket
        input = activeIn
        output = activeOut
        peerMetadata = metadata
        metadata
      } catch (t: Throwable) {
        runCatching { rawSocket.close() }
        disconnectInternal()
        throw t
      }
    }
  }

  suspend fun listDirectory(
    relativePath: String,
    recursive: Boolean = false,
    includeHidden: Boolean = false,
  ): ListDirResponseMessage = ioMutex.withLock {
    withContext(Dispatchers.IO) {
      val (inp, out) = ensureStreams()
      FrameProtocol.writeJsonFrame(
        out,
        FrameType.ListDirRequest,
        ListDirRequestMessage.serializer(),
        ListDirRequestMessage(relativePath, recursive, includeHidden),
      )
      FrameProtocol.readJsonFrame(inp, FrameType.ListDirResponse, ListDirResponseMessage.serializer())
    }
  }

  suspend fun getFileInfo(
    relativePath: String,
    includeSha256: Boolean = false,
  ): FileInfoResponseMessage = ioMutex.withLock {
    withContext(Dispatchers.IO) {
      val (inp, out) = ensureStreams()
      FrameProtocol.writeJsonFrame(
        out,
        FrameType.FileInfoRequest,
        FileInfoRequestMessage.serializer(),
        FileInfoRequestMessage(relativePath, includeSha256),
      )
      FrameProtocol.readJsonFrame(inp, FrameType.FileInfoResponse, FileInfoResponseMessage.serializer())
    }
  }

  suspend fun createDirectory(relativePath: String): CreateDirResponseMessage = ioMutex.withLock {
    withContext(Dispatchers.IO) {
      val (inp, out) = ensureStreams()
      FrameProtocol.writeJsonFrame(
        out,
        FrameType.CreateDirRequest,
        CreateDirRequestMessage.serializer(),
        CreateDirRequestMessage(relativePath),
      )
      FrameProtocol.readJsonFrame(inp, FrameType.CreateDirResponse, CreateDirResponseMessage.serializer())
    }
  }

  suspend fun deletePath(relativePath: String): DeletePathResponseMessage = ioMutex.withLock {
    withContext(Dispatchers.IO) {
      val (inp, out) = ensureStreams()
      FrameProtocol.writeJsonFrame(
        out,
        FrameType.DeletePathRequest,
        DeletePathRequestMessage.serializer(),
        DeletePathRequestMessage(relativePath),
      )
      FrameProtocol.readJsonFrame(inp, FrameType.DeletePathResponse, DeletePathResponseMessage.serializer())
    }
  }

  suspend fun renamePath(
    relativePath: String,
    newName: String,
  ): RenamePathResponseMessage = ioMutex.withLock {
    withContext(Dispatchers.IO) {
      val (inp, out) = ensureStreams()
      FrameProtocol.writeJsonFrame(
        out,
        FrameType.RenamePathRequest,
        RenamePathRequestMessage.serializer(),
        RenamePathRequestMessage(relativePath, newName),
      )
      FrameProtocol.readJsonFrame(inp, FrameType.RenamePathResponse, RenamePathResponseMessage.serializer())
    }
  }

  suspend fun sendText(
    text: String,
    senderName: String,
  ): SendTextAckMessage = ioMutex.withLock {
    withContext(Dispatchers.IO) {
      val (inp, out) = ensureStreams()
      FrameProtocol.writeJsonFrame(
        out,
        FrameType.SendTextRequest,
        SendTextRequestMessage.serializer(),
        SendTextRequestMessage(text, senderName),
      )
      FrameProtocol.readJsonFrame(inp, FrameType.SendTextAck, SendTextAckMessage.serializer())
    }
  }

  suspend fun ping(): Boolean = ioMutex.withLock {
    withContext(Dispatchers.IO) {
      runCatching {
        val (inp, out) = ensureStreams()
        FrameProtocol.writeFrame(out, FrameType.Ping, ByteArray(0), flush = true)
        val resp = FrameProtocol.readFrame(inp)
        resp?.first == FrameType.Pong
      }.getOrDefault(false)
    }
  }

  suspend fun pullFile(
    remoteRelativePath: String,
    destination: OutputStream,
    onProgress: (transferredBytes: Long, totalBytes: Long) -> Unit = { _, _ -> },
  ): PullRangeHeaderMessage = ioMutex.withLock {
    withContext(Dispatchers.IO) {
      val (inp, out) = ensureStreams()
      var midStream = false
      try {
        FrameProtocol.writeJsonFrame(
          out,
          FrameType.PullRangeRequest,
          PullRangeRequestMessage.serializer(),
          PullRangeRequestMessage(remoteRelativePath, offset = 0L, length = -1L, allowCompression = false),
        )

        val header = FrameProtocol.readJsonFrame(
          inp,
          FrameType.PullRangeHeader,
          PullRangeHeaderMessage.serializer(),
        )
        if (!header.success) {
          return@withContext header
        }

        midStream = true
        val chunkBuf = ByteArray(ProtocolConstants.DATA_FRAME_CHUNK_BYTES)
        var received = 0L
        onProgress(0L, header.length)

        while (true) {
          val frameHeader = FrameProtocol.readFrameHeader(inp)
            ?: throw EOFException("Connection dropped mid-download.")

          val (frameType, frameLength) = frameHeader
          when (frameType) {
            FrameType.DataEnd -> {
              midStream = false
              break
            }
            FrameType.Error -> {
              val errBytes = ByteArray(frameLength)
              if (frameLength > 0) FrameProtocol.readExact(inp, errBytes)
              val err = ProtocolJson.decodeFromString(ErrorMessage.serializer(), errBytes.toString(Charsets.UTF_8))
              midStream = false
              throw RemoteProtocolException(err.message)
            }
            FrameType.DataChunk -> {
              var remaining = frameLength
              while (remaining > 0) {
                val slice = minOf(remaining, chunkBuf.size)
                FrameProtocol.readExact(inp, chunkBuf, 0, slice)
                destination.write(chunkBuf, 0, slice)
                received += slice
                remaining -= slice
                onProgress(received, header.length)
              }
            }
            else -> throw IOException("Unexpected frame $frameType during pull stream.")
          }
        }

        destination.flush()
        if (received != header.length) {
          throw IOException("Incomplete download: expected ${header.length} bytes, received $received bytes.")
        }
        header
      } catch (t: Throwable) {
        if (midStream) disconnectInternal()
        throw t
      }
    }
  }

  suspend fun pushFile(
    remoteRelativePath: String,
    source: InputStream,
    totalFileSize: Long,
    lastModifiedUtcIso: String,
    onProgress: (transferredBytes: Long, totalBytes: Long) -> Unit = { _, _ -> },
  ): PushFinalizeAckMessage = ioMutex.withLock {
    withContext(Dispatchers.IO) {
      val (inp, out) = ensureStreams()

      if (totalFileSize <= 0L) {
        onProgress(0L, 0L)
        FrameProtocol.writeJsonFrame(
          out,
          FrameType.PushFinalizeRequest,
          PushFinalizeRequestMessage.serializer(),
          PushFinalizeRequestMessage(
            relativePath = remoteRelativePath,
            totalFileSize = 0L,
            lastModifiedUtc = lastModifiedUtcIso,
            expectedSha256Hex = null,
          ),
        )
        val finAck = FrameProtocol.readJsonFrame(inp, FrameType.PushFinalizeAck, PushFinalizeAckMessage.serializer())
        if (!finAck.success) {
          throw IOException(finAck.error ?: "Remote host failed to finalize empty file.")
        }
        return@withContext finAck
      }

      var headerSent = false
      var ackReceived = false
      try {
        FrameProtocol.writeJsonFrame(
          out,
          FrameType.PushChunkRequest,
          PushChunkRequestMessage.serializer(),
          PushChunkRequestMessage(
            relativePath = remoteRelativePath,
            totalFileSize = totalFileSize,
            offset = 0L,
            length = totalFileSize,
            lastModifiedUtc = lastModifiedUtcIso,
            allowCompression = false,
          ),
        )
        headerSent = true

        val digest = MessageDigest.getInstance("SHA-256")
        val buffer = ByteArray(ProtocolConstants.DATA_FRAME_CHUNK_BYTES)
        var sent = 0L
        onProgress(0L, totalFileSize)

        while (sent < totalFileSize) {
          val toRead = minOf(buffer.size.toLong(), totalFileSize - sent).toInt()
          val read = source.read(buffer, 0, toRead)
          if (read < 0) break
          if (read == 0) continue

          digest.update(buffer, 0, read)
          FrameProtocol.writeFrame(out, FrameType.DataChunk, buffer, payloadLength = read, flush = false)
          sent += read
          onProgress(sent, totalFileSize)
        }

        if (sent != totalFileSize) {
          throw IOException("Local file size changed during upload ($sent of $totalFileSize bytes read).")
        }

        FrameProtocol.writeFrame(out, FrameType.DataEnd, ByteArray(0), payloadLength = 0, flush = true)
        val chunkAck = FrameProtocol.readJsonFrame(inp, FrameType.PushChunkAck, PushChunkAckMessage.serializer())
        ackReceived = true
        if (!chunkAck.success) {
          throw IOException(chunkAck.error ?: "Remote host rejected uploaded data.")
        }

        val shaHex = digest.digest().joinToString("") { "%02x".format(it) }
        FrameProtocol.writeJsonFrame(
          out,
          FrameType.PushFinalizeRequest,
          PushFinalizeRequestMessage.serializer(),
          PushFinalizeRequestMessage(
            relativePath = remoteRelativePath,
            totalFileSize = totalFileSize,
            lastModifiedUtc = lastModifiedUtcIso,
            expectedSha256Hex = shaHex,
          ),
        )
        val finAck = FrameProtocol.readJsonFrame(inp, FrameType.PushFinalizeAck, PushFinalizeAckMessage.serializer())
        if (!finAck.success) {
          throw IOException(finAck.error ?: "Remote host failed to verify uploaded file.")
        }
        finAck
      } catch (t: Throwable) {
        if (headerSent && !ackReceived) {
          disconnectInternal()
        }
        throw t
      }
    }
  }

  suspend fun disconnect() = ioMutex.withLock {
    withContext(Dispatchers.IO) {
      disconnectInternal()
    }
  }

  private fun ensureStreams(): Pair<InputStream, OutputStream> {
    val inp = input
    val out = output
    val sock = socket
    if (inp == null || out == null || sock == null || sock.isClosed) {
      throw IOException("Not connected to any SwiftBeam PC.")
    }
    return inp to out
  }

  private fun disconnectInternal() {
    runCatching { output?.close() }
    runCatching { input?.close() }
    runCatching { socket?.close() }
    output = null
    input = null
    socket = null
    peerMetadata = null
  }

  private fun createTrustEphemeralSslContext(): SSLContext {
    val trustAll = arrayOf<TrustManager>(
      object : X509TrustManager {
        override fun checkClientTrusted(chain: Array<X509Certificate>, authType: String) = Unit
        override fun checkServerTrusted(chain: Array<X509Certificate>, authType: String) = Unit
        override fun getAcceptedIssuers(): Array<X509Certificate> = emptyArray()
      },
    )
    return SSLContext.getInstance("TLS").apply {
      init(null, trustAll, SecureRandom())
    }
  }
}
