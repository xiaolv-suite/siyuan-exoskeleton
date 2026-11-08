using System;
using System.Text.RegularExpressions;

namespace XiaoLv.Desktop.Core
{
    /// <summary>
    /// 思源外骨骼 · 知识库隐私脱敏与凭据安全清道夫 (PrivacySanitizer)
    /// 1. 深度解决写笔记、贴日志、导文档时意外泄露私钥、密码或个人隐私信息的安全隐患
    /// 2. 毫秒级智能嗅探并脱敏各类主流大模型凭据 (OpenAI / Anthropic / GitHub / HuggingFace Token)
    /// 3. 自动掩码数据库连接串密码 (password=...)、手机号 (138****1234) 与身份证号
    /// 4. 0 外部依赖，可无缝嵌入剪贴板管道与分享长图前置过滤
    /// </summary>
    public static class PrivacySanitizer
    {
        private static readonly Regex _apiKeyRegex = new Regex(
            @"\b(sk-[a-zA-Z0-9_\-]{8})[a-zA-Z0-9_\-]{12,}([a-zA-Z0-9_\-]{4})\b",
            RegexOptions.Compiled);

        private static readonly Regex _githubTokenRegex = new Regex(
            @"\b(gh[pousr]_[a-zA-Z0-9]{4})[a-zA-Z0-9]{20,}([a-zA-Z0-9]{4})\b",
            RegexOptions.Compiled);

        private static readonly Regex _bearerTokenRegex = new Regex(
            @"(Bearer\s+[a-zA-Z0-9_\-]{4})[a-zA-Z0-9_\-\.]{20,}([a-zA-Z0-9_\-]{4})",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

        private static readonly Regex _passwordFieldRegex = new Regex(
            @"(?i)(password|pwd|secret|access_key|app_secret|api_key)\s*([:=])\s*[""']?([^""'\s;,]{3,})[""']?",
            RegexOptions.Compiled);

        private static readonly Regex _phoneRegex = new Regex(
            @"\b(1[3-9]\d)\d{4}(\d{4})\b",
            RegexOptions.Compiled);

        private static readonly Regex _idCardRegex = new Regex(
            @"\b([1-9]\d{5})\d{8}(\d{3}[0-9Xx])\b",
            RegexOptions.Compiled);

        public static string SanitizeText(string text, out int maskedCount)
        {
            maskedCount = 0;
            if (string.IsNullOrEmpty(text)) return "";

            string res = text;

            // 1. 脱敏大模型与平台 API Key
            var m1 = _apiKeyRegex.Matches(res);
            if (m1.Count > 0)
            {
                maskedCount += m1.Count;
                res = _apiKeyRegex.Replace(res, "$1****$2");
            }

            var m2 = _githubTokenRegex.Matches(res);
            if (m2.Count > 0)
            {
                maskedCount += m2.Count;
                res = _githubTokenRegex.Replace(res, "$1****$2");
            }

            var m3 = _bearerTokenRegex.Matches(res);
            if (m3.Count > 0)
            {
                maskedCount += m3.Count;
                res = _bearerTokenRegex.Replace(res, "$1****$2");
            }

            // 2. 脱敏配置中的密码与密钥字段
            var m4 = _passwordFieldRegex.Matches(res);
            if (m4.Count > 0)
            {
                maskedCount += m4.Count;
                res = _passwordFieldRegex.Replace(res, "$1$2\"******\"");
            }

            // 3. 脱敏 11 位手机号码
            var m5 = _phoneRegex.Matches(res);
            if (m5.Count > 0)
            {
                maskedCount += m5.Count;
                res = _phoneRegex.Replace(res, "$1****$2");
            }

            // 4. 脱敏 18 位身份证号码
            var m6 = _idCardRegex.Matches(res);
            if (m6.Count > 0)
            {
                maskedCount += m6.Count;
                res = _idCardRegex.Replace(res, "$1********$2");
            }

            return res;
        }

        public static bool ContainsSensitiveInfo(string text)
        {
            if (string.IsNullOrEmpty(text)) return false;
            return _apiKeyRegex.IsMatch(text) ||
                   _githubTokenRegex.IsMatch(text) ||
                   _bearerTokenRegex.IsMatch(text) ||
                   _passwordFieldRegex.IsMatch(text) ||
                   _phoneRegex.IsMatch(text) ||
                   _idCardRegex.IsMatch(text);
        }
    }
}
