using System.Text;

namespace GithubMCPSharp.Tools;

internal static class LogText
{
    /// <summary>
    /// Clip <paramref name="text"/> to at most <paramref name="maxBytes"/> UTF-8 bytes, marking what was dropped.
    /// With <paramref name="fromEnd"/> the tail is kept (where a CI failure reports itself); <paramref name="headBytes"/>
    /// additionally keeps that many bytes from the start, so one call can show both which step ran and how it died.
    /// </summary>
    public static string Clip(string text, int maxBytes, bool fromEnd, int headBytes)
    {
        var limit = Math.Max(1, maxBytes);
        var bytes = Encoding.UTF8.GetBytes(text);
        var total = bytes.Length;
        if (total <= limit) return text;

        if (!fromEnd)
        {
            var headOnly = SliceFromStart(bytes, limit);
            return headOnly + $"\n\n[clipped: showing the first {Encoding.UTF8.GetByteCount(headOnly)} of {total} bytes — " +
                              "raise maxBytes, or use fromEnd=true for the failure at the end]";
        }

        // Reserve at least one byte for the tail; the tail is the point of fromEnd.
        var headBudget = Math.Clamp(headBytes, 0, limit - 1);
        var tail = SliceFromEnd(bytes, limit - headBudget);

        if (headBudget == 0)
        {
            var omitted = total - Encoding.UTF8.GetByteCount(tail);
            return $"[clipped: first {omitted} of {total} bytes omitted — raise maxBytes or set headBytes>0 for the start]\n\n" + tail;
        }

        var head = SliceFromStart(bytes, headBudget);
        var gap = total - Encoding.UTF8.GetByteCount(head) - Encoding.UTF8.GetByteCount(tail);
        return head + $"\n\n[... {gap} of {total} bytes omitted ...]\n\n" + tail;
    }

    /// <summary>Take the first <paramref name="count"/> bytes, trimming back so a multi-byte sequence is never split.</summary>
    private static string SliceFromStart(byte[] bytes, int count)
    {
        var take = Math.Clamp(count, 0, bytes.Length);
        // bytes[take] is the first byte *past* the slice; if it continues a sequence, step back off it.
        while (take > 0 && take < bytes.Length && IsContinuation(bytes[take])) take--;
        return Encoding.UTF8.GetString(bytes, 0, take);
    }

    /// <summary>Take the last <paramref name="count"/> bytes, advancing so the slice starts on a lead byte.</summary>
    private static string SliceFromEnd(byte[] bytes, int count)
    {
        var take = Math.Clamp(count, 0, bytes.Length);
        var start = bytes.Length - take;
        while (start < bytes.Length && IsContinuation(bytes[start])) start++;
        return Encoding.UTF8.GetString(bytes, start, bytes.Length - start);
    }

    private static bool IsContinuation(byte b) => (b & 0xC0) == 0x80;
}
