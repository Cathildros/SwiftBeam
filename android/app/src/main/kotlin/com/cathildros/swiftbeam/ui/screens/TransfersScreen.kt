package com.cathildros.swiftbeam.ui.screens

import androidx.compose.foundation.BorderStroke
import androidx.compose.foundation.layout.Arrangement
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
import androidx.compose.foundation.shape.CircleShape
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.automirrored.rounded.OpenInNew
import androidx.compose.material.icons.rounded.ArrowDownward
import androidx.compose.material.icons.rounded.ArrowUpward
import androidx.compose.material.icons.rounded.CheckCircle
import androidx.compose.material.icons.rounded.Close
import androidx.compose.material.icons.rounded.DeleteSweep
import androidx.compose.material.icons.rounded.ErrorOutline
import androidx.compose.material3.Card
import androidx.compose.material3.CardDefaults
import androidx.compose.material3.Icon
import androidx.compose.material3.IconButton
import androidx.compose.material3.LinearProgressIndicator
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.OutlinedButton
import androidx.compose.material3.Surface
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.runtime.Composable
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.dp
import com.cathildros.swiftbeam.protocol.PathUtils
import com.cathildros.swiftbeam.service.TransferDirection
import com.cathildros.swiftbeam.service.TransferStatus
import com.cathildros.swiftbeam.ui.MainUiState
import com.cathildros.swiftbeam.ui.theme.SwiftColors

@Composable
fun TransfersScreen(
  state: MainUiState,
  onCancelTransfer: (String) -> Unit,
  onClearFinished: () -> Unit,
  onClearHistory: () -> Unit,
  onOpenFile: (uri: String, mimeType: String?) -> Unit,
) {
  LazyColumn(
    modifier = Modifier.fillMaxSize(),
    contentPadding = PaddingValues(16.dp),
    verticalArrangement = Arrangement.spacedBy(12.dp),
  ) {
    item {
      Row(
        modifier = Modifier.fillMaxWidth(),
        verticalAlignment = Alignment.CenterVertically,
        horizontalArrangement = Arrangement.SpaceBetween,
      ) {
        Text("Active Queue", style = MaterialTheme.typography.titleMedium)
        if (state.activeTransfers.any { it.status == TransferStatus.Completed || it.status == TransferStatus.Failed || it.status == TransferStatus.Cancelled }) {
          TextButton(onClick = onClearFinished) {
            Text("Clear Finished", color = SwiftColors.Primary)
          }
        }
      }
    }

    if (state.activeTransfers.isEmpty()) {
      item {
        Card(
          colors = CardDefaults.cardColors(containerColor = SwiftColors.Surface),
          shape = RoundedCornerShape(16.dp),
          border = BorderStroke(1.dp, SwiftColors.Border),
        ) {
          Text(
            "No active transfers right now. Large transfers continue automatically in the background with a progress notification.",
            style = MaterialTheme.typography.bodySmall,
            modifier = Modifier.padding(16.dp),
          )
        }
      }
    } else {
      items(state.activeTransfers, key = { it.id }) { item ->
        val progress = if (item.totalBytes > 0L) {
          (item.transferredBytes.toFloat() / item.totalBytes.toFloat()).coerceIn(0f, 1f)
        } else if (item.status == TransferStatus.Completed) 1f else 0f

        Card(
          colors = CardDefaults.cardColors(containerColor = SwiftColors.Surface),
          shape = RoundedCornerShape(16.dp),
          border = BorderStroke(1.dp, SwiftColors.Border),
        ) {
          Column(modifier = Modifier.padding(14.dp), verticalArrangement = Arrangement.spacedBy(8.dp)) {
            Row(verticalAlignment = Alignment.CenterVertically) {
              val isUp = item.direction == TransferDirection.Upload
              Surface(
                color = (if (isUp) SwiftColors.Primary else SwiftColors.Cyan).copy(alpha = 0.16f),
                shape = CircleShape,
              ) {
                Icon(
                  if (isUp) Icons.Rounded.ArrowUpward else Icons.Rounded.ArrowDownward,
                  contentDescription = null,
                  tint = if (isUp) SwiftColors.Primary else SwiftColors.Cyan,
                  modifier = Modifier.padding(6.dp).size(16.dp),
                )
              }
              Spacer(Modifier.width(10.dp))
              Column(modifier = Modifier.weight(1f)) {
                Text(item.fileName, fontWeight = FontWeight.SemiBold, maxLines = 1, overflow = TextOverflow.Ellipsis)
                val info = when (item.status) {
                  TransferStatus.Queued -> "Queued • ${PathUtils.formatBytes(item.totalBytes)}"
                  TransferStatus.Running -> "${PathUtils.formatBytes(item.transferredBytes)} / ${PathUtils.formatBytes(item.totalBytes)} • ${PathUtils.formatSpeed(item.bytesPerSec)}"
                  TransferStatus.Completed -> "Completed • ${PathUtils.formatBytes(item.totalBytes)} (${PathUtils.formatSpeed(item.bytesPerSec)})"
                  TransferStatus.Failed -> "Failed: ${item.message}"
                  TransferStatus.Cancelled -> "Cancelled"
                }
                Text(
                  info,
                  style = MaterialTheme.typography.bodySmall,
                  color = if (item.status == TransferStatus.Failed) SwiftColors.Rose else SwiftColors.TextSecondary,
                  maxLines = 2,
                  overflow = TextOverflow.Ellipsis,
                )
              }

              if (item.status == TransferStatus.Queued || item.status == TransferStatus.Running) {
                IconButton(onClick = { onCancelTransfer(item.id) }) {
                  Icon(Icons.Rounded.Close, contentDescription = "Cancel transfer", tint = SwiftColors.TextSecondary)
                }
              } else if (item.status == TransferStatus.Completed && item.savedUri != null) {
                OutlinedButton(
                  onClick = { onOpenFile(item.savedUri, item.mimeType) },
                  shape = CircleShape,
                  border = BorderStroke(1.dp, SwiftColors.Emerald.copy(alpha = 0.6f)),
                  contentPadding = PaddingValues(horizontal = 10.dp, vertical = 4.dp),
                ) {
                  Icon(Icons.AutoMirrored.Rounded.OpenInNew, contentDescription = null, modifier = Modifier.size(14.dp), tint = SwiftColors.Emerald)
                  Spacer(Modifier.width(4.dp))
                  Text("Open", style = MaterialTheme.typography.labelMedium, color = SwiftColors.Emerald)
                }
              } else if (item.status == TransferStatus.Completed) {
                Icon(Icons.Rounded.CheckCircle, contentDescription = null, tint = SwiftColors.Emerald)
              } else if (item.status == TransferStatus.Failed) {
                Icon(Icons.Rounded.ErrorOutline, contentDescription = null, tint = SwiftColors.Rose)
              }
            }

            if (item.status == TransferStatus.Running || item.status == TransferStatus.Queued) {
              LinearProgressIndicator(
                progress = { progress },
                modifier = Modifier.fillMaxWidth().height(6.dp).clip(CircleShape),
                color = SwiftColors.Primary,
                trackColor = SwiftColors.SurfaceVariant,
              )
            }
          }
        }
      }
    }

    item {
      Spacer(Modifier.height(4.dp))
      Row(
        modifier = Modifier.fillMaxWidth(),
        verticalAlignment = Alignment.CenterVertically,
        horizontalArrangement = Arrangement.SpaceBetween,
      ) {
        Text("Recent History", style = MaterialTheme.typography.titleMedium)
        if (state.transferHistory.isNotEmpty()) {
          TextButton(onClick = onClearHistory) {
            Icon(Icons.Rounded.DeleteSweep, contentDescription = null, modifier = Modifier.size(16.dp), tint = SwiftColors.TextSecondary)
            Spacer(Modifier.width(4.dp))
            Text("Clear", color = SwiftColors.TextSecondary)
          }
        }
      }
    }

    if (state.transferHistory.isEmpty()) {
      item {
        Text("No completed transfers recorded yet.", style = MaterialTheme.typography.bodySmall)
      }
    } else {
      items(state.transferHistory, key = { "${it.id}_${it.completedAtMs}" }) { hist ->
        Surface(
          color = SwiftColors.Surface,
          shape = RoundedCornerShape(14.dp),
          border = BorderStroke(1.dp, SwiftColors.Border),
        ) {
          Row(
            modifier = Modifier.fillMaxWidth().padding(12.dp),
            verticalAlignment = Alignment.CenterVertically,
          ) {
            val isUpload = hist.direction == TransferDirection.Upload.name
            Icon(
              if (isUpload) Icons.Rounded.ArrowUpward else Icons.Rounded.ArrowDownward,
              contentDescription = null,
              tint = if (isUpload) SwiftColors.Primary else SwiftColors.Cyan,
              modifier = Modifier.size(18.dp),
            )
            Spacer(Modifier.width(10.dp))
            Column(modifier = Modifier.weight(1f)) {
              Text(hist.fileName, style = MaterialTheme.typography.bodyMedium, fontWeight = FontWeight.Medium, maxLines = 1, overflow = TextOverflow.Ellipsis)
              Text(
                "${hist.direction} • ${hist.peerName} • ${PathUtils.formatBytes(hist.totalBytes)}",
                style = MaterialTheme.typography.bodySmall,
              )
              if (hist.message.isNotBlank()) {
                Text(
                  hist.message,
                  style = MaterialTheme.typography.bodySmall,
                  color = if (hist.status == "Failed") SwiftColors.Rose else SwiftColors.TextSecondary,
                  maxLines = 1,
                  overflow = TextOverflow.Ellipsis,
                )
              }
            }
            if (hist.savedUri != null && hist.status == "Completed") {
              IconButton(onClick = { onOpenFile(hist.savedUri, hist.mimeType) }) {
                Icon(Icons.AutoMirrored.Rounded.OpenInNew, contentDescription = "Open file", tint = SwiftColors.Emerald, modifier = Modifier.size(18.dp))
              }
            } else {
              Text(
                hist.status,
                style = MaterialTheme.typography.labelMedium,
                color = when (hist.status) {
                  "Completed" -> SwiftColors.Emerald
                  "Failed" -> SwiftColors.Rose
                  else -> SwiftColors.TextSecondary
                },
              )
            }
          }
        }
      }
    }
  }
}
