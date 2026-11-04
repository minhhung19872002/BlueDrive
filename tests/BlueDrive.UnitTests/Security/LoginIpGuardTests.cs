using BlueDrive.Application.Features.Auth;
using FluentAssertions;

namespace BlueDrive.UnitTests.Security;

/// <summary>HSMT #14 / G7: failed logins counted per address so one address cannot guess passwords across many accounts.</summary>
public class LoginIpGuardTests
{
    private sealed class ManualClock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = new(2026, 11, 4, 1, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private static readonly TimeSpan Window = TimeSpan.FromMinutes(15);

    [Fact]
    public void Address_is_blocked_on_the_failure_that_reaches_the_limit_and_only_that_one_reports_it()
    {
        var clock = new ManualClock();
        var guard = new LoginIpGuard(clock);
        for (var i = 1; i < 3; i++) guard.RecordFailure("203.0.113.5", 3, Window).Should().BeFalse();
        guard.BlockedFor("203.0.113.5").Should().BeNull();

        guard.RecordFailure("203.0.113.5", 3, Window).Should().BeTrue();
        guard.BlockedFor("203.0.113.5").Should().Be(Window);
        guard.RecordFailure("203.0.113.5", 3, Window).Should().BeFalse("the block is reported once");
        guard.BlockedFor("198.51.100.1").Should().BeNull("other addresses are not affected");

        clock.Now += Window;
        guard.BlockedFor("203.0.113.5").Should().BeNull();
    }

    [Fact]
    public void Failures_outside_the_window_do_not_add_up()
    {
        var clock = new ManualClock();
        var guard = new LoginIpGuard(clock);
        guard.RecordFailure("203.0.113.6", 2, Window);
        clock.Now += Window + TimeSpan.FromSeconds(1);
        guard.RecordFailure("203.0.113.6", 2, Window).Should().BeFalse();
        guard.BlockedFor("203.0.113.6").Should().BeNull();
    }

    [Theory]
    [InlineData(null, 3)]
    [InlineData("203.0.113.7", 0)]
    public void Unknown_address_or_zero_limit_never_blocks(string? ip, int max)
    {
        var guard = new LoginIpGuard(new ManualClock());
        for (var i = 0; i < 10; i++) guard.RecordFailure(ip, max, Window).Should().BeFalse();
        guard.BlockedFor(ip).Should().BeNull();
    }
}
