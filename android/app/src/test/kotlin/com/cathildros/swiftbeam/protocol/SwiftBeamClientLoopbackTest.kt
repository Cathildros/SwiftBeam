package com.cathildros.swiftbeam.protocol

import java.io.BufferedInputStream
import java.io.BufferedOutputStream
import java.io.ByteArrayInputStream
import java.io.ByteArrayOutputStream
import java.net.InetAddress
import java.net.ServerSocket
import java.security.MessageDigest
import java.util.Base64
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.launch
import kotlinx.coroutines.runBlocking
import org.junit.Assert.assertArrayEquals
import org.junit.Assert.assertEquals
import org.junit.Assert.assertTrue
import org.junit.Assert.fail
import org.junit.Test

class SwiftBeamClientLoopbackTest {
  @Test
  fun clientConnectsListsPushesPullsRenamesDeletesAndSendsText() = runBlocking {
    val expectedPin = "654321"
    val storedFiles = LinkedHashMap<String, ByteArray>().apply {
      put("hello.txt", "Hello from SwiftBeam Desktop!".toByteArray(Charsets.UTF_8))
    }
    var receivedText: SendTextRequestMessage? = null
    var createdDir: String? = null
    var renamedPair: Pair<String, String>? = null
    var deletedPath: String? = null

    val server = ServerSocket(0, 4, InetAddress.getByName("127.0.0.1"))
    val serverJob = launch(Dispatchers.IO) {
      while (!server.isClosed) {
        val sock = runCatching { server.accept() }.getOrNull() ?: break
        sock.use { s ->
          val inp = BufferedInputStream(s.getInputStream())
          val out = BufferedOutputStream(s.getOutputStream())

          val magic = ByteArray(4)
          FrameProtocol.readExact(inp, magic)
          if (!magic.contentEquals(ProtocolConstants.MAGIC_PLAIN)) {
            out.write(byteArrayOf(ProtocolConstants.PREAMBLE_INVALID.toByte()))
            out.flush()
            return@use
          }
          out.write(byteArrayOf(ProtocolConstants.PREAMBLE_OK))
          out.flush()

          val serverNonce = PinAuth.generateNonce()
          FrameProtocol.writeJsonFrame(
            out,
            FrameType.AuthChallenge,
            AuthChallengeMessage.serializer(),
            AuthChallengeMessage(
              serverId = "srv-1",
              deviceName = "TestPC",
              osPlatform = "Windows",
              serverNonceB64 = Base64.getEncoder().encodeToString(serverNonce),
              requiresPin = true,
              tlsActive = false,
              certFingerprint = null,
            ),
          )

          val authResp = FrameProtocol.readJsonFrame(inp, FrameType.AuthResponse, AuthResponseMessage.serializer())
          val clientNonce = Base64.getDecoder().decode(authResp.clientNonceB64)
          val proof = Base64.getDecoder().decode(authResp.proofB64)
          if (!PinAuth.verifyProof(expectedPin, serverNonce, clientNonce, proof, null)) {
            FrameProtocol.writeJsonFrame(
              out,
              FrameType.AuthResult,
              AuthResultMessage.serializer(),
              AuthResultMessage(success = false, error = "Invalid PIN"),
            )
            return@use
          }

          FrameProtocol.writeJsonFrame(
            out,
            FrameType.AuthResult,
            AuthResultMessage.serializer(),
            AuthResultMessage(success = true, sharedRootName = "Shared"),
          )

          var pendingUploadPath: String? = null
          var pendingUploadBytes: ByteArray? = null

          while (true) {
            val frame = FrameProtocol.readFrame(inp) ?: break
            when (frame.first) {
              FrameType.Ping -> FrameProtocol.writeFrame(out, FrameType.Pong, ByteArray(0))

              FrameType.ListDirRequest -> {
                val list = storedFiles.entries.map { (k, v) ->
                  RemoteEntry(k, k, isDirectory = false, size = v.size.toLong(), lastModifiedUtc = "2026-11-26T00:00:00Z")
                }
                FrameProtocol.writeJsonFrame(
                  out,
                  FrameType.ListDirResponse,
                  ListDirResponseMessage.serializer(),
                  ListDirResponseMessage("", list),
                )
              }

              FrameType.CreateDirRequest -> {
                val req = ProtocolJson.decodeFromString(CreateDirRequestMessage.serializer(), frame.second.toString(Charsets.UTF_8))
                createdDir = req.relativePath
                FrameProtocol.writeJsonFrame(out, FrameType.CreateDirResponse, CreateDirResponseMessage.serializer(), CreateDirResponseMessage(true))
              }

              FrameType.PushChunkRequest -> {
                val req = ProtocolJson.decodeFromString(PushChunkRequestMessage.serializer(), frame.second.toString(Charsets.UTF_8))
                val buf = ByteArrayOutputStream()
                while (true) {
                  val df = FrameProtocol.readFrame(inp) ?: break
                  if (df.first == FrameType.DataEnd) break
                  buf.write(df.second)
                }
                pendingUploadPath = req.relativePath
                pendingUploadBytes = buf.toByteArray()
                FrameProtocol.writeJsonFrame(out, FrameType.PushChunkAck, PushChunkAckMessage.serializer(), PushChunkAckMessage(true))
              }

              FrameType.PushFinalizeRequest -> {
                val req = ProtocolJson.decodeFromString(PushFinalizeRequestMessage.serializer(), frame.second.toString(Charsets.UTF_8))
                val uploaded = pendingUploadBytes ?: ByteArray(0)
                val sha = MessageDigest.getInstance("SHA-256").digest(uploaded).joinToString("") { "%02x".format(it) }
                assertEquals(req.expectedSha256Hex, sha)
                storedFiles[pendingUploadPath ?: req.relativePath] = uploaded
                FrameProtocol.writeJsonFrame(out, FrameType.PushFinalizeAck, PushFinalizeAckMessage.serializer(), PushFinalizeAckMessage(true))
              }

              FrameType.PullRangeRequest -> {
                val req = ProtocolJson.decodeFromString(PullRangeRequestMessage.serializer(), frame.second.toString(Charsets.UTF_8))
                val data = storedFiles[req.relativePath] ?: ByteArray(0)
                FrameProtocol.writeJsonFrame(
                  out,
                  FrameType.PullRangeHeader,
                  PullRangeHeaderMessage.serializer(),
                  PullRangeHeaderMessage(true, data.size.toLong(), "2026-11-26T00:00:00Z", 0L, data.size.toLong()),
                )
                FrameProtocol.writeFrame(out, FrameType.DataChunk, data, flush = false)
                FrameProtocol.writeFrame(out, FrameType.DataEnd, ByteArray(0), flush = true)
              }

              FrameType.RenamePathRequest -> {
                val req = ProtocolJson.decodeFromString(RenamePathRequestMessage.serializer(), frame.second.toString(Charsets.UTF_8))
                renamedPair = req.relativePath to req.newName
                FrameProtocol.writeJsonFrame(
                  out,
                  FrameType.RenamePathResponse,
                  RenamePathResponseMessage.serializer(),
                  RenamePathResponseMessage(true, req.newName),
                )
              }

              FrameType.DeletePathRequest -> {
                val req = ProtocolJson.decodeFromString(DeletePathRequestMessage.serializer(), frame.second.toString(Charsets.UTF_8))
                deletedPath = req.relativePath
                FrameProtocol.writeJsonFrame(out, FrameType.DeletePathResponse, DeletePathResponseMessage.serializer(), DeletePathResponseMessage(true))
              }

              FrameType.SendTextRequest -> {
                receivedText = ProtocolJson.decodeFromString(SendTextRequestMessage.serializer(), frame.second.toString(Charsets.UTF_8))
                FrameProtocol.writeJsonFrame(out, FrameType.SendTextAck, SendTextAckMessage.serializer(), SendTextAckMessage(true))
              }

              else -> break
            }
          }
        }
      }
    }

    try {
      val badClient = SwiftBeamClient()
      try {
        badClient.connect(ConnectionTarget("127.0.0.1", server.localPort, "000000", useTls = false, clientName = "Pixel"))
        fail("Expected invalid PIN to throw SecurityException")
      } catch (_: SecurityException) {
        // expected
      }

      val client = SwiftBeamClient()
      val meta = client.connect(ConnectionTarget("127.0.0.1", server.localPort, expectedPin, useTls = false, clientName = "Pixel"))
      assertEquals("TestPC", meta.deviceName)
      assertTrue(client.ping())

      val listResp = client.listDirectory("")
      assertEquals(1, listResp.entries.size)
      assertEquals("hello.txt", listResp.entries[0].name)

      val downloaded = ByteArrayOutputStream()
      client.pullFile("hello.txt", downloaded)
      assertEquals("Hello from SwiftBeam Desktop!", downloaded.toString(Charsets.UTF_8.name()))

      val uploadBytes = ByteArray(1024 * 600) { i -> (i % 251).toByte() }
      client.pushFile("photos/pic.bin", ByteArrayInputStream(uploadBytes), uploadBytes.size.toLong(), "2026-11-26T00:00:00Z")
      assertArrayEquals(uploadBytes, storedFiles["photos/pic.bin"])

      assertTrue(client.createDirectory("Vacation").success)
      assertEquals("Vacation", createdDir)

      assertTrue(client.renamePath("hello.txt", "renamed.txt").success)
      assertEquals("hello.txt" to "renamed.txt", renamedPair)

      assertTrue(client.deletePath("renamed.txt").success)
      assertEquals("renamed.txt", deletedPath)

      assertTrue(client.sendText("Meet at 5pm", "Pixel").success)
      assertEquals("Meet at 5pm", receivedText?.text)

      client.disconnect()
    } finally {
      server.close()
      serverJob.cancel()
    }
  }
}
