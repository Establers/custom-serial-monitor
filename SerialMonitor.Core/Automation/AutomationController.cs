using System.Text;
using System.Text.Json;

namespace SerialMonitor.Core.Automation;

public sealed record AutomationDeviceState(bool Connected, string Port, int BaudRate, long SessionGeneration,
    string Encoding, long SerialErrors, bool Busy, string? LastError,
    int DataBits = 8, string Parity = "None", string StopBits = "One", string Handshake = "None",
    string Mode = "terminal", int ByteTimeoutMs = 40);

// The WinUI adapter performs only UI-owned operations on its dispatcher. Capture,
// persistence, paging and transport remain in background services.
public interface IAutomationDevice
{
    CaptureConnection Connection { get; }
    Task<object> InvokeAsync(string operation, JsonElement arguments, CancellationToken cancellationToken);
}

public sealed class AutomationController(IAutomationDevice device, RxCaptureService captures) : IAsyncDisposable
{
    private readonly AutomationSequenceRunner _sequences = new(device, captures);

    public bool CancelActiveSequence() => _sequences.CancelActive();
    public ValueTask DisposeAsync() => _sequences.DisposeAsync();

    public async Task<AutomationResponse> HandleAsync(AutomationRequest request, CancellationToken cancellationToken)
    {
        try
        {
            AutomationArguments.Validate(request.Operation, request.Arguments);
            object data;
            switch (request.Operation)
            {
                case "status":
                case "ports":
                case "list_commands":
                case "sequence_list":
                case "sequence_get":
                    data = await device.InvokeAsync(request.Operation, request.Arguments, cancellationToken).ConfigureAwait(false);
                    break;
                case "connect":
                case "disconnect":
                case "set_mode":
                case "configure":
                case "sequence_save":
                case "sequence_delete":
                    if (request.Operation == "set_mode" && request.Arguments.String("mode") is not ("terminal" or "hex"))
                        throw new AutomationException("invalid_argument", "mode must be terminal or hex. Changing mode clears the visible log and changes GUI manual TX interpretation; per-call txMode is independent.");
                    if (request.Operation == "configure")
                        _ = AutomationSerialConfiguration.Parse(request.Arguments);
                    if (request.Operation == "sequence_save")
                    {
                        if (!request.Arguments.TryGetProperty("sequence", out var definition))
                            throw new AutomationException("invalid_argument", "sequence is required.");
                        _ = AutomationSequenceValidation.Parse(definition);
                    }
                    return await ControlAsync(request, cancellationToken).ConfigureAwait(false);
                case "sequence_start":
                    data = await _sequences.StartAsync(request.Arguments, cancellationToken).ConfigureAwait(false);
                    break;
                case "sequence_status":
                    data = _sequences.Status(request.Arguments.String("runId"));
                    break;
                case "sequence_cancel":
                    data = _sequences.Cancel(request.Arguments.String("runId")
                        ?? throw new AutomationException("invalid_argument", "runId is required to cancel a sequence run."));
                    break;
                case "send":
                case "capture_rx":
                case "send_and_capture":
                {
                    var result = await CaptureAsync(request, cancellationToken).ConfigureAwait(false);
                    if (result.ControlError is { } controlError)
                        return AutomationResponse.Fail(controlError.Code, controlError.Message, result);
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
                    var capture = captures.GetResult(id);
                    data = new
                    {
                        capture,
                        page = await captures.ReadAsync(id, offset, request.Arguments.Integer("limit", 16384),
                            request.Arguments.String("format", capture.PreviewFormat ?? (capture.TxMode == "hex" ? "hex" : "text"))!, cancellationToken).ConfigureAwait(false)
                    };
                    break;
                }
                case "cancel_capture":
                    data = new { canceled = _sequences.CancelCaptureWhenIdle() };
                    break;
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

    private sealed record CaptureResponse(CaptureResult Capture, CapturePage? Page, AutomationError? ControlError = null);

    private async Task<AutomationResponse> ControlAsync(AutomationRequest request, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        await device.InvokeAsync("reserve", request.Arguments, token).ConfigureAwait(false);
        object? committed = null;
        Exception? failure = null;
        try
        {
            token.ThrowIfCancellationRequested();
            committed = await device.InvokeAsync(request.Operation, request.Arguments, token).ConfigureAwait(false);
        }
        catch (Exception ex) { failure = ex; }
        finally
        {
            try { await device.InvokeAsync("release", default, CancellationToken.None).ConfigureAwait(false); }
            catch (Exception ex) { failure ??= ex; }
        }
        if (failure is null) return AutomationResponse.Ok(committed!);
        if (committed is not null)
            return AutomationResponse.Fail(failure is AutomationException a ? a.Code : "post_control_error",
                "The operation completed but GUI cleanup failed: " + failure.Message + " Inspect data before retrying.", committed);
        System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
        throw new InvalidOperationException("Control did not produce a result.");
    }

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
        var format = args.String("format", options.TxMode == "hex" ? "hex" : "text");
        if (format is not ("text" or "hex"))
            throw new AutomationException("invalid_argument", "format must be text or hex.");
        options = options with { PreviewFormat = format };
        if (options.DurationMs is < 0 or > 60000 || options.TxMode is not ("text" or "hex") ||
            options.LineEnding is not ("none" or "cr" or "lf" or "crlf") ||
            (isSend && (string.IsNullOrEmpty(options.Command) || options.Command.Length > 16384)))
            throw new AutomationException("invalid_argument", "Check command (1..16384 characters), durationMs (0..60000), txMode and lineEnding.");
        var state = (AutomationDeviceState)await device.InvokeAsync("reserve", args, cancellationToken).ConfigureAwait(false);
        CaptureResponse? response = null;
        Exception? failure = null;
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
                isSend ? async token => { await device.InvokeAsync("send", AutomationSendContext.Bind(args, state.SessionGeneration), token).ConfigureAwait(false); } : null,
                cancellationToken).ConfigureAwait(false);
            response = new CaptureResponse(result, null);
            CapturePage? page = null;
            // An empty/error capture may not have readable files. Preserve its
            // metadata and TX status even if preview generation fails.
            if (File.Exists(format == "hex" ? result.RawFile : result.TextFile))
            {
                try { page = await captures.ReadAsync(result.CaptureId, limit: previewBytes, format: format, cancellationToken: CancellationToken.None).ConfigureAwait(false); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or AutomationException)
                { result = result with { CaptureStatus = "write_error", Error = "Capture preview: " + ex.Message }; }
            }
            response = new CaptureResponse(result, page);
            await device.InvokeAsync("capture_result", JsonSerializer.SerializeToElement(result, AutomationJson.Options), CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex) { failure = ex; }
        finally
        {
            try { await device.InvokeAsync("release", default, CancellationToken.None).ConfigureAwait(false); }
            catch (Exception ex) { failure ??= ex; }
        }
        if (response is not null)
            return failure is null ? response : response with
            {
                ControlError = new(failure is AutomationException automation ? automation.Code : "post_capture_error",
                    "GUI result/cleanup failed after capture: " + failure.Message + " Inspect capture and sendStatus; do not automatically retry TX.")
            };
        System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure!).Throw();
        throw new InvalidOperationException("Capture did not produce a result.");
    }
}
