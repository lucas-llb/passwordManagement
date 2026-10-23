package com.passwordmanagement.core.vault

import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertTrue
import org.junit.Test

class VaultJsonTest {
    private val sampleEntry = VaultEntry(
        id = "0b6f7c1e-2f5d-4a6b-9a8e-3c1d2e4f5a6b",
        title = "Example",
        username = "user@example.com",
        password = "not-a-real-password",
        url = "https://example.com",
        notes = "notes",
        folder = "Personal",
        favorite = true,
        createdAt = "2026-01-01T12:00:00.000Z",
        updatedAt = "2026-01-02T12:00:00.000Z",
    )

    @Test
    fun content_roundTrips() {
        val content = VaultContent(entries = listOf(sampleEntry))

        val json = VaultJson.encodeToString(VaultContent.serializer(), content)
        val restored = VaultJson.decodeFromString(VaultContent.serializer(), json)

        assertEquals(content, restored)
    }

    @Test
    fun content_usesCamelCaseFieldNames() {
        val json = VaultJson.encodeToString(VaultContent.serializer(), VaultContent(listOf(sampleEntry)))

        assertTrue(json.contains("\"createdAt\""))
        assertTrue(json.contains("\"favorite\""))
    }

    @Test
    fun entry_toString_doesNotLeakSecrets() {
        val text = sampleEntry.toString()

        assertFalse(text.contains(sampleEntry.password))
        assertFalse(text.contains(sampleEntry.username))
    }
}
