using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Media;

namespace SerialMonitor.WinUI.Infrastructure;

public sealed class SequenceResultStatusConverter : IValueConverter
{
    public static string Text(string? value) => value switch
    {
        "running" => "실행 중", "failed" => "실패", "interrupted" => "비정상 종료",
        "sent" => "전송 완료", "unknown" => "전송 불확실", "not_sent" => "미전송",
        "matched" => "일치", "not_matched" => "미확인", "not_requested" => "검사 안 함",
        "inconclusive" => "판정 불가", "canceled" => "중단", "completed" => "완료",
        _ => value ?? ""
    };
    public object Convert(object value, Type targetType, object parameter, string language) => Text(value?.ToString());
    public object ConvertBack(object value, Type targetType, object parameter, string language) => throw new NotSupportedException();
}

// Small decorative accents; status labels keep theme-provided text contrast.
public sealed class SequenceResultAccentConverter : IValueConverter
{
    private static readonly Brush Positive = Color(0x72, 0xC9, 0x9B);
    private static readonly Brush Caution = Color(0xE1, 0xB5, 0x68);
    private static readonly Brush Negative = Color(0xE5, 0x87, 0x87);
    private static readonly Brush Neutral = Color(0x96, 0xA2, 0xAF);
    private static readonly Brush Tx = Color(0x69, 0xAB, 0xF2);
    private static readonly Brush Rx = Color(0x5D, 0xC6, 0xBA);

    private static Brush Color(byte red, byte green, byte blue) =>
        new SolidColorBrush(Windows.UI.Color.FromArgb(255, red, green, blue));

    public object Convert(object value, Type targetType, object parameter, string language)
    {
        if (parameter?.ToString() == "kind") return value?.ToString() == "RxCheck" ? Rx : Tx;
        return value?.ToString() switch
        {
            "sent" or "matched" => Positive,
            "not_matched" or "inconclusive" or "unknown" => Caution,
            "failed" or "not_sent" => Negative,
            _ => Neutral
        };
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language) => throw new NotSupportedException();
}
