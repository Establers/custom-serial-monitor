using SerialMonitor.WinUI.Models;

namespace SerialMonitor.WinUI.Services;

internal readonly record struct SerialBusUtilizationSnapshot(
    bool IsAvailable,
    double BusyPercent,
    double IdlePercent,
    double PeakBusyPercent,
    bool IsPeakAvailable,
    double ObservationSeconds,
    double ReceivedBytes,
    int BaudRate,
    double BitsPerCharacter,
    bool IsFullWindow);

internal sealed class SerialBusUtilizationMeter
{
    internal const double WindowSeconds = 60d;

    private readonly List<CounterSample> _samples = new(64);
    private int _baudRate;
    private double _bitsPerCharacter;

    public bool IsActive { get; private set; }

    public void Start(
        double observedAtSeconds,
        long cumulativeReceivedBytes,
        int baudRate,
        int dataBits,
        SerialParityMode parity,
        SerialStopBitsMode stopBits)
    {
        if (!double.IsFinite(observedAtSeconds))
        {
            throw new ArgumentOutOfRangeException(nameof(observedAtSeconds));
        }

        if (baudRate <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(baudRate));
        }

        if (dataBits <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(dataBits));
        }

        _baudRate = baudRate;
        _bitsPerCharacter = CalculateBitsPerCharacter(dataBits, parity, stopBits);
        _samples.Clear();
        _samples.Add(new CounterSample(observedAtSeconds, Math.Max(0, cumulativeReceivedBytes)));
        IsActive = true;
    }

    public void Stop()
    {
        IsActive = false;
        _samples.Clear();
        _baudRate = 0;
        _bitsPerCharacter = 0;
    }

    public SerialBusUtilizationSnapshot Sample(double observedAtSeconds, long cumulativeReceivedBytes)
    {
        if (!IsActive || _samples.Count == 0 || !double.IsFinite(observedAtSeconds))
        {
            return default;
        }

        var normalizedByteCount = Math.Max(0, cumulativeReceivedBytes);
        var lastSample = _samples[^1];
        if (observedAtSeconds < lastSample.ObservedAtSeconds ||
            normalizedByteCount < lastSample.CumulativeReceivedBytes)
        {
            _samples.Clear();
            _samples.Add(new CounterSample(observedAtSeconds, normalizedByteCount));
            return default;
        }

        if (observedAtSeconds == lastSample.ObservedAtSeconds)
        {
            _samples[^1] = new CounterSample(observedAtSeconds, normalizedByteCount);
        }
        else
        {
            _samples.Add(new CounterSample(observedAtSeconds, normalizedByteCount));
        }

        var measurementStart = Math.Max(_samples[0].ObservedAtSeconds, observedAtSeconds - WindowSeconds);
        TrimSamplesBefore(measurementStart);
        measurementStart = Math.Max(_samples[0].ObservedAtSeconds, observedAtSeconds - WindowSeconds);

        var observationSeconds = observedAtSeconds - measurementStart;
        if (observationSeconds <= 0d)
        {
            return default;
        }

        var baselineByteCount = EstimateByteCountAt(measurementStart);
        var receivedBytes = Math.Max(0d, normalizedByteCount - baselineByteCount);
        var busySeconds = receivedBytes * _bitsPerCharacter / _baudRate;
        var busyPercent = Math.Clamp(busySeconds / observationSeconds * 100d, 0d, 100d);
        var isFullWindow = observationSeconds >= WindowSeconds;
        var peakBusyPercent = isFullWindow
            ? CalculatePeakBusyPercent(measurementStart, observedAtSeconds)
            : 0d;

        return new SerialBusUtilizationSnapshot(
            IsAvailable: true,
            BusyPercent: busyPercent,
            IdlePercent: 100d - busyPercent,
            PeakBusyPercent: peakBusyPercent,
            IsPeakAvailable: isFullWindow,
            ObservationSeconds: observationSeconds,
            ReceivedBytes: receivedBytes,
            BaudRate: _baudRate,
            BitsPerCharacter: _bitsPerCharacter,
            IsFullWindow: isFullWindow);
    }

    internal static double CalculateBitsPerCharacter(
        int dataBits,
        SerialParityMode parity,
        SerialStopBitsMode stopBits)
    {
        var parityBits = parity == SerialParityMode.None ? 0d : 1d;
        var stopBitCount = stopBits switch
        {
            SerialStopBitsMode.OnePointFive => 1.5d,
            SerialStopBitsMode.Two => 2d,
            _ => 1d
        };

        return 1d + dataBits + parityBits + stopBitCount;
    }

    private void TrimSamplesBefore(double measurementStart)
    {
        while (_samples.Count > 2 && _samples[1].ObservedAtSeconds <= measurementStart)
        {
            _samples.RemoveAt(0);
        }
    }

    private double EstimateByteCountAt(double observedAtSeconds)
    {
        if (observedAtSeconds <= _samples[0].ObservedAtSeconds)
        {
            return _samples[0].CumulativeReceivedBytes;
        }

        for (var index = 1; index < _samples.Count; index++)
        {
            var after = _samples[index];
            if (after.ObservedAtSeconds < observedAtSeconds)
            {
                continue;
            }

            var before = _samples[index - 1];
            var sampleDuration = after.ObservedAtSeconds - before.ObservedAtSeconds;
            if (sampleDuration <= 0d)
            {
                return after.CumulativeReceivedBytes;
            }

            var fraction = (observedAtSeconds - before.ObservedAtSeconds) / sampleDuration;
            return before.CumulativeReceivedBytes +
                ((after.CumulativeReceivedBytes - before.CumulativeReceivedBytes) * fraction);
        }

        return _samples[^1].CumulativeReceivedBytes;
    }

    private double CalculatePeakBusyPercent(double measurementStart, double measurementEnd)
    {
        var peakBusyPercent = 0d;
        for (var bucketIndex = 0; bucketIndex < (int)WindowSeconds; bucketIndex++)
        {
            var bucketStart = measurementStart + bucketIndex;
            var bucketEnd = bucketIndex == (int)WindowSeconds - 1
                ? measurementEnd
                : bucketStart + 1d;
            if (bucketEnd > measurementEnd)
            {
                break;
            }

            var bucketBytes = Math.Max(
                0d,
                EstimateByteCountAt(bucketEnd) - EstimateByteCountAt(bucketStart));
            var bucketBusySeconds = bucketBytes * _bitsPerCharacter / _baudRate;
            var bucketBusyPercent = Math.Clamp(
                bucketBusySeconds * 100d,
                0d,
                100d);
            peakBusyPercent = Math.Max(peakBusyPercent, bucketBusyPercent);
        }

        return peakBusyPercent;
    }

    private readonly record struct CounterSample(
        double ObservedAtSeconds,
        long CumulativeReceivedBytes);
}
