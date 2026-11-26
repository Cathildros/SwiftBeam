package com.cathildros.swiftbeam.protocol

import java.net.URI
import java.net.URLDecoder
import java.util.Locale

data class ParsedQrTarget(
  val host: String,
  val port: Int,
  val pin: String?,
)

object PathUtils {
  fun joinRemotePath(
    currentDir: String,
    childName: String,
    fullFilesystem: Boolean,
  ): String {
    val cleanChild = childName.trim().trimStart('/', '\\')
    if (cleanChild.isEmpty()) return currentDir

    if (!fullFilesystem) {
      val trimmed = currentDir.trim().replace('\\', '/').trim('/')
      return if (trimmed.isEmpty()) cleanChild else "$trimmed/$cleanChild"
    }

    val cur = currentDir.trim().replace('\\', '/')
    return when {
      cur.isEmpty() || cur == ProtocolConstants.DRIVES_VIRTUAL_PATH -> cleanChild
      cur == "/" -> "/$cleanChild"
      cur.endsWith(":/") -> "$cur$cleanChild"
      else -> "${cur.trimEnd('/')}/$cleanChild"
    }
  }

  fun parentRemotePath(
    currentDir: String,
    fullFilesystem: Boolean,
  ): String? {
    val cur = currentDir.trim().replace('\\', '/')
    if (!fullFilesystem) {
      val trimmed = cur.trim('/')
      if (trimmed.isEmpty()) return null
      val idx = trimmed.lastIndexOf('/')
      return if (idx <= 0) "" else trimmed.substring(0, idx)
    }

    if (cur.isEmpty() || cur == ProtocolConstants.DRIVES_VIRTUAL_PATH) return null
    if (cur == "/") return ProtocolConstants.DRIVES_VIRTUAL_PATH
    if (cur.length == 3 && cur[1] == ':' && cur[2] == '/') return ProtocolConstants.DRIVES_VIRTUAL_PATH

    val noTrailing = cur.trimEnd('/')
    val idx = noTrailing.lastIndexOf('/')
    return when {
      idx < 0 -> ProtocolConstants.DRIVES_VIRTUAL_PATH
      idx == 0 -> "/"
      idx == 2 && noTrailing.length > 3 && noTrailing[1] == ':' -> noTrailing.substring(0, 3)
      else -> noTrailing.substring(0, idx)
    }
  }

  fun parseHostInput(raw: String): Pair<String, Int?> {
    val trimmed = raw.trim()
    if (trimmed.isEmpty()) return "" to null

    if (trimmed.startsWith("http://", ignoreCase = true) ||
      trimmed.startsWith("https://", ignoreCase = true) ||
      trimmed.startsWith("swiftbeam://", ignoreCase = true)
    ) {
      return runCatching {
        val uri = URI(trimmed)
        val host = uri.host ?: trimmed
        val params = parseQueryParams(uri.rawQuery)
        val sbPort = params["sbport"]?.toIntOrNull()?.takeIf { it in 1..65535 }
        val port = when {
          sbPort != null -> sbPort
          uri.port in 1..65535 && uri.port != 45893 && uri.port != 45892 -> uri.port
          uri.port == 45893 || uri.port == 45892 -> ProtocolConstants.DEFAULT_TRANSFER_PORT
          else -> null
        }
        host to port
      }.getOrElse { trimmed to null }
    }

    if (trimmed.startsWith("[")) {
      val endBracket = trimmed.indexOf(']')
      if (endBracket > 1) {
        val host = trimmed.substring(1, endBracket)
        val after = trimmed.substring(endBracket + 1)
        val port = if (after.startsWith(":")) after.substring(1).toIntOrNull() else null
        return host to port
      }
    }

    val firstColon = trimmed.indexOf(':')
    val lastColon = trimmed.lastIndexOf(':')
    if (firstColon > 0 && firstColon == lastColon) {
      val host = trimmed.substring(0, firstColon).trim()
      val port = trimmed.substring(firstColon + 1).trim().toIntOrNull()
      if (port != null && port in 1..65535) {
        return host to port
      }
    }

    return trimmed to null
  }

  fun parseQrPayload(raw: String): ParsedQrTarget? {
    val trimmed = raw.trim()
    if (trimmed.isEmpty()) return null

    if (trimmed.startsWith("http://", ignoreCase = true) ||
      trimmed.startsWith("https://", ignoreCase = true) ||
      trimmed.startsWith("swiftbeam://", ignoreCase = true)
    ) {
      return runCatching {
        val uri = URI(trimmed)
        val host = uri.host?.takeIf { it.isNotBlank() } ?: return@runCatching null
        val params = parseQueryParams(uri.rawQuery)
        val sbPort = params["sbport"]?.toIntOrNull()?.takeIf { it in 1..65535 }
        val port = sbPort ?: when {
          uri.port in 1..65535 && uri.port != 45893 && uri.port != 45892 -> uri.port
          else -> ProtocolConstants.DEFAULT_TRANSFER_PORT
        }
        val pin = params["pin"]?.let { PinAuth.normalizePin(it) }?.takeIf { it.isNotEmpty() }
        ParsedQrTarget(host = host, port = port, pin = pin)
      }.getOrNull()
    }

    val (host, port) = parseHostInput(trimmed)
    if (host.isBlank()) return null
    return ParsedQrTarget(host = host, port = port ?: ProtocolConstants.DEFAULT_TRANSFER_PORT, pin = null)
  }

  private fun parseQueryParams(rawQuery: String?): Map<String, String> {
    if (rawQuery.isNullOrBlank()) return emptyMap()
    val result = LinkedHashMap<String, String>()
    for (part in rawQuery.split('&')) {
      val eq = part.indexOf('=')
      if (eq > 0) {
        val key = URLDecoder.decode(part.substring(0, eq), "UTF-8").lowercase(Locale.US)
        val valStr = URLDecoder.decode(part.substring(eq + 1), "UTF-8")
        result[key] = valStr
      }
    }
    return result
  }

  fun isPreviewableImage(name: String): Boolean {
    val ext = name.substringAfterLast('.', "").lowercase(Locale.US)
    return ext in setOf("jpg", "jpeg", "png", "webp", "gif", "bmp")
  }

  fun isPreviewableText(name: String): Boolean {
    val ext = name.substringAfterLast('.', "").lowercase(Locale.US)
    return ext in setOf(
      "txt", "log", "md", "json", "xml", "yaml", "yml", "ini", "cfg", "conf",
      "csv", "cs", "kt", "kts", "java", "py", "js", "ts", "html", "css", "sh", "ps1", "bat",
    )
  }

  fun formatBytes(bytes: Long): String {
    if (bytes < 0) return "0 B"
    if (bytes < 1024L) return "$bytes B"
    val units = arrayOf("KB", "MB", "GB", "TB")
    var value = bytes.toDouble()
    var unitIdx = -1
    while (value >= 1024.0 && unitIdx < units.lastIndex) {
      value /= 1024.0
      unitIdx++
    }
    return if (value >= 100.0) {
      String.format(Locale.US, "%.0f %s", value, units[unitIdx])
    } else if (value >= 10.0) {
      String.format(Locale.US, "%.1f %s", value, units[unitIdx])
    } else {
      String.format(Locale.US, "%.2f %s", value, units[unitIdx])
    }
  }

  fun formatSpeed(bytesPerSec: Double): String {
    if (bytesPerSec <= 0.5) return "0 B/s"
    return "${formatBytes(bytesPerSec.toLong())}/s"
  }

  fun formatRemoteDate(isoUtc: String): String {
    if (isoUtc.isBlank()) return ""
    val clean = isoUtc.trim()
    val tIdx = clean.indexOf('T')
    return if (tIdx > 0 && clean.length >= tIdx + 6) {
      "${clean.substring(0, tIdx)} ${clean.substring(tIdx + 1, tIdx + 6)}"
    } else {
      clean.take(16)
    }
  }
}
