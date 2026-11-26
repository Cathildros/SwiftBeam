package com.cathildros.swiftbeam.protocol

import java.net.URI
import java.util.Locale

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

    if (trimmed.startsWith("http://", ignoreCase = true) || trimmed.startsWith("https://", ignoreCase = true)) {
      return runCatching {
        val uri = URI(trimmed)
        val host = uri.host ?: trimmed
        val port = when {
          uri.port in 1..65535 && uri.port != 45893 && uri.port != 45892 -> uri.port
          uri.port == 45893 || uri.port == 45892 -> ProtocolConstants.DEFAULT_TRANSFER_PORT
          else -> null
        }
        host to port
      }.getOrElse { trimmed to null }
    }

    // IPv6 bracketed [fe80::1]:45890
    if (trimmed.startsWith("[")) {
      val endBracket = trimmed.indexOf(']')
      if (endBracket > 1) {
        val host = trimmed.substring(1, endBracket)
        val after = trimmed.substring(endBracket + 1)
        val port = if (after.startsWith(":")) after.substring(1).toIntOrNull() else null
        return host to port
      }
    }

    // host:port (only when there's a single colon, so unbracketed IPv6 stays intact)
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
    // Convert "2026-11-26T08:15:30.123Z" -> "2026-11-26 08:15"
    val clean = isoUtc.trim()
    val tIdx = clean.indexOf('T')
    return if (tIdx > 0 && clean.length >= tIdx + 6) {
      "${clean.substring(0, tIdx)} ${clean.substring(tIdx + 1, tIdx + 6)}"
    } else {
      clean.take(16)
    }
  }
}
