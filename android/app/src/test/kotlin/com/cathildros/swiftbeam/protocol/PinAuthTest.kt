package com.cathildros.swiftbeam.protocol

import javax.crypto.Mac
import javax.crypto.spec.SecretKeySpec
import org.junit.Assert.assertArrayEquals
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertTrue
import org.junit.Test

class PinAuthTest {
  @Test
  fun normalizeAndFormatPin() {
    assertEquals("482915", PinAuth.normalizePin("482-915"))
    assertEquals("482915", PinAuth.normalizePin(" 482 915 "))
    assertEquals("", PinAuth.normalizePin("   "))
    assertEquals("482-915", PinAuth.formatPin("482915"))
  }

  @Test
  fun computeProofMatchesExpectedHmacSha256WithAndWithoutCertHash() {
    val serverNonce = ByteArray(32) { i -> (i + 1).toByte() }
    val clientNonce = ByteArray(32) { i -> (i + 33).toByte() }
    val certHash = ByteArray(32) { i -> (0xA0 + i).toByte() }

    val proofPlain = PinAuth.computeProof("482-915", serverNonce, clientNonce, null)
    val expectedPlain = Mac.getInstance("HmacSHA256").run {
      init(SecretKeySpec("482915".toByteArray(Charsets.UTF_8), "HmacSHA256"))
      doFinal(serverNonce + clientNonce)
    }
    assertArrayEquals(expectedPlain, proofPlain)
    assertTrue(PinAuth.verifyProof("482915", serverNonce, clientNonce, proofPlain, null))
    assertFalse(PinAuth.verifyProof("000000", serverNonce, clientNonce, proofPlain, null))

    val proofTls = PinAuth.computeProof("482915", serverNonce, clientNonce, certHash)
    val expectedTls = Mac.getInstance("HmacSHA256").run {
      init(SecretKeySpec("482915".toByteArray(Charsets.UTF_8), "HmacSHA256"))
      doFinal(serverNonce + clientNonce + certHash)
    }
    assertArrayEquals(expectedTls, proofTls)
    assertTrue(PinAuth.verifyProof("482915", serverNonce, clientNonce, proofTls, certHash))
    assertFalse(PinAuth.verifyProof("482915", serverNonce, clientNonce, proofTls, null))
  }
}
