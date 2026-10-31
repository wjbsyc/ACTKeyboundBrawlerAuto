using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Web.Script.Serialization;

namespace ACTKeyboundBrawlerAuto
{
    public class WordEntry
    {
        public uint TableTag { get; set; }
        public uint Index { get; set; }
        public string Text { get; set; }
        public string Pinyin { get; set; }
        public string Source { get; set; }
    }

    public class RandomWord
    {
        public uint TableTag { get; set; }
        public uint Index { get; set; }
        public string Text { get; set; }
        public string Pinyin { get; set; }
    }

    public class UnknownWordRecord
    {
        public uint TableTag { get; set; }
        public uint Index { get; set; }
        public int Count { get; set; }
        public DateTime FirstSeen { get; set; }
        public DateTime LastSeen { get; set; }
        public List<string> Examples { get; set; }

        public UnknownWordRecord()
        {
            Examples = new List<string>();
        }
    }

    public class WordTable
    {
        public const uint DefaultTableTag = 0x004C4B4A;

        private readonly object _lock = new object();
        private readonly Dictionary<ulong, WordEntry> _words =
            new Dictionary<ulong, WordEntry>();
        private readonly Dictionary<ulong, UnknownWordRecord> _unknowns =
            new Dictionary<ulong, UnknownWordRecord>();
        private readonly List<string> _warnings = new List<string>();

        public string WordsPath { get; private set; }
        public string UnknownPath { get; private set; }
        public bool Dirty { get; private set; }

        public int KnownCount
        {
            get { lock (_lock) return _words.Count; }
        }

        public int UnknownCount
        {
            get { lock (_lock) return _unknowns.Count; }
        }

        public IList<string> Warnings
        {
            get { lock (_lock) return _warnings.ToArray(); }
        }

        public void Load(string wordsPath, string unknownPath)
        {
            LoadWords(wordsPath);
            LoadPinyin(Path.ChangeExtension(wordsPath, null) + ".pinyin.json");
            LoadUnknown(unknownPath);
            PruneUnknowns();
        }

        public void Reload(string wordsPath)
        {
            LoadWords(wordsPath);
            PruneUnknowns();
        }

        private void LoadWords(string wordsPath)
        {
            WordsPath = Path.GetFullPath(wordsPath);
            lock (_lock)
            {
                _words.Clear();
                _warnings.Clear();
            }

            if (!File.Exists(wordsPath))
            {
                lock (_lock) _warnings.Add("词表文件不存在: " + wordsPath);
                return;
            }

            try
            {
                string json = File.ReadAllText(wordsPath, Encoding.UTF8);
                var serializer = new JavaScriptSerializer();
                var root = serializer.Deserialize<Dictionary<string, object>>(json);
                if (root == null)
                {
                    lock (_lock) _warnings.Add("词表 JSON 根节点不是对象");
                    return;
                }

                object wordsObject;
                if (!root.TryGetValue("words", out wordsObject))
                    wordsObject = root;

                LoadWordsValue(wordsObject);
            }
            catch (Exception ex)
            {
                lock (_lock) _warnings.Add("词表读取失败: " + ex.Message);
            }
        }

        private void LoadWordsValue(object wordsObject)
        {
            var entries = wordsObject as System.Collections.IEnumerable;
            if (entries != null && !(wordsObject is string))
            {
                foreach (object entryObject in entries)
                    LoadWordEntry(entryObject);
                return;
            }

            var simple = wordsObject as Dictionary<string, object>;
            if (simple == null) return;

            foreach (KeyValuePair<string, object> pair in simple)
            {
                uint index;
                if (!TryParseIndex(pair.Key, out index)) continue;
                string text = ConvertToString(pair.Value);
                if (!string.IsNullOrWhiteSpace(text))
                    AddWord(DefaultTableTag, index, text, "simple-json");
            }
        }

        private void LoadPinyin(string pinyinPath)
        {
            lock (_lock)
            {
                foreach (WordEntry entry in _words.Values)
                    entry.Pinyin = null;
            }

            if (!File.Exists(pinyinPath))
                return;

            try
            {
                string json = File.ReadAllText(pinyinPath, Encoding.UTF8);
                var serializer = new JavaScriptSerializer();
                var root = serializer.Deserialize<Dictionary<string, object>>(json);
                object wordsObject;
                if (root == null || !root.TryGetValue("words", out wordsObject))
                    return;

                var entries = wordsObject as System.Collections.IEnumerable;
                if (entries == null || entries is string)
                    return;

                lock (_lock)
                {
                    foreach (object entryObject in entries)
                    {
                        var entry = entryObject as Dictionary<string, object>;
                        if (entry == null)
                            continue;

                        object value;
                        uint table = DefaultTableTag;
                        if ((entry.TryGetValue("table", out value) ||
                             entry.TryGetValue("tableTag", out value)) &&
                            !TryParseTableValue(value, out table))
                            continue;

                        uint index;
                        if (!(entry.TryGetValue("index", out value) ||
                              entry.TryGetValue("id", out value)) ||
                            !TryParseIndexValue(value, out index))
                            continue;

                        string pinyin = null;
                        if (entry.TryGetValue("pinyin", out value) ||
                            entry.TryGetValue("inputText", out value))
                            pinyin = ConvertToString(value);

                        ulong key = MakeKey(table, index);
                        WordEntry word;
                        if (_words.TryGetValue(key, out word) &&
                            !string.IsNullOrWhiteSpace(pinyin))
                            word.Pinyin = pinyin.Trim();
                    }
                }
            }
            catch (Exception ex)
            {
                lock (_lock) _warnings.Add("拼音表读取失败: " + ex.Message);
            }
        }

        private void LoadWordEntry(object entryObject)
        {
            var entry = entryObject as Dictionary<string, object>;
            if (entry == null) return;

            object value;
            uint index = 0;
            if (!(entry.TryGetValue("index", out value) || entry.TryGetValue("id", out value) ||
                  entry.TryGetValue("wordIndex", out value)))
                return;
            if (!TryParseIndexValue(value, out index))
                return;

            uint table = DefaultTableTag;
            if (entry.TryGetValue("table", out value) || entry.TryGetValue("tableTag", out value))
            {
                if (!TryParseTableValue(value, out table))
                    return;
            }

            string text = null;
            if (entry.TryGetValue("text", out value) || entry.TryGetValue("word", out value) ||
                entry.TryGetValue("zh", out value))
                text = ConvertToString(value);

            string source = null;
            if (entry.TryGetValue("source", out value))
                source = ConvertToString(value);

            if (!string.IsNullOrWhiteSpace(text))
                AddWord(table, index, text, source);
        }

        private void AddWord(uint table, uint index, string text, string source)
        {
            lock (_lock)
            {
                ulong key = MakeKey(table, index);
                WordEntry old;
                if (_words.TryGetValue(key, out old))
                {
                    if (!string.Equals(old.Text, text, StringComparison.OrdinalIgnoreCase))
                        _warnings.Add(string.Format(
                            CultureInfo.InvariantCulture,
                            "词表 {0}/0x{1:X} 冲突: {2} / {3}", FormatTable(table), index, old.Text, text));
                    return;
                }

                _words[key] = new WordEntry
                {
                    TableTag = table,
                    Index = index,
                    Text = text.Trim(),
                    Source = string.IsNullOrWhiteSpace(source) ? "" : source.Trim()
                };
            }
        }

        private void LoadUnknown(string unknownPath)
        {
            UnknownPath = Path.GetFullPath(unknownPath);
            lock (_lock) _unknowns.Clear();

            if (!File.Exists(unknownPath)) return;

            try
            {
                string json = File.ReadAllText(unknownPath, Encoding.UTF8);
                var serializer = new JavaScriptSerializer();
                var root = serializer.Deserialize<Dictionary<string, object>>(json);
                if (root == null) return;

                object listObject;
                if (!root.TryGetValue("unknownWords", out listObject) &&
                    !root.TryGetValue("unknowns", out listObject))
                    return;

                var listItems = listObject as System.Collections.IEnumerable;
                if (listItems == null || listObject is string) return;

                foreach (object itemObject in listItems)
                    LoadUnknownRecord(itemObject);
            }
            catch (Exception ex)
            {
                lock (_lock) _warnings.Add("未知词记录读取失败: " + ex.Message);
            }
        }

        private void LoadUnknownRecord(object itemObject)
        {
            var item = itemObject as Dictionary<string, object>;
            if (item == null) return;

            object value;
            uint index;
            if (!item.TryGetValue("index", out value) ||
                !TryParseIndexValue(value, out index))
                return;

            uint table = DefaultTableTag;
            if (item.TryGetValue("table", out value) || item.TryGetValue("tableTag", out value))
            {
                if (!TryParseTableValue(value, out table))
                    table = DefaultTableTag;
            }

            var record = new UnknownWordRecord { TableTag = table, Index = index };
            if (item.TryGetValue("count", out value))
            {
                int count;
                if (TryConvertInt(value, out count) && count > 0)
                    record.Count = count;
            }

            DateTime parsed;
            if (item.TryGetValue("firstSeen", out value) &&
                TryParseDateTime(ConvertToString(value), out parsed))
                record.FirstSeen = parsed;
            if (item.TryGetValue("lastSeen", out value) &&
                TryParseDateTime(ConvertToString(value), out parsed))
                record.LastSeen = parsed;

            object examplesObject;
            if (!item.TryGetValue("examples", out examplesObject))
                examplesObject = null;
            var examples = examplesObject as System.Collections.IEnumerable;
            if (examples != null && !(examplesObject is string))
            {
                foreach (object example in examples)
                {
                    string text = ConvertToString(example);
                    if (!string.IsNullOrWhiteSpace(text) && !record.Examples.Contains(text))
                        record.Examples.Add(text);
                }
            }

            lock (_lock) _unknowns[MakeKey(table, index)] = record;
        }

        private void PruneUnknowns()
        {
            lock (_lock)
            {
                List<ulong> known = new List<ulong>();
                foreach (ulong key in _unknowns.Keys)
                    if (_words.ContainsKey(key))
                        known.Add(key);

                foreach (ulong key in known)
                    _unknowns.Remove(key);
            }
        }

        public bool Contains(uint tableTag, uint index)
        {
            lock (_lock) return _words.ContainsKey(MakeKey(tableTag, index));
        }

        public bool Contains(uint index)
        {
            return Contains(DefaultTableTag, index);
        }

        public string GetText(uint tableTag, uint index)
        {
            lock (_lock)
            {
                WordEntry entry;
                return _words.TryGetValue(MakeKey(tableTag, index), out entry)
                    ? entry.Text
                    : null;
            }
        }

        public string GetText(uint index)
        {
            return GetText(DefaultTableTag, index);
        }

        public RandomWord GetRandomDifferent(uint tableTag, uint index, Random random)
        {
            if (random == null)
                throw new ArgumentNullException("random");

            lock (_lock)
            {
                if (_words.Count == 0)
                    return null;

                ulong excludedKey = MakeKey(tableTag, index);
                List<WordEntry> entries = new List<WordEntry>(_words.Values);
                int start = random.Next(0, entries.Count);
                for (int offset = 0; offset < entries.Count; offset++)
                {
                    WordEntry candidate = entries[(start + offset) % entries.Count];
                    if (MakeKey(candidate.TableTag, candidate.Index) == excludedKey ||
                        string.IsNullOrWhiteSpace(candidate.Text))
                        continue;

                    return new RandomWord
                    {
                        TableTag = candidate.TableTag,
                        Index = candidate.Index,
                        Text = candidate.Text,
                        Pinyin = candidate.Pinyin
                    };
                }

                return null;
            }
        }

        public string Display(uint tableTag, uint index)
        {
            if (index == 0) return "0x0000(无状态)";
            string text = GetText(tableTag, index);
            return text == null
                ? string.Format(CultureInfo.InvariantCulture, "{0}/0x{1:X}(未知)", FormatTable(tableTag), index)
                : string.Format(CultureInfo.InvariantCulture, "{0}/0x{1:X}({2})", FormatTable(tableTag), index, text);
        }

        public string Display(uint index)
        {
            return Display(DefaultTableTag, index);
        }

        public bool RecordUnknown(uint tableTag, uint index, DateTime time, string context)
        {
            if (index == 0 || Contains(tableTag, index)) return false;

            lock (_lock)
            {
                ulong key = MakeKey(tableTag, index);
                UnknownWordRecord record;
                if (!_unknowns.TryGetValue(key, out record))
                {
                    record = new UnknownWordRecord
                    {
                        TableTag = tableTag,
                        Index = index,
                        FirstSeen = time,
                        LastSeen = time,
                        Count = 0
                    };
                    _unknowns[key] = record;
                }

                record.Count++;
                record.LastSeen = time;
                if (!string.IsNullOrWhiteSpace(context) &&
                    !record.Examples.Contains(context) &&
                    record.Examples.Count < 50)
                {
                    record.Examples.Add(context);
                }

                Dirty = true;
                return true;
            }
        }

        public bool RecordUnknown(uint index, DateTime time, string context)
        {
            return RecordUnknown(DefaultTableTag, index, time, context);
        }

        public string GetStatus()
        {
            lock (_lock)
            {
                int occurrences = 0;
                foreach (UnknownWordRecord record in _unknowns.Values)
                    occurrences += record.Count;
                return string.Format(CultureInfo.InvariantCulture,
                    "词表 {0} 项；未知 {1} 项/{2} 次{3}",
                    _words.Count, _unknowns.Count, occurrences, Dirty ? "（未保存）" : "");
            }
        }

        public bool SaveUnknown(string path)
        {
            UnknownPath = Path.GetFullPath(path);
            string json;
            lock (_lock)
            {
                json = BuildUnknownJson(_unknowns.Values);
                Dirty = false;
            }

            try
            {
                string temp = path + ".tmp";
                File.WriteAllText(temp, json, new UTF8Encoding(false));
                if (File.Exists(path))
                    File.Replace(temp, path, path + ".bak");
                else
                    File.Move(temp, path);
                return true;
            }
            catch
            {
                lock (_lock) Dirty = true;
                return false;
            }
        }

        private static string BuildUnknownJson(IEnumerable<UnknownWordRecord> records)
        {
            StringBuilder sb = new StringBuilder();
            sb.AppendLine("{");
            sb.AppendLine("  \"version\": 2,");
            sb.AppendLine("  \"updatedAt\": " + Quote(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss")) + ",");
            sb.AppendLine("  \"unknownWords\": [");

            bool first = true;
            foreach (UnknownWordRecord record in records)
            {
                if (!first) sb.AppendLine(",");
                first = false;

                sb.Append("    {");
                sb.Append("\"table\": " + Quote(FormatTable(record.TableTag)) + ", ");
                sb.Append("\"index\": " + Quote("0x" + record.Index.ToString("X", CultureInfo.InvariantCulture)) + ", ");
                sb.Append("\"count\": " + record.Count.ToString(CultureInfo.InvariantCulture) + ", ");
                sb.Append("\"firstSeen\": " + Quote(record.FirstSeen.ToString("yyyy-MM-dd HH:mm:ss")) + ", ");
                sb.Append("\"lastSeen\": " + Quote(record.LastSeen.ToString("yyyy-MM-dd HH:mm:ss")));

                if (record.Examples.Count > 0)
                {
                    sb.Append(", \"examples\": [");
                    for (int i = 0; i < record.Examples.Count; i++)
                    {
                        if (i > 0) sb.Append(", ");
                        sb.Append(Quote(record.Examples[i]));
                    }
                    sb.Append("]");
                }

                sb.Append("}");
            }

            if (!first) sb.AppendLine();
            sb.AppendLine("  ]");
            sb.AppendLine("}");
            return sb.ToString();
        }

        private static ulong MakeKey(uint tableTag, uint index)
        {
            return ((ulong)tableTag << 32) | index;
        }

        private static string FormatTable(uint tableTag)
        {
            return "0x" + tableTag.ToString("X8", CultureInfo.InvariantCulture);
        }

        private static bool TryParseIndex(string text, out uint index)
        {
            index = 0;
            if (string.IsNullOrWhiteSpace(text)) return false;
            text = text.Trim();
            if (text.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
                text = text.Substring(2);
            return uint.TryParse(text, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out index);
        }

        private static bool TryParseIndexValue(object value, out uint index)
        {
            if (value is int) index = unchecked((uint)(int)value);
            else if (value is long) index = unchecked((uint)(long)value);
            else if (value is uint) index = (uint)value;
            else if (value is ulong) index = unchecked((uint)(ulong)value);
            else return TryParseIndex(ConvertToString(value), out index);
            return true;
        }

        private static bool TryParseTableValue(object value, out uint table)
        {
            if (value is int) table = unchecked((uint)(int)value);
            else if (value is long) table = unchecked((uint)(long)value);
            else if (value is uint) table = (uint)value;
            else if (value is ulong) table = unchecked((uint)(ulong)value);
            else return TryParseIndex(ConvertToString(value), out table);
            return true;
        }

        private static bool TryConvertInt(object value, out int result)
        {
            if (value is int) { result = (int)value; return true; }
            if (value is long) { result = (int)(long)value; return true; }
            return int.TryParse(ConvertToString(value), NumberStyles.Integer,
                CultureInfo.InvariantCulture, out result);
        }

        private static bool TryParseDateTime(string text, out DateTime value)
        {
            return DateTime.TryParseExact(text, "yyyy-MM-dd HH:mm:ss",
                CultureInfo.InvariantCulture, DateTimeStyles.None, out value);
        }

        private static string ConvertToString(object value)
        {
            return value == null ? null : Convert.ToString(value, CultureInfo.InvariantCulture);
        }

        private static string Quote(string value)
        {
            if (value == null) return "\"\"";

            StringBuilder sb = new StringBuilder("\"");
            foreach (char c in value)
            {
                switch (c)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\b': sb.Append("\\b"); break;
                    case '\f': sb.Append("\\f"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    default:
                        if (c < ' ') sb.Append("\\u" + ((int)c).ToString("x4", CultureInfo.InvariantCulture));
                        else sb.Append(c);
                        break;
                }
            }
            sb.Append("\"");
            return sb.ToString();
        }
    }
}
