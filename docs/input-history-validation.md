# TX and search recall history by COM port

Both histories use the actual COM identifier, with case/whitespace/numeric aliases
normalized (`com007` and `COM7` share history). A new port has empty TX/search recall
lists. MOCK and no selected port have their own histories. Choosing another port
while disconnected switches histories immediately. While connected or waiting for
automatic reconnect, the current connection continues owning history until the
connection changes. Switching clears the outgoing TX text and history filter,
restores the port's last search, and resets recall cursors and search drafts.

Profile saves include every port used since load. The normal limits are 100 TX
commands and 50 search terms per port. Shared-file leases and three-way history
merges preserve other ports and additions made by another window. An unchanged
window cannot resurrect cleared history; clearing observed entries also retains
new entries that another window added after load. Startup profile loading finishes
before shutdown takes its save snapshot.

TX captures its history port before awaiting either serial transmission or the
bridge scheduler. A successfully completed send appends to that captured port even
if disconnect/selection or a reentrant `TxSent` handler has switched the visible
port before completion processing. An inactive-port update changes only its cached
TX list; it does not replace the new port's history, search text or TX draft.
Direct sends also recheck cancellation after the transport await, matching the
bridge path. Failed/canceled operations do not create a recall entry.

`PortInputHistorySendRaceTests` executes the production `SendCommandAsync`, actual
disconnect/selection setters and TX notifications using controlled transport tasks
and a held synchronization context. The transport succeeds before the UI
continuation is released; the test then switches COM7 to COM8. The GUI's
`CanManualDisconnect` guard is verified to remain true while the send is pending.
UI dispatch uses a small adapter over the same native queue, allowing the test to
exercise these paths without creating a native window or opening a serial device.

The pre-fix run reproduced four ownership failures (direct/bridge delayed
continuation and direct/bridge TX-notification reentrancy), plus the direct
cancellation-after-write gap: five failed and five passed in
`.codex-build/port-history-send-race/results/send-race-before.trx`.
The initial corrected run passed all ten in `send-race-after.trx` in that directory.
The final regression run passed 177 tests, including all eleven race cases after
adding preservation of the newly selected port's existing history/search/draft.
That run also included Quick command creation/persistence, TX byte integrity,
profile migration/backups, concurrent histories, cancellation and shutdown checks.
Its report is `send-race-regression.trx` in the same directory. The raw-payload
changes from the Quick-command work were retained in `SendCommandAsync`; the
ownership fix only adds the captured history key, a post-await cancellation check
and routing of the completed history update.

TX history stores the exact nonblank command string. Leading/trailing spaces and
tabs are payload bytes, so `PING`, ` PING ` and `\tPING\t` are different entries.
Only an ordinally identical resend updates an existing entry's timestamp; profile
normalization, JSON round trips, port switching, merging and recall preserve that
string. Duplicate records with the same exact payload keep the newest timestamp.
Null, empty and whitespace-only commands remain excluded from recall history.
Port identifiers and the history-filter query still normalize surrounding spaces.

Older common and slot histories contain no reliable per-entry port identity. They
are imported once into the port saved in the old profile. Each old slot remains in
`InstanceHistories`, the old common history remains in `LegacyInputHistory`, and
the exact original profile is backed up as
`default.json.before-port-history-<hash>.json` before the first replacement. If the
combined history exceeds UI retention, its originals are still present in those
archives and the backup. Failed backup prevents replacement; the migration marker
prevents cleared histories from being imported again. A changed COM number starts
a new history without deleting the old port's data.

Atomic profile publication also tolerates transient Windows sharing/lock violations
(Win32 32/33) on the staged source or destination. `ProfileFileCommit` retries the
same `File.Move`/`File.Replace` operation up to 41 attempts with 50 ms asynchronous
delays (at most two seconds of retry delays, plus filesystem/scheduling time).
The profile lease remains held across serialization, merge, backup and publication;
comparison baselines advance only after success. Cancellation interrupts a pending
delay. Permission errors and other I/O errors propagate without retries.

The choice between creating and replacing a profile is pinned before retries. If a
new destination unexpectedly appears during first creation, the save fails rather
than overwriting it. Migration backups use the same sharing retry policy with an
immutable no-overwrite move. No copy/truncate fallback is used.

The v1.6.2 release-review log showed two `File.Move` sharing failures in the 200,000
and 1,000,000 line profile-capacity round trips. Their locking process is not
identified in that log. `ProfileFileCommitTests` reproduces `0x80070020` with real
Windows handles that allow reads but deny deletion, then verifies successful atomic
publication after releasing the handle. It also verifies destination locking,
cancellation after retry begins, finite failure while a lock remains held, a
destination appearing during retry, immutable backups, immediate missing-file and
read-only permission failures, and error exposure/cleanup through ProfileService.
The persistent-lock test surfaced the original IOException after about 2.15 s.

Release/win10-x64 validation passed 154 selected tests, including all 11 new file
commit tests, both previously failing capacity cases, concurrent profile merging,
port histories, original backups and conflict protection. Results are in
`artifacts/profile-sharing-fix/results/profile-sharing-regression.trx`; the initial
13-case focused run is in `profile-sharing.trx` beside it. The isolated command
uses `-c Release -p:Platform=x64 -p:RuntimeIdentifier=win10-x64 -p:UseRidGraph=true
--artifacts-path artifacts/profile-sharing-fix/build`. COM3, running GUIs, shared
release output, and the Core test suite were not touched by this verification.

Automated regression checks:

```powershell
dotnet test SerialMonitor.WinUI.Tests/SerialMonitor.WinUI.Tests.csproj -c Debug -p:Platform=x64 -p:RuntimeIdentifier=win-x64 --artifacts-path artifacts/port-history-check/build -m:1 -nr:false --filter "FullyQualifiedName~InputHistoryIsolationTests|FullyQualifiedName~CommandHistoryTests|FullyQualifiedName~ProfileService|FullyQualifiedName~UiSettingsTests|FullyQualifiedName~BackgroundProfileTests|FullyQualifiedName~LogRuleProfileCompatibilityTests|FullyQualifiedName~RuleNotificationPersistenceTests" --logger "trx;LogFileName=port-history.trx" --results-directory artifacts/port-history-check/results
```

On 2026-10-31 the full test project compiled and all 130 selected tests passed,
including history/profile checks plus shutdown, connection cancellation and queued
TX session regressions. Add `FullyQualifiedName~ShutdownRegressionTests`,
`FullyQualifiedName~ConnectionCancellationRegressionTests` and
`FullyQualifiedName~SerialQueuedSendSessionTests` to the filter above to reproduce
that expanded run. The report is
`artifacts/port-history-check/results/port-history.trx`.
The application was built in isolation from other development builds. No real
serial device was opened and no full native GUI interaction test was performed.

The subsequent TX payload fidelity review passed 134 selected tests, including
the independent `PortHistorySnapshotsKeepDifferentWirePayloadsDistinct` probe.
Its captured hex is `2050494E4720;50494E47`, preserving padded and unpadded PING
as separate entries. The report is
`.codex-build/port-history-fidelity/results/port-history-fidelity.trx`.
New regressions exercise null/whitespace-only exclusion, exact resend timestamps,
100 distinct padded payloads through UI and profile retention, JSON restoration,
production port switching/recall, both shutdown orders, concurrent saves, stale
saves after clears and legacy migration with an unchanged original-byte backup.
The isolated review included the temporary audit probe through
`-p:CustomAfterMicrosoftCommonTargets=$PWD/.codex-build/critical-session-audit/ReviewProbes.targets`
and filtered history/profile tests plus that one probe. COM3 and existing running
applications were not accessed.

Manual GUI check:

- Send `PING`, ` PING ` and a tab-padded command on a disposable test device.
  Verify distinct entries and exact recall after switching ports and restarting;
  use a receiver that displays the transmitted bytes to check padding explicitly.
- Open two copies, select separate ports, send different commands to independently
  owned devices, and execute different searches. Check TX/search Up/Down recall.
- In one copy, disconnect and select the other port. Confirm the TX list, last
  search and recalled search terms follow the port; a previous TX draft is cleared.
- Switch repeatedly between ports, close both copies, and reopen in reverse order.
  Confirm both histories for every port survive. Repeat with reverse shutdown order.
- Open a third copy and select a previously unused port: its histories start empty.
- Clear TX history for one port, close that copy, then save an unchanged old copy
  that had the same port's previous history. Confirm the clear remains after reload.
- Save new histories after migration and confirm the original backup and archived
  common/slot histories remain unchanged. Do not open an older build concurrently.
- Confirm shared settings, saved commands/sequences, RX and file logging continue
  following their existing behaviors.
