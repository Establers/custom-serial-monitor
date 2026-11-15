using System.Text.Json;
using Windows.Foundation;

public static partial class StartupHook
{
    // Opt-in operator fixture: input comes through the real Windows keyboard,
    // never by calling a button handler or synthesizing routed KeyDown events.
    private static async Task RunDialogKeyboardAudit()
    {
        S(Host,"Title","Serial Monitor - Dialog keyboard check");
        Call(P(Host,"AppWindow")!,"Resize",new Windows.Graphics.SizeInt32(800,600));
        await Settle(Root);
        var tabOnly = Environment.GetEnvironmentVariable("SERIAL_MODBUS_LAYOUT_FILTER") == "dialog-keyboard-tab";
        object Plain(string title, string defaultButton, bool input = false, bool secondary = false, bool enabled = true)
        {
            var dialog = New("Microsoft.UI.Xaml.Controls.ContentDialog");
            S(dialog,"Title",title); S(dialog,"PrimaryButtonText","적용"); S(dialog,"CloseButtonText","취소");
            S(dialog,"DefaultButton",defaultButton); S(dialog,"IsPrimaryButtonEnabled",enabled);
            if (secondary) S(dialog,"SecondaryButtonText","다른 동작");
            if (input)
            {
                var editor = New("Microsoft.UI.Xaml.Controls.TextBox");
                S(editor,"Name","AuditInput"); S(editor,"Text","25"); S(dialog,"Content",editor);
            }
            else S(dialog,"Content","키보드 검사 전용 창이에요. 제품 값이나 파일은 바꾸지 않아요.");
            return dialog;
        }
        async Task<string[]> Exercise(object dialog, string name, string expected, Func<object>? extra = null)
        {
            Case = name; S(dialog,"XamlRoot",P(Root,"XamlRoot"));
            var operation = Call(dialog,"ShowAsync")!;
            await Settle(dialog);
            if (P(dialog,"Content") is { } content && content.GetType().Name == "TextBox")
                Call(content,"Focus",Enum.Parse(FindType("Microsoft.UI.Xaml.FocusState"),"Programmatic"));
            var focusLog = new List<string>();
            var closingFocusLog = new List<string>();
            var layoutRoot = Tree(dialog).First(e => P(e,"Name")?.ToString() == "LayoutRoot");
            var focusMethod = FindType("Microsoft.UI.Xaml.Input.FocusManager").GetMethods()
                .Single(m => m.Name == "GetFocusedElement" && m.GetParameters().Length == 1);
            var deadline = DateTime.UtcNow.AddMinutes(3);
            while (((IAsyncInfo)operation).Status == AsyncStatus.Started)
            {
                var focus = focusMethod.Invoke(null,[P(dialog,"XamlRoot")]);
                var focusName = focus is null ? "none" : P(focus,"Name")?.ToString() ?? focus.GetType().Name;
                // The native closing storyboard disables input before ShowAsync
                // completes. Record restored focus separately from Tab traversal.
                var log = P(layoutRoot,"IsHitTestVisible") is false ? closingFocusLog : focusLog;
                if (log.LastOrDefault() != focusName && log.Count < 512) log.Add(focusName);
                File.WriteAllText(Path.Combine(Path.GetDirectoryName(Output)!,"keyboard-state.json"),
                    JsonSerializer.Serialize(new { name, focusName, focusLog, closingFocusLog, status = "waiting", extra = extra?.Invoke() }));
                if (DateTime.UtcNow > deadline) throw new TimeoutException("Keyboard fixture timed out: "+name);
                await Task.Delay(100);
            }
            var result = Call(operation,"GetResults")!.ToString();
            if (result != expected) Issue($"keyboard result {result}; expected {expected}");
            Results.Add(new { name, result, expected, focusLog, closingFocusLog, extra = extra?.Invoke(), formGrids = Array.Empty<object>() });
            Save(); await Task.Delay(250);
            return focusLog.ToArray();
        }
        if (!tabOnly)
        {
            await Exercise(Plain("1 · 전체 복원 — Enter는 취소", "Close"),"keyboard-restore-enter","None");
            await Exercise(Plain("2 · 값 적용 — 입력칸에서 Enter", "Primary",true),"keyboard-primary-enter","Primary");
            await Exercise(Plain("3 · 적용 불가 — Enter 후 Esc", "Primary",true,enabled:false),"keyboard-disabled-enter-escape","None");
        }
        var focus = await Exercise(Plain("4 · Tab 4회, Shift+Tab 4회, Esc", "Primary",true,true),"keyboard-tab-cycle","None");
        var expectedFocus = new[] { "AuditInput","PrimaryButton","SecondaryButton","CloseButton","AuditInput","CloseButton","SecondaryButton","PrimaryButton","AuditInput" };
        if (!focus.SequenceEqual(expectedFocus)) Issue("Tab/Shift+Tab did not cycle through the dialog's input and three buttons");
        if (tabOnly) return;
        await Exercise(Plain("5 · Tab 2회 후 Enter — 다른 동작", "Primary",true,true),"keyboard-secondary-enter","Secondary");

        var valueDialog = New("SerialMonitor.WinUI.Views.ModbusSensorValueDialog");
        var definition = FindType("SerialMonitor.Core.Modbus.ModbusSensorDefinition").GetMethod("Parse")!.Invoke(null,
            ["{\"schemaVersion\":1,\"name\":\"Keyboard\",\"unitId\":1,\"points\":[{\"name\":\"검사용 온도\",\"area\":4,\"address\":25,\"initialValue\":\"25\"}]}"])!;
        var point = ((System.Collections.IEnumerable)P(definition,"Points")!).Cast<object>().First();
        S(valueDialog,"DataContext",New("SerialMonitor.WinUI.ViewModels.ModbusDevicePointRow",0,point));
        S(valueDialog,"Title","6 · Enter → 적용 중 Enter/Esc → 오류 후 35 입력 → Enter");
        S(valueDialog,"Value","25");
        var attempts = 0;
        S(valueDialog,"ApplyValue",(Func<string,Task>)(async value => {
            attempts++;
            if (attempts == 1) { await Task.Delay(30000); throw new ArgumentException("검사용 실패예요. 35로 고쳐 다시 적용하세요."); }
            if (value != "35") throw new ArgumentException("35를 입력하세요.");
        }));
        await Exercise(valueDialog,"keyboard-async-deferral-retry","Primary",() => new {
            attempts, applying = Field(valueDialog,"_applying"), value = P(valueDialog,"Value"), error = P(Field(valueDialog,"ErrorText"),"Text") });
        if (attempts != 2 || P(valueDialog,"Value")?.ToString() != "35") Issue("async apply did not reject duplicate input and allow correction/retry");
        File.WriteAllText(Path.Combine(Path.GetDirectoryName(Output)!,"keyboard-state.json"),"{\"status\":\"complete\"}");
    }
}
