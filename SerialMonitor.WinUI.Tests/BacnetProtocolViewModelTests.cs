using SerialMonitor.Core.Protocols;
using SerialMonitor.WinUI.ViewModels;

namespace SerialMonitor.WinUI.Tests;

public sealed class BacnetProtocolViewModelTests
{
    static readonly byte[] Response=Convert.FromHexString("55FF04020100036C4142432F9F");
    static async Task Push(IProtocolAnalyzer analyzer,byte[] bytes)
    {
        long before=analyzer.Snapshot().TotalFrames;
        Assert.True(analyzer.TryEnqueue(bytes,ProtocolDirection.Rx,1,DateTimeOffset.Now));
        using var stop=new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while(analyzer.Snapshot().TotalFrames<=before) await Task.Delay(5,stop.Token);
    }
    [Fact]
    public async Task SelectionShowsMstpFieldsExactTypeFiltersAndSeparateCrcCounters()
    {
        await using var analyzer=new ProtocolAnalyzer();
        var model=new ProtocolViewModel(analyzer) {IsVisible=true,SelectedProtocolIndex=2};
        Assert.Contains("BACnet MS/TP",model.ProtocolOptions);Assert.Equal("Src",model.AddressLabel);Assert.Equal("Type",model.CodeLabel);
        await Push(analyzer,Response); model.Refresh();
        var row=Assert.Single(model.Frames);Assert.Equal("1",row.UnitText);Assert.Equal("04",row.FunctionCodeText);
        Assert.Equal("Test Response",row.FunctionText); model.SelectedFrame=row;
        Assert.Contains("Source MAC: 1; destination MAC: 2",model.Details);
        model.FilterFunctionText="84";Assert.True(model.TryApplyFilters());Assert.Empty(model.Frames);
        model.FilterFunctionText="04";Assert.True(model.TryApplyFilters());Assert.Single(model.Frames);
        var badHeader=(byte[])Response.Clone();badHeader[7]^=1;await Push(analyzer,badHeader);
        var badData=(byte[])Response.Clone();badData[^1]^=1;await Push(analyzer,badData);
        model.ResetFilters();model.Refresh();
        Assert.Contains("header 1 / data 1",model.ErrorSummary);
        Assert.Equal(2,model.Frames.Count(r=>r.Frame.Status==ProtocolFrameStatus.CrcError));
        Assert.Equal("—",model.Frames.Single(r=>r.Frame is BacnetMstpFrame {HeaderCrc:ProtocolCrcStatus.Invalid}).UnitText);
    }
    [Fact]
    public async Task SwitchingWhilePausedClearsOldRowsFiltersAndCounters_WithoutBreakingModbus()
    {
        await using var analyzer=new ProtocolAnalyzer();
        var model=new ProtocolViewModel(analyzer) {IsVisible=true,SelectedProtocolIndex=2};
        await Push(analyzer,Response);model.Refresh();model.SelectedFrame=Assert.Single(model.Frames);
        model.FilterUnitText="1";Assert.True(model.TryApplyFilters());model.IsDisplayPaused=true;
        model.SelectedProtocolIndex=1;
        Assert.False(model.IsDisplayPaused);Assert.False(model.HasActiveFilters);Assert.Null(model.SelectedFrame);Assert.Empty(model.Frames);
        Assert.Equal("ID",model.AddressLabel);Assert.Equal("FC",model.CodeLabel);Assert.Equal(0,analyzer.Snapshot().TotalFrames);
        await Push(analyzer,Convert.FromHexString("01030400FA0064DBE9"));model.Refresh();
        Assert.IsType<ModbusRtuFrame>(Assert.Single(model.Frames).Frame);
        Assert.Equal(1,analyzer.Snapshot().Statistics.CrcValid);
    }
}
