using System.Text.Json;
using SerialMonitor.WinUI.Models;
using SerialMonitor.WinUI.Services;

namespace SerialMonitor.WinUI.Tests;

public sealed class ProfileServiceDefaultsTests
{
    [Fact]
    public async Task SearchText_PreservesMeaningfulLeadingWhitespace()
    {
        var profilePath = CreateTemporaryProfilePath();
        try
        {
            var service = new ProfileService();
            var profile = service.CreateDefaultProfile();
            profile.UiSettings.LastSearchText = " rx_mq";

            await service.SaveAsync(profilePath, profile, CancellationToken.None);
            var loaded = await service.LoadAsync(profilePath, CancellationToken.None);

            Assert.Equal(" rx_mq", loaded.UiSettings.LastSearchText);
        }
        finally
        {
            DeleteTemporaryProfileDirectory(profilePath);
        }
    }

    [Fact]
    public void DefaultProfile_UsesFixedFortyMillisecondHexTimeout()
    {
        var service = new ProfileService();

        var profile = service.CreateDefaultProfile();

        Assert.Equal(40, profile.UiSettings.HexGroupTimeoutMs);
        Assert.False(profile.UiSettings.HexGroupTimeoutUserConfigured);
    }

    [Fact]
    public async Task DirectionPrefixDisplay_DefaultsToShownAndPersistsHiddenChoice()
    {
        var service = new ProfileService();
        var profile = service.CreateDefaultProfile();
        var path = CreateTemporaryProfilePath();

        Assert.True(profile.UiSettings.ShowRxTxDirectionPrefixInLogView);
        profile.UiSettings.ShowRxTxDirectionPrefixInLogView = false;

        try
        {
            await service.SaveAsync(path, profile, CancellationToken.None);
            var loaded = await service.LoadAsync(path, CancellationToken.None);

            Assert.False(loaded.UiSettings.ShowRxTxDirectionPrefixInLogView);
        }
        finally
        {
            DeleteTemporaryProfileDirectory(path);
        }
    }

    [Fact]
    public async Task LegacyProfileWithoutDirectionPrefixSetting_DefaultsToShown()
    {
        var service = new ProfileService();
        var path = CreateTemporaryProfilePath();
        var json = """
            {
              "ProfileSchemaVersion": 1,
              "Name": "Legacy profile",
              "UiSettings": {}
            }
            """;

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await File.WriteAllTextAsync(path, json);

            var loaded = await service.LoadAsync(path, CancellationToken.None);

            Assert.True(loaded.UiSettings.ShowRxTxDirectionPrefixInLogView);
        }
        finally
        {
            DeleteTemporaryProfileDirectory(path);
        }
    }

    [Fact]
    public async Task MissingProfile_IsCreatedWithOneVisibleScrollbackSetting()
    {
        var service = new ProfileService();
        var path = CreateTemporaryProfilePath();

        try
        {
            var loaded = await service.LoadAsync(path, CancellationToken.None);

            Assert.True(File.Exists(path));
            Assert.Equal(50_000, loaded.UiSettings.MaxVisibleLogLines);

            await using var stream = File.OpenRead(path);
            using var document = await JsonDocument.ParseAsync(stream);
            var uiSettings = document.RootElement.GetProperty(nameof(AppProfile.UiSettings));
            Assert.True(uiSettings.TryGetProperty(nameof(UiSettings.MaxVisibleLogLines), out _));
            Assert.False(uiSettings.TryGetProperty("XtermScrollbackSize", out _));
        }
        finally
        {
            DeleteTemporaryProfileDirectory(path);
        }
    }

    [Fact]
    public async Task SequenceRepeatAndRuleTrigger_ArePersistedAndProjected()
    {
        var service = new ProfileService();
        var path = CreateTemporaryProfilePath();
        var profile = service.CreateDefaultProfile();
        profile.CommandSequences =
        [
            new CommandSequence
            {
                Name = "Recover",
                RepeatCount = 3,
                Steps = [new CommandSequenceStep { CommandText = "reset" }]
            }
        ];
        profile.LogRules =
        [
            new LogRule
            {
                Name = "FAULT",
                Keyword = "FAULT",
                UseForEvent = false,
                ForegroundColor = "Red",
                BackgroundColor = "Yellow",
                TriggerSequenceName = "Recover"
            }
        ];

        try
        {
            await service.SaveAsync(path, profile, CancellationToken.None);
            var loaded = await service.LoadAsync(path, CancellationToken.None);

            Assert.Equal(3, Assert.Single(loaded.CommandSequences).RepeatCount);
            Assert.Equal("Recover", Assert.Single(loaded.LogRules).TriggerSequenceName);
            var projectedRule = Assert.Single(loaded.EventRules);
            Assert.False(projectedRule.ShowInEventList);
            Assert.Equal("Recover", projectedRule.TriggerSequenceName);
            Assert.Equal("Red", projectedRule.HighlightColor);
            Assert.Equal("Yellow", projectedRule.BackgroundColor);
        }
        finally
        {
            DeleteTemporaryProfileDirectory(path);
        }
    }

    [Fact]
    public async Task SequenceRepeatCount_IsClampedToSharedUiMaximum()
    {
        var service = new ProfileService();
        var path = CreateTemporaryProfilePath();
        var profile = service.CreateDefaultProfile();
        profile.CommandSequences =
        [
            new CommandSequence
            {
                Name = "Recover",
                RepeatCount = CommandSequence.MaxRepeatCount + 1,
                Steps = [new CommandSequenceStep { CommandText = "reset" }]
            }
        ];

        try
        {
            await service.SaveAsync(path, profile, CancellationToken.None);
            var loaded = await service.LoadAsync(path, CancellationToken.None);

            Assert.Equal(CommandSequence.MaxRepeatCount, Assert.Single(loaded.CommandSequences).RepeatCount);
        }
        finally
        {
            DeleteTemporaryProfileDirectory(path);
        }
    }

    [Fact]
    public async Task MissingTriggeredSequence_IsClearedDuringProfileNormalization()
    {
        var service = new ProfileService();
        var path = CreateTemporaryProfilePath();
        var profile = service.CreateDefaultProfile();
        profile.LogRules[0].TriggerSequenceName = "Does not exist";

        try
        {
            await service.SaveAsync(path, profile, CancellationToken.None);
            var loaded = await service.LoadAsync(path, CancellationToken.None);

            Assert.Null(loaded.LogRules[0].TriggerSequenceName);
        }
        finally
        {
            DeleteTemporaryProfileDirectory(path);
        }
    }

    [Fact]
    public async Task InvalidTxOnlyAndEmptySequenceTriggers_AreClearedDuringProfileNormalization()
    {
        var service = new ProfileService();
        var path = CreateTemporaryProfilePath();
        var profile = service.CreateDefaultProfile();
        profile.CommandSequences =
        [
            new CommandSequence
            {
                Name = "Recover",
                Steps = [new CommandSequenceStep { CommandText = "reset" }]
            },
            new CommandSequence { Name = "Empty" }
        ];
        profile.LogRules =
        [
            new LogRule
            {
                Name = "TX",
                Keyword = "TX",
                MatchDirection = HighlightMatchDirection.TxOnly,
                TriggerSequenceName = "Recover"
            },
            new LogRule
            {
                Name = "EMPTY",
                Keyword = "EMPTY",
                MatchDirection = HighlightMatchDirection.RxOnly,
                TriggerSequenceName = "Empty"
            }
        ];

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await File.WriteAllTextAsync(path, JsonSerializer.Serialize(profile));

            var loaded = await service.LoadAsync(path, CancellationToken.None);

            Assert.All(loaded.LogRules, rule => Assert.Null(rule.TriggerSequenceName));
            Assert.DoesNotContain(loaded.EventRules, rule => !string.IsNullOrWhiteSpace(rule.TriggerSequenceName));
            Assert.Contains("TX-only", service.LastError, StringComparison.Ordinal);
            Assert.Contains("empty command sequence", service.LastError, StringComparison.Ordinal);
        }
        finally
        {
            DeleteTemporaryProfileDirectory(path);
        }
    }

    [Fact]
    public async Task DuplicateSequenceNames_AreRenamedWithoutRetargetingExistingRules()
    {
        var service = new ProfileService();
        var path = CreateTemporaryProfilePath();
        var profile = service.CreateDefaultProfile();
        profile.CommandSequences =
        [
            new CommandSequence { Name = "Recover", Steps = [new CommandSequenceStep { CommandText = "first" }] },
            new CommandSequence { Name = "recover", Steps = [new CommandSequenceStep { CommandText = "duplicate" }] },
            new CommandSequence { Name = "Recover (2)", Steps = [new CommandSequenceStep { CommandText = "reserved" }] }
        ];
        profile.LogRules[0].TriggerSequenceName = "Recover";

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await File.WriteAllTextAsync(path, JsonSerializer.Serialize(profile));
            var loaded = await service.LoadAsync(path, CancellationToken.None);

            Assert.Equal(
                ["Recover", "recover (3)", "Recover (2)"],
                loaded.CommandSequences.Select(sequence => sequence.Name));
            Assert.Equal("Recover", loaded.LogRules[0].TriggerSequenceName);
            Assert.Equal("first", loaded.CommandSequences[0].Steps[0].CommandText);
            Assert.Contains("Duplicate command sequence", service.LastError, StringComparison.Ordinal);
        }
        finally
        {
            DeleteTemporaryProfileDirectory(path);
        }
    }

    [Fact]
    public async Task AutomaticDefault_RemainsFortyMillisecondsAcrossBaudRates()
    {
        var service = new ProfileService();
        var profile = service.CreateDefaultProfile();
        profile.SerialSettings.BaudRate = 9_600;
        profile.UiSettings.HexGroupTimeoutMs = 3;
        profile.UiSettings.HexGroupTimeoutUserConfigured = false;
        var path = CreateTemporaryProfilePath();

        try
        {
            await service.SaveAsync(path, profile, CancellationToken.None);
            var loaded = await service.LoadAsync(path, CancellationToken.None);

            Assert.Equal(40, loaded.UiSettings.HexGroupTimeoutMs);
            Assert.False(loaded.UiSettings.HexGroupTimeoutUserConfigured);
        }
        finally
        {
            DeleteTemporaryProfileDirectory(path);
        }
    }

    [Fact]
    public async Task UserConfiguredTimeout_IsPreservedExactly()
    {
        var service = new ProfileService();
        var profile = service.CreateDefaultProfile();
        profile.SerialSettings.BaudRate = 9_600;
        profile.UiSettings.HexGroupTimeoutMs = 37;
        profile.UiSettings.HexGroupTimeoutUserConfigured = true;
        var path = CreateTemporaryProfilePath();

        try
        {
            await service.SaveAsync(path, profile, CancellationToken.None);
            var loaded = await service.LoadAsync(path, CancellationToken.None);

            Assert.Equal(37, loaded.UiSettings.HexGroupTimeoutMs);
            Assert.True(loaded.UiSettings.HexGroupTimeoutUserConfigured);
        }
        finally
        {
            DeleteTemporaryProfileDirectory(path);
        }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(5_001)]
    public async Task InvalidUserConfiguredTimeout_UsesFixedDefaultAndReturnsToAutomaticMode(
        int invalidTimeoutMs)
    {
        var service = new ProfileService();
        var path = CreateTemporaryProfilePath();
        var json = $$"""
            {
              "ProfileSchemaVersion": 1,
              "Name": "Invalid custom timeout",
              "SerialSettings": {
                "PortName": "MOCK",
                "BaudRate": 1200,
                "DataBits": 8,
                "Parity": "None",
                "StopBits": "One"
              },
              "UiSettings": {
                "HexGroupTimeoutMs": {{invalidTimeoutMs}},
                "HexGroupTimeoutUserConfigured": true
              }
            }
            """;

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await File.WriteAllTextAsync(path, json);

            var loaded = await service.LoadAsync(path, CancellationToken.None);

            Assert.Equal(40, loaded.UiSettings.HexGroupTimeoutMs);
            Assert.False(loaded.UiSettings.HexGroupTimeoutUserConfigured);
            Assert.Contains("automatic 40 ms default", service.LastError, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            DeleteTemporaryProfileDirectory(path);
        }
    }

    [Fact]
    public async Task LegacyTimeoutWithoutMarker_IsTreatedAsUserConfigured()
    {
        var service = new ProfileService();
        var path = CreateTemporaryProfilePath();
        var json = """
            {
              "ProfileSchemaVersion": 1,
              "Name": "Legacy timeout",
              "SerialSettings": {
                "PortName": "MOCK",
                "BaudRate": 9600,
                "DataBits": 8,
                "Parity": "None",
                "StopBits": "One"
              },
              "UiSettings": {
                "HexGroupTimeoutMs": 29
              }
            }
            """;

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await File.WriteAllTextAsync(path, json);

            var loaded = await service.LoadAsync(path, CancellationToken.None);

            Assert.Equal(29, loaded.UiSettings.HexGroupTimeoutMs);
            Assert.True(loaded.UiSettings.HexGroupTimeoutUserConfigured);
        }
        finally
        {
            DeleteTemporaryProfileDirectory(path);
        }
    }

    [Fact]
    public void DefaultProfilePath_IsUnderLocalAppData()
    {
        var service = new ProfileService();
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

        Assert.StartsWith(localAppData, service.DefaultProfilePath, StringComparison.OrdinalIgnoreCase);
        Assert.EndsWith(
            Path.Combine("SerialMonitor", "profiles", "default.json"),
            service.DefaultProfilePath,
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task InvalidXtermFontSettings_AreResetToDefaults()
    {
        var service = new ProfileService();
        var profile = service.CreateDefaultProfile();
        profile.UiSettings.XtermFontFamily = (XtermFontFamily)999;
        profile.UiSettings.XtermFontSize = 100;
        var path = CreateTemporaryProfilePath();

        try
        {
            await service.SaveAsync(path, profile, CancellationToken.None);
            var loaded = await service.LoadAsync(path, CancellationToken.None);

            Assert.Equal(XtermFontFamily.Consolas, loaded.UiSettings.XtermFontFamily);
            Assert.Equal(UiSettings.DefaultXtermFontSize, loaded.UiSettings.XtermFontSize);
        }
        finally
        {
            DeleteTemporaryProfileDirectory(path);
        }
    }

    private static string CreateTemporaryProfilePath()
    {
        return Path.Combine(
            Path.GetTempPath(),
            "SerialMonitor.Tests",
            Guid.NewGuid().ToString("N"),
            "profile.json");
    }

    private static void DeleteTemporaryProfileDirectory(string path)
    {
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(directory) && Directory.Exists(directory))
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}
