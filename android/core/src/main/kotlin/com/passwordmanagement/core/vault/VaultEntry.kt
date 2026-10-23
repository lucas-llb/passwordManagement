package com.passwordmanagement.core.vault

import kotlinx.serialization.Serializable

@Serializable
data class VaultEntry(
    val id: String,
    val title: String,
    val username: String = "",
    val password: String = "",
    val url: String = "",
    val notes: String = "",
    val folder: String = "",
    val favorite: Boolean = false,
    /** ISO-8601 UTC with milliseconds, e.g. 2026-01-01T12:00:00.000Z. */
    val createdAt: String,
    /** ISO-8601 UTC with milliseconds, e.g. 2026-01-01T12:00:00.000Z. */
    val updatedAt: String,
) {
    // Never print secrets (logs, debugger, exceptions).
    override fun toString(): String = "VaultEntry(id=$id, title=$title)"
}
