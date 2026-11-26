package com.cathildros.swiftbeam.sync

import com.cathildros.swiftbeam.data.TreeFileEntry
import com.cathildros.swiftbeam.protocol.PathUtils
import com.cathildros.swiftbeam.protocol.RemoteEntry

data class SyncUploadItem(
  val localFile: TreeFileEntry,
  val remoteRelativePath: String,
)

data class SyncDownloadItem(
  val remoteEntry: RemoteEntry,
  val downloadsSubfolder: String,
)

data class PhoneSyncPlan(
  val toUpload: List<SyncUploadItem>,
  val toDownload: List<SyncDownloadItem>,
  val alreadyInSyncCount: Int,
)

object PhoneAutoSyncEngine {
  fun buildPlan(
    localFiles: List<TreeFileEntry>,
    remoteEntries: List<RemoteEntry>,
    remoteRootPath: String,
    localLabel: String,
    twoWay: Boolean,
  ): PhoneSyncPlan {
    val cleanRoot = remoteRootPath.trim().replace('\\', '/').trimEnd('/')
    val remoteFilesByRel = HashMap<String, RemoteEntry>()
    for (entry in remoteEntries) {
      if (entry.isDirectory) continue
      val entryNorm = entry.relativePath.replace('\\', '/')
      val rel = if (cleanRoot.isEmpty()) {
        entryNorm.trimStart('/')
      } else {
        entryNorm.removePrefix(cleanRoot).trimStart('/')
      }
      if (rel.isNotEmpty()) {
        remoteFilesByRel[rel] = entry
      }
    }

    val localByRel = localFiles.associateBy { it.relativePath.replace('\\', '/').trimStart('/') }

    val uploads = ArrayList<SyncUploadItem>()
    val downloads = ArrayList<SyncDownloadItem>()
    var inSync = 0

    for ((rel, local) in localByRel) {
      val existing = remoteFilesByRel[rel]
      if (existing == null || (local.sizeBytes >= 0 && existing.size != local.sizeBytes)) {
        val targetRemote = if (cleanRoot.isEmpty()) rel else PathUtils.joinRemotePath(cleanRoot, rel, fullFilesystem = true)
        uploads.add(SyncUploadItem(localFile = local, remoteRelativePath = targetRemote))
      } else {
        inSync++
      }
    }

    if (twoWay) {
      for ((rel, remote) in remoteFilesByRel) {
        if (!localByRel.containsKey(rel)) {
          val subParent = rel.substringBeforeLast('/', "")
          val subfolder = if (subParent.isEmpty()) "Sync/$localLabel" else "Sync/$localLabel/$subParent"
          downloads.add(SyncDownloadItem(remoteEntry = remote, downloadsSubfolder = subfolder))
        }
      }
    }

    return PhoneSyncPlan(
      toUpload = uploads,
      toDownload = downloads,
      alreadyInSyncCount = inSync,
    )
  }
}
