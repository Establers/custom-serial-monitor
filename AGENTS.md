# AGENTS.md

## Project Rules

This is a WinUI 3 C# serial monitoring application for long-running embedded device debugging.

Stability is more important than visual polish.

## Architecture Rules

- Keep serial RX, parsing, logging, event detection, and UI rendering separated.
- Never update UI controls directly from the serial receive callback.
- Never write files directly from the serial receive callback.
- Use channels or queues between background components.
- Use batched UI updates.
- Keep only a bounded amount of logs in the UI.
- Full logs must be written to disk asynchronously.
- Handle background task exceptions and expose errors to the UI.

## Coding Style

- Prefer small classes with clear responsibilities.
- Prefer interfaces for services.
- Avoid putting business logic in MainWindow.xaml.cs.
- Use async/await carefully.
- CancellationToken must be supported for long-running tasks.
- Avoid blocking the UI thread.
- Avoid unbounded memory growth.

## Implementation Order

1. Create project skeleton and compile.
2. Implement mock serial data pipeline.
3. Implement real serial RX.
4. Implement file logging.
5. Implement bounded UI log buffer.
6. Implement TX command send.
7. Implement event detection.
8. Implement profile save/load.
9. Implement search/filter/highlight.
10. Implement long-running stability tests.