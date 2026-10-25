// Protocol derived from liquidctl rgb_fusion2.py.
// Copyright CaseySJ, Jonas Malaco and contributors. SPDX-License-Identifier: GPL-3.0-or-later
// See docs/it5702.md for provenance and scope.
using System.Globalization;
using System.Text;
using System.Text.Json;
using ControlCenter.Contracts;

namespace ControlCenter.GigabyteIt5702;

internal sealed record LightingRequest(string DeviceId, string OperationId, string Color, double Brightness, bool Off)
{
    public static LightingRequest Parse(JsonElement args)
    {
        if (args.ValueKind != JsonValueKind.Object)
            throw new PluginException("InvalidArgument", "Apply parameters must be an object.");
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in args.EnumerateObject())
            if (!names.Add(property.Name) || property.Name is not ("deviceId" or "operationId" or "color" or "brightness" or "off"))
                throw new PluginException("InvalidArgument", "Unknown or duplicate apply parameter.");
        string device = RequiredString(args, "deviceId", 128);
        string operation = RequiredString(args, "operationId", 128);
        string color = RequiredString(args, "color", 7).ToUpperInvariant();
        It5702Protocol.ParseColor(color);
        if (!args.TryGetProperty("brightness", out var brightness) || brightness.ValueKind != JsonValueKind.Number ||
            !brightness.TryGetDouble(out double value))
            throw new PluginException("InvalidArgument", "brightness must be a finite number from 0 to 1.");
        It5702Protocol.ValidateBrightness(value);
        if (!args.TryGetProperty("off", out var off) || off.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
            throw new PluginException("InvalidArgument", "off must be a boolean.");
        return new(device, operation, color, value, off.GetBoolean());
    }

    internal static string RequiredString(JsonElement args, string name, int maxLength)
    {
        if (args.ValueKind != JsonValueKind.Object || !args.TryGetProperty(name, out var field) ||
            field.ValueKind != JsonValueKind.String || field.GetString() is not { } value ||
            string.IsNullOrWhiteSpace(value) || value.Length > maxLength)
            throw new PluginException("InvalidArgument", $"{name} must be a nonempty string of at most {maxLength} characters.");
        return value;
    }
}

internal static class It5702Protocol
{
    public const int ReportLength = 64;

    public static byte[] Command(byte command, byte argument = 0)
    {
        var report = new byte[ReportLength];
        report[0] = 0xcc;
        report[1] = command;
        report[2] = argument;
        return report;
    }

    public static (byte Red, byte Green, byte Blue) ParseColor(string color)
    {
        if (color.Length != 7 || color[0] != '#' || color.AsSpan(1).ContainsAnyExcept("0123456789abcdefABCDEF"))
            throw new PluginException("InvalidArgument", "color must use exactly #RRGGBB hexadecimal notation.");
        return (byte.Parse(color.AsSpan(1, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture),
                byte.Parse(color.AsSpan(3, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture),
                byte.Parse(color.AsSpan(5, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture));
    }

    public static void ValidateBrightness(double brightness)
    {
        if (!double.IsFinite(brightness) || brightness < 0 || brightness > 1)
            throw new PluginException("InvalidArgument", "brightness must be a finite number from 0 to 1.");
    }

    public static byte[] StaticReport(int channel, string color, double brightness, bool off)
    {
        if (channel is < 0 or > 7) throw new ArgumentOutOfRangeException(nameof(channel));
        var rgb = ParseColor(color);
        ValidateBrightness(brightness);
        var report = Command((byte)(0x20 + channel), (byte)(1 << channel));
        report[11] = 1;
        // liquidctl limits fixed mode to 90 percent. Keep RGB intact; use the hardware brightness field.
        report[12] = off ? (byte)0 : (byte)Math.Round(90 * brightness, MidpointRounding.AwayFromZero);
        if (!off)
        {
            report[14] = rgb.Blue;
            report[15] = rgb.Green;
            report[16] = rgb.Red;
        }
        return report;
    }

    public static Identification Identify(byte[] report)
    {
        if (report.Length != ReportLength || report[0] is not (0xcc or 0))
            throw new PluginException("IdentificationFailed", "灯控识别响应格式无效，未发送灯光命令。");
        if (report[1] != 1 || report.AsSpan(4, 4).IndexOfAnyExcept((byte)0) < 0)
            throw new PluginException("ControllerIdentityUnavailable", "灯控器未返回有效产品标识或固件版本（响应头 " + Convert.ToHexString(report.AsSpan(0, 12)) + "）。请先完整断电再重新检测；软件重启 USB 未能恢复。未发送灯光命令。");
        int end = Array.IndexOf(report, (byte)0, 12, 28);
        if (end < 0) end = 40;
        string name = Encoding.ASCII.GetString(report, 12, end - 12);
        if (string.IsNullOrWhiteSpace(name) || !name.StartsWith("IT57", StringComparison.Ordinal) || !name.Contains("GIGABYTE", StringComparison.Ordinal))
            throw new PluginException("ControllerIdentityUnavailable", "灯控器产品名称缺失或不在已适配范围内，未发送灯光命令。");
        string firmware = $"{report[4]}.{report[5]}.{report[6]}.{report[7]}";
        return new(name, firmware, (report[11] & 2) != 0);
    }
}

internal sealed record Identification(string Name, string Firmware, bool HasLampArray);
