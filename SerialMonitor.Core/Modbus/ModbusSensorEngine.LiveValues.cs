using System.Collections.Immutable;

namespace SerialMonitor.Core.Modbus;

public enum ModbusSensorChangeSource { Initial, Master, User, Reset }
public sealed record ModbusSensorPointState(int Index, ImmutableArray<ushort> Words, long Revision,
    ModbusSensorChangeSource Source, DateTimeOffset ChangedAt);
public sealed record ModbusSensorSnapshot(long Revision, ImmutableArray<ModbusSensorPointState> Points);

public sealed partial class ModbusSensorEngine
{
    private (int Area, ModbusItemDefinition Item)[] _points = [];
    private ushort[][] _initialValues = [];
    private readonly Dictionary<(int Area, int Address), int> _pointIndices = [];
    private long[] _pointRevisions = [];
    private ModbusSensorChangeSource[] _sources = [];
    private DateTimeOffset[] _changedAt = [];
    private long _revision;
    private ModbusSensorSnapshot? _snapshot;

    private void InitializeLiveValues(ModbusSensorDefinition definition)
    {
        _points = definition.Points.Select(p => (p.Area, p.Item())).ToArray();
        _initialValues = _points.Select(p => ReadWords(p.Area, p.Item)).ToArray();
        _pointRevisions = new long[_points.Length];
        _sources = new ModbusSensorChangeSource[_points.Length];
        _changedAt = new DateTimeOffset[_points.Length];
        for (var index = 0; index < _points.Length; index++)
            for (var word = 0; word < _points[index].Item.RegisterCount; word++)
                _pointIndices.Add((_points[index].Area, _points[index].Item.Address + word), index);
    }

    // Called by the same consumer as Respond. User edits simulate the device's
    // own measurements; they do not grant the master write access to input areas.
    public void SetPointValue(int index, string value)
    {
        var point = Point(index);
        var words = ModbusItemCodec.Encode(value, point.Item, point.Area <= 2);
        for (var word = 0; word < words.Length; word++)
            SetCell(point.Area, point.Item.Address + word, words[word], ModbusSensorChangeSource.User);
    }

    public void ResetValues(int? index = null)
    {
        if (index is { } selected) _ = Point(selected);
        for (var i = index ?? 0; i < (index is { } item ? item + 1 : _points.Length); i++)
            for (var word = 0; word < _initialValues[i].Length; word++)
                SetCell(_points[i].Area, _points[i].Item.Address + word, _initialValues[i][word], ModbusSensorChangeSource.Reset);
    }

    public ModbusSensorSnapshot GetSnapshot()
    {
        if (_snapshot?.Revision == _revision) return _snapshot;
        var states = ImmutableArray.CreateBuilder<ModbusSensorPointState>(_points.Length);
        for (var i = 0; i < _points.Length; i++)
            states.Add(_snapshot is not null && _snapshot.Points[i].Revision == _pointRevisions[i] ? _snapshot.Points[i] :
                new(i, ImmutableArray.CreateRange(ReadWords(_points[i].Area, _points[i].Item)),
                    _pointRevisions[i], _sources[i], _changedAt[i]));
        return _snapshot = new(_revision, states.MoveToImmutable());
    }

    private (int Area, ModbusItemDefinition Item) Point(int index) => index >= 0 && index < _points.Length
        ? _points[index] : throw new ArgumentOutOfRangeException(nameof(index), "항목을 다시 선택하세요.");
    private ushort[] ReadWords(int area, ModbusItemDefinition item) => Enumerable.Range(item.Address, item.RegisterCount)
        .Select(address => _cells[(area, address)].Value).ToArray();
    private void SetCell(int area, int address, ushort value, ModbusSensorChangeSource source)
    {
        var previous = _cells[(area, address)];
        if (previous.Value == value) return;
        _cells[(area, address)] = (value, previous.Writable);
        var index = _pointIndices[(area, address)];
        _pointRevisions[index] = ++_revision;
        _sources[index] = source;
        _changedAt[index] = DateTimeOffset.UtcNow;
    }
}
