using System.Text.Json;

if (args.Length == 0 || args[0] is not ("offline" or "mcp" or "inspect"))
    throw new ArgumentException("offline <C-export-directory> <manifest.json> | mcp|inspect <plugin-root> <manifest.json> <new-evidence-directory>");
if (args[0] is "mcp" or "inspect")
{
    if (args.Length != 4) throw new ArgumentException("mcp <plugin-root> <manifest.json> <new-evidence-directory>");
    Environment.ExitCode = await LiveCheck.Run(args[1],args[2],args[3],inspectOnly:args[0] == "inspect");
    return;
}
if (args.Length != 3) throw new ArgumentException("offline <C-export-directory> <manifest.json>");
string directory = Path.GetFullPath(args[1]);
var identity = FixtureContract.Manifest(args[2]);
FixtureContract.AssertBuild(File.ReadAllBytes(Path.Combine(directory,"BUILD.txt")),identity.Build,identity.Version);
FixtureContract.AssertSelftest(File.ReadAllBytes(Path.Combine(directory,"SELFTEST.txt")),identity.Build);
FixtureContract.AssertCaps(File.ReadAllBytes(Path.Combine(directory,"CAPS.txt")),identity.Build,identity.Version);
Console.WriteLine("PASS selected binary/source manifest, BUILD?, CAPS?, SELFTEST (actual native C responses)");
int assertions = 4;
foreach (string name in FixtureContract.Names)
{
    var received = File.ReadAllBytes(Path.Combine(directory,$"fixture-{name}.bin"));
    FixtureContract.AssertResponse(received,name,"native_" + name,identity.Build);
    ++assertions;
    Console.WriteLine("PASS independent bytes/length/CRC/run/build/trailer: " + name);
    void Reject(Action action,string description)
    {
        bool rejected = false;
        try { action(); } catch { rejected = true; }
        if (!rejected) throw new Exception("Verifier accepted " + description + " in " + name);
        ++assertions;
    }
    Reject(() => FixtureContract.AssertResponse(received,name,"stale_run",identity.Build),"wrong/stale run token");
    Reject(() => FixtureContract.AssertResponse(received,name,"native_" + name,"WRONG_BUILD"),"wrong firmware build");
    Reject(() => FixtureContract.AssertResponse(received[..^1],name,"native_" + name,identity.Build),"truncated trailer");
    var corrupt = (byte[])received.Clone();
    int payloadOffset = Array.IndexOf(corrupt,(byte)'\n') + 1;
    corrupt[payloadOffset] ^= 1;
    Reject(() => FixtureContract.AssertResponse(corrupt,name,"native_" + name,identity.Build),"corrupted payload");
}
int binaryAssertions = BinaryContract.VerifyNative(directory);
assertions += binaryAssertions;
File.WriteAllText(Path.Combine(directory,"contract-report.json"),JsonSerializer.Serialize(new {
    mode="offline-native-C", hardwareVerified=false, buildId=identity.Build, version=identity.Version,
    assertions, cases=FixtureContract.Names, binaryLengths=BinaryContract.Lengths, binaryAssertions, passed=true,
    scope="C protocol core exported replies vs independent .NET expected data; not UART/GUI/device verification"
},new JsonSerializerOptions{WriteIndented=true}));
Console.WriteLine($"PASS {assertions} independent contract checks; hardwareVerified=false");
