package com.cathildros.swiftbeam.ui

import android.net.Uri
import androidx.compose.foundation.BorderStroke
import androidx.compose.foundation.background
import androidx.compose.foundation.clickable
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.WindowInsets
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.safeDrawing
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.shape.CircleShape
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.automirrored.rounded.Chat
import androidx.compose.material.icons.rounded.Bolt
import androidx.compose.material.icons.rounded.Close
import androidx.compose.material.icons.rounded.Folder
import androidx.compose.material.icons.rounded.Link
import androidx.compose.material.icons.rounded.SwapVert
import androidx.compose.material.icons.rounded.Sync
import androidx.compose.material3.Badge
import androidx.compose.material3.BadgedBox
import androidx.compose.material3.Icon
import androidx.compose.material3.IconButton
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.NavigationBar
import androidx.compose.material3.NavigationBarItem
import androidx.compose.material3.NavigationBarItemDefaults
import androidx.compose.material3.Scaffold
import androidx.compose.material3.SnackbarHost
import androidx.compose.material3.SnackbarHostState
import androidx.compose.material3.Surface
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.getValue
import androidx.compose.runtime.remember
import androidx.compose.runtime.rememberCoroutineScope
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.dp
import androidx.lifecycle.compose.collectAsStateWithLifecycle
import com.cathildros.swiftbeam.service.ConnectionUiState
import com.cathildros.swiftbeam.service.TransferStatus
import com.cathildros.swiftbeam.ui.screens.ConnectScreen
import com.cathildros.swiftbeam.ui.screens.FilesScreen
import com.cathildros.swiftbeam.ui.screens.QuickTextScreen
import com.cathildros.swiftbeam.ui.screens.SyncScreen
import com.cathildros.swiftbeam.ui.screens.TransfersScreen
import com.cathildros.swiftbeam.ui.theme.SwiftColors
import kotlinx.coroutines.launch

@Composable
fun SwiftBeamApp(
  viewModel: MainViewModel,
  pendingSyncTreeUri: Uri?,
  onConsumePendingSyncTree: () -> Unit,
  onPickPhotos: () -> Unit,
  onPickFiles: () -> Unit,
  onPickFolder: () -> Unit,
  onPickPhoneHostFolder: () -> Unit,
  onPickSyncFolder: () -> Unit,
  onScanQrClick: () -> Unit,
  onRequestNearbyPermission: () -> Unit,
  onOpenUpdateUrl: (String) -> Unit,
) {
  val state by viewModel.uiState.collectAsStateWithLifecycle()
  val snackbarHostState = remember { SnackbarHostState() }
  val scope = rememberCoroutineScope()

  LaunchedEffect(Unit) {
    viewModel.messages.collect { msg ->
      snackbarHostState.showSnackbar(msg)
    }
  }

  val runningCount = state.activeTransfers.count {
    it.status == TransferStatus.Queued || it.status == TransferStatus.Running
  }

  Scaffold(
    containerColor = SwiftColors.Background,
    contentWindowInsets = WindowInsets.safeDrawing,
    snackbarHost = { SnackbarHost(snackbarHostState) },
    topBar = {
      SwiftTopBar(
        state = state,
        onDisconnect = { viewModel.disconnect() },
        onClearPendingShare = { viewModel.clearPendingShare() },
      )
    },
    bottomBar = {
      NavigationBar(
        containerColor = SwiftColors.Surface,
        tonalElevation = 0.dp,
      ) {
        val navColors = NavigationBarItemDefaults.colors(
          selectedIconColor = SwiftColors.Primary,
          selectedTextColor = SwiftColors.TextPrimary,
          indicatorColor = SwiftColors.Primary.copy(alpha = 0.18f),
          unselectedIconColor = SwiftColors.TextSecondary,
          unselectedTextColor = SwiftColors.TextSecondary,
        )
        NavigationBarItem(
          selected = state.activeTab == AppTab.Connect,
          onClick = { viewModel.selectTab(AppTab.Connect) },
          icon = { Icon(Icons.Rounded.Link, contentDescription = null) },
          label = { Text(AppTab.Connect.label, maxLines = 1) },
          colors = navColors,
        )
        NavigationBarItem(
          selected = state.activeTab == AppTab.Files,
          onClick = { viewModel.selectTab(AppTab.Files) },
          icon = { Icon(Icons.Rounded.Folder, contentDescription = null) },
          label = { Text(AppTab.Files.label, maxLines = 1) },
          colors = navColors,
        )
        NavigationBarItem(
          selected = state.activeTab == AppTab.Sync,
          onClick = { viewModel.selectTab(AppTab.Sync) },
          icon = { Icon(Icons.Rounded.Sync, contentDescription = null) },
          label = { Text(AppTab.Sync.label, maxLines = 1) },
          colors = navColors,
        )
        NavigationBarItem(
          selected = state.activeTab == AppTab.Transfers,
          onClick = { viewModel.selectTab(AppTab.Transfers) },
          icon = {
            BadgedBox(
              badge = {
                if (runningCount > 0) {
                  Badge(containerColor = SwiftColors.Primary, contentColor = Color.White) {
                    Text(runningCount.toString())
                  }
                }
              },
            ) {
              Icon(Icons.Rounded.SwapVert, contentDescription = null)
            }
          },
          label = { Text(AppTab.Transfers.label, maxLines = 1) },
          colors = navColors,
        )
        NavigationBarItem(
          selected = state.activeTab == AppTab.QuickText,
          onClick = { viewModel.selectTab(AppTab.QuickText) },
          icon = {
            BadgedBox(
              badge = {
                if (state.receivedTexts.isNotEmpty() && state.activeTab != AppTab.QuickText) {
                  Badge(containerColor = SwiftColors.Emerald, contentColor = Color.Black) {
                    Text(state.receivedTexts.size.toString())
                  }
                }
              },
            ) {
              Icon(Icons.AutoMirrored.Rounded.Chat, contentDescription = null)
            }
          },
          label = { Text(AppTab.QuickText.label, maxLines = 1) },
          colors = navColors,
        )
      }
    },
  ) { paddingValues ->
    Box(
      modifier = Modifier
        .fillMaxSize()
        .padding(paddingValues),
    ) {
      when (state.activeTab) {
        AppTab.Connect -> ConnectScreen(
          state = state,
          onRefreshDiscovery = viewModel::refreshDiscovery,
          onRequestNearbyPermission = onRequestNearbyPermission,
          onScanQrClick = onScanQrClick,
          onHostChanged = viewModel::updateHostInput,
          onPortChanged = viewModel::updatePortInput,
          onPinChanged = viewModel::updatePinInput,
          onUseTlsChanged = viewModel::updateUseTls,
          onConnectManual = viewModel::connectManual,
          onConnectPeer = viewModel::connectTo,
          onFillSavedPeer = viewModel::fillFromSavedPeer,
          onRemoveSavedPeer = viewModel::removeSavedPeer,
          onTogglePhoneHost = viewModel::togglePhoneHostEnabled,
          onPickPhoneHostFolder = onPickPhoneHostFolder,
          onCheckUpdates = { viewModel.checkForUpdates(silent = false) },
          onOpenUpdateUrl = onOpenUpdateUrl,
        )

        AppTab.Files -> FilesScreen(
          state = state,
          onGoToConnect = { viewModel.selectTab(AppTab.Connect) },
          onNavigateTo = viewModel::loadDirectory,
          onNavigateUp = viewModel::navigateUp,
          onRefresh = { viewModel.loadDirectory() },
          onToggleHidden = viewModel::toggleShowHidden,
          onSearchChanged = viewModel::updateSearchQuery,
          onEntryClick = viewModel::openEntry,
          onPreviewEntry = viewModel::previewEntry,
          onDismissPreview = viewModel::dismissPreview,
          onToggleSelect = viewModel::toggleSelection,
          onClearSelection = viewModel::clearSelection,
          onPickPhotos = onPickPhotos,
          onPickFiles = onPickFiles,
          onPickFolder = onPickFolder,
          onDownloadEntries = viewModel::downloadEntries,
          onCreateFolder = viewModel::createFolder,
          onRenameEntry = viewModel::renameEntry,
          onDeleteEntries = viewModel::deleteEntries,
        )

        AppTab.Sync -> SyncScreen(
          state = state,
          pendingSyncTreeUri = pendingSyncTreeUri,
          pendingSyncTreeLabel = pendingSyncTreeUri?.let { viewModel.resolveSyncFolderLabel(it) },
          onConsumePendingSyncTree = onConsumePendingSyncTree,
          onPickSyncFolder = onPickSyncFolder,
          onAddSyncPair = viewModel::addSyncPair,
          onSyncNow = viewModel::syncPairNow,
          onToggleAuto = viewModel::toggleSyncPairAuto,
          onDeletePair = viewModel::removeSyncPair,
        )

        AppTab.Transfers -> TransfersScreen(
          state = state,
          onCancelTransfer = viewModel::cancelTransfer,
          onClearFinished = viewModel::clearFinishedTransfers,
          onClearHistory = viewModel::clearHistory,
          onOpenFile = viewModel::openDownloadedUri,
        )

        AppTab.QuickText -> QuickTextScreen(
          state = state,
          onTextChanged = viewModel::updateQuickTextInput,
          onSendText = viewModel::sendQuickText,
          onShowToast = { msg -> scope.launch { snackbarHostState.showSnackbar(msg) } },
        )
      }
    }
  }
}

@Composable
private fun SwiftTopBar(
  state: MainUiState,
  onDisconnect: () -> Unit,
  onClearPendingShare: () -> Unit,
) {
  Column(
    modifier = Modifier
      .fillMaxWidth()
      .background(SwiftColors.Surface)
      .padding(horizontal = 16.dp, vertical = 10.dp),
    verticalArrangement = Arrangement.spacedBy(8.dp),
  ) {
    Row(
      modifier = Modifier.fillMaxWidth(),
      verticalAlignment = Alignment.CenterVertically,
      horizontalArrangement = Arrangement.SpaceBetween,
    ) {
      Row(verticalAlignment = Alignment.CenterVertically, horizontalArrangement = Arrangement.spacedBy(8.dp)) {
        Surface(color = SwiftColors.Primary, shape = CircleShape) {
          Icon(Icons.Rounded.Bolt, contentDescription = null, tint = Color.White, modifier = Modifier.padding(6.dp).size(18.dp))
        }
        Text("SwiftBeam", style = MaterialTheme.typography.titleLarge)
      }

      when (val conn = state.connection) {
        is ConnectionUiState.Connected -> {
          Surface(
            color = SwiftColors.EmeraldBg,
            shape = CircleShape,
            border = BorderStroke(1.dp, SwiftColors.Emerald.copy(alpha = 0.7f)),
            modifier = Modifier.clickable(onClick = onDisconnect),
          ) {
            Row(
              modifier = Modifier.padding(horizontal = 12.dp, vertical = 6.dp),
              verticalAlignment = Alignment.CenterVertically,
              horizontalArrangement = Arrangement.spacedBy(6.dp),
            ) {
              Box(modifier = Modifier.size(8.dp).background(SwiftColors.Emerald, CircleShape))
              Text(
                "Disconnect • ${conn.metadata.deviceName}",
                color = SwiftColors.Emerald,
                style = MaterialTheme.typography.labelMedium,
                maxLines = 1,
                overflow = TextOverflow.Ellipsis,
              )
            }
          }
        }

        is ConnectionUiState.Connecting -> {
          Surface(
            color = SwiftColors.Primary.copy(alpha = 0.2f),
            shape = CircleShape,
            border = BorderStroke(1.dp, SwiftColors.Primary),
          ) {
            Text(
              "Connecting...",
              color = SwiftColors.TextPrimary,
              style = MaterialTheme.typography.labelMedium,
              modifier = Modifier.padding(horizontal = 12.dp, vertical = 6.dp),
            )
          }
        }

        ConnectionUiState.Disconnected -> {
          Surface(
            color = SwiftColors.SurfaceVariant,
            shape = CircleShape,
            border = BorderStroke(1.dp, SwiftColors.Border),
          ) {
            Text(
              "Not Connected",
              color = SwiftColors.TextSecondary,
              style = MaterialTheme.typography.labelMedium,
              modifier = Modifier.padding(horizontal = 12.dp, vertical = 6.dp),
            )
          }
        }
      }
    }

    if (state.pendingSharedUris.isNotEmpty() || !state.pendingSharedText.isNullOrBlank()) {
      Surface(
        color = SwiftColors.Primary.copy(alpha = 0.16f),
        shape = RoundedCornerShape(12.dp),
        border = BorderStroke(1.dp, SwiftColors.Primary.copy(alpha = 0.5f)),
      ) {
        Row(
          modifier = Modifier.fillMaxWidth().padding(horizontal = 12.dp, vertical = 6.dp),
          verticalAlignment = Alignment.CenterVertically,
        ) {
          val msg = if (state.pendingSharedUris.isNotEmpty()) {
            "Ready to send ${state.pendingSharedUris.size} shared item(s) once connected"
          } else {
            "Ready to send shared text once connected"
          }
          Text(msg, style = MaterialTheme.typography.bodySmall, color = Color.White, modifier = Modifier.weight(1f))
          IconButton(onClick = onClearPendingShare, modifier = Modifier.size(28.dp)) {
            Icon(Icons.Rounded.Close, contentDescription = "Dismiss", tint = SwiftColors.TextSecondary, modifier = Modifier.size(16.dp))
          }
        }
      }
    }
  }
}
