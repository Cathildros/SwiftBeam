package com.cathildros.swiftbeam.ui.screens

import android.view.ViewGroup
import androidx.camera.core.CameraSelector
import androidx.camera.core.ImageAnalysis
import androidx.camera.core.ImageProxy
import androidx.camera.core.Preview
import androidx.camera.lifecycle.ProcessCameraProvider
import androidx.camera.view.PreviewView
import androidx.compose.foundation.BorderStroke
import androidx.compose.foundation.background
import androidx.compose.foundation.border
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.shape.CircleShape
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.material3.Button
import androidx.compose.material3.ButtonDefaults
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Surface
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.runtime.DisposableEffect
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.unit.dp
import androidx.compose.ui.viewinterop.AndroidView
import androidx.compose.ui.window.Dialog
import androidx.compose.ui.window.DialogProperties
import androidx.core.content.ContextCompat
import androidx.lifecycle.compose.LocalLifecycleOwner
import com.cathildros.swiftbeam.ui.theme.SwiftColors
import com.google.zxing.BarcodeFormat
import com.google.zxing.BinaryBitmap
import com.google.zxing.DecodeHintType
import com.google.zxing.MultiFormatReader
import com.google.zxing.PlanarYUVLuminanceSource
import com.google.zxing.common.HybridBinarizer
import java.util.concurrent.Executors

@Composable
fun QrScannerDialog(
  onQrScanned: (String) -> Unit,
  onDismiss: () -> Unit,
) {
  val context = LocalContext.current
  val lifecycleOwner = LocalLifecycleOwner.current
  val executor = remember { Executors.newSingleThreadExecutor() }
  var delivered by remember { mutableStateOf(false) }

  DisposableEffect(Unit) {
    onDispose { executor.shutdownNow() }
  }

  Dialog(
    onDismissRequest = onDismiss,
    properties = DialogProperties(usePlatformDefaultWidth = false),
  ) {
    Box(
      modifier = Modifier
        .fillMaxSize()
        .background(Color.Black),
    ) {
      AndroidView(
        modifier = Modifier.fillMaxSize(),
        factory = { ctx ->
          val previewView = PreviewView(ctx).apply {
            layoutParams = ViewGroup.LayoutParams(
              ViewGroup.LayoutParams.MATCH_PARENT,
              ViewGroup.LayoutParams.MATCH_PARENT,
            )
            scaleType = PreviewView.ScaleType.FILL_CENTER
          }

          val cameraProviderFuture = ProcessCameraProvider.getInstance(ctx)
          cameraProviderFuture.addListener(
            {
              runCatching {
                val cameraProvider = cameraProviderFuture.get()
                val preview = Preview.Builder().build().also {
                  it.surfaceProvider = previewView.surfaceProvider
                }
                val analysis = ImageAnalysis.Builder()
                  .setBackpressureStrategy(ImageAnalysis.STRATEGY_KEEP_ONLY_LATEST)
                  .build()
                val reader = MultiFormatReader().apply {
                  setHints(mapOf(DecodeHintType.POSSIBLE_FORMATS to listOf(BarcodeFormat.QR_CODE)))
                }
                analysis.setAnalyzer(executor) { imageProxy ->
                  try {
                    val decoded = decodeQrFromProxy(reader, imageProxy)
                    if (decoded != null && !delivered) {
                      delivered = true
                      ContextCompat.getMainExecutor(ctx).execute { onQrScanned(decoded) }
                    }
                  } finally {
                    imageProxy.close()
                  }
                }
                cameraProvider.unbindAll()
                cameraProvider.bindToLifecycle(
                  lifecycleOwner,
                  CameraSelector.DEFAULT_BACK_CAMERA,
                  preview,
                  analysis,
                )
              }
            },
            ContextCompat.getMainExecutor(ctx),
          )
          previewView
        },
      )

      Column(
        modifier = Modifier
          .fillMaxSize()
          .padding(24.dp),
        horizontalAlignment = Alignment.CenterHorizontally,
        verticalArrangement = Arrangement.SpaceBetween,
      ) {
        Surface(
          color = SwiftColors.Background.copy(alpha = 0.85f),
          shape = CircleShape,
          border = BorderStroke(1.dp, SwiftColors.Border),
        ) {
          Text(
            "Point at SwiftBeam's QR code on your PC",
            color = SwiftColors.TextPrimary,
            style = MaterialTheme.typography.bodyMedium,
            modifier = Modifier.padding(horizontal = 16.dp, vertical = 10.dp),
          )
        }

        Box(
          modifier = Modifier
            .size(240.dp)
            .border(3.dp, SwiftColors.Primary, RoundedCornerShape(24.dp)),
        )

        Button(
          onClick = onDismiss,
          shape = CircleShape,
          colors = ButtonDefaults.buttonColors(containerColor = SwiftColors.SurfaceVariant),
          modifier = Modifier.fillMaxWidth(0.55f).height(48.dp),
        ) {
          Text("Cancel", color = Color.White)
        }
      }
    }
  }
}

private fun decodeQrFromProxy(reader: MultiFormatReader, image: ImageProxy): String? {
  val plane = image.planes.firstOrNull() ?: return null
  val buffer = plane.buffer
  val data = ByteArray(buffer.remaining())
  buffer.get(data)
  val source = PlanarYUVLuminanceSource(
    data,
    plane.rowStride,
    image.height,
    0,
    0,
    minOf(image.width, plane.rowStride),
    image.height,
    false,
  )
  val bitmap = BinaryBitmap(HybridBinarizer(source))
  return runCatching { reader.decodeWithState(bitmap).text }.getOrNull()
}
