package com.cathildros.swiftbeam.data

import android.content.ContentValues
import android.content.Context
import android.content.Intent
import android.net.Uri
import android.os.Build
import android.os.Environment
import android.provider.DocumentsContract
import android.provider.MediaStore
import android.provider.OpenableColumns
import android.webkit.MimeTypeMap
import androidx.core.content.FileProvider
import java.io.File
import java.io.FileInputStream
import java.io.FileOutputStream
import java.io.InputStream
import java.io.OutputStream
import java.time.Instant

data class LocalUploadSource(
  val uri: Uri,
  val displayName: String,
  val sizeBytes: Long,
  val lastModifiedUtcIso: String,
)

data class TreeFileEntry(
  val uri: Uri,
  val relativePath: String,
  val displayName: String,
  val sizeBytes: Long,
  val lastModifiedMs: Long,
)

data class SavedDownloadResult(
  val displayPath: String,
  val contentUri: Uri?,
  val mimeType: String,
)

class AndroidFileHelper(private val context: Context) {
  private val resolver = context.applicationContext.contentResolver

  fun inspectUri(uri: Uri): LocalUploadSource {
    var name = uri.lastPathSegment?.substringAfterLast('/')?.ifBlank { null } ?: "file_${System.currentTimeMillis()}"
    var size = -1L

    runCatching {
      resolver.query(uri, arrayOf(OpenableColumns.DISPLAY_NAME, OpenableColumns.SIZE), null, null, null)?.use { cursor ->
        if (cursor.moveToFirst()) {
          val nameIdx = cursor.getColumnIndex(OpenableColumns.DISPLAY_NAME)
          val sizeIdx = cursor.getColumnIndex(OpenableColumns.SIZE)
          if (nameIdx >= 0) {
            cursor.getString(nameIdx)?.takeIf { it.isNotBlank() }?.let { name = sanitizeFileName(it) }
          }
          if (sizeIdx >= 0 && !cursor.isNull(sizeIdx)) {
            size = cursor.getLong(sizeIdx)
          }
        }
      }
    }

    return LocalUploadSource(
      uri = uri,
      displayName = sanitizeFileName(name),
      sizeBytes = size,
      lastModifiedUtcIso = Instant.now().toString(),
    )
  }

  fun resolveTreeLabel(treeUri: Uri): String {
    return runCatching {
      val docId = DocumentsContract.getTreeDocumentId(treeUri)
      val docUri = DocumentsContract.buildDocumentUriUsingTree(treeUri, docId)
      resolver.query(docUri, arrayOf(DocumentsContract.Document.COLUMN_DISPLAY_NAME), null, null, null)?.use { c ->
        if (c.moveToFirst()) c.getString(0)?.takeIf { it.isNotBlank() } else null
      } ?: docId.substringAfterLast(':').ifBlank { "Selected Folder" }
    }.getOrDefault("Selected Folder")
  }

  fun walkTree(treeUri: Uri): List<TreeFileEntry> {
    val rootDocId = runCatching { DocumentsContract.getTreeDocumentId(treeUri) }.getOrNull() ?: return emptyList()
    val results = ArrayList<TreeFileEntry>()
    val stack = ArrayDeque<Pair<String, String>>()
    stack.add(rootDocId to "")

    val projection = arrayOf(
      DocumentsContract.Document.COLUMN_DOCUMENT_ID,
      DocumentsContract.Document.COLUMN_DISPLAY_NAME,
      DocumentsContract.Document.COLUMN_MIME_TYPE,
      DocumentsContract.Document.COLUMN_SIZE,
      DocumentsContract.Document.COLUMN_LAST_MODIFIED,
    )

    while (stack.isNotEmpty()) {
      val (dirDocId, prefix) = stack.removeFirst()
      val childrenUri = DocumentsContract.buildChildDocumentsUriUsingTree(treeUri, dirDocId)
      runCatching {
        resolver.query(childrenUri, projection, null, null, null)?.use { cursor ->
          val idCol = cursor.getColumnIndex(DocumentsContract.Document.COLUMN_DOCUMENT_ID)
          val nameCol = cursor.getColumnIndex(DocumentsContract.Document.COLUMN_DISPLAY_NAME)
          val mimeCol = cursor.getColumnIndex(DocumentsContract.Document.COLUMN_MIME_TYPE)
          val sizeCol = cursor.getColumnIndex(DocumentsContract.Document.COLUMN_SIZE)
          val modCol = cursor.getColumnIndex(DocumentsContract.Document.COLUMN_LAST_MODIFIED)

          while (cursor.moveToNext()) {
            val childId = if (idCol >= 0) cursor.getString(idCol) else continue
            val rawName = if (nameCol >= 0) cursor.getString(nameCol) else continue
            val safeName = sanitizeFileName(rawName)
            val mime = if (mimeCol >= 0) cursor.getString(mimeCol) else null
            val rel = if (prefix.isEmpty()) safeName else "$prefix/$safeName"

            if (mime == DocumentsContract.Document.MIME_TYPE_DIR) {
              stack.add(childId to rel)
            } else {
              val size = if (sizeCol >= 0 && !cursor.isNull(sizeCol)) cursor.getLong(sizeCol) else -1L
              val mod = if (modCol >= 0 && !cursor.isNull(modCol)) cursor.getLong(modCol) else System.currentTimeMillis()
              val fileUri = DocumentsContract.buildDocumentUriUsingTree(treeUri, childId)
              results.add(
                TreeFileEntry(
                  uri = fileUri,
                  relativePath = rel,
                  displayName = safeName,
                  sizeBytes = size,
                  lastModifiedMs = mod,
                ),
              )
            }
          }
        }
      }
    }
    return results
  }

  fun openInputStream(uri: Uri): InputStream? = resolver.openInputStream(uri)

  suspend fun <T> withUploadStream(
    source: LocalUploadSource,
    block: suspend (stream: InputStream, exactSize: Long) -> T,
  ): T {
    if (source.sizeBytes >= 0L) {
      val stream = resolver.openInputStream(source.uri)
        ?: throw IllegalStateException("Cannot read selected file: ${source.displayName}")
      try {
        return block(stream, source.sizeBytes)
      } finally {
        runCatching { stream.close() }
      }
    }

    val tempFile = File.createTempFile("sb_up_", ".tmp", context.cacheDir)
    try {
      resolver.openInputStream(source.uri)?.use { input ->
        FileOutputStream(tempFile).use { output ->
          input.copyTo(output, bufferSize = 64 * 1024)
        }
      } ?: throw IllegalStateException("Cannot read selected file: ${source.displayName}")

      val exactStream = FileInputStream(tempFile)
      try {
        return block(exactStream, tempFile.length())
      } finally {
        runCatching { exactStream.close() }
      }
    } finally {
      tempFile.delete()
    }
  }

  suspend fun saveToDownloads(
    fileName: String,
    relativeSubfolder: String = "",
    writer: suspend (OutputStream) -> Unit,
  ): SavedDownloadResult {
    val safeName = sanitizeFileName(fileName)
    val cleanSub = relativeSubfolder
      .replace('\\', '/')
      .split('/')
      .filter { it.isNotBlank() && it != "." && it != ".." }
      .joinToString("/")

    val ext = safeName.substringAfterLast('.', "").lowercase()
    val mimeType = MimeTypeMap.getSingleton().getMimeTypeFromExtension(ext) ?: "application/octet-stream"

    if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.Q) {
      val relPath = buildString {
        append(Environment.DIRECTORY_DOWNLOADS)
        append("/SwiftBeam")
        if (cleanSub.isNotEmpty()) {
          append('/')
          append(cleanSub)
        }
      }

      val values = ContentValues().apply {
        put(MediaStore.Downloads.DISPLAY_NAME, safeName)
        put(MediaStore.Downloads.MIME_TYPE, mimeType)
        put(MediaStore.Downloads.RELATIVE_PATH, relPath)
        put(MediaStore.Downloads.IS_PENDING, 1)
      }

      val itemUri = resolver.insert(MediaStore.Downloads.EXTERNAL_CONTENT_URI, values)
        ?: throw IllegalStateException("Failed to create file in Downloads/SwiftBeam.")

      try {
        val out = resolver.openOutputStream(itemUri)
          ?: throw IllegalStateException("Cannot open output stream for $safeName.")
        try {
          writer(out)
        } finally {
          runCatching { out.close() }
        }

        val publish = ContentValues().apply {
          put(MediaStore.Downloads.IS_PENDING, 0)
        }
        resolver.update(itemUri, publish, null, null)
        return SavedDownloadResult(
          displayPath = "$relPath/$safeName",
          contentUri = itemUri,
          mimeType = mimeType,
        )
      } catch (t: Throwable) {
        runCatching { resolver.delete(itemUri, null, null) }
        throw t
      }
    } else {
      @Suppress("DEPRECATION")
      val base = File(Environment.getExternalStoragePublicDirectory(Environment.DIRECTORY_DOWNLOADS), "SwiftBeam")
      val targetDir = if (cleanSub.isEmpty()) base else File(base, cleanSub)
      if (!targetDir.exists()) targetDir.mkdirs()

      val dest = uniqueFile(targetDir, safeName)
      try {
        val out = FileOutputStream(dest)
        try {
          writer(out)
        } finally {
          runCatching { out.close() }
        }
        val uri = runCatching {
          FileProvider.getUriForFile(context, "${context.packageName}.fileprovider", dest)
        }.getOrNull()
        return SavedDownloadResult(
          displayPath = dest.absolutePath,
          contentUri = uri,
          mimeType = mimeType,
        )
      } catch (t: Throwable) {
        dest.delete()
        throw t
      }
    }
  }

  fun openDownloadedFile(uriString: String, mimeType: String?): Boolean {
    return runCatching {
      val uri = Uri.parse(uriString)
      val intent = Intent(Intent.ACTION_VIEW).apply {
        setDataAndType(uri, mimeType ?: "*/*")
        addFlags(Intent.FLAG_GRANT_READ_URI_PERMISSION or Intent.FLAG_ACTIVITY_NEW_TASK)
      }
      context.startActivity(Intent.createChooser(intent, "Open file with").addFlags(Intent.FLAG_ACTIVITY_NEW_TASK))
      true
    }.getOrDefault(false)
  }

  private fun uniqueFile(dir: File, name: String): File {
    var candidate = File(dir, name)
    if (!candidate.exists()) return candidate
    val dot = name.lastIndexOf('.')
    val stem = if (dot > 0) name.substring(0, dot) else name
    val ext = if (dot > 0) name.substring(dot) else ""
    var idx = 1
    while (candidate.exists()) {
      candidate = File(dir, "${stem} ($idx)$ext")
      idx++
    }
    return candidate
  }

  private fun sanitizeFileName(raw: String): String {
    val cleaned = raw.trim().replace('/', '_').replace('\\', '_')
    return cleaned.ifBlank { "file_${System.currentTimeMillis()}" }
  }
}
