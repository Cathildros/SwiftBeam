package com.cathildros.swiftbeam.server

import android.content.Context
import android.net.Uri
import android.os.Build
import android.provider.DocumentsContract
import com.cathildros.swiftbeam.protocol.AuthChallengeMessage
import com.cathildros.swiftbeam.protocol.AuthResponseMessage
import com.cathildros.swiftbeam.protocol.AuthResultMessage
import com.cathildros.swiftbeam.protocol.CreateDirRequestMessage
import com.cathildros.swiftbeam.protocol.CreateDirResponseMessage
import com.cathildros.swiftbeam.protocol.DeletePathRequestMessage
import com.cathildros.swiftbeam.protocol.DeletePathResponseMessage
import com.cathildros.swiftbeam.protocol.FileInfoRequestMessage
import com.cathildros.swiftbeam.protocol.FileInfoResponseMessage
import com.cathildros.swiftbeam.protocol.FrameProtocol
import com.cathildros.swiftbeam.protocol.FrameType
import com.cathildros.swiftbeam.protocol.ListDirRequestMessage
import com.cathildros.swiftbeam.protocol.ListDirResponseMessage
import com.cathildros.swiftbeam.protocol.PinAuth
import com.cathildros.swiftbeam.protocol.ProtocolConstants
import com.cathildros.swiftbeam.protocol.ProtocolJson
import com.cathildros.swiftbeam.protocol.PullRangeHeaderMessage
import com.cathildros.swiftbeam.protocol.PullRangeRequestMessage
import com.cathildros.swiftbeam.protocol.PushChunkAckMessage
import com.cathildros.swiftbeam.protocol.PushChunkRequestMessage
import com.cathildros.swiftbeam.protocol.PushFinalizeAckMessage
import com.cathildros.swiftbeam.protocol.PushFinalizeRequestMessage
import com.cathildros.swiftbeam.protocol.RemoteEntry
import com.cathildros.swiftbeam.protocol.RenamePathRequestMessage
import com.cathildros.swiftbeam.protocol.RenamePathResponseMessage
import com.cathildros.swiftbeam.protocol.SendTextAckMessage
import com.cathildros.swiftbeam.protocol.SendTextRequestMessage
import java.io.BufferedInputStream
import java.io.BufferedOutputStream
import java.io.ByteArrayOutputStream
import java.io.File
import java.io.FileOutputStream
import java.io.InputStream
import java.io.OutputStream
import java.io.RandomAccessFile
import java.math.BigInteger
import java.net.ServerSocket
import java.net.Socket
import java.security.KeyPairGenerator
import java.security.KeyStore
import java.security.MessageDigest
import java.security.SecureRandom
import java.security.cert.X509Certificate
import java.security.spec.ECGenParameterSpec
import java.time.Instant
import java.util.Base64
import java.util.Date
import java.util.UUID
import java.util.concurrent.ConcurrentHashMap
import javax.net.ssl.KeyManagerFactory
import javax.net.ssl.SSLContext
import javax.net.ssl.SSLSocket
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.Job
import kotlinx.coroutines.SupervisorJob
import kotlinx.coroutines.cancel
import kotlinx.coroutines.isActive
import kotlinx.coroutines.launch
import org.bouncycastle.asn1.x500.X500Name
import org.bouncycastle.cert.jcajce.JcaX509CertificateConverter
import org.bouncycastle.cert.jcajce.JcaX509v3CertificateBuilder
import org.bouncycastle.jce.provider.BouncyCastleProvider
import org.bouncycastle.operator.jcajce.JcaContentSignerBuilder

interface PhoneStorageProvider {
  val rootName: String
  fun listDirectory(relativePath: String, recursive: Boolean, includeHidden: Boolean): ListDirResponseMessage
  fun fileInfo(relativePath: String, includeSha256: Boolean): FileInfoResponseMessage
  fun createDirectory(relativePath: String): CreateDirResponseMessage
  fun deletePath(relativePath: String): DeletePathResponseMessage
  fun renamePath(relativePath: String, newName: String): RenamePathResponseMessage
  fun readRange(relativePath: String, offset: Long, length: Long, streamWriter: (fileSize: Long, lastModIso: String, dataStartOffset: Long, dataLen: Long, provider: InputStream?) -> Unit)
  fun prepareWritePart(relativePath: String, totalFileSize: Long): OutputStream?
  fun writePartChunk(relativePath: String, totalFileSize: Long, offset: Long, length: Long, inp: InputStream)
  fun finalizePart(relativePath: String, totalFileSize: Long, lastModIso: String, expectedSha256Hex: String?): PushFinalizeAckMessage
}

class DirectoryStorageProvider(
  private val rootDir: File,
  override val rootName: String = rootDir.name.ifBlank { "Shared" },
) : PhoneStorageProvider {
  init {
    if (!rootDir.exists()) rootDir.mkdirs()
  }

  private fun resolveSafe(rel: String): File? {
    val clean = rel.trim().replace('\\', '/').trim('/')
    val candidate = if (clean.isEmpty()) rootDir else File(rootDir, clean)
    val rootCanon = rootDir.canonicalPath
    val targetCanon = candidate.canonicalPath
    if (targetCanon != rootCanon && !targetCanon.startsWith(rootCanon + File.separator)) return null
    return candidate
  }

  private fun formatWire(file: File): String {
    val rootCanon = rootDir.canonicalPath
    val targetCanon = file.canonicalPath
    if (targetCanon == rootCanon) return ""
    return targetCanon.removePrefix(rootCanon + File.separator).replace(File.separatorChar, '/')
  }

  override fun listDirectory(relativePath: String, recursive: Boolean, includeHidden: Boolean): ListDirResponseMessage {
    val dir = resolveSafe(relativePath) ?: return ListDirResponseMessage(relativePath, emptyList(), "Invalid path.")
    if (!dir.exists() || !dir.isDirectory) return ListDirResponseMessage(relativePath, emptyList(), "Folder not found.")

    val entries = ArrayList<RemoteEntry>()
    fun walk(current: File) {
      val children = current.listFiles() ?: return
      for (f in children.sortedWith(compareBy<File> { !it.isDirectory }.thenBy { it.name.lowercase() })) {
        if (f.name.endsWith(".sbpart")) continue
        val hidden = f.isHidden || f.name.startsWith(".")
        if (hidden && !includeHidden) continue
        entries.add(
          RemoteEntry(
            name = f.name,
            relativePath = formatWire(f),
            isDirectory = f.isDirectory,
            size = if (f.isDirectory) 0L else f.length(),
            lastModifiedUtc = Instant.ofEpochMilli(f.lastModified()).toString(),
            isHidden = hidden,
          ),
        )
        if (recursive && f.isDirectory) walk(f)
      }
    }
    walk(dir)
    return ListDirResponseMessage(
      currentPath = formatWire(dir),
      entries = entries,
      freeDiskBytes = rootDir.usableSpace,
      totalDiskBytes = rootDir.totalSpace,
    )
  }

  override fun fileInfo(relativePath: String, includeSha256: Boolean): FileInfoResponseMessage {
    val file = resolveSafe(relativePath) ?: return FileInfoResponseMessage(false, 0, error = "Invalid path.")
    if (!file.exists() || !file.isFile) return FileInfoResponseMessage(false, 0, error = "File not found.")
    val sha = if (includeSha256) sha256File(file) else null
    return FileInfoResponseMessage(true, file.length(), Instant.ofEpochMilli(file.lastModified()).toString(), sha)
  }

  override fun createDirectory(relativePath: String): CreateDirResponseMessage {
    val dir = resolveSafe(relativePath) ?: return CreateDirResponseMessage(false, "Invalid path.")
    return if (dir.exists() || dir.mkdirs()) CreateDirResponseMessage(true)
    else CreateDirResponseMessage(false, "Could not create folder.")
  }

  override fun deletePath(relativePath: String): DeletePathResponseMessage {
    val target = resolveSafe(relativePath) ?: return DeletePathResponseMessage(false, "Invalid path.")
    if (target.canonicalPath == rootDir.canonicalPath) return DeletePathResponseMessage(false, "Cannot delete root.")
    val ok = if (target.isDirectory) target.deleteRecursively() else target.delete()
    return if (ok) DeletePathResponseMessage(true) else DeletePathResponseMessage(false, "Delete failed.")
  }

  override fun renamePath(relativePath: String, newName: String): RenamePathResponseMessage {
    val target = resolveSafe(relativePath) ?: return RenamePathResponseMessage(false, error = "Invalid path.")
    val cleanNew = newName.trim()
    if (cleanNew.isEmpty() || cleanNew.contains('/') || cleanNew.contains('\\')) {
      return RenamePathResponseMessage(false, error = "Invalid new name.")
    }
    val dest = File(target.parentFile, cleanNew)
    return if (target.renameTo(dest)) RenamePathResponseMessage(true, formatWire(dest))
    else RenamePathResponseMessage(false, error = "Rename failed.")
  }

  override fun readRange(
    relativePath: String,
    offset: Long,
    length: Long,
    streamWriter: (Long, String, Long, Long, InputStream?) -> Unit,
  ) {
    val file = resolveSafe(relativePath)
    if (file == null || !file.exists() || !file.isFile) {
      streamWriter(0L, "", 0L, -1L, null)
      return
    }
    val total = file.length()
    val start = offset.coerceIn(0L, total)
    val count = if (length < 0) total - start else minOf(length, total - start)
    val lastMod = Instant.ofEpochMilli(file.lastModified()).toString()
    val fis = file.inputStream()
    try {
      var skipped = 0L
      while (skipped < start) {
        val s = fis.skip(start - skipped)
        if (s <= 0) break
        skipped += s
      }
      streamWriter(total, lastMod, start, count, fis)
    } finally {
      runCatching { fis.close() }
    }
  }

  override fun prepareWritePart(relativePath: String, totalFileSize: Long): OutputStream? = null

  override fun writePartChunk(relativePath: String, totalFileSize: Long, offset: Long, length: Long, inp: InputStream) {
    val target = resolveSafe(relativePath) ?: error("Invalid target path.")
    target.parentFile?.mkdirs()
    val part = File(target.parentFile, target.name + ".sbpart")
    RandomAccessFile(part, "rw").use { raf ->
      if (raf.length() < totalFileSize) raf.setLength(totalFileSize)
      raf.seek(offset)
      val buf = ByteArray(ProtocolConstants.DATA_FRAME_CHUNK_BYTES)
      var remaining = length
      while (remaining > 0) {
        val header = FrameProtocol.readFrameHeader(inp) ?: error("Connection closed during upload.")
        when (header.first) {
          FrameType.DataEnd -> return
          FrameType.DataChunk -> {
            var frameRem = header.second
            while (frameRem > 0) {
              val slice = minOf(frameRem, buf.size)
              FrameProtocol.readExact(inp, buf, 0, slice)
              raf.write(buf, 0, slice)
              frameRem -= slice
              remaining -= slice
            }
          }
          else -> error("Expected DataChunk frame, got ${header.first}")
        }
      }
      // Consume trailing DataEnd
      val end = FrameProtocol.readFrameHeader(inp)
      if (end != null && end.first != FrameType.DataEnd) {
        error("Expected DataEnd after chunk, got ${end.first}")
      }
    }
  }

  override fun finalizePart(relativePath: String, totalFileSize: Long, lastModIso: String, expectedSha256Hex: String?): PushFinalizeAckMessage {
    val target = resolveSafe(relativePath) ?: return PushFinalizeAckMessage(false, "Invalid target path.")
    target.parentFile?.mkdirs()
    val part = File(target.parentFile, target.name + ".sbpart")
    if (totalFileSize == 0L) {
      FileOutputStream(target).close()
      part.delete()
      return PushFinalizeAckMessage(true)
    }
    if (!part.exists()) return PushFinalizeAckMessage(false, "Uploaded partial file is missing.")
    if (!expectedSha256Hex.isNullOrBlank()) {
      val actual = sha256File(part)
      if (!actual.equals(expectedSha256Hex, ignoreCase = true)) {
        return PushFinalizeAckMessage(false, "SHA-256 verification failed on phone.")
      }
    }
    if (target.exists()) target.delete()
    if (!part.renameTo(target)) {
      part.copyTo(target, overwrite = true)
      part.delete()
    }
    return PushFinalizeAckMessage(true)
  }

  private fun sha256File(file: File): String {
    val digest = MessageDigest.getInstance("SHA-256")
    val buf = ByteArray(64 * 1024)
    file.inputStream().use { inp ->
      while (true) {
        val r = inp.read(buf)
        if (r <= 0) break
        digest.update(buf, 0, r)
      }
    }
    return digest.digest().joinToString("") { "%02x".format(it) }
  }
}

class SafTreeStorageProvider(
  context: Context,
  private val treeUri: Uri,
  override val rootName: String,
) : PhoneStorageProvider {
  private val resolver = context.applicationContext.contentResolver
  private val cacheDir = File(context.applicationContext.cacheDir, "phone_host_staging").apply { mkdirs() }
  private val rootDocId: String = DocumentsContract.getTreeDocumentId(treeUri)

  private fun findNode(relativePath: String): Pair<String, Boolean>? {
    val clean = relativePath.trim().replace('\\', '/').trim('/')
    if (clean.isEmpty()) return rootDocId to true
    var currentId = rootDocId
    var currentIsDir = true
    val segments = clean.split('/').filter { it.isNotBlank() }
    for (seg in segments) {
      if (!currentIsDir) return null
      val childrenUri = DocumentsContract.buildChildDocumentsUriUsingTree(treeUri, currentId)
      var found: Pair<String, Boolean>? = null
      resolver.query(
        childrenUri,
        arrayOf(
          DocumentsContract.Document.COLUMN_DOCUMENT_ID,
          DocumentsContract.Document.COLUMN_DISPLAY_NAME,
          DocumentsContract.Document.COLUMN_MIME_TYPE,
        ),
        null,
        null,
        null,
      )?.use { c ->
        while (c.moveToNext()) {
          val id = c.getString(0) ?: continue
          val name = c.getString(1) ?: continue
          val mime = c.getString(2)
          if (name == seg) {
            found = id to (mime == DocumentsContract.Document.MIME_TYPE_DIR)
            break
          }
        }
      }
      val resolved = found ?: return null
      currentId = resolved.first
      currentIsDir = resolved.second
    }
    return currentId to currentIsDir
  }

  override fun listDirectory(relativePath: String, recursive: Boolean, includeHidden: Boolean): ListDirResponseMessage {
    val node = findNode(relativePath) ?: return ListDirResponseMessage(relativePath, emptyList(), "Folder not found.")
    if (!node.second) return ListDirResponseMessage(relativePath, emptyList(), "Not a folder.")
    val entries = ArrayList<RemoteEntry>()

    fun walk(docId: String, prefix: String) {
      val childrenUri = DocumentsContract.buildChildDocumentsUriUsingTree(treeUri, docId)
      resolver.query(
        childrenUri,
        arrayOf(
          DocumentsContract.Document.COLUMN_DOCUMENT_ID,
          DocumentsContract.Document.COLUMN_DISPLAY_NAME,
          DocumentsContract.Document.COLUMN_MIME_TYPE,
          DocumentsContract.Document.COLUMN_SIZE,
          DocumentsContract.Document.COLUMN_LAST_MODIFIED,
        ),
        null,
        null,
        null,
      )?.use { c ->
        while (c.moveToNext()) {
          val cid = c.getString(0) ?: continue
          val name = c.getString(1) ?: continue
          val hidden = name.startsWith(".")
          if (hidden && !includeHidden) continue
          val isDir = c.getString(2) == DocumentsContract.Document.MIME_TYPE_DIR
          val size = if (c.isNull(3)) 0L else c.getLong(3)
          val mod = if (c.isNull(4)) System.currentTimeMillis() else c.getLong(4)
          val rel = if (prefix.isEmpty()) name else "$prefix/$name"
          entries.add(RemoteEntry(name, rel, isDir, if (isDir) 0L else size, Instant.ofEpochMilli(mod).toString(), hidden))
          if (recursive && isDir) walk(cid, rel)
        }
      }
    }

    val cleanCur = relativePath.trim().replace('\\', '/').trim('/')
    walk(node.first, cleanCur)
    return ListDirResponseMessage(cleanCur, entries.sortedWith(compareBy<RemoteEntry> { !it.isDirectory }.thenBy { it.name.lowercase() }))
  }

  override fun fileInfo(relativePath: String, includeSha256: Boolean): FileInfoResponseMessage {
    val node = findNode(relativePath) ?: return FileInfoResponseMessage(false, 0, error = "File not found.")
    if (node.second) return FileInfoResponseMessage(false, 0, error = "Path is a folder.")
    val docUri = DocumentsContract.buildDocumentUriUsingTree(treeUri, node.first)
    var size = 0L
    var mod = System.currentTimeMillis()
    resolver.query(docUri, arrayOf(DocumentsContract.Document.COLUMN_SIZE, DocumentsContract.Document.COLUMN_LAST_MODIFIED), null, null, null)?.use { c ->
      if (c.moveToFirst()) {
        if (!c.isNull(0)) size = c.getLong(0)
        if (!c.isNull(1)) mod = c.getLong(1)
      }
    }
    return FileInfoResponseMessage(true, size, Instant.ofEpochMilli(mod).toString(), null)
  }

  override fun createDirectory(relativePath: String): CreateDirResponseMessage {
    val clean = relativePath.trim().replace('\\', '/').trim('/')
    if (clean.isEmpty()) return CreateDirResponseMessage(true)
    var curId = rootDocId
    for (seg in clean.split('/').filter { it.isNotBlank() }) {
      val existing = findChild(curId, seg)
      curId = if (existing != null) {
        existing
      } else {
        val parentUri = DocumentsContract.buildDocumentUriUsingTree(treeUri, curId)
        val createdUri = DocumentsContract.createDocument(resolver, parentUri, DocumentsContract.Document.MIME_TYPE_DIR, seg)
          ?: return CreateDirResponseMessage(false, "Could not create folder $seg.")
        DocumentsContract.getDocumentId(createdUri)
      }
    }
    return CreateDirResponseMessage(true)
  }

  override fun deletePath(relativePath: String): DeletePathResponseMessage {
    val node = findNode(relativePath) ?: return DeletePathResponseMessage(false, "Not found.")
    if (node.first == rootDocId) return DeletePathResponseMessage(false, "Cannot delete root folder.")
    val docUri = DocumentsContract.buildDocumentUriUsingTree(treeUri, node.first)
    return if (DocumentsContract.deleteDocument(resolver, docUri)) DeletePathResponseMessage(true)
    else DeletePathResponseMessage(false, "Delete failed.")
  }

  override fun renamePath(relativePath: String, newName: String): RenamePathResponseMessage {
    val node = findNode(relativePath) ?: return RenamePathResponseMessage(false, error = "Not found.")
    val docUri = DocumentsContract.buildDocumentUriUsingTree(treeUri, node.first)
    val renamed = runCatching { DocumentsContract.renameDocument(resolver, docUri, newName.trim()) }.getOrNull()
      ?: return RenamePathResponseMessage(false, error = "Rename failed.")
    val parent = relativePath.trim().replace('\\', '/').trim('/').substringBeforeLast('/', "")
    val newRel = if (parent.isEmpty()) newName.trim() else "$parent/${newName.trim()}"
    return RenamePathResponseMessage(true, newRel)
  }

  override fun readRange(
    relativePath: String,
    offset: Long,
    length: Long,
    streamWriter: (Long, String, Long, Long, InputStream?) -> Unit,
  ) {
    val node = findNode(relativePath)
    if (node == null || node.second) {
      streamWriter(0L, "", 0L, -1L, null)
      return
    }
    val info = fileInfo(relativePath, false)
    val total = info.size
    val start = offset.coerceIn(0L, total)
    val count = if (length < 0) total - start else minOf(length, total - start)
    val docUri = DocumentsContract.buildDocumentUriUsingTree(treeUri, node.first)
    val inp = resolver.openInputStream(docUri)
    if (inp == null) {
      streamWriter(0L, "", 0L, -1L, null)
      return
    }
    try {
      var skipped = 0L
      while (skipped < start) {
        val s = inp.skip(start - skipped)
        if (s <= 0) break
        skipped += s
      }
      streamWriter(total, info.lastModifiedUtc, start, count, inp)
    } finally {
      runCatching { inp.close() }
    }
  }

  override fun prepareWritePart(relativePath: String, totalFileSize: Long): OutputStream? = null

  private fun stagingFileFor(relativePath: String): File {
    val key = MessageDigest.getInstance("SHA-256").digest(relativePath.toByteArray()).joinToString("") { "%02x".format(it) }.take(24)
    return File(cacheDir, "$key.sbpart")
  }

  override fun writePartChunk(relativePath: String, totalFileSize: Long, offset: Long, length: Long, inp: InputStream) {
    val part = stagingFileFor(relativePath)
    RandomAccessFile(part, "rw").use { raf ->
      if (raf.length() < totalFileSize) raf.setLength(totalFileSize)
      raf.seek(offset)
      val buf = ByteArray(ProtocolConstants.DATA_FRAME_CHUNK_BYTES)
      var remaining = length
      while (remaining > 0) {
        val header = FrameProtocol.readFrameHeader(inp) ?: error("Connection closed during upload.")
        when (header.first) {
          FrameType.DataEnd -> return
          FrameType.DataChunk -> {
            var frameRem = header.second
            while (frameRem > 0) {
              val slice = minOf(frameRem, buf.size)
              FrameProtocol.readExact(inp, buf, 0, slice)
              raf.write(buf, 0, slice)
              frameRem -= slice
              remaining -= slice
            }
          }
          else -> error("Expected DataChunk frame, got ${header.first}")
        }
      }
      val end = FrameProtocol.readFrameHeader(inp)
      if (end != null && end.first != FrameType.DataEnd) error("Expected DataEnd frame.")
    }
  }

  override fun finalizePart(relativePath: String, totalFileSize: Long, lastModIso: String, expectedSha256Hex: String?): PushFinalizeAckMessage {
    val clean = relativePath.trim().replace('\\', '/').trim('/')
    val parentRel = clean.substringBeforeLast('/', "")
    val fileName = clean.substringAfterLast('/')
    if (parentRel.isNotEmpty()) {
      val dirRes = createDirectory(parentRel)
      if (!dirRes.success) return PushFinalizeAckMessage(false, dirRes.error)
    }
    val parentNode = findNode(parentRel) ?: return PushFinalizeAckMessage(false, "Target folder missing.")
    val part = stagingFileFor(relativePath)
    if (totalFileSize > 0L && !part.exists()) return PushFinalizeAckMessage(false, "Partial file missing.")

    val existingId = findChild(parentNode.first, fileName)
    if (existingId != null) {
      runCatching { DocumentsContract.deleteDocument(resolver, DocumentsContract.buildDocumentUriUsingTree(treeUri, existingId)) }
    }
    val parentUri = DocumentsContract.buildDocumentUriUsingTree(treeUri, parentNode.first)
    val createdUri = DocumentsContract.createDocument(resolver, parentUri, "application/octet-stream", fileName)
      ?: return PushFinalizeAckMessage(false, "Could not create file in selected phone folder.")

    resolver.openOutputStream(createdUri)?.use { out ->
      if (totalFileSize > 0L) {
        part.inputStream().use { inp -> inp.copyTo(out, 64 * 1024) }
      }
    } ?: return PushFinalizeAckMessage(false, "Failed writing to SAF file.")
    part.delete()
    return PushFinalizeAckMessage(true)
  }

  private fun findChild(parentDocId: String, name: String): String? {
    val childrenUri = DocumentsContract.buildChildDocumentsUriUsingTree(treeUri, parentDocId)
    resolver.query(
      childrenUri,
      arrayOf(DocumentsContract.Document.COLUMN_DOCUMENT_ID, DocumentsContract.Document.COLUMN_DISPLAY_NAME),
      null,
      null,
      null,
    )?.use { c ->
      while (c.moveToNext()) {
        if (c.getString(1) == name) return c.getString(0)
      }
    }
    return null
  }
}

class PhoneFileShareServer(
  private val storageProvider: () -> PhoneStorageProvider,
  private val deviceName: String = Build.MODEL?.takeIf { it.isNotBlank() } ?: "Android Phone",
  private val requestedPort: Int = 0,
  private val pin: String = PinAuth.formatPin("000000").replace("-", ""),
  private val requirePin: Boolean = true,
  private val onTextReceived: (text: String, senderName: String) -> Unit = { _, _ -> },
) {
  private val serverId = "and-" + UUID.randomUUID().toString().replace("-", "").take(10)
  private val validTokens = ConcurrentHashMap<String, Long>()
  private val bcProvider = BouncyCastleProvider()
  private val tlsMaterial = generateEphemeralTlsContext(deviceName)

  private var scope: CoroutineScope? = null
  private var serverSocket: ServerSocket? = null
  private var acceptJob: Job? = null

  @Volatile
  var listeningPort: Int = 0
    private set

  val isRunning: Boolean
    get() = serverSocket?.let { !it.isClosed } == true

  fun registerMutualToken(): String {
    val token = UUID.randomUUID().toString().replace("-", "") + UUID.randomUUID().toString().replace("-", "").take(16)
    validTokens[token] = System.currentTimeMillis()
    return token
  }

  fun start() {
    if (isRunning) return
    val sock = ServerSocket(requestedPort)
    sock.reuseAddress = true
    serverSocket = sock
    listeningPort = sock.localPort

    val s = CoroutineScope(SupervisorJob() + Dispatchers.IO)
    scope = s
    acceptJob = s.launch {
      while (isActive && !sock.isClosed) {
        val client = runCatching { sock.accept() }.getOrNull() ?: break
        launch { handleClient(client) }
      }
    }
  }

  fun stop() {
    validTokens.clear()
    runCatching { serverSocket?.close() }
    serverSocket = null
    listeningPort = 0
    acceptJob?.cancel()
    acceptJob = null
    scope?.cancel()
    scope = null
  }

  private fun handleClient(rawSocket: Socket) {
    try {
      rawSocket.tcpNoDelay = true
      rawSocket.soTimeout = 60_000
      val rawIn = BufferedInputStream(rawSocket.getInputStream(), 64 * 1024)
      val rawOut = BufferedOutputStream(rawSocket.getOutputStream(), 64 * 1024)

      val magic = ByteArray(4)
      FrameProtocol.readExact(rawIn, magic)
      val useTls = when {
        magic.contentEquals(ProtocolConstants.MAGIC_TLS) -> true
        magic.contentEquals(ProtocolConstants.MAGIC_PLAIN) -> false
        else -> {
          rawOut.write(byteArrayOf(ProtocolConstants.PREAMBLE_INVALID))
          rawOut.flush()
          return
        }
      }
      rawOut.write(byteArrayOf(ProtocolConstants.PREAMBLE_OK))
      rawOut.flush()

      var activeIn: InputStream = rawIn
      var activeOut: OutputStream = rawOut
      if (useTls) {
        val ssl = tlsMaterial.sslContext.socketFactory.createSocket(
          rawSocket,
          null,
          rawSocket.port,
          true,
        ) as SSLSocket
        ssl.useClientMode = false
        ssl.enabledProtocols = ssl.supportedProtocols
          .filter { it == "TLSv1.3" || it == "TLSv1.2" }
          .toTypedArray()
          .ifEmpty { ssl.enabledProtocols }
        ssl.soTimeout = 60_000
        ssl.startHandshake()
        activeIn = BufferedInputStream(ssl.inputStream, 64 * 1024)
        activeOut = BufferedOutputStream(ssl.outputStream, 64 * 1024)
      }

      val serverNonce = PinAuth.generateNonce()
      FrameProtocol.writeJsonFrame(
        activeOut,
        FrameType.AuthChallenge,
        AuthChallengeMessage.serializer(),
        AuthChallengeMessage(
          serverId = serverId,
          deviceName = deviceName,
          osPlatform = "Android",
          serverNonceB64 = Base64.getEncoder().encodeToString(serverNonce),
          requiresPin = requirePin,
          tlsActive = useTls,
          certFingerprint = if (useTls) tlsMaterial.fingerprint else null,
        ),
      )

      val authResp = FrameProtocol.readJsonFrame(activeIn, FrameType.AuthResponse, AuthResponseMessage.serializer())
      val usedToken = !authResp.mutualAuthToken.isNullOrBlank() && validTokens.containsKey(authResp.mutualAuthToken)

      if (!usedToken && requirePin) {
        val clientNonce = runCatching { Base64.getDecoder().decode(authResp.clientNonceB64) }.getOrDefault(ByteArray(0))
        val proof = runCatching { Base64.getDecoder().decode(authResp.proofB64) }.getOrDefault(ByteArray(0))
        val ok = PinAuth.verifyProof(pin, serverNonce, clientNonce, proof, if (useTls) tlsMaterial.certHash else null)
        if (!ok) {
          FrameProtocol.writeJsonFrame(
            activeOut,
            FrameType.AuthResult,
            AuthResultMessage.serializer(),
            AuthResultMessage(false, "Invalid pairing credentials."),
          )
          return
        }
      }

      val storage = storageProvider()
      FrameProtocol.writeJsonFrame(
        activeOut,
        FrameType.AuthResult,
        AuthResultMessage.serializer(),
        AuthResultMessage(
          success = true,
          sharedRootName = storage.rootName,
          fullFilesystemAccess = false,
          readOnly = false,
        ),
      )

      while (true) {
        val frame = FrameProtocol.readFrame(activeIn) ?: break
        val store = storageProvider()
        when (frame.first) {
          FrameType.Ping -> FrameProtocol.writeFrame(activeOut, FrameType.Pong, ByteArray(0), flush = true)

          FrameType.ListDirRequest -> {
            val req = ProtocolJson.decodeFromString(ListDirRequestMessage.serializer(), frame.second.toString(Charsets.UTF_8))
            val resp = store.listDirectory(req.relativePath, req.recursive, req.includeHidden)
            FrameProtocol.writeJsonFrame(activeOut, FrameType.ListDirResponse, ListDirResponseMessage.serializer(), resp)
          }

          FrameType.FileInfoRequest -> {
            val req = ProtocolJson.decodeFromString(FileInfoRequestMessage.serializer(), frame.second.toString(Charsets.UTF_8))
            val resp = store.fileInfo(req.relativePath, req.includeSha256)
            FrameProtocol.writeJsonFrame(activeOut, FrameType.FileInfoResponse, FileInfoResponseMessage.serializer(), resp)
          }

          FrameType.CreateDirRequest -> {
            val req = ProtocolJson.decodeFromString(CreateDirRequestMessage.serializer(), frame.second.toString(Charsets.UTF_8))
            val resp = store.createDirectory(req.relativePath)
            FrameProtocol.writeJsonFrame(activeOut, FrameType.CreateDirResponse, CreateDirResponseMessage.serializer(), resp)
          }

          FrameType.DeletePathRequest -> {
            val req = ProtocolJson.decodeFromString(DeletePathRequestMessage.serializer(), frame.second.toString(Charsets.UTF_8))
            val resp = store.deletePath(req.relativePath)
            FrameProtocol.writeJsonFrame(activeOut, FrameType.DeletePathResponse, DeletePathResponseMessage.serializer(), resp)
          }

          FrameType.RenamePathRequest -> {
            val req = ProtocolJson.decodeFromString(RenamePathRequestMessage.serializer(), frame.second.toString(Charsets.UTF_8))
            val resp = store.renamePath(req.relativePath, req.newName)
            FrameProtocol.writeJsonFrame(activeOut, FrameType.RenamePathResponse, RenamePathResponseMessage.serializer(), resp)
          }

          FrameType.PullRangeRequest -> {
            val req = ProtocolJson.decodeFromString(PullRangeRequestMessage.serializer(), frame.second.toString(Charsets.UTF_8))
            store.readRange(req.relativePath, req.offset, req.length) { fileSize, lastMod, dataStart, dataLen, dataStream ->
              if (dataStream == null || dataLen < 0) {
                FrameProtocol.writeJsonFrame(
                  activeOut,
                  FrameType.PullRangeHeader,
                  PullRangeHeaderMessage.serializer(),
                  PullRangeHeaderMessage(false, 0L, "", 0L, 0L, "File not found."),
                )
              } else {
                FrameProtocol.writeJsonFrame(
                  activeOut,
                  FrameType.PullRangeHeader,
                  PullRangeHeaderMessage.serializer(),
                  PullRangeHeaderMessage(true, fileSize, lastMod, dataStart, dataLen),
                )
                val buf = ByteArray(ProtocolConstants.DATA_FRAME_CHUNK_BYTES)
                var rem = dataLen
                while (rem > 0) {
                  val r = dataStream.read(buf, 0, minOf(buf.size.toLong(), rem).toInt())
                  if (r <= 0) break
                  FrameProtocol.writeFrame(activeOut, FrameType.DataChunk, buf, payloadLength = r, flush = false)
                  rem -= r
                }
                FrameProtocol.writeFrame(activeOut, FrameType.DataEnd, ByteArray(0), flush = true)
              }
            }
          }

          FrameType.PushChunkRequest -> {
            val req = ProtocolJson.decodeFromString(PushChunkRequestMessage.serializer(), frame.second.toString(Charsets.UTF_8))
            val ack = runCatching {
              store.writePartChunk(req.relativePath, req.totalFileSize, req.offset, req.length, activeIn)
              PushChunkAckMessage(true)
            }.getOrElse { err ->
              PushChunkAckMessage(false, err.message ?: "Write failed.")
            }
            FrameProtocol.writeJsonFrame(activeOut, FrameType.PushChunkAck, PushChunkAckMessage.serializer(), ack)
          }

          FrameType.PushFinalizeRequest -> {
            val req = ProtocolJson.decodeFromString(PushFinalizeRequestMessage.serializer(), frame.second.toString(Charsets.UTF_8))
            val ack = store.finalizePart(req.relativePath, req.totalFileSize, req.lastModifiedUtc, req.expectedSha256Hex)
            FrameProtocol.writeJsonFrame(activeOut, FrameType.PushFinalizeAck, PushFinalizeAckMessage.serializer(), ack)
          }

          FrameType.SendTextRequest -> {
            val req = ProtocolJson.decodeFromString(SendTextRequestMessage.serializer(), frame.second.toString(Charsets.UTF_8))
            onTextReceived(req.text, req.senderName)
            FrameProtocol.writeJsonFrame(activeOut, FrameType.SendTextAck, SendTextAckMessage.serializer(), SendTextAckMessage(true))
          }

          else -> break
        }
      }
    } catch (_: Exception) {
      // Client closed or timed out cleanly
    } finally {
      runCatching { rawSocket.close() }
    }
  }

  private data class TlsMaterial(
    val sslContext: SSLContext,
    val certHash: ByteArray,
    val fingerprint: String,
  )

  private fun generateEphemeralTlsContext(cnDevice: String): TlsMaterial {
    val kpg = KeyPairGenerator.getInstance("EC", bcProvider)
    kpg.initialize(ECGenParameterSpec("secp256r1"), SecureRandom())
    val kp = kpg.generateKeyPair()

    val now = System.currentTimeMillis()
    val start = Date(now - 5 * 60_000L)
    val end = Date(now + 730L * 24 * 60 * 60_000L)
    val serial = BigInteger(64, SecureRandom()).abs().max(BigInteger.ONE)
    val safeName = cnDevice.replace("\"", "").replace(",", "").ifBlank { "SwiftBeamAndroid" }
    val issuer = X500Name("CN=SwiftBeam-$safeName")

    val builder = JcaX509v3CertificateBuilder(issuer, serial, start, end, issuer, kp.public)
    val signer = JcaContentSignerBuilder("SHA256withECDSA").setProvider(bcProvider).build(kp.private)
    val cert: X509Certificate = JcaX509CertificateConverter().setProvider(bcProvider).getCertificate(builder.build(signer))

    val ks = KeyStore.getInstance("PKCS12")
    ks.load(null, null)
    ks.setKeyEntry("swiftbeam", kp.private, CharArray(0), arrayOf(cert))

    val kmf = KeyManagerFactory.getInstance(KeyManagerFactory.getDefaultAlgorithm())
    kmf.init(ks, CharArray(0))

    val ctx = SSLContext.getInstance("TLS")
    ctx.init(kmf.keyManagers, null, SecureRandom())

    val certHash = MessageDigest.getInstance("SHA-256").digest(cert.encoded)
    val hex = certHash.joinToString("") { "%02X".format(it) }
    val fp = if (hex.length >= 16) {
      "${hex.substring(0, 4)}:${hex.substring(4, 8)}:${hex.substring(8, 12)}:${hex.substring(12, 16)}"
    } else hex
    return TlsMaterial(ctx, certHash, fp)
  }
}
