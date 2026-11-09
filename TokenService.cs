using System;
using System.Text;
using SharpToken;

namespace TreeDump
{
    public static class TokenService
    {
        private const int ExactTokenizationMaxChars = 256_000;

        private static readonly Lazy<GptEncoding?> EncodingInstance = new(() =>
        {
            try
            {
                return GptEncoding.GetEncoding("cl100k_base");
            }
            catch (Exception ex)
            {
                App.LogException("TokenService.Init", ex);
                return null;
            }
        }, isThreadSafe: true);

        public static int CountTokens(string? text)
        {
            if (string.IsNullOrEmpty(text))
                return 0;

            // For very large strings, use fast UTF-8 byte heuristic to keep UI responsive
            if (text.Length > ExactTokenizationMaxChars)
            {
                return EstimateFromUtf8Bytes(text);
            }

            GptEncoding? encoding = EncodingInstance.Value;
            if (encoding != null)
            {
                try
                {
                    return encoding.CountTokens(text);
                }
                catch
                {
                    // Fall back to byte estimation if tokenizer fails on unusual character sequences
                }
            }

            return EstimateFromUtf8Bytes(text);
        }

        public static int EstimateFromUtf8Bytes(string? text)
        {
            if (string.IsNullOrEmpty(text))
                return 0;

            int byteCount = Encoding.UTF8.GetByteCount(text);
            return Math.Max(1, (byteCount + 3) / 4);
        }
    }
}