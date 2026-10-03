using SerialMonitor.WinUI.Services;
using SerialMonitor.WinUI.ViewModels;

namespace SerialMonitor.WinUI.Tests;

public sealed class PortAliasViewModelTests
{
    [Fact]
    public async Task SelectionRestoresItsOwnNameAndCanceledDraftDoesNotChangeTheTitle()
    {
        var service = new FakeService();
        service.Names["COM3"] = "메인 보드";
        service.Names["COM7"] = "Module";
        using var model = new PortAliasViewModel(service);
        model.SetPort("com3");
        await model.InitializeAsync();
        Assert.Equal("메인 보드 · COM3 - Serial Monitor", model.WindowTitle);
        model.BeginEdit();
        model.DraftAlias = "Unsaved";
        Assert.Equal("메인 보드 · COM3 - Serial Monitor", model.WindowTitle);
        model.BeginEdit();
        Assert.Equal("메인 보드", model.DraftAlias);
        model.SetPort("COM7");
        Assert.Equal("Module · COM7 - Serial Monitor", model.WindowTitle);
        Assert.False(await model.ApplyAsync());
        Assert.Equal(0, service.Saves);
    }

    [Fact]
    public async Task PortChangeDuringSaveNeverNamesTheNewPortWithTheOldDraft()
    {
        var service = new FakeService { SaveGate = new() };
        service.Names["COM7"] = "Module";
        using var model = new PortAliasViewModel(service);
        model.SetPort("COM3");
        await model.InitializeAsync();
        model.BeginEdit();
        model.DraftAlias = "Board";
        var saving = model.ApplyAsync();
        Assert.False(model.CanEdit);
        model.SetPort("COM7");
        service.SaveGate.SetResult();
        Assert.False(await saving);
        Assert.Equal("Module · COM7 - Serial Monitor", model.WindowTitle);
        Assert.Equal("Board", service.Names["COM3"]);
        Assert.Equal("Module", service.Names["COM7"]);
        model.SetPort("COM3");
        Assert.Equal("Board · COM3 - Serial Monitor", model.WindowTitle);
    }

    [Fact]
    public async Task SaveFailureKeepsExistingNameAndAllowsRetryAndRemoval()
    {
        var service = new FakeService { FailSave = true };
        service.Names["COM3"] = "Original";
        using var model = new PortAliasViewModel(service);
        model.SetPort("COM3");
        await model.InitializeAsync();
        model.BeginEdit();
        model.DraftAlias = "New";
        Assert.False(await model.ApplyAsync());
        Assert.Equal("Original · COM3 - Serial Monitor", model.WindowTitle);
        Assert.NotEmpty(model.ErrorMessage);
        Assert.True(model.CanApply);
        service.FailSave = false;
        Assert.True(await model.ApplyAsync());
        Assert.Equal("New · COM3 - Serial Monitor", model.WindowTitle);
        Assert.Empty(model.ErrorMessage);
        model.BeginEdit();
        model.DraftAlias = string.Empty;
        Assert.True(await model.ApplyAsync());
        Assert.Equal("COM3 - Serial Monitor", model.WindowTitle);
    }

    [Fact]
    public async Task ClosingDuringPendingSaveCancelsItWithoutUpdatingTheTitle()
    {
        var service = new FakeService { SaveGate = new() };
        var model = new PortAliasViewModel(service);
        model.SetPort("COM3");
        await model.InitializeAsync();
        model.BeginEdit();
        model.DraftAlias = "Board";
        var saving = model.ApplyAsync();
        model.Dispose();
        Assert.False(await saving);
        Assert.False(model.CanEdit);
        Assert.Equal("COM3 - Serial Monitor", model.WindowTitle);
        Assert.Empty(service.Names);
    }

    [Fact]
    public async Task NoPortAndOverlongNameNeverSave()
    {
        var service = new FakeService();
        using var model = new PortAliasViewModel(service);
        await model.InitializeAsync();
        Assert.False(model.CanEdit);
        Assert.Equal("Serial Monitor", model.WindowTitle);
        model.SetPort("COM3");
        model.BeginEdit();
        model.DraftAlias = new string('a', 65);
        Assert.False(await model.ApplyAsync());
        Assert.NotEmpty(model.ErrorMessage);
        Assert.Equal(0, service.Saves);
    }

    [Fact]
    public async Task MalformedPortSelectionDoesNotThrowOrKeepAnotherPortsName()
    {
        var service = new FakeService();
        service.Names["COM3"] = "Board";
        using var model = new PortAliasViewModel(service);
        await model.InitializeAsync();
        foreach (var port in new[] { "COM\0bad", new string('a', 129), "COM" + (char)0xD800 })
        {
            model.SetPort("COM3");
            model.SetPort(port);
            Assert.False(model.CanEdit);
            Assert.Equal("Serial Monitor", model.WindowTitle);
        }
    }

    [Fact]
    public async Task NullDraftIsHandledAsAnEmptyName()
    {
        var service = new FakeService();
        service.Names["COM3"] = "Board";
        using var model = new PortAliasViewModel(service);
        model.SetPort("COM3");
        await model.InitializeAsync();
        model.BeginEdit();
        model.DraftAlias = null!;
        Assert.True(await model.ApplyAsync());
        Assert.Equal("COM3 - Serial Monitor", model.WindowTitle);
    }

    [Fact]
    public async Task EndingAnEditPreventsTheCanceledDraftFromBeingApplied()
    {
        var service = new FakeService();
        using var model = new PortAliasViewModel(service);
        model.SetPort("COM3");
        await model.InitializeAsync();
        model.BeginEdit();
        model.DraftAlias = "Canceled";
        model.EndEdit();
        Assert.False(await model.ApplyAsync());
        model.BeginEdit();
        model.DraftAlias = "Also canceled";
        model.SetPort("COM7");
        model.SetPort("COM3");
        Assert.False(await model.ApplyAsync());
        Assert.Equal(0, service.Saves);
    }

    [Fact]
    public async Task ClosingDuringLoadFailureDoesNotNotifyTheClosedUi()
    {
        var service = new FakeService { LoadGate = new() };
        var model = new PortAliasViewModel(service);
        var loading = model.InitializeAsync();
        model.Dispose();
        var notifications = 0;
        model.PropertyChanged += (_, _) => notifications++;
        service.LoadGate.SetException(new IOException("Late load error"));
        await loading;
        Assert.Equal(0, notifications);
    }

    [Fact]
    public async Task RandomUtf16InputIsRejectedOrSafelyRoundTripsWithoutUnhandledExceptions()
    {
        var service = new FakeService();
        using var model = new PortAliasViewModel(service);
        model.SetPort("COM3");
        await model.InitializeAsync();
        var random = new Random(10403);
        for (var sample = 0; sample < 2_000; sample++)
        {
            var input = new string(Enumerable.Range(0, random.Next(0, 90))
                .Select(_ => (char)random.Next(0, 65_536)).ToArray());
            model.BeginEdit();
            model.DraftAlias = input;
            var previousTitle = model.WindowTitle;
            if (await model.ApplyAsync())
            {
                var restored = System.Text.Json.JsonSerializer.Deserialize<string>(
                    System.Text.Json.JsonSerializer.Serialize(model.Alias));
                Assert.Equal(model.Alias, restored);
                Assert.True(model.Alias.Length <= 64);
                Assert.EndsWith("COM3 - Serial Monitor", model.WindowTitle);
            }
            else Assert.Equal(previousTitle, model.WindowTitle);
            Assert.True(model.CanEdit);
        }
    }

    [Fact]
    public async Task LoadFailureIsContainedAndLaterSuccessfulSaveRecovers()
    {
        var service = new FakeService { LoadGate = new() };
        service.LoadGate.SetException(new System.Text.Json.JsonException("Broken JSON"));
        using var model = new PortAliasViewModel(service);
        model.SetPort("COM3");
        await model.InitializeAsync();
        Assert.True(model.CanEdit);
        Assert.NotEmpty(model.ErrorMessage);
        model.BeginEdit();
        model.DraftAlias = "Board";
        Assert.True(await model.ApplyAsync());
        Assert.Equal("Board · COM3 - Serial Monitor", model.WindowTitle);
    }

    private sealed class FakeService : IPortAliasService
    {
        public Dictionary<string, string> Names { get; } = new(StringComparer.OrdinalIgnoreCase);
        public TaskCompletionSource? SaveGate { get; init; }
        public TaskCompletionSource<IReadOnlyDictionary<string, string>>? LoadGate { get; init; }
        public bool FailSave { get; set; }
        public int Saves { get; private set; }

        public Task<IReadOnlyDictionary<string, string>> LoadAsync(CancellationToken cancellationToken) =>
            LoadGate?.Task ?? Task.FromResult<IReadOnlyDictionary<string, string>>(new Dictionary<string, string>(Names));

        public async Task SetAsync(string portName, string alias, CancellationToken cancellationToken)
        {
            if (FailSave) throw new IOException("Disk unavailable");
            Saves++;
            if (SaveGate is not null) await SaveGate.Task.WaitAsync(cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            if (alias.Length == 0) Names.Remove(portName);
            else Names[portName] = alias;
        }
    }
}
