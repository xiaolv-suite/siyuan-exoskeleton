using System;
using System.Collections;
using System.Collections.Generic;
using System.Web.Script.Serialization;

namespace XiaoLv.Desktop.Core
{
    /// <summary>
    /// 高性能纯原生 JSON 序列化/反序列化助手（基于 .NET 4.8 System.Web.Script.Serialization）
    /// 彻底告别 Quicker 时代的脆弱正则表达式匹配，支持任意复杂嵌套结构、Unicode 字符与多行转义
    /// </summary>
    public static class JsonHelper
    {
        [ThreadStatic]
        private static JavaScriptSerializer _threadSerializer;

        private static JavaScriptSerializer GetSerializer()
        {
            if (_threadSerializer == null)
            {
                _threadSerializer = new JavaScriptSerializer
                {
                    MaxJsonLength = int.MaxValue,
                    RecursionLimit = 256
                };
            }
            return _threadSerializer;
        }

        public static string ToJson(object obj)
        {
            if (obj == null) return "null";
            try
            {
                return GetSerializer().Serialize(obj);
            }
            catch
            {
                return "{}";
            }
        }

        public static T FromJson<T>(string json)
        {
            if (string.IsNullOrWhiteSpace(json)) return default(T);
            try
            {
                return GetSerializer().Deserialize<T>(json);
            }
            catch
            {
                return default(T);
            }
        }

        public static Dictionary<string, object> ParseDict(string json)
        {
            if (string.IsNullOrWhiteSpace(json)) return new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
            try
            {
                var dict = GetSerializer().Deserialize<Dictionary<string, object>>(json);
                if (dict != null)
                {
                    return new Dictionary<string, object>(dict, StringComparer.OrdinalIgnoreCase);
                }
            }
            catch { }
            return new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
        }

        public static List<object> ParseList(string json)
        {
            if (string.IsNullOrWhiteSpace(json)) return new List<object>();
            try
            {
                var list = GetSerializer().Deserialize<List<object>>(json);
                return list ?? new List<object>();
            }
            catch
            {
                return new List<object>();
            }
        }

        public static string GetString(IDictionary<string, object> dict, string key, string def = "")
        {
            if (dict == null || string.IsNullOrEmpty(key)) return def;
            object val;
            if (dict.TryGetValue(key, out val) && val != null)
            {
                return val.ToString();
            }
            return def;
        }

        public static bool GetBool(IDictionary<string, object> dict, string key, bool def = false)
        {
            if (dict == null || string.IsNullOrEmpty(key)) return def;
            object val;
            if (dict.TryGetValue(key, out val) && val != null)
            {
                if (val is bool) return (bool)val;
                bool b;
                if (bool.TryParse(val.ToString(), out b)) return b;
            }
            return def;
        }

        public static int GetInt(IDictionary<string, object> dict, string key, int def = 0)
        {
            if (dict == null || string.IsNullOrEmpty(key)) return def;
            object val;
            if (dict.TryGetValue(key, out val) && val != null)
            {
                if (val is int) return (int)val;
                int i;
                if (int.TryParse(val.ToString(), out i)) return i;
            }
            return def;
        }

        public static double GetDouble(IDictionary<string, object> dict, string key, double def = 0.0)
        {
            if (dict == null || string.IsNullOrEmpty(key)) return def;
            object val;
            if (dict.TryGetValue(key, out val) && val != null)
            {
                if (val is double) return (double)val;
                if (val is int) return (double)(int)val;
                if (val is float) return (double)(float)val;
                double d;
                if (double.TryParse(val.ToString(), System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out d)) return d;
            }
            return def;
        }

        public static Dictionary<string, object> GetDict(IDictionary<string, object> dict, string key)
        {
            if (dict == null || string.IsNullOrEmpty(key)) return null;
            object val;
            if (dict.TryGetValue(key, out val) && val != null)
            {
                if (val is Dictionary<string, object>)
                {
                    return new Dictionary<string, object>((Dictionary<string, object>)val, StringComparer.OrdinalIgnoreCase);
                }
            }
            return null;
        }

        public static List<object> GetList(IDictionary<string, object> dict, string key)
        {
            if (dict == null || string.IsNullOrEmpty(key)) return null;
            object val;
            if (dict.TryGetValue(key, out val) && val != null)
            {
                if (val is ArrayList)
                {
                    var list = new List<object>();
                    foreach (var item in (ArrayList)val) list.Add(item);
                    return list;
                }
                if (val is List<object>) return (List<object>)val;
                if (val is IEnumerable)
                {
                    var list = new List<object>();
                    foreach (var item in (IEnumerable)val) list.Add(item);
                    return list;
                }
            }
            return null;
        }
    }
}
