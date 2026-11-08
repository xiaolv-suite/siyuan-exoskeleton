using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace XiaoLv.Desktop.Core
{
    public class TableColumnInfo
    {
        public string Header { get; set; }
        public string DetectedType { get; set; } // "text" | "number" | "date" | "checkbox" | "url"
        public int MaxWidth { get; set; }

        public TableColumnInfo()
        {
            Header = "";
            DetectedType = "text";
            MaxWidth = 4;
        }
    }

    public class TableStructure
    {
        public List<TableColumnInfo> Columns { get; set; }
        public List<List<string>> Rows { get; set; }
        public int RowCount { get { return Rows != null ? Rows.Count : 0; } }
        public int ColCount { get { return Columns != null ? Columns.Count : 0; } }

        public TableStructure()
        {
            Columns = new List<TableColumnInfo>();
            Rows = new List<List<string>>();
        }
    }

    /// <summary>
    /// 思源外骨骼 · 表格与属性视图数据库双向转换与排版引擎 (TableToAvConverter)
    /// 1. 智能解析剪贴板多源表格数据 (Markdown 表格 / TSV 制表符 / CSV 逗号 / 纯文本栅格)
    /// 2. 毫秒级启发式推导列字段数据类型 (文本 / 数值 / 日期 / 复选布尔 / 超链接)
    /// 3. 输出美化居中对齐的 GitHub 规范 Markdown 表格与思源折叠大纲卡片
    /// 4. 支持 1 键转译生成思源原生属性视图 (Attribute View / AV 数据库) 数据结构
    /// 5. 支持 1 键逆向生成原生 Excel (.xlsx) 工作簿并在独立软件中秒开
    /// </summary>
    public static class TableToAvConverter
    {
        /// <summary>
        /// 智能解析多源表格文本为强类型结构对象
        /// </summary>
        public static TableStructure ParseTable(string rawText)
        {
            var result = new TableStructure();
            if (string.IsNullOrWhiteSpace(rawText)) return result;

            string text = rawText.Replace("\r\n", "\n").Replace("\r", "\n").Trim();
            var lines = text.Split(new char[] { '\n' }, StringSplitOptions.RemoveEmptyEntries);
            if (lines.Length == 0) return result;

            // 1. 判断是否为 Markdown 表格 (首行含 '|' 且第二行含 '---')
            bool isMarkdown = false;
            if (lines.Length >= 2 && lines[0].Contains("|") && Regex.IsMatch(lines[1], @"^\|?\s*[-:]+[-| :]*\|$"))
            {
                isMarkdown = true;
            }

            if (isMarkdown)
            {
                ParseMarkdownTable(lines, result);
            }
            else
            {
                // 2. 探测制表符 TSV 或 CSV
                char delimiter = '\t';
                if (!lines[0].Contains("\t") && lines[0].Contains(","))
                {
                    delimiter = ',';
                }
                ParseDelimitedTable(lines, delimiter, result);
            }

            // 3. 自动推导列数据类型与最大列宽
            InferColumnTypes(result);

            return result;
        }

        private static void ParseMarkdownTable(string[] lines, TableStructure result)
        {
            // 首行为表头
            var rawHeaders = lines[0].Trim('|').Split('|');
            for (int c = 0; c < rawHeaders.Length; c++)
            {
                string h = rawHeaders[c].Trim();
                result.Columns.Add(new TableColumnInfo { Header = h, MaxWidth = Math.Max(4, Encoding.UTF8.GetByteCount(h)) });
            }

            // 从第 3 行开始为数据行 (跳过第 2 行的 --- 分隔线)
            for (int i = 2; i < lines.Length; i++)
            {
                var line = lines[i].Trim();
                if (string.IsNullOrEmpty(line)) continue;
                if (line.StartsWith("|") && line.Contains("---")) continue;

                var rawCells = line.Trim('|').Split('|');
                var row = new List<string>();
                for (int c = 0; c < result.Columns.Count; c++)
                {
                    string cell = (c < rawCells.Length) ? rawCells[c].Trim() : "";
                    row.Add(cell);
                    int byteLen = Encoding.UTF8.GetByteCount(cell);
                    if (byteLen > result.Columns[c].MaxWidth)
                    {
                        result.Columns[c].MaxWidth = Math.Min(40, byteLen);
                    }
                }
                result.Rows.Add(row);
            }
        }

        private static void ParseDelimitedTable(string[] lines, char delimiter, TableStructure result)
        {
            for (int r = 0; r < lines.Length; r++)
            {
                var line = lines[r].Trim();
                if (string.IsNullOrEmpty(line)) continue;

                var cells = SplitCsvLine(line, delimiter);
                if (r == 0)
                {
                    for (int c = 0; c < cells.Count; c++)
                    {
                        string h = cells[c].Trim();
                        result.Columns.Add(new TableColumnInfo { Header = h, MaxWidth = Math.Max(4, Encoding.UTF8.GetByteCount(h)) });
                    }
                }
                else
                {
                    var row = new List<string>();
                    for (int c = 0; c < result.Columns.Count; c++)
                    {
                        string cell = (c < cells.Count) ? cells[c].Trim() : "";
                        row.Add(cell);
                        int byteLen = Encoding.UTF8.GetByteCount(cell);
                        if (byteLen > result.Columns[c].MaxWidth)
                        {
                            result.Columns[c].MaxWidth = Math.Min(40, byteLen);
                        }
                    }
                    result.Rows.Add(row);
                }
            }
        }

        private static List<string> SplitCsvLine(string line, char delimiter)
        {
            var list = new List<string>();
            if (string.IsNullOrEmpty(line)) return list;

            var sb = new StringBuilder();
            bool inQuotes = false;

            for (int i = 0; i < line.Length; i++)
            {
                char ch = line[i];
                if (ch == '"')
                {
                    inQuotes = !inQuotes;
                }
                else if (ch == delimiter && !inQuotes)
                {
                    list.Add(sb.ToString().Trim('"', ' '));
                    sb.Clear();
                }
                else
                {
                    sb.Append(ch);
                }
            }
            list.Add(sb.ToString().Trim('"', ' '));
            return list;
        }

        private static void InferColumnTypes(TableStructure table)
        {
            if (table == null || table.Columns.Count == 0 || table.Rows.Count == 0) return;

            for (int c = 0; c < table.Columns.Count; c++)
            {
                int numCount = 0;
                int dateCount = 0;
                int boolCount = 0;
                int urlCount = 0;
                int nonEmptyCount = 0;

                for (int r = 0; r < table.Rows.Count; r++)
                {
                    if (c >= table.Rows[r].Count) continue;
                    string cell = table.Rows[r][c].Trim();
                    if (string.IsNullOrEmpty(cell)) continue;
                    nonEmptyCount++;

                    // 1. 检查布尔
                    if (cell.Equals("true", StringComparison.OrdinalIgnoreCase) || cell.Equals("false", StringComparison.OrdinalIgnoreCase) ||
                        cell == "是" || cell == "否" || cell == "✓" || cell == "✗" || cell == "[x]" || cell == "[ ]")
                    {
                        boolCount++;
                    }
                    // 2. 检查数值 (包含货币 ¥/$ 与百分比 %)
                    else if (Regex.IsMatch(cell, @"^[¥\$€]?\s*-?\d+(?:\.\d+)?%?$"))
                    {
                        numCount++;
                    }
                    // 3. 检查日期
                    else if (Regex.IsMatch(cell, @"^\d{4}[-/年]\d{1,2}[-/月]\d{1,2}[日]?$"))
                    {
                        dateCount++;
                    }
                    // 4. 检查 URL
                    else if (cell.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || cell.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                    {
                        urlCount++;
                    }
                }

                if (nonEmptyCount > 0)
                {
                    double ratio = (double)nonEmptyCount * 0.7;
                    if (boolCount >= ratio) table.Columns[c].DetectedType = "checkbox";
                    else if (numCount >= ratio) table.Columns[c].DetectedType = "number";
                    else if (dateCount >= ratio) table.Columns[c].DetectedType = "date";
                    else if (urlCount >= ratio) table.Columns[c].DetectedType = "url";
                    else table.Columns[c].DetectedType = "text";
                }
            }
        }

        /// <summary>
        /// 格式化为美化对齐的 GitHub 标准 Markdown 表格
        /// 数值右对齐，日期/布尔居中对齐，文本左对齐
        /// </summary>
        public static string FormatBeautifulMarkdownTable(TableStructure table)
        {
            if (table == null || table.Columns.Count == 0) return "";

            var sb = new StringBuilder();

            // 1. 表头行
            sb.Append("|");
            for (int c = 0; c < table.Columns.Count; c++)
            {
                sb.Append(" " + table.Columns[c].Header + " |");
            }
            sb.AppendLine();

            // 2. 分隔线行 (依据推导类型确定对齐符号)
            sb.Append("|");
            for (int c = 0; c < table.Columns.Count; c++)
            {
                string type = table.Columns[c].DetectedType;
                if (type == "number")
                {
                    sb.Append(" ---: |"); // 右对齐
                }
                else if (type == "date" || type == "checkbox")
                {
                    sb.Append(" :---: |"); // 居中对齐
                }
                else
                {
                    sb.Append(" :--- |"); // 左对齐
                }
            }
            sb.AppendLine();

            // 3. 数据行
            for (int r = 0; r < table.Rows.Count; r++)
            {
                sb.Append("|");
                for (int c = 0; c < table.Columns.Count; c++)
                {
                    string cell = (c < table.Rows[r].Count) ? table.Rows[r][c].Replace("|", "\\|").Replace("\n", " ") : "";
                    sb.Append(" " + cell + " |");
                }
                sb.AppendLine();
            }

            return sb.ToString();
        }

        /// <summary>
        /// 将表格转换为思源原生折叠大纲列表 (Tier 2 保底形态)
        /// </summary>
        public static string ConvertToFoldableList(TableStructure table, string title = "数据结构大纲")
        {
            if (table == null || table.Columns.Count == 0 || table.Rows.Count == 0) return "";

            var sb = new StringBuilder();
            sb.AppendFormat("### 📊 《{0}》 (共 {1} 行 · {2} 列)\n", title, table.RowCount, table.ColCount);

            for (int r = 0; r < table.Rows.Count; r++)
            {
                string firstCol = (table.Rows[r].Count > 0) ? table.Rows[r][0] : ("记录 " + (r + 1));
                sb.AppendFormat("* **{0}**\n", firstCol);

                for (int c = 1; c < table.Columns.Count; c++)
                {
                    string h = table.Columns[c].Header;
                    string v = (c < table.Rows[r].Count) ? table.Rows[r][c] : "";
                    sb.AppendFormat("  * {0}: {1}\n", h, v);
                }
            }
            sb.AppendFormat("\n{{: custom-type=\"data-outline\" custom-rows=\"{0}\" custom-cols=\"{1}\" }}\n", table.RowCount, table.ColCount);

            return sb.ToString();
        }

        /// <summary>
        /// 导出为标准 CSV 文本
        /// </summary>
        public static string ConvertToCsv(TableStructure table)
        {
            if (table == null || table.Columns.Count == 0) return "";

            var sb = new StringBuilder();
            var hList = new List<string>();
            foreach (var col in table.Columns)
            {
                hList.Add(EscapeCsvCell(col.Header));
            }
            sb.AppendLine(string.Join(",", hList));

            foreach (var row in table.Rows)
            {
                var rList = new List<string>();
                for (int c = 0; c < table.Columns.Count; c++)
                {
                    string cell = (c < row.Count) ? row[c] : "";
                    rList.Add(EscapeCsvCell(cell));
                }
                sb.AppendLine(string.Join(",", rList));
            }

            return sb.ToString();
        }

        private static string EscapeCsvCell(string cell)
        {
            if (string.IsNullOrEmpty(cell)) return "";
            if (cell.Contains(",") || cell.Contains("\"") || cell.Contains("\n") || cell.Contains("\r"))
            {
                return "\"" + cell.Replace("\"", "\"\"") + "\"";
            }
            return cell;
        }

        /// <summary>
        /// 1 键将表格结构逆向导出为本地 Excel .xlsx 文件秒开
        /// </summary>
        public static string ExportToXlsxAndOpen(TableStructure table, string title = "数据表格")
        {
            if (table == null || table.Columns.Count == 0) return "";
            try
            {
                string md = FormatBeautifulMarkdownTable(table);
                string path = OfficeExporter.ExportTableToXlsx(md, title);
                if (!string.IsNullOrEmpty(path) && File.Exists(path))
                {
                    System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(path) { UseShellExecute = true });
                    return path;
                }
            }
            catch { }
            return "";
        }

        /// <summary>
        /// 构造思源原生属性视图 (Attribute View / AV 数据库) 标准视图数据结构
        /// </summary>
        public static Dictionary<string, object> BuildSiYuanAvDataJson(TableStructure table, string avName = "数据视图")
        {
            var av = new Dictionary<string, object>();
            av["name"] = avName;
            av["type"] = "table";

            var cols = new List<object>();
            for (int c = 0; c < table.Columns.Count; c++)
            {
                var colDict = new Dictionary<string, object>();
                colDict["id"] = "col_" + c;
                colDict["name"] = table.Columns[c].Header;
                colDict["type"] = MapToAvColType(table.Columns[c].DetectedType);
                cols.Add(colDict);
            }
            av["columns"] = cols;

            var rows = new List<object>();
            for (int r = 0; r < table.Rows.Count; r++)
            {
                var rowDict = new Dictionary<string, object>();
                rowDict["id"] = "row_" + r;
                var values = new Dictionary<string, object>();
                for (int c = 0; c < table.Columns.Count; c++)
                {
                    string v = (c < table.Rows[r].Count) ? table.Rows[r][c] : "";
                    values["col_" + c] = v;
                }
                rowDict["values"] = values;
                rows.Add(rowDict);
            }
            av["rows"] = rows;

            return av;
        }

        private static string MapToAvColType(string detectedType)
        {
            switch (detectedType)
            {
                case "number": return "number";
                case "date": return "date";
                case "checkbox": return "checkbox";
                case "url": return "url";
                default: return "text";
            }
        }
    }
}
