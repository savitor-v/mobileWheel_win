using System.Text;

namespace MobileWheel.Win.Services;

/// <summary>
/// 把 BLE 通知的字节流按 JSON 对象边界重新拼装成完整帧。
/// 安卓端每次通知发一整行 JSON，但协商到的 MTU 较小时会被拆成多个包，
/// 这里用括号配对（忽略字符串内的括号）来判定一个对象是否收完。
/// </summary>
public sealed class TiltFrameParser
{
    private const int MaxBufferBytes = 64 * 1024;

    private readonly List<byte> _buffer = new(256);

    public List<string> Append(ReadOnlySpan<byte> data)
    {
        var frames = new List<string>();
        foreach (var b in data)
        {
            _buffer.Add(b);
        }

        var consumed = 0;
        while (consumed < _buffer.Count)
        {
            // 丢弃对象之前的分隔符/噪声字节
            if (_buffer[consumed] != (byte)'{')
            {
                consumed++;
                continue;
            }

            var end = FindObjectEnd(consumed);
            if (end < 0) break; // 对象还没收全，等下一个包

            frames.Add(Encoding.UTF8.GetString(_buffer.GetRange(consumed, end - consumed + 1).ToArray()));
            consumed = end + 1;
        }

        if (consumed > 0)
        {
            _buffer.RemoveRange(0, consumed);
        }

        if (_buffer.Count > MaxBufferBytes)
        {
            _buffer.Clear();
        }

        return frames;
    }

    public void Reset() => _buffer.Clear();

    /// <summary>从 start（'{' 的位置）开始找配对的 '}'，未收全返回 -1。</summary>
    private int FindObjectEnd(int start)
    {
        var depth = 0;
        var inString = false;
        var escaped = false;

        for (var i = start; i < _buffer.Count; i++)
        {
            var current = _buffer[i];

            if (inString)
            {
                if (escaped) escaped = false;
                else if (current == (byte)'\\') escaped = true;
                else if (current == (byte)'"') inString = false;
                continue;
            }

            switch (current)
            {
                case (byte)'"':
                    inString = true;
                    break;
                case (byte)'{':
                    depth++;
                    break;
                case (byte)'}':
                    depth--;
                    if (depth == 0) return i;
                    break;
            }
        }

        return -1;
    }
}