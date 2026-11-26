package com.cathildros.swiftbeam.ui.screens

import androidx.compose.foundation.BorderStroke
import androidx.compose.foundation.ExperimentalFoundationApi
import androidx.compose.foundation.combinedClickable
import androidx.compose.foundation.horizontalScroll
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.PaddingValues
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.lazy.items
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.shape.CircleShape
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.automirrored.rounded.ArrowBack
import androidx.compose.material.icons.automirrored.rounded.InsertDriveFile
import androidx.compose.material.icons.rounded.Archive
import androidx.compose.material.icons.rounded.AudioFile
import androidx.compose.material.icons.rounded.Close
import androidx.compose.material.icons.rounded.Computer
import androidx.compose.material.icons.rounded.CreateNewFolder
import androidx.compose.material.icons.rounded.Delete
import androidx.compose.material.icons.rounded.Download
import androidx.compose.material.icons.rounded.Folder
import androidx.compose.material.icons.rounded.Image
import androidx.compose.material.icons.rounded.MoreVert
import androidx.compose.material.icons.rounded.PhotoLibrary
import androidx.compose.material.icons.rounded.Refresh
import androidx.compose.material.icons.rounded.Search
import androidx.compose.material.icons.rounded.UploadFile
import androidx.compose.material.icons.rounded.VideoFile
import androidx.compose.material.icons.rounded.Visibility
import androidx.compose.material.icons.rounded.VisibilityOff
import androidx.compose.material3.AlertDialog
import androidx.compose.material3.Button
import androidx.compose.material3.ButtonDefaults
import androidx.compose.material3.Checkbox
import androidx.compose.material3.CheckboxDefaults
import androidx.compose.material3.CircularProgressIndicator
import androidx.compose.material3.DropdownMenu
import androidx.compose.material3.DropdownMenuItem
import androidx.compose.material3.FilterChip
import androidx.compose.material3.FilterChipDefaults
import androidx.compose.material3.Icon
import androidx.compose.material3.IconButton
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.OutlinedButton
import androidx.compose.material3.OutlinedTextField
import androidx.compose.material3.Surface
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.runtime.Composable
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.graphics.vector.ImageVector
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.dp
import com.cathildros.swiftbeam.protocol.PathUtils
import com.cathildros.swiftbeam.protocol.ProtocolConstants
import com.cathildros.swiftbeam.protocol.RemoteEntry
import com.cathildros.swiftbeam.ui.ConnectionUiState
import com.cathildros.swiftbeam.ui.MainUiState
import com.cathildros.swiftbeam.ui.theme.SwiftColors

@OptIn(ExperimentalFoundationApi::class)
@Composable
fun FilesScreen(
  state: MainUiState,
  onGoToConnect: () -> Unit,
  onNavigateTo: (String) -> Unit,
  onNavigateUp: () -> Unit,
  onRefresh: () -> Unit,
  onToggleHidden: () -> Unit,
  onSearchChanged: (String) -> Unit,
  onEntryClick: (RemoteEntry) -> Unit,
  onToggleSelect: (String) -> Unit,
  onClearSelection: () -> Unit,
  onPickPhotos: () -> Unit,
  onPickFiles: () -> Unit,
  onDownloadEntries: (List<RemoteEntry>) -> Unit,
  onCreateFolder: (String) -> Unit,
  onRenameEntry: (RemoteEntry, String) -> Unit,
  onDeleteEntries: (List<RemoteEntry>) -> Unit,
) {
  val conn = state.connection as? ConnectionUiState.Connected
  if (conn == null) {
    Column(
      modifier = Modifier.fillMaxSize().padding(24.dp),
      verticalArrangement = Arrangement.Center,
      horizontalAlignment = Alignment.CenterHorizontally,
    ) {
      Icon(Icons.Rounded.Computer, contentDescription = null, tint = SwiftColors.TextSecondary, modifier = Modifier.size(48.dp))
      Spacer(Modifier.height(12.dp))
      Text("Not connected to a PC", style = MaterialTheme.typography.titleMedium)
      Spacer(Modifier.height(6.dp))
      Text(
        "Connect to your Windows or Linux computer first to browse folders, send photos, or download files.",
        style = MaterialTheme.typography.bodySmall,
      )
      Spacer(Modifier.height(16.dp))
      Button(
        onClick = onGoToConnect,
        shape = CircleShape,
        colors = ButtonDefaults.buttonColors(containerColor = SwiftColors.Primary),
      ) {
        Text("Go to Connect")
      }
    }
    return
  }

  var showNewFolderDialog by remember { mutableStateOf(false) }
  var newFolderName by remember { mutableStateOf("") }
  var renameTarget by remember { mutableStateOf<RemoteEntry?>(null) }
  var renameInput by remember { mutableStateOf("") }
  var deleteTargets by remember { mutableStateOf<List<RemoteEntry>?>(null) }

  val filteredEntries = remember(state.entries, state.searchQuery) {
    val q = state.searchQuery.trim()
    if (q.isEmpty()) state.entries
    else state.entries.filter { it.name.contains(q, ignoreCase = true) }
  }

  Column(modifier = Modifier.fillMaxSize()) {
    // Quick Locations bar (when PC allows full filesystem browsing)
    if (conn.metadata.fullFilesystemAccess) {
      val quickPlaces = buildList {
        add("Drives" to ProtocolConstants.DRIVES_VIRTUAL_PATH)
        conn.metadata.desktopPath?.let { add("Desktop" to it) }
        conn.metadata.downloadsPath?.let { add("Downloads" to it) }
        conn.metadata.documentsPath?.let { add("Documents" to it) }
        conn.metadata.homePath?.let { add("Home" to it) }
      }
      Row(
        modifier = Modifier
          .fillMaxWidth()
          .horizontalScroll(rememberScrollState())
          .padding(horizontal = 14.dp, vertical = 6.dp),
        horizontalArrangement = Arrangement.spacedBy(8.dp),
      ) {
        for ((label, path) in quickPlaces) {
          val selected = state.currentPath.equals(path, ignoreCase = true)
          FilterChip(
            selected = selected,
            onClick = { onNavigateTo(path) },
            label = { Text(label, style = MaterialTheme.typography.labelMedium) },
            shape = CircleShape,
            colors = FilterChipDefaults.filterChipColors(
              selectedContainerColor = SwiftColors.Primary,
              selectedLabelColor = Color.White,
              containerColor = SwiftColors.SurfaceVariant,
              labelColor = SwiftColors.TextSecondary,
            ),
            border = FilterChipDefaults.filterChipBorder(
              enabled = true,
              selected = selected,
              borderColor = SwiftColors.Border,
            ),
          )
        }
      }
    }

    // Current path & action controls
    Surface(
      color = SwiftColors.Surface,
      shape = RoundedCornerShape(16.dp),
      border = BorderStroke(1.dp, SwiftColors.Border),
      modifier = Modifier.fillMaxWidth().padding(horizontal = 14.dp, vertical = 4.dp),
    ) {
      Column(modifier = Modifier.padding(10.dp), verticalArrangement = Arrangement.spacedBy(8.dp)) {
        Row(verticalAlignment = Alignment.CenterVertically) {
          val canGoUp = PathUtils.parentRemotePath(state.currentPath, conn.metadata.fullFilesystemAccess) != null
          IconButton(onClick = onNavigateUp, enabled = canGoUp, modifier = Modifier.size(34.dp)) {
            Icon(
              Icons.AutoMirrored.Rounded.ArrowBack,
              contentDescription = "Up folder",
              tint = if (canGoUp) SwiftColors.TextPrimary else SwiftColors.TextSecondary.copy(alpha = 0.4f),
            )
          }
          Column(modifier = Modifier.weight(1f).padding(horizontal = 6.dp)) {
            val pathLabel = when {
              state.currentPath == ProtocolConstants.DRIVES_VIRTUAL_PATH -> "${conn.metadata.deviceName} / Drives"
              state.currentPath.isBlank() -> conn.metadata.sharedRootName
              else -> state.currentPath
            }
            Text(pathLabel, style = MaterialTheme.typography.bodyMedium, fontWeight = FontWeight.SemiBold, maxLines = 1, overflow = TextOverflow.Ellipsis)
            if (state.freeDiskBytes != null) {
              Text("${PathUtils.formatBytes(state.freeDiskBytes)} free on PC", style = MaterialTheme.typography.bodySmall)
            }
          }
          IconButton(onClick = onToggleHidden, modifier = Modifier.size(34.dp)) {
            Icon(
              if (state.showHidden) Icons.Rounded.Visibility else Icons.Rounded.VisibilityOff,
              contentDescription = "Toggle hidden files",
              tint = if (state.showHidden) SwiftColors.Primary else SwiftColors.TextSecondary,
            )
          }
          IconButton(onClick = onRefresh, modifier = Modifier.size(34.dp)) {
            Icon(Icons.Rounded.Refresh, contentDescription = "Refresh folder", tint = SwiftColors.TextPrimary)
          }
        }

        // Send / New Folder pills
        if (!conn.metadata.readOnly) {
          Row(
            modifier = Modifier.fillMaxWidth(),
            horizontalArrangement = Arrangement.spacedBy(8.dp),
          ) {
            Button(
              onClick = onPickPhotos,
              shape = CircleShape,
              colors = ButtonDefaults.buttonColors(containerColor = SwiftColors.Primary),
              contentPadding = PaddingValues(horizontal = 12.dp, vertical = 8.dp),
              modifier = Modifier.weight(1f),
            ) {
              Icon(Icons.Rounded.PhotoLibrary, contentDescription = null, modifier = Modifier.size(16.dp))
              Spacer(Modifier.width(6.dp))
              Text("Send Photos", style = MaterialTheme.typography.labelMedium, maxLines = 1)
            }
            Button(
              onClick = onPickFiles,
              shape = CircleShape,
              colors = ButtonDefaults.buttonColors(containerColor = SwiftColors.PrimaryDark),
              contentPadding = PaddingValues(horizontal = 12.dp, vertical = 8.dp),
              modifier = Modifier.weight(1f),
            ) {
              Icon(Icons.Rounded.UploadFile, contentDescription = null, modifier = Modifier.size(16.dp))
              Spacer(Modifier.width(6.dp))
              Text("Send Files", style = MaterialTheme.typography.labelMedium, maxLines = 1)
            }
            OutlinedButton(
              onClick = {
                newFolderName = ""
                showNewFolderDialog = true
              },
              shape = CircleShape,
              border = BorderStroke(1.dp, SwiftColors.Border),
              contentPadding = PaddingValues(horizontal = 12.dp, vertical = 8.dp),
            ) {
              Icon(Icons.Rounded.CreateNewFolder, contentDescription = "New Folder", modifier = Modifier.size(16.dp))
            }
          }
        }

        OutlinedTextField(
          value = state.searchQuery,
          onValueChange = onSearchChanged,
          placeholder = { Text("Filter current folder...") },
          leadingIcon = { Icon(Icons.Rounded.Search, contentDescription = null, modifier = Modifier.size(18.dp)) },
          trailingIcon = if (state.searchQuery.isNotEmpty()) {
            {
              IconButton(onClick = { onSearchChanged("") }) {
                Icon(Icons.Rounded.Close, contentDescription = "Clear filter", modifier = Modifier.size(16.dp))
              }
            }
          } else null,
          singleLine = true,
          modifier = Modifier.fillMaxWidth(),
          shape = CircleShape,
          colors = swiftTextFieldColors(),
        )
      }
    }

    // Multi-select toolbar
    if (state.selectedPaths.isNotEmpty()) {
      val selectedEntries = state.entries.filter { it.relativePath in state.selectedPaths }
      Surface(
        color = SwiftColors.Primary.copy(alpha = 0.16f),
        shape = RoundedCornerShape(14.dp),
        border = BorderStroke(1.dp, SwiftColors.Primary.copy(alpha = 0.45f)),
        modifier = Modifier.fillMaxWidth().padding(horizontal = 14.dp, vertical = 4.dp),
      ) {
        Row(
          modifier = Modifier.padding(horizontal = 12.dp, vertical = 8.dp),
          verticalAlignment = Alignment.CenterVertically,
          horizontalArrangement = Arrangement.spacedBy(8.dp),
        ) {
          Text("${selectedEntries.size} selected", fontWeight = FontWeight.SemiBold, modifier = Modifier.weight(1f))
          Button(
            onClick = { onDownloadEntries(selectedEntries) },
            shape = CircleShape,
            colors = ButtonDefaults.buttonColors(containerColor = SwiftColors.Primary),
            contentPadding = PaddingValues(horizontal = 12.dp, vertical = 6.dp),
          ) {
            Icon(Icons.Rounded.Download, contentDescription = null, modifier = Modifier.size(16.dp))
            Spacer(Modifier.width(4.dp))
            Text("Download", style = MaterialTheme.typography.labelMedium)
          }
          if (!conn.metadata.readOnly) {
            IconButton(onClick = { deleteTargets = selectedEntries }, modifier = Modifier.size(34.dp)) {
              Icon(Icons.Rounded.Delete, contentDescription = "Delete selected", tint = SwiftColors.Rose)
            }
          }
          IconButton(onClick = onClearSelection, modifier = Modifier.size(34.dp)) {
            Icon(Icons.Rounded.Close, contentDescription = "Clear selection", tint = SwiftColors.TextSecondary)
          }
        }
      }
    }

    if (state.dirError != null) {
      Text(
        state.dirError,
        color = SwiftColors.Rose,
        style = MaterialTheme.typography.bodySmall,
        modifier = Modifier.padding(horizontal = 16.dp, vertical = 6.dp),
      )
    }

    if (state.isLoadingDir) {
      Box(modifier = Modifier.fillMaxSize(), contentAlignment = Alignment.Center) {
        CircularProgressIndicator(color = SwiftColors.Primary)
      }
    } else if (filteredEntries.isEmpty()) {
      Box(modifier = Modifier.fillMaxSize().padding(24.dp), contentAlignment = Alignment.Center) {
        Text(
          if (state.searchQuery.isNotBlank()) "No matching files in this folder." else "This folder is empty.",
          style = MaterialTheme.typography.bodySmall,
        )
      }
    } else {
      LazyColumn(
        modifier = Modifier.fillMaxSize(),
        contentPadding = PaddingValues(horizontal = 14.dp, vertical = 6.dp),
        verticalArrangement = Arrangement.spacedBy(6.dp),
      ) {
        items(filteredEntries, key = { it.relativePath }) { entry ->
          val isSelected = entry.relativePath in state.selectedPaths
          var menuExpanded by remember { mutableStateOf(false) }

          Surface(
            color = if (isSelected) SwiftColors.Primary.copy(alpha = 0.14f) else SwiftColors.Surface,
            shape = RoundedCornerShape(14.dp),
            border = BorderStroke(1.dp, if (isSelected) SwiftColors.Primary else SwiftColors.Border),
            modifier = Modifier
              .fillMaxWidth()
              .combinedClickable(
                onClick = {
                  if (state.selectedPaths.isNotEmpty()) {
                    onToggleSelect(entry.relativePath)
                  } else {
                    onEntryClick(entry)
                  }
                },
                onLongClick = { onToggleSelect(entry.relativePath) },
              ),
          ) {
            Row(
              modifier = Modifier.padding(horizontal = 10.dp, vertical = 10.dp),
              verticalAlignment = Alignment.CenterVertically,
            ) {
              if (state.selectedPaths.isNotEmpty()) {
                Checkbox(
                  checked = isSelected,
                  onCheckedChange = { onToggleSelect(entry.relativePath) },
                  colors = CheckboxDefaults.colors(checkedColor = SwiftColors.Primary),
                )
              }

              val (icon, tint) = entryIconAndTint(entry)
              Surface(color = tint.copy(alpha = 0.14f), shape = RoundedCornerShape(10.dp)) {
                Icon(icon, contentDescription = null, tint = tint, modifier = Modifier.padding(8.dp).size(20.dp))
              }
              Spacer(Modifier.width(10.dp))

              Column(modifier = Modifier.weight(1f)) {
                Text(entry.name, style = MaterialTheme.typography.bodyMedium, fontWeight = FontWeight.Medium, maxLines = 1, overflow = TextOverflow.Ellipsis)
                val subtitle = buildString {
                  append(if (entry.isDirectory) "Folder" else PathUtils.formatBytes(entry.size))
                  val date = PathUtils.formatRemoteDate(entry.lastModifiedUtc)
                  if (date.isNotEmpty()) {
                    append(" • ")
                    append(date)
                  }
                }
                Text(subtitle, style = MaterialTheme.typography.bodySmall, maxLines = 1, overflow = TextOverflow.Ellipsis)
              }

              Box {
                IconButton(onClick = { menuExpanded = true }, modifier = Modifier.size(34.dp)) {
                  Icon(Icons.Rounded.MoreVert, contentDescription = "Actions", tint = SwiftColors.TextSecondary)
                }
                DropdownMenu(expanded = menuExpanded, onDismissRequest = { menuExpanded = false }) {
                  DropdownMenuItem(
                    text = { Text(if (entry.isDirectory) "Download Folder" else "Download") },
                    onClick = {
                      menuExpanded = false
                      onDownloadEntries(listOf(entry))
                    },
                  )
                  DropdownMenuItem(
                    text = { Text(if (isSelected) "Deselect" else "Select") },
                    onClick = {
                      menuExpanded = false
                      onToggleSelect(entry.relativePath)
                    },
                  )
                  if (!conn.metadata.readOnly) {
                    DropdownMenuItem(
                      text = { Text("Rename") },
                      onClick = {
                        menuExpanded = false
                        renameTarget = entry
                        renameInput = entry.name
                      },
                    )
                    DropdownMenuItem(
                      text = { Text("Delete", color = SwiftColors.Rose) },
                      onClick = {
                        menuExpanded = false
                        deleteTargets = listOf(entry)
                      },
                    )
                  }
                }
              }
            }
          }
        }
      }
    }
  }

  if (showNewFolderDialog) {
    AlertDialog(
      onDismissRequest = { showNewFolderDialog = false },
      containerColor = SwiftColors.Surface,
      title = { Text("Create Folder on PC") },
      text = {
        OutlinedTextField(
          value = newFolderName,
          onValueChange = { newFolderName = it },
          label = { Text("Folder name") },
          singleLine = true,
          shape = RoundedCornerShape(14.dp),
          colors = swiftTextFieldColors(),
        )
      },
      confirmButton = {
        Button(
          onClick = {
            showNewFolderDialog = false
            onCreateFolder(newFolderName)
          },
          shape = CircleShape,
          colors = ButtonDefaults.buttonColors(containerColor = SwiftColors.Primary),
        ) {
          Text("Create")
        }
      },
      dismissButton = {
        TextButton(onClick = { showNewFolderDialog = false }) {
          Text("Cancel", color = SwiftColors.TextSecondary)
        }
      },
    )
  }

  val pendingRename = renameTarget
  if (pendingRename != null) {
    AlertDialog(
      onDismissRequest = { renameTarget = null },
      containerColor = SwiftColors.Surface,
      title = { Text("Rename '${pendingRename.name}'") },
      text = {
        OutlinedTextField(
          value = renameInput,
          onValueChange = { renameInput = it },
          label = { Text("New name") },
          singleLine = true,
          shape = RoundedCornerShape(14.dp),
          colors = swiftTextFieldColors(),
        )
      },
      confirmButton = {
        Button(
          onClick = {
            renameTarget = null
            onRenameEntry(pendingRename, renameInput)
          },
          shape = CircleShape,
          colors = ButtonDefaults.buttonColors(containerColor = SwiftColors.Primary),
        ) {
          Text("Rename")
        }
      },
      dismissButton = {
        TextButton(onClick = { renameTarget = null }) {
          Text("Cancel", color = SwiftColors.TextSecondary)
        }
      },
    )
  }

  val pendingDelete = deleteTargets
  if (pendingDelete != null) {
    AlertDialog(
      onDismissRequest = { deleteTargets = null },
      containerColor = SwiftColors.Surface,
      title = { Text("Delete on PC?") },
      text = {
        Text(
          if (pendingDelete.size == 1) "Permanently delete '${pendingDelete[0].name}' on ${conn.metadata.deviceName}?"
          else "Permanently delete ${pendingDelete.size} selected items on ${conn.metadata.deviceName}?",
          style = MaterialTheme.typography.bodyMedium,
        )
      },
      confirmButton = {
        Button(
          onClick = {
            deleteTargets = null
            onDeleteEntries(pendingDelete)
          },
          shape = CircleShape,
          colors = ButtonDefaults.buttonColors(containerColor = SwiftColors.Rose),
        ) {
          Text("Delete")
        }
      },
      dismissButton = {
        TextButton(onClick = { deleteTargets = null }) {
          Text("Cancel", color = SwiftColors.TextSecondary)
        }
      },
    )
  }
}

private fun entryIconAndTint(entry: RemoteEntry): Pair<ImageVector, Color> {
  if (entry.isDirectory) return Icons.Rounded.Folder to SwiftColors.Amber
  val ext = entry.name.substringAfterLast('.', "").lowercase()
  return when (ext) {
    "jpg", "jpeg", "png", "gif", "webp", "heic", "bmp", "svg" -> Icons.Rounded.Image to SwiftColors.Cyan
    "mp4", "mkv", "mov", "avi", "webm" -> Icons.Rounded.VideoFile to SwiftColors.Primary
    "mp3", "wav", "flac", "aac", "ogg", "m4a" -> Icons.Rounded.AudioFile to SwiftColors.Emerald
    "zip", "7z", "rar", "tar", "gz", "apk" -> Icons.Rounded.Archive to Color(0xFFFB923C)
    else -> Icons.AutoMirrored.Rounded.InsertDriveFile to SwiftColors.TextSecondary
  }
}
