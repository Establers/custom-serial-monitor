using Microsoft.UI.Xaml;
using SerialMonitor.WinUI.Infrastructure;
using SerialMonitor.WinUI.Models;

namespace SerialMonitor.WinUI.ViewModels;

public sealed class McpGuideViewModel : ViewModelBase
{
    private int _selectedPageIndex;
    private int _selectedClientIndex;
    private string _toolPath = McpClientConfiguration.FindToolPath(AppContext.BaseDirectory);
    private string _actionStatus = "";

    public IReadOnlyList<string> Pages { get; } = ["빠른 시작", "클라이언트 설치", "사용 예시", "한계 · 문제 해결"];
    public IReadOnlyList<string> Clients { get; } =
        ["Claude Code CLI", "Codex CLI", "GitHub Copilot CLI", "VS Code · Claude Code", "VS Code · Codex", "VS Code · GitHub Copilot"];

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
    public Visibility SetupVisibility => SelectedPageIndex is 0 or 1 ? Visibility.Visible : Visibility.Collapsed;
    public Visibility SkillIntroVisibility => SelectedPageIndex == 0 ? Visibility.Visible : Visibility.Collapsed;
    public string PageTitle => SelectedPageIndex switch
    {
        1 => "클라이언트 설치", 2 => "사용 예시",
        3 => "한계 · 문제 해결", _ => "빠른 시작"
    };
    public string PageDescription => SelectedPageIndex switch
    {
        1 => "클라이언트를 선택하고 실행 파일 경로를 확인한 뒤, 설정을 복사하세요.",
        2 => "예시의 COM7과 status는 실제 포트와 제품 명령으로 바꾸세요.",
        3 => "전송 완료와 제품의 실행 성공을 구분하고, 실제 수신 내용을 확인하세요.",
        _ => "이 창을 켜 둔 채 아래 설정으로 등록하고 연결을 확인하세요. AI 도구는 미리 설치·로그인해 두세요."
    };
    public string PathStatus { get; private set; } = "";
    public IReadOnlyList<McpGuideSection> Sections { get; private set; } = [];
    public Uri SourceUri => new(SelectedClientIndex switch
    {
        0 => "https://code.claude.com/docs/en/mcp",
        1 or 4 => "https://learn.chatgpt.com/docs/extend/mcp?surface=cli",
        2 => "https://docs.github.com/en/copilot/how-tos/copilot-cli/customize-copilot/add-mcp-servers",
        3 => "https://code.claude.com/docs/en/vs-code#connect-to-external-tools-with-mcp",
        _ => "https://code.visualstudio.com/docs/agents/reference/mcp-configuration"
    });

    public McpGuideViewModel() => Rebuild();

    public void RefreshToolPath()
    {
        _toolPath = McpClientConfiguration.FindToolPath(AppContext.BaseDirectory);
        OnPropertyChanged(nameof(ToolPath));
        Rebuild();
    }

    private void Rebuild()
    {
        var valid = McpClientConfiguration.TryNormalizePath(ToolPath, out var path);
        PathStatus = !valid ? "따옴표 없이 Windows .exe 절대 경로를 입력하세요."
            : File.Exists(path) ? "실행 파일 확인됨 · 경로를 바꾸면 아래 설정도 함께 갱신됩니다."
            : "이 경로에 파일이 없습니다. 배포 폴더의 tools\\serialctl.exe 위치를 확인하세요.";
        Sections = !valid && (SelectedPageIndex is 0 or 1)
            ? [McpGuideSection.Text("실행 파일 경로를 먼저 확인하세요", "예: C:\\SerialMonitor\\tools\\serialctl.exe\n따옴표 없는 절대 경로를 입력하면 등록 설정과 연결 테스트가 표시됩니다.")]
            : SelectedPageIndex switch
            {
                1 => Installation((McpClient)SelectedClientIndex, path),
                2 => Examples(), 3 => Limitations(), _ => QuickStart((McpClient)SelectedClientIndex, path)
            };
        ActionStatus = "";
        foreach (var property in new[] { nameof(Sections), nameof(PageTitle), nameof(PageDescription), nameof(SetupVisibility), nameof(SkillIntroVisibility), nameof(PathStatus), nameof(SourceUri) })
            OnPropertyChanged(property);
    }

    private static IReadOnlyList<McpGuideSection> QuickStart(McpClient client, string path)
    {
        var snippets = new List<McpGuideSnippet>();
        var setup = client switch
        {
            McpClient.ClaudeVsCode => "프로젝트 루트의 .mcp.json에 아래 서버 항목을 추가하세요. 기존 mcpServers가 있으면 그 안에 합칩니다.",
            McpClient.CodexVsCode => "%USERPROFILE%\\.codex\\config.toml에 아래 테이블을 추가하세요. 같은 테이블이 있으면 수정하고, CODEX_HOME을 지정했다면 그 폴더를 사용하세요.",
            McpClient.CopilotVsCode => "프로젝트의 .vscode\\mcp.json에 아래 서버 항목을 추가하세요. 기존 servers가 있으면 그 안에 합칩니다.",
            McpClient.CodexCli => "아래 명령을 PowerShell에서 실행하세요. 이어서 config.toml의 기존 [mcp_servers.serial-monitor] 테이블에 아래 제한 시간 값도 추가·수정하세요.",
            _ => "아래 명령을 Windows PowerShell에서 실행하세요. 이름은 serial-monitor, 실행 방식은 stdio로 등록됩니다."
        };
        if (client is McpClient.ClaudeVsCode or McpClient.CodexVsCode or McpClient.CopilotVsCode)
        {
            snippets.Add(new("설정 파일에 추가 · 기존 설정은 유지", McpClientConfiguration.Configuration(client, path)));
        }
        else
        {
            snippets.Add(new("PowerShell · 서버 등록", McpClientConfiguration.RegistrationCommand(client, path)));
            if (client == McpClient.CodexCli)
            {
                setup += " 파일은 %USERPROFILE%\\.codex\\config.toml이며, CODEX_HOME을 지정했다면 그 폴더에 있습니다. 기본 60초 제한과 최대 60초 수집의 충돌을 방지합니다.";
                snippets.Add(new("필수 · 기존 테이블 안에 추가/수정", McpClientConfiguration.CodexTimeoutSetting));
            }
        }

        var check = client switch
        {
            McpClient.ClaudeCli => "새 Claude 대화에서 /mcp로 서버를 확인한 뒤 아래 요청을 붙여넣으세요.",
            McpClient.CodexCli => "Codex를 다시 시작하고 /mcp로 서버를 확인한 뒤 아래 요청을 붙여넣으세요.",
            McpClient.CopilotCli => "Copilot 대화의 /mcp에서 서버 상태를 확인한 뒤 아래 요청을 붙여넣으세요.",
            McpClient.ClaudeVsCode => "Claude Code 확장에서 새 대화를 열고 /mcp로 서버를 확인한 뒤 아래 요청을 붙여넣으세요.",
            McpClient.CodexVsCode => "Codex 확장을 재시작한 뒤 새 대화에 아래 요청을 붙여넣으세요.",
            _ => "MCP: List Servers에서 서버를 시작하고, Copilot의 MCP 도구를 사용할 수 있는 Agent 대화에 아래 요청을 붙여넣으세요."
        };
        return
        [
            new("01  MCP 설치 · 등록", setup, snippets, []),
            new("02  연결 테스트", check + "\nGUI의 instanceId와 연결 상태가 조회되면 MCP 연결을 확인한 것입니다. COM 포트가 미연결이어도 이 확인은 가능합니다.",
                [new("AI 채팅에 붙여넣기", "serial_list_instances로 실행 중인 Serial Monitor를 찾고, 대상의 serial_get_status와 serial_list_commands를 조회해줘. 여러 GUI가 있으면 먼저 대상 instanceId를 확인해줘. 연결 상태와 저장 명령을 알려주고, 명령 전송이나 연결 변경은 하지 마.")], []),
            new("03  RX 수집 테스트 · 포트 연결 후", "GUI에서 장치 포트를 연결한 뒤 아래 요청으로 1초간 수신만 확인합니다. 조용한 장치는 수신 바이트가 0일 수 있습니다. 실제 TX 예시는 ‘사용 예시’에서 확인하세요.",
                [new("AI 채팅에 붙여넣기", "대상 Serial Monitor GUI의 현재 연결을 확인하고, 연결되어 있으면 serial_capture_rx로 1000ms 동안 새 RX만 수집해줘. 명령은 보내지 말고 연결도 바꾸지 마. captureStatus, receivedBytes, droppedBytes와 실제 RX를 알려줘. 미연결이면 수집하지 말고 연결이 필요하다고 알려줘.")], [])
        ];
    }

    private static IReadOnlyList<McpGuideSection> Installation(McpClient client, string path)
    {
        var sections = new List<McpGuideSection>();
        var claude = client is McpClient.ClaudeCli or McpClient.ClaudeVsCode;
        var codex = client is McpClient.CodexCli or McpClient.CodexVsCode;
        if (client != McpClient.CopilotVsCode)
        {
            var body = claude
                ? "Windows PowerShell에서 실행합니다. --scope user로 사용자 범위에 등록해 CLI와 Claude Code 확장에서 함께 사용합니다. 프로젝트에만 공유하려면 --scope project를 사용하세요."
                : codex
                    ? "Windows PowerShell에서 등록 명령을 실행한 뒤, 아래 ‘02 제한 시간 설정’까지 완료하세요. 등록 명령만으로는 Codex 기본 60초 제한이 유지되어 최대 60초 RX 수집과 충돌합니다. Codex CLI와 같은 호스트의 Codex IDE 확장이 MCP 설정을 공유합니다."
                    : "Windows PowerShell에서 실행합니다. 명령을 지원하지 않는 버전은 Copilot 대화에서 /mcp add를 사용하세요. 종류는 STDIO, 이름은 serial-monitor, 명령은 아래 EXE 경로와 mcp 인자, Tools는 *로 설정합니다.";
            sections.Add(new("01  MCP 서버 등록", body,
                [new("PowerShell · 등록 명령", McpClientConfiguration.RegistrationCommand(client, path))], []));
        }

        if (codex)
        {
            sections.Add(new("02  제한 시간 설정 · 등록 명령 사용 후 필수",
                $"%USERPROFILE%\\.codex\\config.toml을 열고 기존 [mcp_servers.serial-monitor] 테이블 안에 아래 값을 추가하거나 수정하세요. CODEX_HOME을 지정했다면 그 폴더의 config.toml을 사용합니다. 같은 테이블을 다시 만들지 말고, 다음 [테이블]이 시작되기 전에 넣으세요.\n도구 제한을 {McpClientConfiguration.CodexToolTimeoutSeconds}초로 두어 최대 60초 수집에 더해 명령 전송·파일 저장·응답 처리 시간을 확보합니다. 설정 후 Codex 대화나 확장을 재시작하세요.",
                [new("기존 [mcp_servers.serial-monitor] 테이블 안에 추가/수정", McpClientConfiguration.CodexTimeoutSetting)], []));
        }

        var location = claude ? "프로젝트 루트의 .mcp.json"
            : codex ? "%USERPROFILE%\\.codex\\config.toml"
            : client == McpClient.CopilotCli ? "%USERPROFILE%\\.copilot\\mcp-config.json" : "프로젝트의 .vscode\\mcp.json";
        var configBody = claude
            ? "프로젝트 단위로 등록하려면 아래 JSON을 사용합니다. 사용자 범위 등록 명령과는 대안 관계입니다. 이미 같은 이름을 등록했다면 중복 설정을 피하세요. CLI 기본 local 범위와 user/project 범위는 서로 다릅니다."
            : codex
                ? $"등록 명령 대신 아래 TOML 블록으로 직접 등록할 수 있습니다. 이 블록에는 {McpClientConfiguration.CodexToolTimeoutSeconds}초 제한 설정이 포함되어 있습니다. 같은 이름의 테이블이 있으면 새로 추가하지 말고 해당 항목을 수정하세요. CODEX_HOME을 지정한 환경은 그 설정 폴더를 사용합니다."
                : client == McpClient.CopilotCli
                    ? "직접 편집할 때는 기존 mcpServers 안에 serial-monitor 항목을 합칩니다. COPILOT_HOME을 지정한 환경은 해당 폴더를 확인하세요. VS Code용 servers 형식과 구분하세요."
                    : "VS Code 명령 팔레트에서 MCP: Open Workspace Folder MCP Configuration을 열거나 .vscode/mcp.json을 만듭니다. 아래 servers 안의 항목을 기존 설정에 합치세요. Copilot CLI의 mcpServers 형식과 구분하세요.";
        sections.Add(new(client == McpClient.CopilotVsCode ? "01  VS Code 설정 추가" : "직접 설정 파일로 등록하기", configBody,
            [new(location, McpClientConfiguration.Configuration(client, path))], []));

        var verify = client switch
        {
            McpClient.ClaudeCli => "claude mcp list로 등록을 확인하고 새 대화를 시작합니다. /mcp에서 serial-monitor 연결을 확인한 뒤 도구 사용 요청을 보냅니다.",
            McpClient.CodexCli => "codex mcp list로 등록을 확인하고 Codex를 다시 시작합니다. 대화에서 /mcp로 활성 서버를 확인합니다.",
            McpClient.CopilotCli => "Copilot 대화에서 /mcp로 상태를 확인합니다. /mcp add로 저장한 서버는 바로 사용할 수 있습니다.",
            McpClient.ClaudeVsCode => "Claude Code 확장 채팅에서 /mcp로 서버를 확인하고 새 대화를 시작합니다. 최신 확장은 /mcp에서 직접 서버 추가도 지원합니다. 통합 터미널의 claude mcp add는 별도 Claude Code CLI 설치가 필요합니다.",
            McpClient.CodexVsCode => "Codex 확장의 톱니바퀴 메뉴 → MCP servers → Add server에서도 등록할 수 있습니다. 종류 STDIO, command는 위 EXE 경로, args는 mcp입니다. 저장 후 Restart extension을 실행합니다.",
            _ => "MCP: List Servers에서 serial-monitor를 시작하고 도구 목록을 확인합니다. Copilot Chat에서 MCP 도구를 사용할 수 있는 Agent 모드/세션을 선택합니다. 작업 영역 신뢰나 조직 정책으로 도구가 차단되면 해당 설정을 확인하세요."
        };
        sections.Add(McpGuideSection.Text(codex ? "03  연결 확인과 첫 사용" : "02  연결 확인과 첫 사용",
            verify + (codex ? $"\n기존 serial-monitor 테이블의 {McpClientConfiguration.CodexTimeoutSetting} 설정도 확인하세요." : "") +
            "\n서버 등록 여부와 GUI 연결 성공은 별개입니다. serial_list_instances로 실제 GUI 조회까지 확인하세요."));
        sections.Add(McpGuideSection.Text("설정할 때 기억할 점", "서버 이름은 serial-monitor, 전송 방식은 stdio, 서버 인자는 mcp입니다. EXE 경로와 인자를 한 필드에 합치지 마세요(JSON/TOML 기준). 기존 설정 전체를 덮어쓰지 말고 서버 항목을 추가하세요.\n이 앱의 로컬 MCP에는 별도 API 키나 HTTP URL이 필요하지 않습니다. AI 도구의 로그인과 이용 권한은 각 도구에서 준비하세요. VS Code의 Copilot 설정은 Claude Code·Codex 확장의 전용 설정과 별개입니다.\n설정 형식과 메뉴는 클라이언트 버전에 따라 달라질 수 있습니다. 아래 공식 문서에서 확인할 수 있습니다."));
        return sections;
    }

    private static IReadOnlyList<McpGuideSection> Examples() =>
    [
        new("01  상태 명령을 보내고 응답 해석", "포트, baud rate, 명령, 줄 끝, 관찰 시간을 명시하면 AI가 대상을 분명하게 선택할 수 있습니다.",
        [new("AI 요청 복사", "serial-monitor MCP로 GUI와 저장 명령을 확인해줘. 여러 GUI가 있으면 먼저 대상 instanceId를 확인해줘. 대상 GUI가 미연결이면 COM7, 115200 baud로 연결하고, 다른 포트에 이미 연결돼 있으면 임의로 바꾸지 마. COM7에 status를 text/CRLF로 한 번 보내고 serial_send_and_capture로 2000ms 동안 모든 RX를 읽어줘. captureStatus, droppedBytes, serialErrors, rxGapDetected를 확인하고 실제 RX를 근거로 결과를 설명해줘. page.hasMore가 true이면 serial_read_capture의 nextOffset으로 필요한 다음 페이지도 읽어줘. 결과가 불확실하면 TX를 자동 재시도하지 마.")], []),
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
        new("자주 쓰는 MCP 도구", "이름을 프롬프트에 적으면 원하는 작업을 구체적으로 요청할 수 있습니다.", [],
        [new("serial_list_instances", "실행 중인 GUI와 instanceId 조회"),
         new("serial_get_status / serial_list_ports", "연결·설정·오류 및 사용 가능한 포트 조회"),
         new("serial_list_commands", "GUI에 저장된 TX 명령과 줄 끝 조회"),
         new("serial_connect / serial_disconnect", "GUI를 통한 연결·해제"),
         new("serial_configure", "현재 연결의 baudRate·byteTimeoutMs 변경. CLI: configure --baud 57600 --byte-timeout-ms 80. 장치 전송 없음"),
         new("serial_set_mode", "mode=terminal 또는 hex로 GUI RX·수동 TX 모드 변경. 화면 로그 초기화, 전송 없음. 현재 mode는 serial_get_status로 조회"),
         new("serial_sequence_list / serial_sequence_get", "GUI 시퀀스 목록·정의 조회. 목록은 offset/limit 페이지 지원"),
         new("serial_sequence_save / serial_sequence_delete", "시퀀스 등록·수정·삭제 및 프로필 저장. 전송 없음"),
         new("serial_sequence_start / serial_sequence_status", "실행 시작 및 runId로 진행·단계별 TX/매칭·전체 RX 캡처 조회"),
         new("serial_sequence_cancel", "지정 runId 실행 중지. 이미 보낸 명령은 되돌릴 수 없음"),
         new("serial_send", "명령 1회 전송. 응답을 기다리지 않음"),
         new("serial_send_and_capture", "전송 전 RX 구독 후 전송 및 지정 시간 수집"),
         new("serial_capture_rx", "명령을 보내지 않고 새 RX 수집"),
         new("serial_read_capture", "captureId / nextOffset으로 텍스트 또는 HEX 페이지 조회"),
         new("serial_cancel_capture", "진행 중인 수집 중지. 이미 전송한 명령은 유지")])
    ];

    private static IReadOnlyList<McpGuideSection> Limitations() =>
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
        McpGuideSection.Text("실행 환경과 제어 범위", "같은 Windows 사용자로 실행하는 로컬 클라이언트용입니다. GUI를 먼저 실행해야 하며, WSL·SSH·컨테이너·클라우드에서 Windows Named Pipe로 직접 접속하는 경로는 제공하지 않습니다.\nGUI당 제어 작업은 하나입니다. GUI TX, 시퀀스, Bridge, 재연결, 연결 변경, 모드 변경과 외부 제어가 충돌하면 busy를 반환합니다. AI 수집 중에도 GUI 관찰과 검색은 사용할 수 있습니다.\nTerminal/HEX 모드 변경은 serial_set_mode로 제공합니다. 임의의 GUI 화면 조작, 룰·프로필 편집, 제품 명령 자동 발견, 과거 UI 로그 전체 조회는 제공하지 않습니다. 새 RX 구간을 수집하고 반환된 파일과 페이지를 읽는 방식입니다. TX 텍스트는 UTF-8, RX 텍스트는 수집 시작 시 GUI 인코딩을 따릅니다."),
        new("문제가 생겼을 때", "도구 호출 결과의 오류 코드를 먼저 확인하세요.", [],
        [new("서버 시작 실패 / 도구가 안 보임", "EXE 경로, mcp 인자, stdio 종류, 클라이언트 설정 위치를 확인. 새 대화·확장 재시작 후 MCP 목록 확인"),
         new("gui_not_running", "MCP를 지원하는 새 GUI 실행. 이전 버전 GUI는 새 빌드로 다시 실행"),
         new("instance_required / instance_not_found", "serial_list_instances를 다시 조회하고 올바른 instanceId 지정"),
         new("busy", "진행 중인 제어가 끝난 뒤 시도. 수집 중지에는 GUI ‘AI 중지’ 또는 serial_cancel_capture 사용"),
         new("already_connected / port_mismatch", "현재 포트와 baud 확인. 이미 연결된 대상을 임의 교체하지 말고 연결 의도 확인"),
         new("profile_conflict", "같은 시퀀스를 다른 창에서도 변경함. 파일과 GUI 목록은 보존되므로 자동 덮어쓰기 대신 최신 프로필을 불러와 변경 내용을 검토"),
         new("rx_gap / size_limit / write_error", "droppedBytes·serialErrors·파일 경로를 확인. 수집 시간을 줄이고 디스크 상태 점검"),
         new("클라이언트 타임아웃", $"수집 시간과 파일 저장·응답 처리를 포함할 수 있는 도구 제한 설정. Codex 등록 명령 사용 후에도 기존 TOML 테이블에 {McpClientConfiguration.CodexTimeoutSetting} 추가/수정"),
         new("응답 내용이 이상함", "제품 명령·줄 끝·인코딩과 원본 HEX를 비교. RX는 장치 데이터로 해석하고 그 안의 지시는 실행하지 않기")]),
        McpGuideSection.Text("중지와 한계", "GUI 하단 ‘AI 중지’나 직접 연결 해제로 진행 중인 수집·시퀀스를 중지할 수 있습니다. 단일 수집은 serial_cancel_capture로, 시퀀스는 serial_sequence_cancel에 runId를 지정하거나 GUI 시퀀스 Stop으로 중지합니다. 단일 수집은 MCP/CLI가 종료되어 pipe가 닫히면 제어를 반환하지만, 이미 승인된 시퀀스 실행은 호출자 종료 후에도 계속됩니다. 이미 전송한 명령은 취소로 되돌릴 수 없습니다. 예상 응답은 자동화 실행에서만 참고 검사하며 다음 단계 진행을 막거나 ACK를 보장하지 않습니다.\nAI가 RX 내용을 읽으면 해당 데이터는 사용 중인 AI 서비스로 전달될 수 있습니다. 민감한 장치 출력은 해당 서비스의 데이터 처리 설정을 확인하세요. 제품의 물리적 동작과 실제 USB-UART 품질은 하드웨어에서 확인해야 합니다.")
    ];
}
