using SerialMonitor.WinUI.Services;

var path = args[0];
var mode = args[1];
if (mode == "hold")
{
    using var handle = new FileStream(path + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
    Console.WriteLine("LOCKED");
    await Console.In.ReadLineAsync();
    return;
}

var service = new UpdateService(path);
for (var index = 0; index < 25; index++)
{
    var change = mode switch
    {
        "automatic" => new UpdatePreferenceChange(Automatic: false),
        "skip" => new UpdatePreferenceChange(SkippedVersion: "1.4.0.0"),
        _ => new UpdatePreferenceChange(LastAttemptUtc: DateTimeOffset.UtcNow,
            LastSuccessUtc: DateTimeOffset.UtcNow, LastKnownTag: "v1.5.0")
    };
    await service.ApplyAsync(change, default);
}
