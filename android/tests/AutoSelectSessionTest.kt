package com.v2ray.ang.ui.fetcher

import org.junit.jupiter.api.Assertions.assertEquals
import org.junit.jupiter.api.Assertions.assertNull
import org.junit.jupiter.api.Test

class AutoSelectSessionTest {
    @Test
    fun `sorted results put lower delays first and failed or untested at the end`() {
        val failed = AutoSelectResult("failed", -1)
        val slower = AutoSelectResult("slower", 150)
        val untested = AutoSelectResult("untested", 0)
        val faster = AutoSelectResult("faster", 12)
        assertEquals(listOf(faster, slower, failed, untested),
            sortByLowestDelay(listOf(failed, slower, untested, faster), AutoSelectResult::delayMs))
    }

    @Test
    fun `a one millisecond improvement beats the active config and failed probes`() {
        val current = AutoSelectResult("current", 81)
        val faster = AutoSelectResult("faster", 80)
        assertEquals(faster, fastestAutoConfig(listOf(current, AutoSelectResult("failed", -1), faster), current.id))
    }

    @Test
    fun `keeps the active config on a tie and has no winner when all probes fail`() {
        val current = AutoSelectResult("current", 80)
        assertEquals(current, fastestAutoConfig(listOf(AutoSelectResult("another", 80), current), current.id))
        assertNull(fastestAutoConfig(listOf(AutoSelectResult("failed", -1)), current.id))
    }
}
