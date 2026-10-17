using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace BamcTest;

partial class BamcEvaluator
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
            throw new FormatException("A string must be enclosed in double quotes");
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
}
