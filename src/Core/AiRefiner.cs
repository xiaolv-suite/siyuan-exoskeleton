using System;
using System.IO;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace XiaoLv.Desktop.Core
{
    public class AiRefineResult
    {
        public bool Success { get; set; }
        public string Content { get; set; }
        public string ErrorMessage { get; set; }
        public long LatencyMs { get; set; }

        public AiRefineResult()
        {
            Success = false;
            Content = "";
            ErrorMessage = "";
            LatencyMs = 0;
        }
    }

    /// <summary>
    /// 思源外骨骼 · 国内与本地大模型前置提炼清洗微舱 (Pre-Ingestion AI Refiner)
    /// 遵循原则：严格专注于外部输入落盘前的高保真提炼、去噪排版与任务结构化，不与思源原生深层知识图谱智能体争地盘
    /// 兼容 OpenAI 标准 API：完美支持本地 Ollama (默认免Key)、通义千问、豆包、DeepSeek、硅基流动、LocalAI、LM Studio
    /// </summary>
    public static class AiRefiner
    {
        public const string PromptKeyPoints = "请阅读以下用户摘录的内容，将其核心要点提炼为3条高度凝练的Markdown无序列表项（- ），保留关键数据与重要结论，去除冗余废话，使用中文直接输出：";
        public const string PromptActionItems = "请从以下文本或会议内容中，提取所有可执行的行动项与任务清单，转换为标准待办列表项（- [ ] 任务内容），包含截止时间或负责人（若有），使用中文直接输出：";
        public const string PromptPolish = "请对以下笔记内容进行结构化排版与去噪润色：纠正错别字，规范中英文间距（盘古排版），修复断行与标点，保留所有原始事实与代码，直接输出润色后的Markdown正文，严禁输出任何额外客套话：";
        public const string PromptTranslate = "请将以下学术文献或技术内容翻译为流畅、准确的中文学术语言，保持专业术语准确性，直接输出翻译结果：";

        public static AiRefineResult TestConnection(string baseUrl, string apiKey, string model)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var res = new AiRefineResult();

            if (string.IsNullOrEmpty(baseUrl)) baseUrl = "http://localhost:11434/v1";
            if (string.IsNullOrEmpty(model)) model = "qwen2.5:latest";

            try
            {
                string url = baseUrl.TrimEnd('/') + "/chat/completions";
                var req = (HttpWebRequest)WebRequest.Create(url);
                req.Method = "POST";
                req.ContentType = "application/json; charset=utf-8";
                req.Timeout = 8000;
                if (!string.IsNullOrEmpty(apiKey))
                {
                    req.Headers["Authorization"] = "Bearer " + apiKey;
                }

                string payload = "{\"model\":\"" + JsonEscape(model) + "\",\"messages\":[{\"role\":\"user\",\"content\":\"ping\"}],\"max_tokens\":5}";
                byte[] bytes = Encoding.UTF8.GetBytes(payload);
                req.ContentLength = bytes.Length;

                using (var reqStream = req.GetRequestStream())
                {
                    reqStream.Write(bytes, 0, bytes.Length);
                }

                using (var resp = (HttpWebResponse)req.GetResponse())
                using (var s = resp.GetResponseStream())
                using (var r = new StreamReader(s, Encoding.UTF8))
                {
                    string json = r.ReadToEnd();
                    sw.Stop();
                    res.Success = true;
                    res.LatencyMs = sw.ElapsedMilliseconds;
                    res.Content = "连接成功 (" + sw.ElapsedMilliseconds + "ms)";
                    return res;
                }
            }
            catch (Exception ex)
            {
                sw.Stop();
                res.Success = false;
                res.LatencyMs = sw.ElapsedMilliseconds;
                res.ErrorMessage = ex.Message;
                return res;
            }
        }

        public static AiRefineResult Refine(string instruction, string inputText, string baseUrl = "", string apiKey = "", string model = "")
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var res = new AiRefineResult();

            if (string.IsNullOrEmpty(inputText))
            {
                res.Success = false;
                res.ErrorMessage = "输入内容为空";
                return res;
            }

            var settings = AppSettings.Instance;
            if (string.IsNullOrEmpty(baseUrl)) baseUrl = !string.IsNullOrEmpty(settings.AiBaseUrl) ? settings.AiBaseUrl : "http://localhost:11434/v1";
            if (string.IsNullOrEmpty(model)) model = !string.IsNullOrEmpty(settings.AiModel) ? settings.AiModel : "qwen2.5:latest";
            if (string.IsNullOrEmpty(apiKey)) apiKey = settings.AiApiKey;

            try
            {
                string url = baseUrl.TrimEnd('/') + "/chat/completions";
                var req = (HttpWebRequest)WebRequest.Create(url);
                req.Method = "POST";
                req.ContentType = "application/json; charset=utf-8";
                req.Timeout = 25000;
                if (!string.IsNullOrEmpty(apiKey))
                {
                    req.Headers["Authorization"] = "Bearer " + apiKey;
                }

                string userPrompt = instruction + "\n\n---\n" + inputText;
                string payload = "{\"model\":\"" + JsonEscape(model) + "\",\"messages\":[{\"role\":\"user\",\"content\":\"" + JsonEscape(userPrompt) + "\"}],\"temperature\":0.3}";
                byte[] bytes = Encoding.UTF8.GetBytes(payload);
                req.ContentLength = bytes.Length;

                using (var reqStream = req.GetRequestStream())
                {
                    reqStream.Write(bytes, 0, bytes.Length);
                }

                using (var resp = (HttpWebResponse)req.GetResponse())
                using (var s = resp.GetResponseStream())
                using (var r = new StreamReader(s, Encoding.UTF8))
                {
                    string json = r.ReadToEnd();
                    sw.Stop();
                    res.LatencyMs = sw.ElapsedMilliseconds;

                    // 提取 content
                    var mContent = Regex.Match(json, "\"content\"\\s*:\\s*\"([\\s\\S]*?)\"\\s*(?:,\\s*\"refusal\"|\\})");
                    if (mContent.Success)
                    {
                        res.Success = true;
                        res.Content = JsonUnescape(mContent.Groups[1].Value);
                        return res;
                    }
                    else
                    {
                        res.Success = false;
                        res.ErrorMessage = "未找到响应正文";
                        return res;
                    }
                }
            }
            catch (Exception ex)
            {
                sw.Stop();
                res.Success = false;
                res.LatencyMs = sw.ElapsedMilliseconds;
                res.ErrorMessage = ex.Message;
                return res;
            }
        }

        private static string JsonEscape(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            return s.Replace("\\", "\\\\")
                    .Replace("\"", "\\\"")
                    .Replace("\r", "\\r")
                    .Replace("\n", "\\n")
                    .Replace("\t", "\\t");
        }

        private static string JsonUnescape(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            return s.Replace("\\n", "\n")
                    .Replace("\\r", "\r")
                    .Replace("\\t", "\t")
                    .Replace("\\\"", "\"")
                    .Replace("\\\\", "\\");
        }
    }
}
