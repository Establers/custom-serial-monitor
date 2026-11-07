using System.Runtime.CompilerServices;
using System.Xml.Linq;
using Microsoft.UI.Xaml;
using SerialMonitor.Core.Protocols;
using SerialMonitor.WinUI.ViewModels;

namespace SerialMonitor.WinUI.Tests;

// Validate the binding scope as well as the view model. A view-model-only
// visibility test cannot catch a binding evaluated against the child context.
public sealed class ProtocolWorkspaceBindingTests
{
    [Fact]
    public void VirtualWorkspaceVisibility_UsesProtocolContextOutsideDeviceContext()
    {
        var view = ReadView();
        var device = Assert.Single(view.Descendants().Where(e => e.Name.LocalName == "VirtualModbusDeviceView"));
        Assert.Equal("{Binding VirtualDevice}", device.Attribute("DataContext")?.Value);
        Assert.Null(device.Attribute("Visibility"));
        var host = device.Parent!;
        Assert.Contains("VirtualDeviceVisibility", host.Attribute("Visibility")?.Value);
        Assert.Contains("FallbackValue=Collapsed", host.Attribute("Visibility")?.Value);
        Assert.DoesNotContain(host.AncestorsAndSelf(), e => e.Attribute("DataContext") is not null);
        Assert.NotNull(typeof(ProtocolViewModel).GetProperty(nameof(ProtocolViewModel.VirtualDeviceVisibility)));
    }

    [Fact]
    public void WorkspaceSelector_HasAnItemsSourceAndASelectedIndexInTheSameContext()
    {
        var view = ReadView();
        var selector = Assert.Single(view.Descendants().Where(e => e.Name.LocalName == "ComboBox" &&
            e.Attribute("SelectedIndex")?.Value.Contains(nameof(ProtocolViewModel.SelectedWorkspaceIndex)) == true));
        Assert.Equal("{Binding WorkspaceOptions}", selector.Attribute("ItemsSource")?.Value);
        Assert.Empty(selector.Elements());
        Assert.Null(selector.Attribute("DataContext"));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public async Task SwitchingWorkspaces_ShowsExactlyOnePane(int selected)
    {
        await using var analyzer = new ModbusRtuAnalyzer();
        var model = new ProtocolViewModel(analyzer);
        model.SelectedWorkspaceIndex = selected;
        Assert.NotEqual(model.AnalysisVisibility, model.VirtualDeviceVisibility);
        Assert.Equal(selected == 0 ? Visibility.Visible : Visibility.Collapsed, model.AnalysisVisibility);
    }

    private static XDocument ReadView([CallerFilePath] string sourceFile = "") =>
        XDocument.Load(Path.GetFullPath(Path.Combine(Path.GetDirectoryName(sourceFile)!, "..", "SerialMonitor.WinUI", "Views", "ProtocolView.xaml")));
}
