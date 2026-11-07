namespace SerialMonitor.Core.Protocols;

public enum ProtocolKind { ModbusRtu, BacnetMstp }
public enum ProtocolFrameStatus { Decoded, CrcError, Incomplete, Undecoded, Exception }
public enum ProtocolCrcStatus { Valid, Invalid, Unavailable }

// Presentation consumes common fields; wire decoding remains protocol-specific.
public interface IProtocolFrame
{
    long Id { get; }
    long Session { get; }
    DateTimeOffset Timestamp { get; }
    ProtocolDirection Direction { get; }
    ReadOnlyMemory<byte> Bytes { get; }
    int Length { get; }
    long RetainedBytes { get; }
    byte? Address { get; }
    byte? Code { get; }
    string CodeName { get; }
    ProtocolFrameStatus Status { get; }
    ProtocolCrcStatus CrcStatus { get; }
    bool MatchesCode(byte code);
    string Describe();
}

public interface ISelectableProtocolAnalyzer : IProtocolAnalyzer
{
    ProtocolKind Protocol { get; }
    void SelectProtocol(ProtocolKind protocol);
}
