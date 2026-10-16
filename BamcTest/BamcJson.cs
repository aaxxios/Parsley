using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace BamcTest
{
    partial class JsonEmbedEvaluator
    {
        static int FromHexChar(char c)
        {
            if (c >= '0' && c <= '9') return c - '0';
            if (c >= 'A' && c <= 'F') return c - 'A' + 10;
            if (c >= 'a' && c <= 'f') return c - 'a' + 10;
            return -1;
        }


        /// <summary>
        /// Takes a string token's text, including its surrounding quotes, and returns its value.
        /// </summary>
        static string UnquoteString(string token)
        {
            if (token.Length < 2 || token[0] != '"' || token[token.Length - 1] != '"')
                throw new FormatException("A JSON string must be enclosed in double quotes");
            return DeescapeString(token.Substring(1, token.Length - 2));
        }

        /// <summary>
        /// Resolves the escape sequences in the contents of a JSON string (without its quotes).
        /// </summary>
        public static string DeescapeString(string s)
        {
            var i = s.IndexOf('\\');
            if (i < 0) return s;

            var result = new StringBuilder(s.Length);
            result.Append(s, 0, i);
            while (i < s.Length)
            {
                var c = s[i++];
                if (c != '\\')
                {
                    result.Append(c);
                    continue;
                }
                if (i >= s.Length)
                    throw new FormatException("Unterminated escape sequence in string");
                c = s[i++];
                switch (c)
                {
                    case '"': result.Append('"'); break;
                    case '\\': result.Append('\\'); break;
                    case '/': result.Append('/'); break;
                    case 'b': result.Append('\b'); break;
                    case 'f': result.Append('\f'); break;
                    case 'n': result.Append('\n'); break;
                    case 'r': result.Append('\r'); break;
                    case 't': result.Append('\t'); break;
                    case 'u':
                        if (i + 4 > s.Length)
                            throw new FormatException("Incomplete \\u escape sequence in string");
                        var cp = 0;
                        for (var j = 0; j < 4; ++j)
                        {
                            var d = FromHexChar(s[i++]);
                            if (d < 0)
                                throw new FormatException("Invalid hex digit in \\u escape sequence");
                            cp = (cp << 4) | d;
                        }
                        // surrogate pairs arrive as two escapes and come out as two UTF-16 chars
                        result.Append((char)cp);
                        break;
                    default:
                        throw new FormatException($"Invalid escape sequence \\{c} in string");
                }
            }
            return result.ToString();
        }

        /// <summary>
        /// Escapes a string's contents for JSON output (without adding quotes).
        /// </summary>
        public static string EscapeString(string str)
        {
            var result = new StringBuilder(str.Length + 8);
            foreach (var c in str)
            {
                switch (c)
                {
                    case '\\': result.Append("\\\\"); break;
                    case '"': result.Append("\\\""); break;
                    case '\t': result.Append("\\t"); break;
                    case '\r': result.Append("\\r"); break;
                    case '\n': result.Append("\\n"); break;
                    case '\b': result.Append("\\b"); break;
                    case '\f': result.Append("\\f"); break;
                    default:
                        // control characters must be escaped; non-ASCII is escaped to keep output ASCII
                        if (c < 0x20 || c >= 0x7F)
                        {
                            result.Append("\\u");
                            result.Append(((int)c).ToString("x4"));
                        }
                        else
                        {
                            result.Append(c);
                        }
                        break;
                }
            }
            return result.ToString();
        }
        static void WriteValue(object? value, TextWriter writer, int depth = 0, bool minimized = false)
        {
            if (value == null)
            {
                writer.Write("null");
            }
            else if (value is string)
            {
                writer.Write("\"");
                writer.Write(EscapeString((string)value));
                writer.Write("\"");
            }
            else if ((value is byte || value is short || value is int || value is long) ||
                (value is sbyte || value is ushort || value is uint || value is ulong))
            {
                writer.Write(value);
            }
            else if (value is double)
            {
                writer.Write(((double)value).ToString("r"));
            }
            else if (value is bool)
            {
                writer.Write(((bool)value) ? "true" : "false");
            }
            else if (value is IDictionary<string, object?>)
            {
                WriteObject((IDictionary<string, object?>)value, writer, depth, minimized);
            }
            else if (value is IList<object?>)
            {
                WriteArray((IList<object?>)value, writer, depth, minimized);
            }
            else
            {
                throw new NotSupportedException("The value type cannot be written");
            }
        }
        static void WriteArray(IList<object?> result, TextWriter writer, int depth = 0, bool minimized = false)
        {
            var tabs = new string(' ', 4 * depth);
            if (!minimized)
                writer.WriteLine("[");
            else writer.Write("[");
            var innerTabs = new string(' ', 4 * (depth + 1));
            var c = result.Count;
            foreach (var value in result)
            {
                if (!minimized)
                    writer.Write(innerTabs);
                WriteValue(value, writer, depth + 1, minimized);
                --c;
                if (c > 0)
                {
                    if (!minimized)
                        writer.WriteLine(",");
                    else writer.Write(",");
                }
                else if (!minimized)
                {
                    writer.WriteLine();
                }
            }
            if (!minimized)
            {
                writer.Write(tabs);
            }
            writer.Write("]");
        }
        static void WriteObject(IDictionary<string, object?> result, TextWriter writer, int depth = 0, bool minimized = false)
        {
            var tabs = new string(' ', 4 * depth);
            if (!minimized)
                writer.WriteLine("{");
            else writer.Write("{");
            var innerTabs = new string(' ', 4 * (depth + 1));
            var c = result.Count;
            foreach (var field in result)
            {
                if (!minimized)
                {
                    writer.Write(innerTabs);
                }
                WriteValue(field.Key, writer, depth, minimized);
                if (!minimized)
                {
                    writer.Write(": ");
                }
                else
                {
                    writer.Write(':');
                }
                WriteValue(field.Value, writer, depth + 1, minimized);
                --c;
                if (c > 0)
                {
                    if (!minimized)
                        writer.WriteLine(",");
                    else writer.Write(',');
                }
                else if (!minimized)
                {
                    writer.WriteLine();
                }
            }
            if (!minimized)
                writer.Write(tabs);
            writer.Write("}");
        }
        public static void WriteTo(object? value, TextWriter output, bool minimized = false)
        {
            WriteValue(value, output, 0, minimized);
        }
    }
}
