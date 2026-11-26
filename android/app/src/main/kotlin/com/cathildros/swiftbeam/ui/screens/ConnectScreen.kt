package com.cathildros.swiftbeam.ui.screens

import androidx.compose.foundation.BorderStroke
import androidx.compose.foundation.clickable
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
import androidx.compose.foundation.text.KeyboardOptions
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.rounded.Bolt
import androidx.compose.material.icons.rounded.BookmarkRemove
import androidx.compose.material.icons.rounded.Computer
import androidx.compose.material.icons.rounded.ErrorOutline
import androidx.compose.material.icons.rounded.Lock
import androidx.compose.material.icons.rounded.Refresh
import androidx.compose.material.icons.rounded.Wifi
import androidx.compose.material3.AlertDialog
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
import androidx.compose.material3.OutlinedTextFieldDefaults
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
import androidx.compose.ui.text.input.KeyboardType
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.dp
import com.cathildros.swiftbeam.data.SavedPeerRecord
import com.cathildros.swiftbeam.discovery.DiscoveredPeer
import com.cathildros.swiftbeam.ui.ConnectionUiState
import com.cathildros.swiftbeam.ui.MainUiState
import com.cathildros.swiftbeam.ui.theme.SwiftColors

@Composable
fun ConnectScreen(
  state: MainUiState,
  onRefreshDiscovery: () -> Unit,
  onRequestNearbyPermission: () -> Unit,
  onHostChanged: (String) -> Unit,
  onPortChanged: (String) -> Unit,
  onPinChanged: (String) -> Unit,
  onUseTlsChanged: (Boolean) -> Unit,
  onConnectManual: () -> Unit,
  onConnectPeer: (host: String, port: Int, pin: String, useTls: Boolean, label: String) -> Unit,
  onFillSavedPeer: (SavedPeerRecord) -> Unit,
  onRemoveSavedPeer: (SavedPeerRecord) -> Unit,
) {
  var promptPeer by remember { mutableStateOf<DiscoveredPeer?>(null) }
  var promptPin by remember { mutableStateOf("") }
  var promptUseTls by remember { mutableStateOf(true) }

  val isConnecting = state.connection is ConnectionUiState.Connecting

  LazyColumn(
    modifier = Modifier.fillMaxSize(),
    contentPadding = PaddingValues(16.dp),
    verticalArrangement = Arrangement.spacedBy(14.dp),
  ) {
    if (!state.nearbyPermissionGranted) {
      item {
        Card(
          colors = CardDefaults.cardColors(containerColor = SwiftColors.SurfaceVariant),
          shape = RoundedCornerShape(18.dp),
          border = BorderStroke(1.dp, SwiftColors.Primary.copy(alpha = 0.5f)),
        ) {
          Column(modifier = Modifier.padding(14.dp), verticalArrangement = Arrangement.spacedBy(8.dp)) {
            Row(verticalAlignment = Alignment.CenterVertically, horizontalArrangement = Arrangement.spacedBy(8.dp)) {
              Icon(Icons.Rounded.Wifi, contentDescription = null, tint = SwiftColors.Primary)
              Text("Allow Nearby Wi-Fi Devices", style = MaterialTheme.typography.titleMedium)
            }
            Text(
              "Android requires Nearby Devices permission to auto-discover SwiftBeam PCs on your Wi-Fi network.",
              style = MaterialTheme.typography.bodySmall,
            )
            Button(
              onClick = onRequestNearbyPermission,
              shape = CircleShape,
              colors = ButtonDefaults.buttonColors(containerColor = SwiftColors.Primary),
            ) {
              Text("Grant Permission")
            }
          }
        }
      }
    }

    if (state.connectionError != null) {
      item {
        Surface(
          color = SwiftColors.Rose.copy(alpha = 0.14f),
          shape = RoundedCornerShape(16.dp),
          border = BorderStroke(1.dp, SwiftColors.Rose.copy(alpha = 0.45f)),
        ) {
          Row(
            modifier = Modifier
              .fillMaxWidth()
              .padding(12.dp),
            verticalAlignment = Alignment.CenterVertically,
            horizontalArrangement = Arrangement.spacedBy(10.dp),
          ) {
            Icon(Icons.Rounded.ErrorOutline, contentDescription = null, tint = SwiftColors.Rose)
            Text(state.connectionError, color = Color(0xFFFECDD3), style = MaterialTheme.typography.bodySmall)
          }
        }
      }
    }

    item {
      Card(
        colors = CardDefaults.cardColors(containerColor = SwiftColors.Surface),
        shape = RoundedCornerShape(20.dp),
        border = BorderStroke(1.dp, SwiftColors.Border),
      ) {
        Column(modifier = Modifier.padding(16.dp), verticalArrangement = Arrangement.spacedBy(12.dp)) {
          Row(
            modifier = Modifier.fillMaxWidth(),
            verticalAlignment = Alignment.CenterVertically,
            horizontalArrangement = Arrangement.SpaceBetween,
          ) {
            Column {
              Text("Nearby Computers", style = MaterialTheme.typography.titleMedium)
              Text(
                if (state.discoveredPeers.isEmpty()) "Listening for SwiftBeam beacons on Wi-Fi..."
                else "${state.discoveredPeers.size} computer(s) found on your LAN",
                style = MaterialTheme.typography.bodySmall,
              )
            }
            OutlinedButton(
              onClick = onRefreshDiscovery,
              shape = CircleShape,
              border = BorderStroke(1.dp, SwiftColors.Border),
              contentPadding = PaddingValues(horizontal = 14.dp, vertical = 6.dp),
            ) {
              Icon(Icons.Rounded.Refresh, contentDescription = null, modifier = Modifier.size(16.dp))
              Spacer(Modifier.width(6.dp))
              Text("Scan", style = MaterialTheme.typography.labelMedium)
            }
          }

          if (state.discoveredPeers.isEmpty()) {
            Surface(
              color = SwiftColors.SurfaceVariant,
              shape = RoundedCornerShape(14.dp),
              modifier = Modifier.fillMaxWidth(),
            ) {
              Row(
                modifier = Modifier.padding(14.dp),
                verticalAlignment = Alignment.CenterVertically,
                horizontalArrangement = Arrangement.spacedBy(12.dp),
              ) {
                CircularProgressIndicator(modifier = Modifier.size(18.dp), strokeWidth = 2.dp, color = SwiftColors.Primary)
                Text(
                  "Open SwiftBeam on your Windows or Linux PC on the same Wi-Fi network, or connect manually below.",
                  style = MaterialTheme.typography.bodySmall,
                )
              }
            }
          } else {
            Column(verticalArrangement = Arrangement.spacedBy(8.dp)) {
              for (peer in state.discoveredPeers) {
                Surface(
                  color = SwiftColors.SurfaceVariant,
                  shape = RoundedCornerShape(14.dp),
                  border = BorderStroke(1.dp, SwiftColors.Border),
                  modifier = Modifier
                    .fillMaxWidth()
                    .clickable(enabled = !isConnecting) {
                      val savedPin = state.savedPeers
                        .firstOrNull { it.host == peer.host && it.port == peer.port }
                        ?.pin
                        .orEmpty()
                      if (!peer.requiresPin) {
                        onConnectPeer(peer.host, peer.port, "", true, peer.deviceName)
                      } else if (savedPin.isNotBlank()) {
                        onConnectPeer(peer.host, peer.port, savedPin, true, peer.deviceName)
                      } else {
                        promptPin = state.pinInput
                        promptUseTls = true
                        promptPeer = peer
                      }
                    },
                ) {
                  Row(
                    modifier = Modifier.padding(12.dp),
                    verticalAlignment = Alignment.CenterVertically,
                    horizontalArrangement = Arrangement.spacedBy(12.dp),
                  ) {
                    Surface(color = SwiftColors.Primary.copy(alpha = 0.16f), shape = CircleShape) {
                      Icon(
                        Icons.Rounded.Computer,
                        contentDescription = null,
                        tint = SwiftColors.Primary,
                        modifier = Modifier.padding(10.dp).size(20.dp),
                      )
                    }
                    Column(modifier = Modifier.weight(1f)) {
                      Text(peer.deviceName, fontWeight = FontWeight.SemiBold, maxLines = 1, overflow = TextOverflow.Ellipsis)
                      Text("${peer.osPlatform} • ${peer.host}:${peer.port}", style = MaterialTheme.typography.bodySmall)
                    }
                    if (peer.requiresPin) {
                      Icon(Icons.Rounded.Lock, contentDescription = "PIN protected", tint = SwiftColors.TextSecondary, modifier = Modifier.size(16.dp))
                    }
                    Surface(color = SwiftColors.Primary, shape = CircleShape) {
                      Text(
                        "Connect",
                        color = Color.White,
                        style = MaterialTheme.typography.labelMedium,
                        modifier = Modifier.padding(horizontal = 12.dp, vertical = 6.dp),
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

    if (state.savedPeers.isNotEmpty()) {
      item {
        Card(
          colors = CardDefaults.cardColors(containerColor = SwiftColors.Surface),
          shape = RoundedCornerShape(20.dp),
          border = BorderStroke(1.dp, SwiftColors.Border),
        ) {
          Column(modifier = Modifier.padding(16.dp), verticalArrangement = Arrangement.spacedBy(10.dp)) {
            Text("Saved Computers", style = MaterialTheme.typography.titleMedium)
            for (saved in state.savedPeers) {
              Surface(
                color = SwiftColors.SurfaceVariant,
                shape = RoundedCornerShape(14.dp),
                border = BorderStroke(1.dp, SwiftColors.Border),
                modifier = Modifier
                  .fillMaxWidth()
                  .clickable(enabled = !isConnecting) {
                    onFillSavedPeer(saved)
                    onConnectPeer(saved.host, saved.port, saved.pin, saved.useTls, saved.deviceName)
                  },
              ) {
                Row(
                  modifier = Modifier.padding(horizontal = 12.dp, vertical = 10.dp),
                  verticalAlignment = Alignment.CenterVertically,
                ) {
                  Column(modifier = Modifier.weight(1f)) {
                    Text(saved.deviceName, fontWeight = FontWeight.SemiBold, maxLines = 1, overflow = TextOverflow.Ellipsis)
                    Text("${saved.host}:${saved.port}", style = MaterialTheme.typography.bodySmall)
                  }
                  IconButton(onClick = { onRemoveSavedPeer(saved) }) {
                    Icon(Icons.Rounded.BookmarkRemove, contentDescription = "Forget PC", tint = SwiftColors.TextSecondary)
                  }
                }
              }
            }
          }
        }
      }
    }

    item {
      Card(
        colors = CardDefaults.cardColors(containerColor = SwiftColors.Surface),
        shape = RoundedCornerShape(20.dp),
        border = BorderStroke(1.dp, SwiftColors.Border),
      ) {
        Column(modifier = Modifier.padding(16.dp), verticalArrangement = Arrangement.spacedBy(12.dp)) {
          Text("Manual Connect", style = MaterialTheme.typography.titleMedium)
          Text(
            "Enter your PC's IP address (or paste a SwiftBeam QR/Portal URL) and the 6-digit pairing PIN.",
            style = MaterialTheme.typography.bodySmall,
          )

          Row(horizontalArrangement = Arrangement.spacedBy(10.dp)) {
            OutlinedTextField(
              value = state.hostInput,
              onValueChange = onHostChanged,
              label = { Text("Host IP / URL") },
              placeholder = { Text("192.168.1.42") },
              singleLine = true,
              modifier = Modifier.weight(2f),
              shape = RoundedCornerShape(14.dp),
              colors = swiftTextFieldColors(),
            )
            OutlinedTextField(
              value = state.portInput,
              onValueChange = onPortChanged,
              label = { Text("Port") },
              singleLine = true,
              keyboardOptions = KeyboardOptions(keyboardType = KeyboardType.Number),
              modifier = Modifier.weight(1f),
              shape = RoundedCornerShape(14.dp),
              colors = swiftTextFieldColors(),
            )
          }

          OutlinedTextField(
            value = state.pinInput,
            onValueChange = onPinChanged,
            label = { Text("Pairing PIN (6 digits on PC)") },
            placeholder = { Text("123456") },
            singleLine = true,
            keyboardOptions = KeyboardOptions(keyboardType = KeyboardType.Number),
            modifier = Modifier.fillMaxWidth(),
            shape = RoundedCornerShape(14.dp),
            colors = swiftTextFieldColors(),
          )

          Row(
            modifier = Modifier.fillMaxWidth(),
            verticalAlignment = Alignment.CenterVertically,
            horizontalArrangement = Arrangement.SpaceBetween,
          ) {
            Column {
              Text("Encrypt transfer (TLS 1.3)", style = MaterialTheme.typography.bodyMedium)
              Text("Recommended on Wi-Fi networks", style = MaterialTheme.typography.bodySmall)
            }
            Switch(
              checked = state.useTls,
              onCheckedChange = onUseTlsChanged,
              colors = SwitchDefaults.colors(checkedThumbColor = Color.White, checkedTrackColor = SwiftColors.Primary),
            )
          }

          Button(
            onClick = onConnectManual,
            enabled = !isConnecting,
            shape = CircleShape,
            colors = ButtonDefaults.buttonColors(containerColor = SwiftColors.Primary),
            modifier = Modifier.fillMaxWidth().height(48.dp),
          ) {
            if (isConnecting) {
              CircularProgressIndicator(modifier = Modifier.size(18.dp), strokeWidth = 2.dp, color = Color.White)
              Spacer(Modifier.width(10.dp))
              Text("Connecting...")
            } else {
              Icon(Icons.Rounded.Bolt, contentDescription = null)
              Spacer(Modifier.width(6.dp))
              Text("Connect to PC", fontWeight = FontWeight.SemiBold)
            }
          }
        }
      }
    }
  }

  val activePromptPeer = promptPeer
  if (activePromptPeer != null) {
    AlertDialog(
      onDismissRequest = { promptPeer = null },
      containerColor = SwiftColors.Surface,
      title = { Text("Pair with ${activePromptPeer.deviceName}") },
      text = {
        Column(verticalArrangement = Arrangement.spacedBy(10.dp)) {
          Text(
            "Enter the 6-digit PIN displayed in SwiftBeam's top bar on ${activePromptPeer.deviceName} (${activePromptPeer.host}).",
            style = MaterialTheme.typography.bodySmall,
          )
          OutlinedTextField(
            value = promptPin,
            onValueChange = { promptPin = it.filter { c -> c.isLetterOrDigit() }.take(12) },
            label = { Text("Pairing PIN") },
            singleLine = true,
            keyboardOptions = KeyboardOptions(keyboardType = KeyboardType.Number),
            shape = RoundedCornerShape(14.dp),
            colors = swiftTextFieldColors(),
          )
          Row(
            modifier = Modifier.fillMaxWidth(),
            verticalAlignment = Alignment.CenterVertically,
            horizontalArrangement = Arrangement.SpaceBetween,
          ) {
            Text("TLS 1.3 Encryption", style = MaterialTheme.typography.bodyMedium)
            Switch(
              checked = promptUseTls,
              onCheckedChange = { promptUseTls = it },
              colors = SwitchDefaults.colors(checkedThumbColor = Color.White, checkedTrackColor = SwiftColors.Primary),
            )
          }
        }
      },
      confirmButton = {
        Button(
          onClick = {
            promptPeer = null
            onConnectPeer(activePromptPeer.host, activePromptPeer.port, promptPin, promptUseTls, activePromptPeer.deviceName)
          },
          shape = CircleShape,
          colors = ButtonDefaults.buttonColors(containerColor = SwiftColors.Primary),
        ) {
          Text("Connect")
        }
      },
      dismissButton = {
        TextButton(onClick = { promptPeer = null }) {
          Text("Cancel", color = SwiftColors.TextSecondary)
        }
      },
    )
  }
}

@Composable
fun swiftTextFieldColors() = OutlinedTextFieldDefaults.colors(
  focusedBorderColor = SwiftColors.Primary,
  unfocusedBorderColor = SwiftColors.Border,
  focusedContainerColor = SwiftColors.SurfaceVariant,
  unfocusedContainerColor = SwiftColors.SurfaceVariant,
  focusedTextColor = SwiftColors.TextPrimary,
  unfocusedTextColor = SwiftColors.TextPrimary,
  focusedLabelColor = SwiftColors.Primary,
  unfocusedLabelColor = SwiftColors.TextSecondary,
)
