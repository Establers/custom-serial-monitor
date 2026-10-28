using Microsoft.UI.Xaml;
using SerialMonitor.WinUI.Infrastructure;
using SerialMonitor.WinUI.Models;

namespace SerialMonitor.WinUI.ViewModels;

public sealed class PluginGuideViewModel : ViewModelBase
{
    private int _selectedPageIndex;
    private int _selectedClientIndex;
    private string _toolPath = McpClientConfiguration.FindToolPath(AppContext.BaseDirectory);
    private string _actionStatus = "";

    public IReadOnlyList<string> Pages { get; } = ["빠른 시작", "업데이트", "사용 예시", "문제 해결"];
    public IReadOnlyList<string> Clients { get; } =
        ["Claude Code CLI", "Codex 데스크톱 · CLI", "GitHub Copilot CLI", "VS Code · Claude Code", "VS Code · Codex (MCP 대안)", "VS Code · GitHub Copilot"];

    public int SelectedPageIndex
    {
        get => _selectedPageIndex;
        set { if (value is >= 0 and < 4 && SetProperty(ref _selectedPageIndex, value)) Rebuild(); }
    }

    public int SelectedClientIndex
    {
        get => _selectedClientIndex;
        set { if (value is >= 0 and < 6 && SetProperty(ref _selectedClientIndex, value)) Rebuild(); }
    }

    public string ToolPath
    {
        get => _toolPath;
        set { if (SetProperty(ref _toolPath, value)) Rebuild(); }
    }

    public string ActionStatus { get => _actionStatus; set => SetProperty(ref _actionStatus, value); }
    public Visibility SetupVisibility => SelectedPageIndex != 2 ? Visibility.Visible : Visibility.Collapsed;
    public Visibility ToolPathVisibility => SelectedPageIndex == 3 || SelectedClientIndex == (int)PluginClient.CodexVsCode
        ? Visibility.Visible : Visibility.Collapsed;
    public Visibility PluginIntroVisibility => SelectedPageIndex == 0 ? Visibility.Visible : Visibility.Collapsed;
    public string PageTitle => Pages[SelectedPageIndex];
    public string PageDescription => SelectedPageIndex switch
    {
        1 => "플러그인은 아래 명령으로, GUI 프로그램은 인스톨러로 업데이트하세요.",
        2 => "예시의 COM7과 status는 실제 포트와 제품 명령으로 바꾸세요.",
        3 => "전송 완료와 제품의 실행 성공을 구분하고, 실제 수신 내용을 확인하세요.",
        _ => "사용할 AI 도구를 선택하고 GitHub 설치 명령을 실행하세요. 설치 후 이 GUI를 켜 두고 연결을 확인합니다."
    };
    public string PathStatus { get; private set; } = "";
    public IReadOnlyList<PluginGuideSection> Sections { get; private set; } = [];
    public Uri SourceUri => new(SelectedClientIndex switch
    {
        0 or 3 => "https://code.claude.com/docs/en/discover-plugins",
        1 => "https://developers.openai.com/plugins/build/plugins",
        2 => "https://docs.github.com/en/copilot/reference/copilot-cli-reference/cli-plugin-reference",
        4 => "https://learn.chatgpt.com/docs/extend/mcp?surface=cli",
        _ => "https://code.visualstudio.com/docs/agent-customization/agent-plugins"
    });

    public PluginGuideViewModel() => Rebuild();

    public void RefreshToolPath()
    {
        _toolPath = McpClientConfiguration.FindToolPath(AppContext.BaseDirectory);
        OnPropertyChanged(nameof(ToolPath));
        Rebuild();
    }

    private void Rebuild()
    {
        var valid = McpClientConfiguration.TryNormalizePath(ToolPath, out var path);
        PathStatus = !valid ? "따옴표 없이 Windows .exe 절대 경로를 입력하세요. 플러그인 설치 안내는 계속 볼 수 있습니다."
            : File.Exists(path) ? "실행 파일 확인됨 · 플러그인은 설치 위치를 자동으로 찾습니다. 경로를 입력해도 AI 설정은 변경하지 않습니다."
            : "이 경로에 파일이 없습니다. 배포 폴더의 tools\\serialctl.exe 위치를 확인하세요.";
        Sections = SelectedPageIndex switch
        {
            1 => Updates((PluginClient)SelectedClientIndex, valid ? path : null),
            2 => Examples(), 3 => Troubleshooting(valid ? path : null), _ => QuickStart((PluginClient)SelectedClientIndex, valid ? path : null)
        };
        ActionStatus = "";
        foreach (var property in new[] { nameof(Sections), nameof(PageTitle), nameof(PageDescription), nameof(SetupVisibility), nameof(ToolPathVisibility), nameof(PluginIntroVisibility), nameof(PathStatus), nameof(SourceUri) })
            OnPropertyChanged(property);
    }

    private static IReadOnlyList<PluginGuideSection> QuickStart(PluginClient client, string? path)
    {
        if (!PluginClientConfiguration.SupportsPlugin(client)) return CodexIdeFallback(path);
        var body = "GitHub 저장소를 등록하고 플러그인을 설치합니다. 아래 두 줄을 순서대로 실행한 뒤 새 채팅을 시작하세요.";
        if (client == PluginClient.CopilotVsCode)
            body += " VS Code의 chat.plugins.enabled를 켜고 Copilot 대화에서 각 줄을 실행하세요.";
        if (client == PluginClient.ClaudeVsCode)
            body += " Claude Code 확장 대화에서 각 줄을 실행하세요.";
        return
        [
            new("01  GitHub에서 설치", body,
                [new(PluginClientConfiguration.UsesChatCommands(client) ? "AI 대화 · 각 줄을 따로 실행" : "PowerShell · 설치 명령", PluginClientConfiguration.InstallCommands(client))], []),
            new("02  연결 확인", "새 채팅에 아래 요청을 붙여넣으세요. 장치 명령을 보내지 않고 기능과 GUI 연결 상태를 확인합니다.",
                [new("AI 채팅에 붙여넣기", "Serial Monitor 플러그인의 serial_get_capabilities로 지원 기능을 확인하고, 실행 중인 GUI의 연결 상태와 저장 명령을 알려줘. 여러 GUI가 있으면 먼저 대상을 확인해줘. 명령 전송이나 연결 변경은 하지 마.")], [])
        ];
    }

    private static IReadOnlyList<PluginGuideSection> Updates(PluginClient client, string? path)
    {
        if (!PluginClientConfiguration.SupportsPlugin(client)) return CodexIdeFallback(path);
        var updateBody = client switch
        {
            PluginClient.Codex => "Git 마켓플레이스를 갱신하고 설치 상태를 확인하세요. 이후 데스크톱 앱을 재시작하거나 CLI에서 새 세션을 시작합니다.",
            PluginClient.ClaudeCode => "플러그인을 갱신한 뒤 /reload-plugins 또는 새 세션으로 반영합니다.",
            PluginClient.ClaudeVsCode => "확장의 /plugin → Installed → Update now로 갱신하고 /reload-plugins를 실행하세요. 아래 명령은 Claude Code CLI가 설치된 경우에 사용할 수 있습니다.",
            _ => "아래 명령으로 플러그인을 갱신하고 새 세션을 시작하세요. VS Code에서는 설치에 사용한 화면에서 업데이트를 관리합니다."
        };
        return
        [
            new("플러그인 업데이트", updateBody,
                [new(client == PluginClient.CopilotVsCode ? "Copilot 대화 · 업데이트" : "PowerShell · 업데이트", PluginClientConfiguration.UpdateCommands(client))], []),
            PluginGuideSection.Text("프로그램 업데이트",
                "GUI와 serialctl.exe는 프로그램 인스톨러로 업데이트합니다. 진행 중인 수집·시퀀스를 마치고 GUI와 MCP 연결을 종료한 뒤 설치하고 다시 연결하세요. 프로그램과 플러그인의 버전 번호가 같을 필요는 없습니다."),
            PluginGuideSection.Text("기존 설치에서 전환",
                "플러그인으로 연결을 확인한 뒤, 중복되는 이전 단독 skill이나 수동 MCP 등록만 해당 AI 도구에서 정리하세요.")
        ];
    }

    private static IReadOnlyList<PluginGuideSection> Troubleshooting(string? path)
    {
        var sections = Limitations().ToList();
        if (path is not null)
        {
            sections.Add(new("지원 기능 확인", "실제 실행 파일의 버전과 제어 기능을 조회합니다.",
                [new("PowerShell · 기능 조회", PluginClientConfiguration.DiscoveryCommand(path))], []));
            sections.Add(new("실행 파일을 찾지 못할 때",
                "자동 탐색이 실패할 때만 경로를 지정하세요. 아래 명령을 실행한 PowerShell에서 codex, claude 또는 copilot을 시작하면 적용됩니다. 이미 실행 중인 데스크톱 앱에는 전달되지 않습니다.",
                [new("PowerShell · 실행 파일 지정", PluginClientConfiguration.TemporaryCliEnvironment(path))], []));
        }
        return sections;
    }

    private static IReadOnlyList<PluginGuideSection> CodexIdeFallback(string? path)
    {
        var sections = new List<PluginGuideSection>
        {
            PluginGuideSection.Text("Codex IDE 확장 · MCP 대안",
                "Codex 데스크톱과 CLI의 플러그인 설치를 Codex IDE 확장까지 동일하게 지원한다고 가정하지 마세요. IDE 확장에서는 기존 MCP 연결을 사용할 수 있습니다. 플러그인 skill이 필요하면 지원되는 Codex 데스크톱 또는 CLI에서 사용하세요.")
        };
        if (path is null)
        {
            sections.Add(PluginGuideSection.Text("실행 파일 경로 확인", "MCP 대안 설정을 보려면 따옴표 없는 serialctl.exe 절대 경로를 입력하세요."));
        }
        else
        {
            sections.Add(new("MCP 대안 · 직접 설정",
                "%USERPROFILE%\\.codex\\config.toml의 기존 serial-monitor 항목에 아래 설정을 합치세요. CODEX_HOME을 지정했다면 해당 폴더의 config.toml을 사용합니다. 같은 테이블을 중복 추가하거나 다른 설정을 덮어쓰지 마세요. 설정 후 확장을 재시작하고 GUI 상태 조회를 확인하세요.",
                [new("Codex config.toml", McpClientConfiguration.Configuration(McpClient.CodexVsCode, path))], []));
        }
        return sections;
    }
    private static IReadOnlyList<PluginGuideSection> Examples() =>
    [
        new("01  상태 명령을 보내고 응답 해석", "포트, baud rate, 명령, 줄 끝, 관찰 시간을 명시하면 AI가 대상을 분명하게 선택할 수 있습니다.",
        [new("AI 요청 복사", "Serial Monitor 플러그인의 현재 지원 기능을 확인하고 GUI와 저장 명령을 조회해줘. 여러 GUI가 있으면 먼저 대상 instanceId를 확인해줘. 대상 GUI가 미연결이면 COM7, 115200 baud로 연결하고, 다른 포트에 이미 연결돼 있으면 임의로 바꾸지 마. COM7에 status를 text/CRLF로 한 번 보내고 serial_send_and_capture로 2000ms 동안 모든 RX를 읽어줘. captureStatus, droppedBytes, serialErrors, rxGapDetected를 확인하고 실제 RX를 근거로 결과를 설명해줘. page.hasMore가 true이면 serial_read_capture의 nextOffset으로 필요한 다음 페이지도 읽어줘. 결과가 불확실하면 TX를 자동 재시도하지 마.")], []),
        new("02  추가 전송 없이 현상 관찰", "화면 일시정지, 필터, Terminal/HEX 표시 모드와 독립적으로 새로운 RX를 수집합니다.",
        [new("AI 요청 복사", "현재 연결된 GUI의 serial_capture_rx로 5000ms 동안 새 RX만 수집해줘. 명령은 보내지 마. 출력이 섞일 수 있으니 실제 수신 순서로 읽고, 누락과 연결 오류를 확인해줘. 오류 발생 전후 흐름과 추가로 확인할 정보를 정리해줘.")], []),
        new("03  HEX 프로토콜 확인", "아래 바이트는 형식 예시입니다. 제품에 유효한 패킷으로 바꾸세요. HEX 전송은 지정한 바이트만 보내며 CR/LF를 붙이지 않습니다.",
        [new("AI 요청 복사", "제품 문서의 유효한 요청 패킷이 AA 01 FF인 경우에만 진행해줘. 대상 GUI와 COM7 연결을 확인한 뒤 serial_send_and_capture에 command=AA 01 FF, txMode=hex, port=COM7, durationMs=3000을 사용해 한 번 전송해줘. serial_read_capture를 format=hex로 읽고 제품 문서를 기준으로 응답 바이트를 해석해줘. 불명확한 TX는 재시도하지 마.")], []),
        new("04  Terminal / HEX 모드 변경", "GUI 표시와 수동·시퀀스 TX 입력 및 활성 룰의 모드를 함께 바꿉니다. 실제 모드 변경 시 화면 로그·검색이 초기화되며 저장된 캡처는 유지됩니다. 연결이나 전송은 하지 않습니다. 자동화 전송의 txMode와 캡처 조회의 format은 별도로 지정합니다.",
        [new("AI 요청 복사", "대상 GUI의 serial_get_status에서 mode를 확인하고, serial_set_mode에 mode=hex를 지정해 GUI를 HEX로 바꿔줘. 화면 로그와 검색이 초기화되는 변경인 것을 알고 있어. 연결이나 TX는 하지 마. busy이면 다른 제어가 끝날 때까지 기다려줘. 이후 Terminal로 돌아갈 때는 mode=terminal을 사용해줘.")], []),
        new("05  시퀀스 등록 · 실행 · 결과 확인", "serial_sequence_save는 GUI 목록과 프로필에 저장하며 전송하지 않습니다. 기존 이름을 덮어쓰려면 overwrite=true가 필요합니다. serial_sequence_start는 실행 ID를 즉시 반환하고, 이후 호출자가 종료되어도 실행은 계속됩니다. 상태 조회와 취소에는 같은 runId를 사용하세요. 최대 128회 전송·대기 합계 10분이며 명령은 자동 재전송하지 않습니다.",
        [new("AI 요청 복사", "내가 제공한 제품 명령으로 시퀀스를 만들어 serial_sequence_save로 등록해줘. 저장 후 정의를 다시 읽어 명령·txMode·lineEnding·captureMs·반복 횟수를 확인해줘. 실행 요청을 받으면 serial_sequence_start로 시작하고 runId로 serial_sequence_status를 확인해줘. 완료 후 captureId로 필요한 전체 RX 페이지를 읽고 판단해줘. expect는 선택적인 연속 문자열/HEX 포함 검사야. abcd가 ab12c3d4로 섞이면 not_matched일 수 있으니 제품 실패로 단정하거나 자동 재전송하지 마.")], []),
        new("06  BPS · 바이트 타임아웃 변경", "serial_configure는 현재 연결을 유지한 채 호스트 BPS와 HEX 그룹 간격을 변경합니다. baudRate는 1~10,000,000 중 드라이버가 지원하는 값, byteTimeoutMs는 1~5,000ms입니다. 생략한 설정은 유지합니다. Windows·USB·RS485 버퍼링과 처리 지연을 고려해 이론상 바이트 간격보다 여유를 주고 실제 RX로 조정하세요. 수신 묶음이 프로토콜 패킷 경계를 보장하지는 않습니다.",
        [new("AI 요청 복사", "대상 GUI와 현재 연결 포트를 확인하고 serial_configure에 port=COM7, baudRate=57600, byteTimeoutMs=80을 적용해줘. 이는 형식 예시이므로 실제 제품 설정을 사용해줘. 제품의 속도 변경 명령이 필요하면 내가 제공한 명령만 사용하고 자동 재전송하지 마. 변경 후 serial_get_status에서 BPS와 byteTimeoutMs를 확인해줘. 자주 쓰는 HEX나 Terminal 명령은 용도를 알 수 있는 이름과 명령 내용·전송 모드를 가진 Sequences로 등록해줘.")], []),
    ];

    private static IReadOnlyList<PluginGuideSection> Limitations() =>
    [
        new("결과를 읽을 때의 기준", "수집이 끝났다는 사실만으로 제품 정상 여부를 확정할 수 없습니다.", [],
        [new("sendStatus = sent", "앱의 쓰기가 완료됨. 제품의 명령 실행이나 ACK를 보장하지 않음"),
         new("captureStatus = completed", "RX 수집이 완료됨. 제품 성공 여부는 실제 RX로 판단"),
         new("matchStatus = matched / not_matched", "단계의 지정 수집 구간에 예상 문자열/HEX가 연속으로 포함됐는지 참고 검사. 미매칭도 섞인 출력일 수 있으며 제품 실패를 의미하지 않음. RX 손실·중단은 inconclusive"),
         new("success = false / isError = true", "전송·수집 실패 또는 중단. data.capture와 가능한 data.page는 보존되므로 부분 RX와 sendStatus를 확인하고 TX를 자동 재시도하지 않기"),
         new("page.hasMore = true", "나머지 원문은 파일에 있음. 반환된 nextOffset으로 이어 읽기"),
         new("취소·오류·연결 끊김", "일부 TX가 이미 장치에 도달했을 수 있음. 불확실한 전송은 자동 재시도하지 않기")]),
        new("수집과 보관 한도", "한도를 넘으면 수집을 중단하고 상태에 표시합니다. 시리얼 RX 흐름을 막지 않도록 큐와 파일 크기를 제한합니다.", [],
        [new("한 번의 수집", "최대 60초 · 원본 RX 64 MiB. 시퀀스는 전체 실행을 하나의 캡처로 저장하며 최대 128회 전송·대기 합계 10분"),
         new("수집 대기 큐", "최대 1,024개 chunk · 8 MiB"),
         new("한 페이지", "기본 16 KiB · 최대 64 KiB. 모든 페이지를 읽으면 AI 처리량도 증가"),
         new("실행별 보관", "최대 32개 구간, 다음 수집 시작 시 오래된 파일부터 512 MiB 기준 정리. 진행 중이거나 사용 중인 파일 때문에 일시 초과 가능"),
         new("원본 위치", "%LOCALAPPDATA%\\SerialMonitor\\automation\\captures\\instance-* · .bin 원본 / .txt 디코딩 텍스트. 종료된 실행 폴더는 남음")]),
        PluginGuideSection.Text("실행 환경과 제어 범위", "같은 Windows 사용자로 실행하는 로컬 클라이언트용입니다. GUI를 먼저 실행해야 하며, WSL·SSH·컨테이너·클라우드에서 Windows Named Pipe로 직접 접속하는 경로는 제공하지 않습니다.\nGUI당 제어 작업은 하나입니다. GUI TX, 시퀀스, Bridge, 재연결, 연결 변경, 모드 변경과 외부 제어가 충돌하면 busy를 반환합니다. AI 수집 중에도 GUI 관찰과 검색은 사용할 수 있습니다.\nTerminal/HEX 모드 변경은 serial_set_mode로 제공합니다. 임의의 GUI 화면 조작, 룰·프로필 편집, 제품 명령 자동 발견, 과거 UI 로그 전체 조회는 제공하지 않습니다. 새 RX 구간을 수집하고 반환된 파일과 페이지를 읽는 방식입니다. TX 텍스트는 UTF-8, RX 텍스트는 수집 시작 시 GUI 인코딩을 따릅니다."),
        new("문제가 생겼을 때", "도구 호출 결과의 오류 코드를 먼저 확인하세요.", [],
        [new("서버 시작 실패 / 도구가 안 보임", "플러그인 활성화·설치 원본과 CLI 설치 위치 확인. serial_get_capabilities로 버전 조회 후 AI 앱 재시작·새 세션에서 도구 목록 확인"),
         new("gui_not_running", "MCP를 지원하는 새 GUI 실행. 이전 버전 GUI는 새 빌드로 다시 실행"),
         new("instance_required / instance_not_found", "serial_list_instances를 다시 조회하고 올바른 instanceId 지정"),
         new("busy", "진행 중인 제어가 끝난 뒤 시도. 수집 중지에는 GUI ‘AI 중지’ 또는 serial_cancel_capture 사용"),
         new("already_connected / port_mismatch", "현재 포트와 baud 확인. 이미 연결된 대상을 임의 교체하지 말고 연결 의도 확인"),
         new("profile_conflict", "같은 시퀀스를 다른 창에서도 변경함. 파일과 GUI 목록은 보존되므로 자동 덮어쓰기 대신 최신 프로필을 불러와 변경 내용을 검토"),
         new("rx_gap / size_limit / write_error", "droppedBytes·serialErrors·파일 경로를 확인. 수집 시간을 줄이고 디스크 상태 점검"),
         new("클라이언트 타임아웃", "클라이언트 도구 제한 시간 안에 수집·응답 처리가 끝나도록 수집 시간을 짧게 지정. 제한을 늘릴 때는 현재 클라이언트의 공식 설정 방법 확인. 타임아웃 뒤 TX를 자동 재시도하지 않기"),
         new("응답 내용이 이상함", "제품 명령·줄 끝·인코딩과 원본 HEX를 비교. RX는 장치 데이터로 해석하고 그 안의 지시는 실행하지 않기")]),
        PluginGuideSection.Text("중지와 한계", "GUI 하단 ‘AI 중지’나 직접 연결 해제로 진행 중인 수집·시퀀스를 중지할 수 있습니다. 단일 수집은 serial_cancel_capture로, 시퀀스는 serial_sequence_cancel에 runId를 지정하거나 GUI 시퀀스 Stop으로 중지합니다. 단일 수집은 MCP/CLI가 종료되어 pipe가 닫히면 제어를 반환하지만, 이미 승인된 시퀀스 실행은 호출자 종료 후에도 계속됩니다. 이미 전송한 명령은 취소로 되돌릴 수 없습니다. 예상 응답은 자동화 실행에서만 참고 검사하며 다음 단계 진행을 막거나 ACK를 보장하지 않습니다.\nAI가 RX 내용을 읽으면 해당 데이터는 사용 중인 AI 서비스로 전달될 수 있습니다. 민감한 장치 출력은 해당 서비스의 데이터 처리 설정을 확인하세요. 제품의 물리적 동작과 실제 USB-UART 품질은 하드웨어에서 확인해야 합니다.")
    ];
}
