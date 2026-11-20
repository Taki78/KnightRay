package com.v2ray.ang.ui.fetcher

import org.junit.jupiter.api.Assertions.assertEquals
import org.junit.jupiter.api.Assertions.assertThrows
import org.junit.jupiter.api.Test
import java.net.URI

class FetcherRepositoryTest {
    private val first = FetchSource("first", "First", "https://example.com/{proto}.txt", listOf("vless", "vmess"), true)
    private val second = FetchSource("second", "Second", "https://example.com/second.txt", emptyList(), true)

    @Test
    fun `restore adds only missing defaults and keeps existing edits`() {
        val edited = first.copy(name = "Edited", enabled = false)
        val custom = second.copy(id = "custom")
        val current = listOf(edited, custom)
        val missing = FetcherRepository.missingDefaults(current, listOf(first, second))
        assertEquals(listOf(second), missing)
        assertEquals(emptyList<FetchSource>(), FetcherRepository.missingDefaults(current + missing, listOf(first, second)))
    }

    @Test
    fun `expands HTTPS files and rejects templates outside the path`() {
        assertEquals(listOf(URI("https://example.com/vless.txt"), URI("https://example.com/vmess.txt")), first.urls())
        assertThrows(IllegalArgumentException::class.java) { first.copy(url = "https://example.com/sub?secret={proto}").urls() }
        assertThrows(IllegalArgumentException::class.java) { first.copy(url = "http://example.com/{proto}.txt").urls() }
        assertThrows(IllegalArgumentException::class.java) { second.copy(url = "https://user@example.com/sub").urls() }
    }
}
