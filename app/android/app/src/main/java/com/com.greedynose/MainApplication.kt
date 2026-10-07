package com.greedynose

import android.app.Application
import android.app.NotificationChannel
import android.app.NotificationManager
import android.os.Build
import com.facebook.react.PackageList
import com.facebook.react.ReactApplication
import com.facebook.react.ReactHost
import com.facebook.react.ReactNativeApplicationEntryPoint.loadReactNative
import com.facebook.react.defaults.DefaultReactHost.getDefaultReactHost

class MainApplication : Application(), ReactApplication {

  override val reactHost: ReactHost by lazy {
    getDefaultReactHost(
      context = applicationContext,
      packageList =
        PackageList(this).packages.apply {
          // Packages that cannot be autolinked yet can be added manually here, for example:
          // add(MyReactNativePackage())
        },
    )
  }

  override fun onCreate() {
    super.onCreate()
    createAlertsChannel()
    loadReactNative(this)
  }

  // Story 2.2: pushes must land in a high-importance channel to show as a heads-up.
  // Created here (not in JS) so it exists before the first push, even if JS never ran;
  // createNotificationChannel is idempotent, so existing installs are fine too.
  private fun createAlertsChannel() {
    if (Build.VERSION.SDK_INT < Build.VERSION_CODES.O) return
    val channel =
      NotificationChannel(ALERTS_CHANNEL_ID, "Alerts", NotificationManager.IMPORTANCE_HIGH)
    getSystemService(NotificationManager::class.java).createNotificationChannel(channel)
  }

  private companion object {
    // Paired with the backend: FirebaseNotificationSender.BuildMessage sets the same
    // channel id per message. Also the manifest default_notification_channel_id. Keep in sync.
    const val ALERTS_CHANNEL_ID = "alerts"
  }
}
