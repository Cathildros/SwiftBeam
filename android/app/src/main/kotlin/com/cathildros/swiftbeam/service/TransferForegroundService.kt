package com.cathildros.swiftbeam.service

import android.app.Notification
import android.app.NotificationChannel
import android.app.NotificationManager
import android.app.PendingIntent
import android.app.Service
import android.content.Context
import android.content.Intent
import android.content.pm.ServiceInfo
import android.os.Build
import android.os.IBinder
import androidx.core.app.NotificationCompat
import com.cathildros.swiftbeam.MainActivity
import com.cathildros.swiftbeam.SwiftBeamApplication
import com.cathildros.swiftbeam.protocol.PathUtils
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.SupervisorJob
import kotlinx.coroutines.cancel
import kotlinx.coroutines.flow.collectLatest
import kotlinx.coroutines.launch

class TransferForegroundService : Service() {
  private val scope = CoroutineScope(SupervisorJob() + Dispatchers.Main.immediate)

  override fun onCreate() {
    super.onCreate()
    ensureNotificationChannels(this)
    val notification = buildProgressNotification("Preparing transfer...", 0, 100, indeterminate = true)
    if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.Q) {
      startForeground(NOTIFICATION_ID_TRANSFER, notification, ServiceInfo.FOREGROUND_SERVICE_TYPE_DATA_SYNC)
    } else {
      startForeground(NOTIFICATION_ID_TRANSFER, notification)
    }

    val coordinator = (application as SwiftBeamApplication).coordinator
    scope.launch {
      coordinator.activeTransfers.collectLatest { items ->
        val running = items.firstOrNull { it.status == TransferStatus.Running }
        val queuedCount = items.count { it.status == TransferStatus.Queued }
        if (running == null && queuedCount == 0) {
          stopForeground(STOP_FOREGROUND_REMOVE)
          stopSelf()
        } else {
          val target = running ?: items.firstOrNull { it.status == TransferStatus.Queued }
          if (target != null) {
            val pct = if (target.totalBytes > 0L) ((target.transferredBytes * 100) / target.totalBytes).toInt().coerceIn(0, 100) else 0
            val dirLabel = if (target.direction == TransferDirection.Upload) "Uploading" else "Downloading"
            val speedStr = PathUtils.formatSpeed(target.bytesPerSec)
            val title = "$dirLabel ${target.fileName}"
            val subtitle = if (queuedCount > 0) "$pct% • $speedStr • +$queuedCount queued" else "$pct% • $speedStr"
            val notif = buildProgressNotification(title, pct, 100, indeterminate = target.totalBytes <= 0L, sub = subtitle)
            val mgr = getSystemService(Context.NOTIFICATION_SERVICE) as NotificationManager
            mgr.notify(NOTIFICATION_ID_TRANSFER, notif)
          }
        }
      }
    }
  }

  override fun onStartCommand(intent: Intent?, flags: Int, startId: Int): Int {
    if (intent?.action == ACTION_CANCEL_ALL) {
      (application as SwiftBeamApplication).coordinator.cancelAllTransfers()
      stopForeground(STOP_FOREGROUND_REMOVE)
      stopSelf()
      return START_NOT_STICKY
    }
    return START_STICKY
  }

  override fun onDestroy() {
    scope.cancel()
    super.onDestroy()
  }

  override fun onBind(intent: Intent?): IBinder? = null

  private fun buildProgressNotification(
    title: String,
    progress: Int,
    max: Int,
    indeterminate: Boolean,
    sub: String = "",
  ): Notification {
    val openIntent = PendingIntent.getActivity(
      this,
      1,
      Intent(this, MainActivity::class.java),
      PendingIntent.FLAG_UPDATE_CURRENT or PendingIntent.FLAG_IMMUTABLE,
    )
    val cancelIntent = PendingIntent.getService(
      this,
      2,
      Intent(this, TransferForegroundService::class.java).apply { action = ACTION_CANCEL_ALL },
      PendingIntent.FLAG_UPDATE_CURRENT or PendingIntent.FLAG_IMMUTABLE,
    )

    return NotificationCompat.Builder(this, CHANNEL_TRANSFERS)
      .setSmallIcon(android.R.drawable.stat_sys_upload)
      .setContentTitle(title)
      .setContentText(sub.ifEmpty { "SwiftBeam active transfer" })
      .setOngoing(true)
      .setOnlyAlertOnce(true)
      .setContentIntent(openIntent)
      .setProgress(max, progress, indeterminate)
      .addAction(android.R.drawable.ic_menu_close_clear_cancel, "Cancel", cancelIntent)
      .build()
  }

  companion object {
    const val CHANNEL_TRANSFERS = "swiftbeam_transfers"
    const val CHANNEL_TEXT = "swiftbeam_text"
    const val NOTIFICATION_ID_TRANSFER = 4501
    const val ACTION_CANCEL_ALL = "com.cathildros.swiftbeam.ACTION_CANCEL_ALL"

    fun ensureNotificationChannels(context: Context) {
      if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.O) {
        val mgr = context.getSystemService(Context.NOTIFICATION_SERVICE) as NotificationManager
        mgr.createNotificationChannel(
          NotificationChannel(CHANNEL_TRANSFERS, "File Transfers", NotificationManager.IMPORTANCE_LOW).apply {
            description = "Live progress for background uploads and downloads"
          },
        )
        mgr.createNotificationChannel(
          NotificationChannel(CHANNEL_TEXT, "Quick Text Clips", NotificationManager.IMPORTANCE_HIGH).apply {
            description = "Notifications when your PC sends Quick Text"
          },
        )
      }
    }

    fun start(context: Context) {
      ensureNotificationChannels(context)
      val intent = Intent(context, TransferForegroundService::class.java)
      if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.O) {
        context.startForegroundService(intent)
      } else {
        context.startService(intent)
      }
    }
  }
}
