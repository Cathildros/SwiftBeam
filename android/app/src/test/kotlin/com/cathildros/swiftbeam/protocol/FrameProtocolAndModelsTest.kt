package com.cathildros.swiftbeam.protocol

import java.io.ByteArrayInputStream
import java.io.ByteArrayOutputStream
import java.io.IOException
import org.junit.Assert.assertArrayEquals
import org.junit.Assert.assertEquals
import org.junit.Assert.assertNull
import org.junit.Assert.assertThrows
import org.junit.Test

class FrameProtocolAndModelsTest {
  @Test
  fun frameRoundTripPreservesTypeBigEndianLengthAndPayload() {
    val out = ByteArrayOutputStream()
    val msg = ListDirRequestMessage(relativePath = "C:/Users/Test", recursive = true, includeHidden = false)
    FrameProtocol.writeJsonFrame(out, FrameType.ListDirRequest, ListDirRequestMessage.serializer(), msg)
    FrameProtocol.writeFrame(out, FrameType.Ping, ByteArray(0))

    val bytes = out.toByteArray()
    assertEquals(FrameType.ListDirRequest.code, bytes[0])

    val inp = ByteArrayInputStream(bytes)
    val decoded = FrameProtocol.readJsonFrame(inp, FrameType.ListDirRequest, ListDirRequestMessage.serializer())
    assertEquals(msg, decoded)

    val ping = FrameProtocol.readFrame(inp)
    assertEquals(FrameType.Ping, ping?.first)
    assertArrayEquals(ByteArray(0), ping?.second)
    assertNull(FrameProtocol.readFrame(inp))
  }

  @Test
  fun errorFrameThrowsRemoteProtocolException() {
    val out = ByteArrayOutputStream()
    FrameProtocol.writeJsonFrame(out, FrameType.Error, ErrorMessage.serializer(), ErrorMessage("Access denied"))
    val inp = ByteArrayInputStream(out.toByteArray())

    val ex = assertThrows(RemoteProtocolException::class.java) {
      FrameProtocol.readJsonFrame(inp, FrameType.ListDirResponse, ListDirResponseMessage.serializer())
    }
    assertEquals("Remote error: Access denied", ex.message)
  }

  @Test
  fun invalidFrameLengthThrowsIoException() {
    val badHeader = byteArrayOf(FrameType.DataChunk.code, 0x7F, 0x00, 0x00, 0x00)
    assertThrows(IOException::class.java) {
      FrameProtocol.readFrameHeader(ByteArrayInputStream(badHeader))
    }
  }

  @Test
  fun pathUtilsHandlesWindowsLinuxSharedAndPortalUrls() {
    assertEquals("sub/photo.jpg", PathUtils.joinRemotePath("sub", "photo.jpg", fullFilesystem = false))
    assertEquals("C:/Users/Alice/file.txt", PathUtils.joinRemotePath("C:/Users/Alice", "file.txt", fullFilesystem = true))
    assertEquals("C:/file.txt", PathUtils.joinRemotePath("C:/", "file.txt", fullFilesystem = true))
    assertEquals("/home/alice/file.txt", PathUtils.joinRemotePath("/home/alice", "file.txt", fullFilesystem = true))

    assertEquals(ProtocolConstants.DRIVES_VIRTUAL_PATH, PathUtils.parentRemotePath("C:/", fullFilesystem = true))
    assertEquals("C:/", PathUtils.parentRemotePath("C:/Users", fullFilesystem = true))
    assertEquals("C:/Users", PathUtils.parentRemotePath("C:/Users/Alice", fullFilesystem = true))
    assertNull(PathUtils.parentRemotePath("", fullFilesystem = false))
    assertEquals("", PathUtils.parentRemotePath("folder", fullFilesystem = false))

    assertEquals("10.0.0.250" to 45890, PathUtils.parseHostInput("http://10.0.0.250:45893/?qr=9108f0e"))
    assertEquals("192.168.1.44" to 50000, PathUtils.parseHostInput("192.168.1.44:50000"))
    assertEquals("fe80::1" to 45890, PathUtils.parseHostInput("[fe80::1]:45890"))
    assertEquals("fe80::1" to null, PathUtils.parseHostInput("fe80::1"))
  }
}
