package com.cathildros.swiftbeam.data

import com.cathildros.swiftbeam.protocol.ProtocolJson
import java.net.HttpURLConnection
import java.net.URL
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.withContext
import kotlinx.serialization.SerialName
import kotlinx.serialization.Serializable

data class UpdateInfo(
  val version: String,
  val apkDownloadUrl: String,
  val releasePageUrl: String,
)

@Serializable
private data class GitHubReleaseAsset(
  val name: String,
  @SerialName("browser_download_url") val browserDownloadUrl: String,
)

@Serializable
private data class GitHubReleaseDto(
  @SerialName("tag_name") val tagName: String,
  @SerialName("html_url") val htmlUrl: String = "",
  val draft: Boolean = false,
  val prerelease: Boolean = false,
  val assets: List<GitHubReleaseAsset> = emptyList(),
)

object UpdateChecker {
  fun isNewerVersion(latestTag: String, currentVersion: String): Boolean {
    val latest = parseParts(latestTag) ?: return false
    val current = parseParts(currentVersion) ?: return false
    val maxLen = maxOf(latest.size, current.size)
    for (i in 0 until maxLen) {
      val l = latest.getOrElse(i) { 0 }
      val c = current.getOrElse(i) { 0 }
      if (l != c) return l > c
    }
    return false
  }

  private fun parseParts(raw: String): List<Int>? {
    val clean = raw.trim().trimStart('v', 'V').substringBefore('-').substringBefore('+')
    if (clean.isEmpty()) return null
    val parts = clean.split('.')
    if (parts.isEmpty()) return null
    val ints = ArrayList<Int>(parts.size)
    for (p in parts) {
      val num = p.toIntOrNull() ?: return null
      ints.add(num)
    }
    return ints
  }

  suspend fun checkLatestRelease(currentVersion: String): UpdateInfo? = withContext(Dispatchers.IO) {
    runCatching {
      val url = URL("https://api.github.com/repos/Cathildros/SwiftBeam/releases/latest")
      val conn = (url.openConnection() as HttpURLConnection).apply {
        requestMethod = "GET"
        connectTimeout = 5000
        readTimeout = 5000
        setRequestProperty("Accept", "application/vnd.github+json")
        setRequestProperty("User-Agent", "SwiftBeam-Android/$currentVersion")
      }
      try {
        if (conn.responseCode !in 200..299) return@runCatching null
        val body = conn.inputStream.bufferedReader().use { it.readText() }
        val dto = ProtocolJson.decodeFromString(GitHubReleaseDto.serializer(), body)
        if (dto.draft || dto.prerelease) return@runCatching null
        if (!isNewerVersion(dto.tagName, currentVersion)) return@runCatching null
        val apkAsset = dto.assets.firstOrNull { it.name.endsWith(".apk", ignoreCase = true) }
          ?: return@runCatching null
        UpdateInfo(
          version = dto.tagName.trimStart('v', 'V'),
          apkDownloadUrl = apkAsset.browserDownloadUrl,
          releasePageUrl = dto.htmlUrl.ifBlank { "https://github.com/Cathildros/SwiftBeam/releases/latest" },
        )
      } finally {
        conn.disconnect()
      }
    }.getOrNull()
  }
}
