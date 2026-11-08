using System;
using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;

namespace XiaoLv.Desktop.Core
{
    /// <summary>
    /// 思源外骨骼 · 开发者极速变量命名法转换工具 (NamingCaseConverter)
    /// 1. 智能拆解并感知变量单词边界 (camelCase / PascalCase / snake_case / kebab-case)
    /// 2. 毫秒级转换为各种目标命名法并在外部光标处无感回贴
    /// 3. 支持 CycleNext 循环迭代切换 (camel ➔ snake ➔ kebab ➔ UPPER ➔ Pascal ➔ camel)
    /// </summary>
    public static class NamingCaseConverter
    {
        public static List<string> SplitIntoWords(string identifier)
        {
            var words = new List<string>();
            if (string.IsNullOrEmpty(identifier)) return words;

            string clean = identifier.Trim();

            // 1. 如果包含下划线、短横线或空格，按分隔符切分
            if (clean.Contains("_") || clean.Contains("-") || clean.Contains(" ") || clean.Contains("."))
            {
                var parts = clean.Split(new char[] { '_', '-', ' ', '.' }, StringSplitOptions.RemoveEmptyEntries);
                foreach (var p in parts)
                {
                    if (!string.IsNullOrEmpty(p)) words.Add(p.ToLowerInvariant());
                }
                return words;
            }

            // 2. 纯字母按驼峰边界切分 (如 userAccountId -> user, Account, Id)
            var matches = Regex.Matches(clean, @"([A-Z]?[a-z0-9]+|[A-Z]+(?=[A-Z][a-z0-9]|\b))");
            if (matches.Count > 0)
            {
                foreach (Match m in matches)
                {
                    if (!string.IsNullOrEmpty(m.Value))
                    {
                        words.Add(m.Value.ToLowerInvariant());
                    }
                }
            }
            else
            {
                words.Add(clean.ToLowerInvariant());
            }

            return words;
        }

        public static string ToCamelCase(string identifier)
        {
            var words = SplitIntoWords(identifier);
            if (words.Count == 0) return "";
            var sb = new StringBuilder();
            sb.Append(words[0].ToLowerInvariant());
            for (int i = 1; i < words.Count; i++)
            {
                string w = words[i];
                if (w.Length > 0)
                {
                    sb.Append(char.ToUpperInvariant(w[0]));
                    if (w.Length > 1) sb.Append(w.Substring(1));
                }
            }
            return sb.ToString();
        }

        public static string ToPascalCase(string identifier)
        {
            var words = SplitIntoWords(identifier);
            if (words.Count == 0) return "";
            var sb = new StringBuilder();
            for (int i = 0; i < words.Count; i++)
            {
                string w = words[i];
                if (w.Length > 0)
                {
                    sb.Append(char.ToUpperInvariant(w[0]));
                    if (w.Length > 1) sb.Append(w.Substring(1));
                }
            }
            return sb.ToString();
        }

        public static string ToSnakeCase(string identifier)
        {
            var words = SplitIntoWords(identifier);
            return string.Join("_", words);
        }

        public static string ToKebabCase(string identifier)
        {
            var words = SplitIntoWords(identifier);
            return string.Join("-", words);
        }

        public static string ToUpperSnakeCase(string identifier)
        {
            var words = SplitIntoWords(identifier);
            var uppers = new List<string>();
            foreach (var w in words) uppers.Add(w.ToUpperInvariant());
            return string.Join("_", uppers);
        }

        /// <summary>
        /// 自动推导当前命名法并轮转为下一个风格
        /// camelCase ➔ snake_case ➔ kebab-case ➔ UPPER_CASE ➔ PascalCase ➔ camelCase
        /// </summary>
        public static string CycleNext(string text)
        {
            if (string.IsNullOrEmpty(text)) return "";
            string trimmed = text.Trim();

            if (trimmed.Contains("-"))
            {
                return ToUpperSnakeCase(trimmed);
            }
            if (trimmed.Contains("_"))
            {
                if (trimmed == trimmed.ToUpperInvariant())
                {
                    return ToPascalCase(trimmed);
                }
                return ToKebabCase(trimmed);
            }
            if (char.IsUpper(trimmed[0]))
            {
                return ToCamelCase(trimmed);
            }
            return ToSnakeCase(trimmed);
        }
    }
}
