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
import androidx.compose.material.icons.automirrored.rounded.Send
import androidx.compose.material.icons.rounded.ContentCopy
import androidx.compose.material.icons.rounded.ContentPaste
import androidx.compose.material3.Button
import androidx.compose.material3.ButtonDefaults
import androidx.compose.material3.Card
import androidx.compose.material3.CardDefaults
import androidx.compose.material3.CircularProgressIndicator
import androidx.compose.material3.Icon
import androidx.compose.material3.IconButton
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.OutlinedButton
import androidx.compose.material3.OutlinedTextField
import androidx.compose.material3.Surface
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.platform.LocalClipboardManager
import androidx.compose.ui.text.AnnotatedString
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.unit.dp
import com.cathildros.swiftbeam.ui.ConnectionUiState
import com.cathildros.swiftbeam.ui.MainUiState
import com.cathildros.swiftbeam.ui.theme.SwiftColors

@Composable
fun QuickTextScreen(
  state: MainUiState,
  onTextChanged: (String) -> Unit,
  onSendText: () -> Unit,
  onShowToast: (String) -> Unit,
) {
  @Suppress("DEPRECATION")
  val clipboard = LocalClipboardManager.current
  val connected = state.connection as? ConnectionUiState.Connected

  LazyColumn(
    modifier = Modifier.fillMaxSize(),
    contentPadding = PaddingValues(16.dp),
    verticalArrangement = Arrangement.spacedBy(14.dp),
  ) {
    item {
      Card(
        colors = CardDefaults.cardColors(containerColor = SwiftColors.Surface),
        shape = RoundedCornerShape(20.dp),
        border = BorderStroke(1.dp, SwiftColors.Border),
      ) {
        Column(modifier = Modifier.padding(16.dp), verticalArrangement = Arrangement.spacedBy(12.dp)) {
          Text("Send Quick Text to PC", style = MaterialTheme.typography.titleMedium)
          Text(
            if (connected != null) "Instantly send links, notes, or codes straight to ${connected.metadata.deviceName}."
            else "Connect to a PC first to beam text clips.",
            style = MaterialTheme.typography.bodySmall,
          )

          OutlinedTextField(
            value = state.quickTextInput,
            onValueChange = onTextChanged,
            label = { Text("Text, URL, or snippet") },
            minLines = 4,
            maxLines = 8,
            modifier = Modifier.fillMaxWidth(),
            shape = RoundedCornerShape(16.dp),
            colors = swiftTextFieldColors(),
          )

          Row(horizontalArrangement = Arrangement.spacedBy(10.dp), modifier = Modifier.fillMaxWidth()) {
            OutlinedButton(
              onClick = {
                val clip = clipboard.getText()?.text
                if (!clip.isNullOrEmpty()) {
                  onTextChanged(clip)
                } else {
                  onShowToast("Clipboard is empty")
                }
              },
              shape = CircleShape,
              border = BorderStroke(1.dp, SwiftColors.Border),
            ) {
              Icon(Icons.Rounded.ContentPaste, contentDescription = null, modifier = Modifier.size(16.dp))
              Spacer(Modifier.width(6.dp))
              Text("Paste")
            }

            Button(
              onClick = onSendText,
              enabled = connected != null && state.quickTextInput.isNotBlank() && !state.isSendingText,
              shape = CircleShape,
              colors = ButtonDefaults.buttonColors(containerColor = SwiftColors.Primary),
              modifier = Modifier.weight(1f).height(44.dp),
            ) {
              if (state.isSendingText) {
                CircularProgressIndicator(modifier = Modifier.size(18.dp), strokeWidth = 2.dp, color = Color.White)
              } else {
                Icon(Icons.AutoMirrored.Rounded.Send, contentDescription = null, modifier = Modifier.size(16.dp))
                Spacer(Modifier.width(6.dp))
                Text("Send to PC", fontWeight = FontWeight.SemiBold)
              }
            }
          }
        }
      }
    }

    if (state.sentTexts.isNotEmpty()) {
      item {
        Text("Recently Sent", style = MaterialTheme.typography.titleMedium)
      }
      items(state.sentTexts, key = { it.id }) { item ->
        Surface(
          color = SwiftColors.Surface,
          shape = RoundedCornerShape(14.dp),
          border = BorderStroke(1.dp, SwiftColors.Border),
        ) {
          Row(
            modifier = Modifier.fillMaxWidth().padding(12.dp),
            verticalAlignment = Alignment.CenterVertically,
          ) {
            Column(modifier = Modifier.weight(1f)) {
              Text("To ${item.peerName}", style = MaterialTheme.typography.bodySmall, color = SwiftColors.Primary)
              Spacer(Modifier.height(2.dp))
              Text(item.text, style = MaterialTheme.typography.bodyMedium)
            }
            IconButton(
              onClick = {
                clipboard.setText(AnnotatedString(item.text))
                onShowToast("Copied to clipboard")
              },
            ) {
              Icon(Icons.Rounded.ContentCopy, contentDescription = "Copy", tint = SwiftColors.TextSecondary)
            }
          }
        }
      }
    }
  }
}
