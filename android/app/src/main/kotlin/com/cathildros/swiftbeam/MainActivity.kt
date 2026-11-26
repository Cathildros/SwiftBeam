package com.cathildros.swiftbeam

import android.Manifest
import android.content.Intent
import android.content.pm.PackageManager
import android.graphics.Color
import android.net.Uri
import android.os.Build
import android.os.Bundle
import androidx.activity.ComponentActivity
import androidx.activity.SystemBarStyle
import androidx.activity.compose.setContent
import androidx.activity.enableEdgeToEdge
import androidx.activity.result.PickVisualMediaRequest
import androidx.activity.result.contract.ActivityResultContracts
import androidx.activity.viewModels
import androidx.core.content.ContextCompat
import com.cathildros.swiftbeam.ui.MainViewModel
import com.cathildros.swiftbeam.ui.SwiftBeamApp
import com.cathildros.swiftbeam.ui.theme.SwiftBeamTheme

class MainActivity : ComponentActivity() {
  private val viewModel: MainViewModel by viewModels()

  private val permissionLauncher = registerForActivityResult(
    ActivityResultContracts.RequestMultiplePermissions(),
  ) {
    viewModel.setNearbyPermissionGranted(hasNearbyWifiPermission())
  }

  private val pickMediaLauncher = registerForActivityResult(
    ActivityResultContracts.PickMultipleVisualMedia(),
  ) { uris ->
    if (uris.isNotEmpty()) {
      viewModel.uploadUris(uris)
    }
  }

  private val pickFilesLauncher = registerForActivityResult(
    ActivityResultContracts.OpenMultipleDocuments(),
  ) { uris ->
    if (uris.isNotEmpty()) {
      viewModel.uploadUris(uris)
    }
  }

  override fun onCreate(savedInstanceState: Bundle?) {
    super.onCreate(savedInstanceState)
    enableEdgeToEdge(
      statusBarStyle = SystemBarStyle.dark(Color.parseColor("#0B1120")),
      navigationBarStyle = SystemBarStyle.dark(Color.parseColor("#0B1120")),
    )

    viewModel.setNearbyPermissionGranted(hasNearbyWifiPermission())
    requestMissingPermissions()
    handleShareIntent(intent)

    setContent {
      SwiftBeamTheme {
        SwiftBeamApp(
          viewModel = viewModel,
          onPickPhotos = {
            pickMediaLauncher.launch(PickVisualMediaRequest(ActivityResultContracts.PickVisualMedia.ImageAndVideo))
          },
          onPickFiles = {
            pickFilesLauncher.launch(arrayOf("*/*"))
          },
          onRequestNearbyPermission = { requestMissingPermissions(force = true) },
        )
      }
    }
  }

  override fun onNewIntent(intent: Intent) {
    super.onNewIntent(intent)
    setIntent(intent)
    handleShareIntent(intent)
  }

  private fun hasNearbyWifiPermission(): Boolean {
    return if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.TIRAMISU) {
      ContextCompat.checkSelfPermission(this, Manifest.permission.NEARBY_WIFI_DEVICES) == PackageManager.PERMISSION_GRANTED
    } else {
      true
    }
  }

  private fun requestMissingPermissions(force: Boolean = false) {
    if (Build.VERSION.SDK_INT < Build.VERSION_CODES.TIRAMISU) return
    val needed = buildList {
      if (force || ContextCompat.checkSelfPermission(this@MainActivity, Manifest.permission.NEARBY_WIFI_DEVICES) != PackageManager.PERMISSION_GRANTED) {
        add(Manifest.permission.NEARBY_WIFI_DEVICES)
      }
      if (ContextCompat.checkSelfPermission(this@MainActivity, Manifest.permission.POST_NOTIFICATIONS) != PackageManager.PERMISSION_GRANTED) {
        add(Manifest.permission.POST_NOTIFICATIONS)
      }
    }
    if (needed.isNotEmpty()) {
      permissionLauncher.launch(needed.toTypedArray())
    }
  }

  private fun handleShareIntent(intent: Intent?) {
    if (intent == null) return
    when (intent.action) {
      Intent.ACTION_SEND -> {
        @Suppress("DEPRECATION")
        val streamUri = if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.TIRAMISU) {
          intent.getParcelableExtra(Intent.EXTRA_STREAM, Uri::class.java)
        } else {
          intent.getParcelableExtra(Intent.EXTRA_STREAM)
        }
        val text = intent.getStringExtra(Intent.EXTRA_TEXT)
        if (streamUri != null || !text.isNullOrBlank()) {
          viewModel.handleIncomingShare(listOfNotNull(streamUri), text)
          intent.action = null
        }
      }

      Intent.ACTION_SEND_MULTIPLE -> {
        @Suppress("DEPRECATION")
        val uris = if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.TIRAMISU) {
          intent.getParcelableArrayListExtra(Intent.EXTRA_STREAM, Uri::class.java)
        } else {
          intent.getParcelableArrayListExtra(Intent.EXTRA_STREAM)
        }.orEmpty()
        val text = intent.getStringExtra(Intent.EXTRA_TEXT)
        if (uris.isNotEmpty() || !text.isNullOrBlank()) {
          viewModel.handleIncomingShare(uris, text)
          intent.action = null
        }
      }
    }
  }
}
