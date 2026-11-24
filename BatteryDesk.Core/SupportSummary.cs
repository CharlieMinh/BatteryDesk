using System.Text.RegularExpressions;

namespace BatteryDesk.Core;

public static partial class SupportSummary
{
    public static string Create(DeviceView view, string appVersion, string windowsVersion, params string[] privateNames)
    {
        var item = view.Display;
        var model = RemoveIdentifiers(item.Endpoint.Model);
        foreach (var name in privateNames.Where(name => !string.IsNullOrWhiteSpace(name)))
            model = model.Replace(name, "[ẩn]", StringComparison.OrdinalIgnoreCase);
        var usb = UsbId().IsMatch(item.Endpoint.UsbId ?? "") ? item.Endpoint.UsbId : "Windows không cung cấp";
        var status = item.Status switch
        {
            ReadStatus.Success => item.IsLast ? "Mẫu cũ" : "Đã nhận dữ liệu",
            ReadStatus.Offline => "Thiết bị ngắt kết nối",
            ReadStatus.Unsupported => "Thiết bị không báo pin qua nguồn này",
            ReadStatus.Unavailable => "Nguồn đọc không phản hồi hoặc lỗi tạm thời",
            _ => "Chưa nhận được mẫu"
        };
        return string.Join(Environment.NewLine,
            $"BatteryDesk {appVersion}",
            $"Windows: {windowsVersion}",
            $"Model: {model}",
            $"Kết nối: {ConnectionLabel(item.Endpoint.Connection)}",
            $"VID/PID: {usb}",
            $"Nguồn pin: {item.Sample?.Source.ToString() ?? "Chưa rõ"}",
            $"Kết quả: {status}");
    }

    public static string ConnectionLabel(DeviceConnection connection) => connection switch
    {
        DeviceConnection.Cable => "Cáp USB",
        DeviceConnection.Receiver => "USB 2.4 GHz",
        _ => "Bluetooth"
    };

    internal static string RemoveIdentifiers(string model)
    {
        var clean = Serial().Replace(Mac().Replace(model, "[ẩn]"), "[ẩn]");
        return string.Join(" ", clean.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
    }

    [GeneratedRegex(@"(?i)\b(?:[0-9a-f]{2}[:-]){5}[0-9a-f]{2}\b|\b[0-9a-f]{12}\b")]
    private static partial Regex Mac();

    [GeneratedRegex(@"(?i)\b(?:serial(?:\s+number)?|s/n|sn)\s*[:=#]\s*\S+")]
    private static partial Regex Serial();

    [GeneratedRegex(@"\A[0-9A-Fa-f]{4}:[0-9A-Fa-f]{4}\z")]
    private static partial Regex UsbId();
}
