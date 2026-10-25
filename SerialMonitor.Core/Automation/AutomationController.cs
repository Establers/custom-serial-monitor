using System.Text;
using System.Text.Json;

namespace SerialMonitor.Core.Automation;

public sealed record AutomationDeviceState(bool Connected, string Port, int BaudRate, long SessionGeneration,
    string Encoding, long SerialErrors, bool Busy, string? LastError,
    int DataBits = 8, string Parity = "None", string StopBits = "One", string Handshake = "None");

// The WinUI adapter performs only UI-owned operations on its dispatcher. Capture,
// persistence, paging and transport remain in background services.
public interface IAutomationDevice
{
    CaptureConnection Connection { get; }
    Task<object> InvokeAsync(string operation, JsonElement arguments, CancellationToken cancellationToken);
}

public sealed class AutomationController(IAutomationDevice device, RxCaptureService captures)
{
    public async Task<AutomationResponse> HandleAsync(AutomationRequest request, CancellationToken cancellationToken)
    {
        try
        {
            object data;
            switch (request.Operation)
            {
                case "status":
                case "ports":
                case "list_commands":
                    data = await device.InvokeAsync(request.Operation, request.Arguments, cancellationToken).ConfigureAwait(false);
                    break;
                case "connect":
                case "disconnect":
                    await device.InvokeAsync("reserve", request.Arguments, cancellationToken).ConfigureAwait(false);
                    try { data = await device.InvokeAsync(request.Operation, request.Arguments, cancellationToken).ConfigureAwait(false); }
                    finally { await device.InvokeAsync("release", default, CancellationToken.None).ConfigureAwait(false); }
                    break;
                case "send":
                case "capture_rx":
                case "send_and_capture":
                {
                    var result = await CaptureAsync(request, cancellationToken).ConfigureAwait(false);
                    if (result.Capture.CaptureStatus != "completed")
                    {
                        var detail = result.Capture.Error is { } error ? $" {error}" : "";
                        return AutomationResponse.Fail(result.Capture.CaptureStatus,
                            $"RX capture ended with status '{result.Capture.CaptureStatus}'.{detail} Inspect capture/page and sendStatus; do not automatically retry TX.",
                            result);
                    }
                    data = result;
                    break;
                }
                case "read_capture":
                {
                    var id = request.Arguments.String("captureId") ?? throw new AutomationException("invalid_argument", "captureId is required.");
                    var offset = request.Arguments.Integer("offset", 0);
                    data = new
                    {
                        capture = captures.GetResult(id),
                        page = await captures.ReadAsync(id, offset, request.Arguments.Integer("limit", 16384),
                            request.Arguments.String("format", "text")!, cancellationToken).ConfigureAwait(false)
                    };
                    break;
                }
                case "cancel_capture": data = new { canceled = captures.Cancel() }; break;
                default: throw new AutomationException("unknown_operation", "Unknown operation: " + request.Operation);
            }
            return AutomationResponse.Ok(data);
        }
        catch (AutomationException ex) { return AutomationResponse.Fail(ex.Code, ex.Message); }
        catch (OperationCanceledException) { return AutomationResponse.Fail("canceled", "Operation canceled; a TX already started is not rolled back."); }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or ArgumentException or FormatException)
        { return AutomationResponse.Fail("invalid_argument", ex.Message); }
        catch (Exception ex) { return AutomationResponse.Fail("operation_failed", ex.Message); }
    }

    private sealed record CaptureResponse(CaptureResult Capture, CapturePage? Page);

    private async Task<CaptureResponse> CaptureAsync(AutomationRequest request, CancellationToken cancellationToken)
    {
        var args = request.Arguments;
        var isSend = request.Operation != "capture_rx";
        var previewBytes = args.Integer("previewBytes", 16384);
        if (previewBytes is < 4 or > 65536)
            throw new AutomationException("invalid_argument", "previewBytes must be between 4 and 65536. It limits the returned page, not RX collection.");
        var options = new CaptureOptions(
            request.Operation == "send" ? 0 : args.Integer("durationMs", 2000),
            isSend ? args.String("command") : null,
            args.String("txMode", "text")!, args.String("lineEnding", "crlf")!);
        if (options.DurationMs is < 0 or > 60000 || options.TxMode is not ("text" or "hex") ||
            options.LineEnding is not ("none" or "cr" or "lf" or "crlf") ||
            (isSend && (string.IsNullOrEmpty(options.Command) || options.Command.Length > 16384)))
            throw new AutomationException("invalid_argument", "Check command (1..16384 characters), durationMs (0..60000), txMode and lineEnding.");
        var state = (AutomationDeviceState)await device.InvokeAsync("reserve", args, cancellationToken).ConfigureAwait(false);
        try
        {
            var expectedPort = args.String("port");
            if (expectedPort is not null && !string.Equals(expectedPort, state.Port, StringComparison.OrdinalIgnoreCase))
                throw new AutomationException("port_mismatch", "GUI is connected to " + state.Port + ", not the requested port.");
            if (isSend) await device.InvokeAsync("validate_send", args, cancellationToken).ConfigureAwait(false);
            var encoding = state.Encoding switch
            {
                "Ascii" => Encoding.ASCII,
                "Cp949" => Encoding.GetEncoding(949),
                _ => Encoding.UTF8
            };
            var result = await captures.CaptureAsync(options, state.Port, encoding, () => device.Connection,
                isSend ? async token => { await device.InvokeAsync("send", args, token).ConfigureAwait(false); } : null,
                cancellationToken).ConfigureAwait(false);
            CapturePage? page = null;
            // An empty/error capture may not have readable files. Preserve its
            // metadata and TX status even if preview generation fails.
            if (File.Exists(result.TextFile))
            {
                try { page = await captures.ReadAsync(result.CaptureId, limit: previewBytes, cancellationToken: CancellationToken.None).ConfigureAwait(false); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or AutomationException)
                { result = result with { CaptureStatus = "write_error", Error = "Capture preview: " + ex.Message }; }
            }
            await device.InvokeAsync("capture_result", JsonSerializer.SerializeToElement(result, AutomationJson.Options), CancellationToken.None).ConfigureAwait(false);
            return new CaptureResponse(result, page);
        }
        finally { await device.InvokeAsync("release", default, CancellationToken.None).ConfigureAwait(false); }
    }
}
