package com.cathildros.swiftbeam.ui.theme

import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Shapes
import androidx.compose.material3.Typography
import androidx.compose.material3.darkColorScheme
import androidx.compose.runtime.Composable
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.text.TextStyle
import androidx.compose.ui.text.font.FontFamily
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp

object SwiftColors {
  val Background = Color(0xFF0B1120)
  val Surface = Color(0xFF111A2E)
  val SurfaceVariant = Color(0xFF18233B)
  val SurfaceElevated = Color(0xFF1E293B)
  val Border = Color(0xFF253452)
  val Primary = Color(0xFF8B5CF6)
  val PrimaryDark = Color(0xFF7C3AED)
  val Emerald = Color(0xFF10B981)
  val EmeraldBg = Color(0xFF064E3B)
  val Rose = Color(0xFFF43F5E)
  val Amber = Color(0xFFF59E0B)
  val Cyan = Color(0xFF38BDF8)
  val TextPrimary = Color(0xFFF8FAFC)
  val TextSecondary = Color(0xFF94A3B8)
}

private val SwiftDarkColorScheme = darkColorScheme(
  primary = SwiftColors.Primary,
  onPrimary = Color.White,
  primaryContainer = SwiftColors.PrimaryDark,
  onPrimaryContainer = Color.White,
  secondary = SwiftColors.Emerald,
  onSecondary = Color(0xFF042F2E),
  secondaryContainer = SwiftColors.EmeraldBg,
  onSecondaryContainer = SwiftColors.Emerald,
  background = SwiftColors.Background,
  onBackground = SwiftColors.TextPrimary,
  surface = SwiftColors.Surface,
  onSurface = SwiftColors.TextPrimary,
  surfaceVariant = SwiftColors.SurfaceVariant,
  onSurfaceVariant = SwiftColors.TextSecondary,
  outline = SwiftColors.Border,
  error = SwiftColors.Rose,
  onError = Color.White,
)

private val SwiftTypography = Typography(
  titleLarge = TextStyle(
    fontFamily = FontFamily.SansSerif,
    fontWeight = FontWeight.Bold,
    fontSize = 20.sp,
    letterSpacing = (-0.2).sp,
    color = SwiftColors.TextPrimary,
  ),
  titleMedium = TextStyle(
    fontFamily = FontFamily.SansSerif,
    fontWeight = FontWeight.SemiBold,
    fontSize = 16.sp,
    color = SwiftColors.TextPrimary,
  ),
  bodyMedium = TextStyle(
    fontFamily = FontFamily.SansSerif,
    fontWeight = FontWeight.Normal,
    fontSize = 14.sp,
    color = SwiftColors.TextPrimary,
  ),
  bodySmall = TextStyle(
    fontFamily = FontFamily.SansSerif,
    fontWeight = FontWeight.Normal,
    fontSize = 12.sp,
    color = SwiftColors.TextSecondary,
  ),
  labelMedium = TextStyle(
    fontFamily = FontFamily.SansSerif,
    fontWeight = FontWeight.SemiBold,
    fontSize = 12.sp,
  ),
)

private val SwiftShapes = Shapes(
  small = RoundedCornerShape(12.dp),
  medium = RoundedCornerShape(16.dp),
  large = RoundedCornerShape(22.dp),
)

@Composable
fun SwiftBeamTheme(content: @Composable () -> Unit) {
  MaterialTheme(
    colorScheme = SwiftDarkColorScheme,
    typography = SwiftTypography,
    shapes = SwiftShapes,
    content = content,
  )
}
