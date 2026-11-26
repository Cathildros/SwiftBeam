package com.cathildros.swiftbeam.server

import com.cathildros.swiftbeam.protocol.ConnectionTarget
import com.cathildros.swiftbeam.protocol.SwiftBeamClient
import java.io.ByteArrayInputStream
import java.io.ByteArrayOutputStream
import java.io.File
import java.nio.file.Files
import kotlinx.coroutines.runBlocking
import org.junit.Assert.assertArrayEquals
import org.junit.Assert.assertEquals
import org.junit.Assert.assertTrue
import org.junit.Test

class PhoneFileShareServerTest {
  @Test
  fun tlsPhoneServerSupportsMutualTokenListingUploadResumePreviewAndQuickText() = runBlocking {
    val tempRoot = Files.createTempDirectory("sb_phone_srv_").toFile()
    var receivedText = ""
    var receivedSender = ""

    val server = PhoneFileShareServer(
      storageProvider = { DirectoryStorageProvider(tempRoot, "Phone Shared") },
      deviceName = "PixelTest",
      requestedPort = 0,
      pin = "654321",
      requirePin = true,
      onTextReceived = { text, sender ->
        receivedText = text
        receivedSender = sender
      },
    )

    try {
      server.start()
      assertTrue(server.isRunning)
      assertTrue(server.listeningPort > 0)

      val sampleFile = File(tempRoot, "hello.txt").apply { writeText("Hello TLS 1.3 Phone Server!") }

      val client = SwiftBeamClient()
      val meta = client.connect(
        ConnectionTarget(
          host = "127.0.0.1",
          port = server.listeningPort,
          pin = "654321",
          useTls = true,
          clientName = "DesktopPC",
        ),
      )
      assertEquals("PixelTest", meta.deviceName)
      assertTrue(meta.tlsActive)
      assertEquals("Phone Shared", meta.sharedRootName)

      val list = client.listDirectory("")
      assertEquals(1, list.entries.size)
      assertEquals("hello.txt", list.entries[0].name)

      val (previewBytes, previewTotal) = client.pullRangeBytes("hello.txt", maxBytes = 5)
      assertEquals("Hello", previewBytes.toString(Charsets.UTF_8))
      assertEquals(sampleFile.length(), previewTotal)

      val resumedOut = ByteArrayOutputStream()
      client.pullFileRange("hello.txt", startOffset = 6L, destination = resumedOut)
      assertEquals("TLS 1.3 Phone Server!", resumedOut.toString(Charsets.UTF_8))

      val payload = ByteArray(256 * 1024) { i -> (i % 199).toByte() }
      val fin = client.pushFile("camera/img.bin", ByteArrayInputStream(payload), payload.size.toLong(), "2026-11-26T00:00:00Z")
      assertTrue(fin.success)
      assertArrayEquals(payload, File(tempRoot, "camera/img.bin").readBytes())

      assertTrue(client.sendText("Copied link from PC", "Workstation").success)
      assertEquals("Copied link from PC", receivedText)
      assertEquals("Workstation", receivedSender)

      client.disconnect()
    } finally {
      server.stop()
      tempRoot.deleteRecursively()
    }
  }
}
