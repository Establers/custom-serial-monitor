# USB removal and driver cleanup validation

Validated on Windows on 2026-10-31 (Asia/Seoul).

## Fixes

- `SerialService.DisconnectAsync` now passes the caller's cancellation token to both the previous-session retirement wait and the current-session teardown wait. Previously these waits could each consume the fixed ten-second driver timeout after cancellation.
- Cancellation of a teardown wait retains the retirement task and its resources. Reconnect remains blocked until native close and the RX worker actually finish; late completion releases the session.
- An open operation that outlives its deadline is retired in the background after Open returns. Cancellation no longer adds a second ten-second cleanup wait, and Close never races an unfinished Open.

Five deterministic regression cases cover pending/active retirement, delayed Open, and cancellation during failed-open cleanup, including safe resource retention and reconnect.

## Physical USB check

Target: STMicroelectronics STLink Virtual COM Port, COM3, USB VID 0483 / PID 374B. Settings: 115200 BPS, 8N1, no handshake, DTR/RTS off, 40 ms HEX grouping timeout. The application received the board's ongoing Task1/Task2 printf output. No device command was sent by the verification workflow.

The user physically removed and reinserted the USB cable while the full WinUI application was running:

| Check | Result |
| --- | --- |
| Physical USB removal during RX | Application stayed responsive. `Serial receive failed: RJCP serial monitor stopped while waiting for RX data.` appeared, and automatic reconnect started. |
| Cancel reconnect after removal | Idle state observed 297 ms after the button request; includes CLI polling/IPC overhead. |
| Reinsert and reopen COM3 | Connected successfully with a new receive generation; live printf RX resumed. |
| Stop a receive-only 600000 ms RX Check | Canceled result timestamp 117 ms after the stop request. All 7560 recorded RX bytes retained; no sequence TX. |
| Close the window during another RX Check and live RX | Process exited; the run was saved as canceled with 8400 RX bytes and no sequence TX. Shutdown diagnostics recorded an active sequence and serial connection, successful cleanup, and no disconnect, flush or profile error. |

The window-close request was at 01:01:07.543. `ShutdownAsync` diagnostics ran from 01:01:07.631 to 01:01:07.705 (74 ms). This measures the shutdown-cleanup portion, not the entire WebView/process-exit interval. No runtime-error file was produced for that launch. The temporary receive-only sequence and verification windows were removed after testing.

Evidence is under `artifacts/usb-gui-*-result.json`; the tested run IDs are `ae6599b060ec499f80be0598f2d48dc6` and `5a94e5d96b4b468dab6e300f851e53f6` in local sequence-run storage.

## Windows driver tests

The registry-verified com0com CNCA0/CNCB0 pair exercises native Windows serial I/O without touching physical ports. Eight driver tests pass, including twenty native-monitor failures/reopens during binary RX and ten failures during CTS-blocked TX. These are driver-path fault tests, distinct from physical USB removal above.

```powershell
$env:SERIAL_MONITOR_TEST_COM0COM_PAIR0 = '1'
dotnet test SerialMonitor.WinUI.Tests/SerialMonitor.WinUI.Tests.csproj --filter 'FullyQualifiedName~SerialServiceVirtualDriverTests|FullyQualifiedName~SerialDriverTeardownTests'
Remove-Item Env:SERIAL_MONITOR_TEST_COM0COM_PAIR0
```

Final automated suite: Core 227 passed; WinUI 1043 passed, one interactive USB fixture skipped. TRX results are under `artifacts/driver-fault-validation`.

`PhysicalUsbDisconnectTests` is separately opt-in: set `SERIAL_MONITOR_TEST_USB_PORT`, the exact `SERIAL_MONITOR_TEST_USB_DEVICE` PnP instance, and an absolute `SERIAL_MONITOR_TEST_USB_RESULT` path. It opens only that USB device for receive-only observation and waits for a human to unplug it. It must not run unattended in CI.

Coverage is limited to this STLink USB driver, the explicit com0com pair, and injected delayed-driver tasks; other USB adapters and indefinitely stuck kernel drivers are not hardware-certified by these tests.
