using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;
using SerialMonitor.Core.Automation;
using SerialMonitor.WinUI.ViewModels;

namespace SerialMonitor.WinUI.Tests;

public sealed class AutomationSequenceProgressTests
{
    [Theory]
    [InlineData("completed")]
    [InlineData("canceled")]
    [InlineData("running")]
    public void ACompletedRunCannotBeResurrectedOrCountedByALateNotification(string state)
    {
        var model = (MainViewModel)RuntimeHelpers.GetUninitializedObject(typeof(MainViewModel));
        typeof(MainViewModel).GetField("_automationSequenceCompletedRunId", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(model, "old");
        var progress = JsonSerializer.SerializeToElement(new { runId = "old", state, name = "old", completedSteps = 2, totalSteps = 2 }, AutomationJson.Options);
        typeof(MainViewModel).GetMethod("ApplyAutomationSequenceProgress", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(model, [progress]);
        Assert.False(model.IsSequenceRunning);
        Assert.Equal(0, model.SequenceRunCount);
    }
}
