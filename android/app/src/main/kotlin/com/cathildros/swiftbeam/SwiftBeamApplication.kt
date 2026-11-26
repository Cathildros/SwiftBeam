package com.cathildros.swiftbeam

import android.app.Application
import com.cathildros.swiftbeam.service.TransferCoordinator

class SwiftBeamApplication : Application() {
  lateinit var coordinator: TransferCoordinator
    private set

  override fun onCreate() {
    super.onCreate()
    coordinator = TransferCoordinator(this)
  }
}
