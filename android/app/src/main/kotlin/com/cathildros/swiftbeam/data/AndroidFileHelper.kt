package com.cathildros.swiftbeam.data

import android.content.ContentValues
import android.content.Context
import android.net.Uri
import android.os.Build
import android.os.Environment
import android.provider.MediaStore
import android.provider.OpenableColumns
import android.webkit.MimeTypeMap
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

    // Some providers omit SIZE; spool once to cache so exact length & SHA-256 work reliably.
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
  ): String {
    val safeName = sanitizeFileName(fileName)
    val cleanSub = relativeSubfolder
      .replace('\\', '/')
      .split('/')
      .filter { it.isNotBlank() && it != "." && it != ".." }
      .joinToString("/")

    if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.Q) {
      val relPath = buildString {
        append(Environment.DIRECTORY_DOWNLOADS)
        append("/SwiftBeam")
        if (cleanSub.isNotEmpty()) {
          append('/')
          append(cleanSub)
        }
      }
      val ext = safeName.substringAfterLast('.', "").lowercase()
      val mimeType = MimeTypeMap.getSingleton().getMimeTypeFromExtension(ext) ?: "application/octet-stream"

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
        return "$relPath/$safeName"
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
        return dest.absolutePath
      } catch (t: Throwable) {
        dest.delete()
        throw t
      }
    }
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
