using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace Cpp2IL.Core.Reporting;

/// <summary>Reflection-free JSON string encoding for fixed output schemas, including AOT builds.</summary>
public static class JsonText
{
    public static string Quote(string? value)
    {
        if (value == null)
            return "null";

        var result = new StringBuilder(value.Length + 2).Append('"');
        foreach (var character in value)
        {
            switch (character)
            {
                case '"': result.Append("\\\""); break;
                case '\\': result.Append("\\\\"); break;
                case '\b': result.Append("\\b"); break;
                case '\f': result.Append("\\f"); break;
                case '\n': result.Append("\\n"); break;
                case '\r': result.Append("\\r"); break;
                case '\t': result.Append("\\t"); break;
                default:
                    if (character < ' ' || char.IsSurrogate(character))
                        result.Append("\\u").Append(((int)character).ToString("x4", CultureInfo.InvariantCulture));
                    else
                        result.Append(character);
                    break;
            }
        }
        return result.Append('"').ToString();
    }

    public static string Array(IEnumerable<string> values) => "[" + string.Join(",", values.Select(Quote)) + "]";
}
