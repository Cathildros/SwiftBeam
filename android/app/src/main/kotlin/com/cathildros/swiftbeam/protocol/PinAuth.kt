package com.cathildros.swiftbeam.protocol

import java.security.MessageDigest
import java.security.SecureRandom
import javax.crypto.Mac
import javax.crypto.spec.SecretKeySpec

object PinAuth {
  private val secureRandom = SecureRandom()

  fun normalizePin(pin: String?): String {
    if (pin.isNullOrBlank()) return ""
    val sb = StringBuilder(pin.length)
    for (c in pin) {
      if (c.isLetterOrDigit()) sb.append(c)
    }
    return sb.toString()
  }

  fun formatPin(pin: String): String {
    val normalized = normalizePin(pin)
    return if (normalized.length == 6) {
      "${normalized.substring(0, 3)}-${normalized.substring(3)}"
    } else {
      normalized
    }
  }

  fun generateNonce(size: Int = 32): ByteArray {
    val bytes = ByteArray(size)
    secureRandom.nextBytes(bytes)
    return bytes
  }

  fun computeProof(
    pin: String,
    serverNonce: ByteArray,
    clientNonce: ByteArray,
    tlsCertHash: ByteArray? = null,
  ): ByteArray {
    val normalized = normalizePin(pin)
    if (normalized.isEmpty()) {
      throw IllegalArgumentException("PIN is required.")
    }
    val key = normalized.toByteArray(Charsets.UTF_8)
    val certLen = tlsCertHash?.size ?: 0
    val payload = ByteArray(serverNonce.size + clientNonce.size + certLen)
    System.arraycopy(serverNonce, 0, payload, 0, serverNonce.size)
    System.arraycopy(clientNonce, 0, payload, serverNonce.size, clientNonce.size)
    if (tlsCertHash != null && certLen > 0) {
      System.arraycopy(tlsCertHash, 0, payload, serverNonce.size + clientNonce.size, certLen)
    }

    val mac = Mac.getInstance("HmacSHA256")
    mac.init(SecretKeySpec(key, "HmacSHA256"))
    return mac.doFinal(payload)
  }

  fun verifyProof(
    expectedPin: String,
    serverNonce: ByteArray,
    clientNonce: ByteArray,
    providedProof: ByteArray,
    tlsCertHash: ByteArray? = null,
  ): Boolean {
    if (providedProof.size != 32) return false
    val expected = computeProof(expectedPin, serverNonce, clientNonce, tlsCertHash)
    return MessageDigest.isEqual(expected, providedProof)
  }
}
