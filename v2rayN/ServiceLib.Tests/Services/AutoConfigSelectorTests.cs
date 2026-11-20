using ServiceLib.Services.ConfigFetcher;

namespace ServiceLib.Tests.Services;

public class AutoConfigSelectorTests
{
    [Test]
    public async Task ChoosesLowerDelayEvenWhenTheDifferenceIsOneMillisecond()
    {
        var current = Result("current", FetchStatus.Working, 81);
        var faster = Result("faster", FetchStatus.Working, 80);

        await (AutoConfigSelector.Choose([current, faster], "current")?.Profile?.IndexId).Should().BeEqualTo("faster");
    }

    [Test]
    public async Task KeepsCurrentConfigWhenItTiesForLowestDelay()
    {
        var current = Result("current", FetchStatus.Working, 80);
        var other = Result("another", FetchStatus.Working, 80);

        await (AutoConfigSelector.Choose([other, current], "current")?.Profile?.IndexId).Should().BeEqualTo("current");
    }

    [Test]
    public async Task PicksFastestPassingConfigWhenCurrentFailsOrIsSlower()
    {
        var fast = Result("fast", FetchStatus.Working, 65);
        var slow = Result("current", FetchStatus.Working, 300);
        var failed = Result("failed", FetchStatus.Unreachable, 0);

        await (AutoConfigSelector.Choose([failed, slow, fast], "current")?.Profile?.IndexId).Should().BeEqualTo("fast");
        await (AutoConfigSelector.Choose([failed, slow, fast], "failed")?.Profile?.IndexId).Should().BeEqualTo("fast");
    }

    [Test]
    public async Task DoesNotChooseFailedUntestedOrUnmeasuredConfigs()
    {
        var failed = Result("failed", FetchStatus.Unreachable, 10);
        var untested = Result("untested", FetchStatus.Untested, null);
        var noMeasurement = Result("no-measurement", FetchStatus.Working, null);

        await (AutoConfigSelector.Choose([failed, untested, noMeasurement], "failed") is null).Should().BeTrue();
    }

    private static FetchResult Result(string id, FetchStatus status, long? delay) =>
        new(id, "Fixture", status, "", new ProfileItem { IndexId = id }, delay);
}
