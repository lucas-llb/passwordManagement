package com.passwordmanagement.core.crypto

import com.goterl.lazysodium.LazySodiumAndroid
import com.goterl.lazysodium.SodiumAndroid

/**
 * Holds the device libsodium so the native library is loaded once, and only when a public
 * constructor that needs it is used. Shared by [Argon2idKdf], [VaultKeyWrap] and [VaultBodyCipher].
 */
internal object AndroidSodium {
    val lazySodium: LazySodiumAndroid by lazy { LazySodiumAndroid(SodiumAndroid()) }
}
