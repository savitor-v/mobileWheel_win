using System.Text.Json;

namespace MobileWheel.Win.Models;

/// <summary>
/// 一帧倾斜角数据，对应安卓端局域网通道里的一行 JSON：
/// {"angle":-3.3,"direction":"向右倾斜","rotation":270,"lt":0,"rt":128,"ts":1790314159000}
/// </summary>
public sealed record TiltReading(double Angle, string Direction, int Rotation, long Timestamp, int LeftTrigger, int RightTrigger)
{
    public static bool TryParse(string json, out TiltReading reading)
    {
        reading = default!;
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return false;
            if (!root.TryGetProperty("angle", out var angleElement) ||
                !angleElement.TryGetDouble(out var angle))
            {
                return false;
            }

            var direction = root.TryGetProperty("direction", out var directionElement)
                ? directionElement.GetString() ?? string.Empty
                : string.Empty;

            var rotation = root.TryGetProperty("rotation", out var rotationElement) &&
                           rotationElement.TryGetInt32(out var rotationValue)
                ? rotationValue
                : 0;

            var timestamp = root.TryGetProperty("ts", out var tsElement) &&
                            tsElement.TryGetInt64(out var tsValue)
                ? tsValue
                : 0;

            // 扳机为可选字段：旧版手机端不发这两项时按 0 处理
            var leftTrigger = ReadTrigger(root, "lt");
            var rightTrigger = ReadTrigger(root, "rt");

            reading = new TiltReading(Math.Round(angle, 1), direction, rotation, timestamp, leftTrigger, rightTrigger);
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    /// <summary>读扳机值并夹到 0..255（Xbox 360 扳机为单字节）</summary>
    private static int ReadTrigger(JsonElement root, string name) =>
        root.TryGetProperty(name, out var element) && element.TryGetInt32(out var value)
            ? Math.Clamp(value, 0, 255)
            : 0;
}