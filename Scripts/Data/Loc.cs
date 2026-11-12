using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace BridgeArchitect3D.Data
{
    /// <summary>
    /// Interface language. Source text is the Chinese string itself; English comes from Data/Locale/en.json
    /// (Chinese text → English). Missing entries fall back to the Chinese text. Pure C# (also used by tests).
    /// </summary>
    public static class Loc
    {
        public const string Chinese = "zh", English = "en";
        private static Dictionary<string, string> _en = new();

        /// <summary>Current language: "zh" (default) or "en".</summary>
        public static string Language { get; set; } = Chinese;
        public static bool IsEnglish => Language == English;

        /// <summary>Load the Chinese → English table (JSON object).</summary>
        public static void LoadEnglish(string json)
        {
            var table = JsonSerializer.Deserialize<Dictionary<string, string>>(json) ?? new();
            table.Remove("_comment");
            _en = table;
        }

        public static IReadOnlyDictionary<string, string> EnglishTable => _en;

        /// <summary>Translate a fixed text.</summary>
        public static string T(string zh)
        {
            if (!IsEnglish || string.IsNullOrEmpty(zh)) return zh;
            return _en.TryGetValue(zh, out var en) && !string.IsNullOrEmpty(en) ? en : zh;
        }

        /// <summary>Translate a format text ({0}, {1:0.0} …) and fill in the values.</summary>
        public static string F(string zhFormat, params object[] args)
        {
            string format = T(zhFormat);
            try { return string.Format(CultureInfo.CurrentCulture, format, args); }
            catch (FormatException) { return string.Format(CultureInfo.CurrentCulture, zhFormat, args); }
        }

        /// <summary>Translate every string value of a JSON document that has an entry (level / material / vehicle texts).</summary>
        public static string TranslateJson(string json)
        {
            if (!IsEnglish) return json;
            var root = JsonNode.Parse(json, documentOptions: new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
            Visit(root);
            return root?.ToJsonString() ?? json;
        }

        private static void Visit(JsonNode node)
        {
            switch (node)
            {
                case JsonObject obj:
                    foreach (var key in new List<string>(GetKeys(obj)))
                    {
                        var child = obj[key];
                        if (child is JsonValue v && v.TryGetValue(out string s)) { if (_en.TryGetValue(s, out var en)) obj[key] = en; }
                        else Visit(child);
                    }
                    break;
                case JsonArray arr:
                    for (int i = 0; i < arr.Count; i++)
                    {
                        if (arr[i] is JsonValue v && v.TryGetValue(out string s)) { if (_en.TryGetValue(s, out var en)) arr[i] = en; }
                        else Visit(arr[i]);
                    }
                    break;
            }
        }

        private static IEnumerable<string> GetKeys(JsonObject obj) { foreach (var kv in obj) yield return kv.Key; }
    }
}
