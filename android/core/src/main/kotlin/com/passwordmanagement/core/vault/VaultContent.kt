package com.passwordmanagement.core.vault

import kotlinx.serialization.Serializable
import kotlinx.serialization.json.Json

/** Plaintext body of the vault. Only exists in memory while unlocked. */
@Serializable
data class VaultContent(
    val entries: List<VaultEntry> = emptyList(),
)

internal val VaultJson = Json {
    ignoreUnknownKeys = true
    encodeDefaults = true
}
