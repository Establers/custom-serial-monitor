using SerialMonitor.WinUI.Models;
using SerialMonitor.WinUI.Services;

namespace SerialMonitor.WinUI.Tests;

public sealed class RuleNotificationPersistenceTests
{
    [Theory]
    [InlineData(7_200, 100_000, 7_200)]
    [InlineData(86_400, int.MaxValue, 86_400)]
    [InlineData(30, int.MinValue, 30)]
    [InlineData(4, 10, 5)]
    [InlineData(86_401, 10, 86_400)]
    public async Task ProfileRoundTrip_AndDetectedEvent_PreserveSupportedCooldownAndPriority(
        int cooldown, int priority, int expectedCooldown)
    {
        var directory = Path.Combine(Path.GetTempPath(), "SerialMonitor.Tests", Guid.NewGuid().ToString("N"));
        var path = Path.Combine(directory, "profile.json");
        try
        {
            var service = new ProfileService();
            var profile = service.CreateDefaultProfile();
            profile.LogRules =
            [
                new LogRule
                {
                    Name = "cooldown",
                    Keyword = "FAULT",
                    UseForEvent = true,
                    TrayNotificationEnabled = true,
                    NotificationCooldownSeconds = cooldown,
                    Priority = priority
                }
            ];

            await service.SaveAsync(path, profile, CancellationToken.None);
            var loaded = await service.LoadAsync(path, CancellationToken.None);

            var rule = Assert.Single(loaded.LogRules);
            Assert.Equal(expectedCooldown, rule.NotificationCooldownSeconds);
            Assert.Equal(priority, rule.Priority);
            Assert.Equal(expectedCooldown, Assert.Single(loaded.EventRules).NotificationCooldownSeconds);

            await using var detector = new EventDetector();
            await detector.StartAsync(loaded.EventRules, new EventContextSettings(), CancellationToken.None);
            Assert.True(detector.TryEnqueue(LogLine.Rx("FAULT", "FAULT"u8.ToArray())));
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            var detected = await detector.DetectedEvents.ReadAsync(timeout.Token);
            Assert.True(detected.TrayNotificationEnabled);
            Assert.Equal(expectedCooldown, detected.NotificationCooldownSeconds);
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }
}
