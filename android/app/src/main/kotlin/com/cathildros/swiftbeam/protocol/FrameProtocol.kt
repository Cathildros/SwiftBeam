package com.cathildros.swiftbeam.protocol

import java.io.EOFException
import java.io.IOException
import java.io.InputStream
import java.io.OutputStream
import kotlinx.serialization.KSerializer

class RemoteProtocolException(message: String) : IOException(message)

object FrameProtocol {
  fun writeFrame(
    out: OutputStream,
    type: FrameType,
    payload: ByteArray,
    payloadLength: Int = payload.size,
    flush: Boolean = true,
  ) {
    val header = byteArrayOf(
      type.code,
      ((payloadLength ushr 24) and 0xFF).toByte(),
      ((payloadLength ushr 16) and 0xFF).toByte(),
      ((payloadLength ushr 8) and 0xFF).toByte(),
      (payloadLength and 0xFF).toByte(),
    )
    out.write(header)
    if (payloadLength > 0) {
      out.write(payload, 0, payloadLength)
    }
    if (flush) {
      out.flush()
    }
  }

  fun <T> writeJsonFrame(
    out: OutputStream,
    type: FrameType,
    serializer: KSerializer<T>,
    payload: T,
  ) {
    val json = ProtocolJson.encodeToString(serializer, payload)
    writeFrame(out, type, json.toByteArray(Charsets.UTF_8), flush = true)
  }

  fun readFrameHeader(inp: InputStream): Pair<FrameType, Int>? {
    val header = ByteArray(5)
    var totalRead = 0
    while (totalRead < 5) {
      val n = inp.read(header, totalRead, 5 - totalRead)
      if (n < 0) {
        if (totalRead == 0) return null
        throw EOFException("Connection closed mid-frame header.")
      }
      totalRead += n
    }

    val type = FrameType.fromCode(header[0])
    val length = ((header[1].toInt() and 0xFF) shl 24) or
      ((header[2].toInt() and 0xFF) shl 16) or
      ((header[3].toInt() and 0xFF) shl 8) or
      (header[4].toInt() and 0xFF)

    if (length < 0 || length > ProtocolConstants.MAX_CONTROL_FRAME_BYTES) {
      throw IOException("Invalid frame length: $length")
    }
    return type to length
  }

  fun readExact(
    inp: InputStream,
    buffer: ByteArray,
    offset: Int = 0,
    length: Int = buffer.size - offset,
    onBytesRead: ((Int) -> Unit)? = null,
  ) {
    var pos = 0
    while (pos < length) {
      val n = inp.read(buffer, offset + pos, length - pos)
      if (n < 0) {
        throw EOFException("Connection closed unexpectedly while reading frame payload.")
      }
      pos += n
      if (n > 0) {
        onBytesRead?.invoke(n)
      }
    }
  }

  fun readFrame(inp: InputStream): Pair<FrameType, ByteArray>? {
    val header = readFrameHeader(inp) ?: return null
    val (type, length) = header
    val payload = if (length == 0) ByteArray(0) else ByteArray(length)
    if (length > 0) {
      readExact(inp, payload, 0, length)
    }
    return type to payload
  }

  fun <T> readJsonFrame(
    inp: InputStream,
    expectedType: FrameType,
    deserializer: KSerializer<T>,
  ): T {
    val frame = readFrame(inp)
      ?: throw EOFException("Connection closed while waiting for $expectedType.")
    val (type, payload) = frame

    if (type == FrameType.Error) {
      val err = ProtocolJson.decodeFromString(ErrorMessage.serializer(), payload.toString(Charsets.UTF_8))
      throw RemoteProtocolException("Remote error: ${err.message}")
    }

    if (type != expectedType) {
      throw IOException("Expected frame $expectedType, got $type.")
    }

    return ProtocolJson.decodeFromString(deserializer, payload.toString(Charsets.UTF_8))
  }
}
