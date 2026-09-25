using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text;

namespace GrdRevit.Core
{
    /// <summary>
    /// Разбор CSV, который выгружают Revit-спецификации: разделитель «;» (или иной),
    /// значения в кавычках «"», экранирование удвоенными кавычками, многострочные ячейки.
    /// Кодировка определяется по BOM / строгой UTF-8, в крайнем случае — Windows-1251.
    /// </summary>
    public static class CsvReader
    {
        private static bool _codePagesTried;
        /// <summary>Читает CSV-файл и возвращает строки как массивы ячеек (без обрезки пустых).</summary>
        public static List<string[]> ParseFile(string path, char delimiter)
        {
            if (path == null) throw new ArgumentNullException(nameof(path));
            return ParseText(DecodeText(File.ReadAllBytes(path)), delimiter);
        }

        /// <summary>Разбирает CSV-текст в строки ячеек (RFC4180-подобный: кавычки, CRLF, вложенные кавычки).</summary>
        public static List<string[]> ParseText(string text, char delimiter)
        {
            if (text == null) throw new ArgumentNullException(nameof(text));
            var rows = new List<string[]>();
            var current = new List<string>();
            var field = new StringBuilder();
            bool inQuotes = false;
            using (var sr = new StringReader(text))
            {
                int ci;
                while ((ci = sr.Read()) != -1)
                {
                    char c = (char)ci;
                    if (inQuotes)
                    {
                        if (c == '"')
                        {
                            if (sr.Peek() == '"') { field.Append('"'); sr.Read(); }
                            else inQuotes = false;
                        }
                        else field.Append(c);
                    }
                    else
                    {
                        if (c == '"' && field.Length == 0) inQuotes = true;
                        else if (c == delimiter)
                        {
                            current.Add(field.ToString());
                            field.Clear();
                        }
                        else if (c == '\r') { /* CRLF: конец строки обработаем на '\n' */ }
                        else if (c == '\n')
                        {
                            current.Add(field.ToString());
                            field.Clear();
                            rows.Add(current.ToArray());
                            current.Clear();
                        }
                        else field.Append(c);
                    }
                }
            }
            if (field.Length > 0 || current.Count > 0)
            {
                current.Add(field.ToString());
                rows.Add(current.ToArray());
            }
            return rows;
        }

        /// <summary>Декодирует байты: BOM (UTF-8 / UTF-16 LE/BE), иначе строгая UTF-8,
        /// иначе Windows-1251, в крайнем случае — UTF-8 с заменой.</summary>
        public static string DecodeText(byte[] bytes)
        {
            if (bytes == null) throw new ArgumentNullException(nameof(bytes));
            if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
                return new UTF8Encoding(false).GetString(bytes, 3, bytes.Length - 3);
            if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE)
                return Encoding.Unicode.GetString(bytes, 2, bytes.Length - 2);
            if (bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF)
                return Encoding.BigEndianUnicode.GetString(bytes, 2, bytes.Length - 2);
            try { return new UTF8Encoding(false, true).GetString(bytes); }
            catch (DecoderFallbackException) { }
            EnsureCodePagesRegistered();
            try { return Encoding.GetEncoding(1251).GetString(bytes); }
            catch (Exception) { }
            return new UTF8Encoding(false, false).GetString(bytes);
        }

        /// <summary>На .NET Core (Revit 2025+) Windows-кодировки (1251) требуют регистрации провайдера —
        /// подключаем его через рефлексию, чтобы не тащить лишние зависимости в net48.</summary>
        private static void EnsureCodePagesRegistered()
        {
            if (_codePagesTried) return;
            _codePagesTried = true;
            try
            {
                var type = Type.GetType("System.Text.CodePagesEncodingProvider, System.Text.Encoding.CodePages");
                if (type == null) return;
                var instance = type.GetProperty("Instance", BindingFlags.Static | BindingFlags.Public)?.GetValue(null);
                if (instance == null) return;
                var register = typeof(Encoding).GetMethod("RegisterProvider", BindingFlags.Static | BindingFlags.Public);
                register?.Invoke(null, new[] { instance });
            }
            catch (Exception) { }
        }
    }
}