using System;
using System.Collections;
using System.Globalization;
using System.Text;

namespace RecoveryValidation
{
    // Keeps the fixture independent of optional Unity packages and external serializers.
    public static class ReportJson
    {
        public static string Encode(object value)
        {
            if (value == null)
                return "null";
            if (value is string text)
            {
                var result = new StringBuilder("\"");
                foreach (var character in text)
                {
                    if (character == '\\' || character == '"')
                        result.Append('\\').Append(character);
                    else if (character < 0x20)
                        result.Append("\\u").Append(((int)character).ToString("x4", CultureInfo.InvariantCulture));
                    else
                        result.Append(character);
                }
                return result.Append('"').ToString();
            }
            if (value is bool boolean)
                return boolean ? "true" : "false";
            if (value is int || value is uint || value is long || value is ulong)
                return Convert.ToString(value, CultureInfo.InvariantCulture);
            if (value is IDictionary fields)
            {
                var result = new StringBuilder("{");
                foreach (DictionaryEntry field in fields)
                {
                    if (result.Length != 1)
                        result.Append(',');
                    result.Append(Encode((string)field.Key)).Append(':').Append(Encode(field.Value));
                }
                return result.Append('}').ToString();
            }
            if (value is IEnumerable values)
            {
                var result = new StringBuilder("[");
                foreach (var item in values)
                {
                    if (result.Length != 1)
                        result.Append(',');
                    result.Append(Encode(item));
                }
                return result.Append(']').ToString();
            }
            throw new ArgumentException("Unsupported validation report value: " + value.GetType());
        }
    }
}
