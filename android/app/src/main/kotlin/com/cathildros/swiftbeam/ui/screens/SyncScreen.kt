package com.cathildros.swiftbeam.ui.screens

import android.net.Uri
import androidx.compose.foundation.BorderStroke
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.PaddingValues
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.lazy.items
import androidx.compose.foundation.shape.CircleShape
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.rounded.Add
import androidx.compose.material.icons.rounded.Delete
import androidx.compose.material.icons.rounded.FolderShared
import androidx.compose.material.icons.rounded.Sync
import androidx.compose.material3.AlertDialog
import androidx.compose.material3.Button
import androidx.compose.material3.ButtonDefaults
import androidx.compose.material3.Card
import androidx.compose.material3.CardDefaults
import androidx.compose.material3.Icon
import androidx.compose.material3.IconButton
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.OutlinedButton
import androidx.compose.material3.OutlinedTextField
import androidx.compose.material3.Surface
import androidx.compose.material3.Switch
import androidx.compose.material3.SwitchDefaults
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
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.dp
import com.cathildros.swiftbeam.service.ConnectionUiState
import com.cathildros.swiftbeam.ui.MainUiState
import com.cathildros.swiftbeam.ui.theme.SwiftColors

@Composable
fun SyncScreen(
  state: MainUiState,
  pendingSyncTreeUri: Uri?,
  pendingSyncTreeLabel: String?,
  onConsumePendingSyncTree: () -> Unit,
  onPickSyncFolder: () -> Unit,
  onAddSyncPair: (treeUri: Uri, remotePath: String, twoWay: Boolean, autoOnConnect: Boolean) -> Unit,
  onSyncNow: (String) -> Unit,
  onToggleAuto: (String, Boolean) -> Unit,
  onDeletePair: (String) -> Unit,
) {
  val conn = state.connection as? ConnectionUiState.Connected
  var remotePathInput by remember(pendingSyncTreeUri, state.currentPath) {
    mutableStateOf(
      if (state.currentPath.isNotBlank() && state.currentPath != "__DRIVES__") state.currentPath
      else conn?.metadata?.desktopPath ?: "",
    )
  }
  var twoWay by remember { mutableStateOf(false) }
  var autoOnConnect by remember { mutableStateOf(true) }

  LazyColumn(
    modifier = Modifier.fillMaxSize(),
    contentPadding = PaddingValues(16.dp),
    verticalArrangement = Arrangement.spacedBy(12.dp),
  ) {
    item {
      Card(
        colors = CardDefaults.cardColors(containerColor = SwiftColors.Surface),
        shape = RoundedCornerShape(20.dp),
        border = BorderStroke(1.dp, SwiftColors.Border),
      ) {
        Column(modifier = Modifier.padding(16.dp), verticalArrangement = Arrangement.spacedBy(10.dp)) {
          Row(
            modifier = Modifier.fillMaxWidth(),
            verticalAlignment = Alignment.CenterVertically,
            horizontalArrangement = Arrangement.SpaceBetween,
          ) {
            Column(modifier = Modifier.weight(1f)) {
              Text("Wi-Fi Folder & Photo Auto-Sync", style = MaterialTheme.typography.titleMedium)
              Text(
                "Back up Camera photos or Downloads to your PC automatically whenever you connect.",
                style = MaterialTheme.typography.bodySmall,
              )
            }
            Spacer(Modifier.width(8.dp))
            Button(
              onClick = onPickSyncFolder,
              shape = CircleShape,
              colors = ButtonDefaults.buttonColors(containerColor = SwiftColors.Primary),
              contentPadding = PaddingValues(horizontal = 14.dp, vertical = 8.dp),
            ) {
              Icon(Icons.Rounded.Add, contentDescription = null, modifier = Modifier.size(16.dp))
              Spacer(Modifier.width(4.dp))
              Text("Add Folder", style = MaterialTheme.typography.labelMedium)
            }
          }
        }
      }
    }

    if (state.syncPairs.isEmpty()) {
      item {
        Surface(
          color = SwiftColors.Surface,
          shape = RoundedCornerShape(16.dp),
          border = BorderStroke(1.dp, SwiftColors.Border),
        ) {
          Column(
            modifier = Modifier.fillMaxWidth().padding(20.dp),
            horizontalAlignment = Alignment.CenterHorizontally,
            verticalArrangement = Arrangement.spacedBy(8.dp),
          ) {
            Icon(Icons.Rounded.FolderShared, contentDescription = null, tint = SwiftColors.Amber, modifier = Modifier.size(36.dp))
            Text("No sync folders configured", fontWeight = FontWeight.SemiBold)
            Text(
              "Tap 'Add Folder' to select DCIM/Camera or any folder on your phone.",
              style = MaterialTheme.typography.bodySmall,
            )
          }
        }
      }
    } else {
      items(state.syncPairs, key = { it.id }) { pair ->
        Card(
          colors = CardDefaults.cardColors(containerColor = SwiftColors.Surface),
          shape = RoundedCornerShape(18.dp),
          border = BorderStroke(1.dp, SwiftColors.Border),
        ) {
          Column(modifier = Modifier.padding(14.dp), verticalArrangement = Arrangement.spacedBy(8.dp)) {
            Row(verticalAlignment = Alignment.CenterVertically) {
              Column(modifier = Modifier.weight(1f)) {
                Text(pair.localLabel, fontWeight = FontWeight.SemiBold, maxLines = 1, overflow = TextOverflow.Ellipsis)
                val destLabel = pair.remotePath.ifBlank { "PC Shared Folder" }
                Text(
                  "${if (pair.twoWay) "Two-Way ↔" else "Backup →"} $destLabel",
                  style = MaterialTheme.typography.bodySmall,
                  color = SwiftColors.Primary,
                  maxLines = 1,
                  overflow = TextOverflow.Ellipsis,
                )
                Text(pair.lastSummary, style = MaterialTheme.typography.bodySmall)
              }
              IconButton(onClick = { onDeletePair(pair.id) }) {
                Icon(Icons.Rounded.Delete, contentDescription = "Remove sync pair", tint = SwiftColors.TextSecondary)
              }
            }

            Row(
              modifier = Modifier.fillMaxWidth(),
              verticalAlignment = Alignment.CenterVertically,
              horizontalArrangement = Arrangement.SpaceBetween,
            ) {
              Row(verticalAlignment = Alignment.CenterVertically, horizontalArrangement = Arrangement.spacedBy(8.dp)) {
                Switch(
                  checked = pair.autoSyncOnConnect,
                  onCheckedChange = { onToggleAuto(pair.id, it) },
                  colors = SwitchDefaults.colors(checkedThumbColor = Color.White, checkedTrackColor = SwiftColors.Primary),
                )
                Text("Auto on connect", style = MaterialTheme.typography.bodySmall)
              }

              OutlinedButton(
                onClick = { onSyncNow(pair.id) },
                enabled = conn != null,
                shape = CircleShape,
                border = BorderStroke(1.dp, SwiftColors.Border),
                contentPadding = PaddingValues(horizontal = 14.dp, vertical = 6.dp),
              ) {
                Icon(Icons.Rounded.Sync, contentDescription = null, modifier = Modifier.size(16.dp))
                Spacer(Modifier.width(6.dp))
                Text("Sync Now", style = MaterialTheme.typography.labelMedium)
              }
            }
          }
        }
      }
    }
  }

  if (pendingSyncTreeUri != null) {
    AlertDialog(
      onDismissRequest = onConsumePendingSyncTree,
      containerColor = SwiftColors.Surface,
      title = { Text("Configure Auto-Sync") },
      text = {
        Column(verticalArrangement = Arrangement.spacedBy(10.dp)) {
          Text("Phone folder: ${pendingSyncTreeLabel ?: "Selected Folder"}", style = MaterialTheme.typography.bodyMedium, fontWeight = FontWeight.SemiBold)
          OutlinedTextField(
            value = remotePathInput,
            onValueChange = { remotePathInput = it },
            label = { Text("Destination folder on PC") },
            placeholder = { Text("C:/Users/You/Pictures/Phone") },
            singleLine = true,
            shape = RoundedCornerShape(14.dp),
            colors = swiftTextFieldColors(),
          )
          Row(
            modifier = Modifier.fillMaxWidth(),
            verticalAlignment = Alignment.CenterVertically,
            horizontalArrangement = Arrangement.SpaceBetween,
          ) {
            Column(modifier = Modifier.weight(1f)) {
              Text("Two-Way Sync", style = MaterialTheme.typography.bodyMedium)
              Text("Also pull missing PC files into Downloads/SwiftBeam/Sync", style = MaterialTheme.typography.bodySmall)
            }
            Switch(
              checked = twoWay,
              onCheckedChange = { twoWay = it },
              colors = SwitchDefaults.colors(checkedThumbColor = Color.White, checkedTrackColor = SwiftColors.Primary),
            )
          }
          Row(
            modifier = Modifier.fillMaxWidth(),
            verticalAlignment = Alignment.CenterVertically,
            horizontalArrangement = Arrangement.SpaceBetween,
          ) {
            Text("Auto-sync when PC connects", style = MaterialTheme.typography.bodyMedium)
            Switch(
              checked = autoOnConnect,
              onCheckedChange = { autoOnConnect = it },
              colors = SwitchDefaults.colors(checkedThumbColor = Color.White, checkedTrackColor = SwiftColors.Primary),
            )
          }
        }
      },
      confirmButton = {
        Button(
          onClick = {
            val uri = pendingSyncTreeUri
            onConsumePendingSyncTree()
            onAddSyncPair(uri, remotePathInput, twoWay, autoOnConnect)
          },
          shape = CircleShape,
          colors = ButtonDefaults.buttonColors(containerColor = SwiftColors.Primary),
        ) {
          Text("Save Sync Pair")
        }
      },
      dismissButton = {
        TextButton(onClick = onConsumePendingSyncTree) {
          Text("Cancel", color = SwiftColors.TextSecondary)
        }
      },
    )
  }
}
