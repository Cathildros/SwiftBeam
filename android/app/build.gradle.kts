plugins {
  alias(libs.plugins.android.application)
  alias(libs.plugins.compose.compiler)
  alias(libs.plugins.kotlin.serialization)
}

android {
  namespace = "com.cathildros.swiftbeam"
  compileSdk = 35

  defaultConfig {
    applicationId = "com.cathildros.swiftbeam"
    minSdk = 26
    targetSdk = 35
    versionCode = 10500
    versionName = "1.5.0"
  }

  signingConfigs {
    val keystorePath = System.getenv("SWIFTBEAM_KEYSTORE_PATH")
    if (!keystorePath.isNullOrBlank()) {
      create("releaseEnv") {
        storeFile = file(keystorePath)
        storePassword = System.getenv("SWIFTBEAM_KEYSTORE_PASSWORD") ?: ""
        keyAlias = System.getenv("SWIFTBEAM_KEY_ALIAS") ?: ""
        keyPassword = System.getenv("SWIFTBEAM_KEY_PASSWORD") ?: ""
      }
    }
  }

  buildTypes {
    release {
      isMinifyEnabled = false
      proguardFiles(getDefaultProguardFile("proguard-android-optimize.txt"), "proguard-rules.pro")
      signingConfig = signingConfigs.findByName("releaseEnv") ?: signingConfigs.getByName("debug")
    }
  }

  compileOptions {
    sourceCompatibility = JavaVersion.VERSION_17
    targetCompatibility = JavaVersion.VERSION_17
  }

  buildFeatures {
    compose = true
    aidl = false
    buildConfig = true
    shaders = false
  }

  packaging {
    resources {
      excludes += "/META-INF/{AL2.0,LGPL2.1}"
    }
  }
}

kotlin {
  jvmToolchain(17)
}

dependencies {
  val composeBom = platform(libs.androidx.compose.bom)
  implementation(composeBom)

  implementation(libs.androidx.core.ktx)
  implementation(libs.androidx.lifecycle.runtime.ktx)
  implementation(libs.androidx.lifecycle.runtime.compose)
  implementation(libs.androidx.lifecycle.viewmodel.compose)
  implementation(libs.androidx.activity.compose)

  implementation(libs.androidx.compose.ui)
  implementation(libs.androidx.compose.ui.tooling.preview)
  implementation(libs.androidx.compose.material3)
  implementation(libs.androidx.compose.material.icons.extended)
  debugImplementation(libs.androidx.compose.ui.tooling)

  implementation(libs.kotlinx.coroutines.android)
  implementation(libs.kotlinx.serialization.json)

  testImplementation(libs.junit)
  testImplementation(libs.kotlinx.coroutines.test)
}
