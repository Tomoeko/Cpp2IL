using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using ScalarFloatConversionCompositionFixture;
using UnityEngine;

namespace RecoveryValidation
{
    public static class BehaviorProbe
    {
        public static void Write(string path, string stage)
        {
            var observations = new List<object>();
            var samples = new ulong[,]
            {
                { 0x0000000000000000UL, 0x3f800000 }, { 0x8000000000000000UL, 0x3f800000 },
                { 0x3ff0000000000000UL, 0x40400000 }, { 0xbff0000000000000UL, 0x40400000 },
                { 0x3ff0000000000000UL, 0xc0400000 }, { 0xbff0000000000000UL, 0xc0400000 },
                { 0x3ff0000010000000UL, 0x40400000 }, { 0x3ff0000010000001UL, 0x40400000 },
                { 0x3ff0000030000000UL, 0x40400000 }, { 0x47effffff0000000UL, 0x7f7fffff },
                { 0x3690000000000000UL, 0x00000001 }, { 0x3690000000000001UL, 0x00000001 },
                { 0x36a0000000000000UL, 0x40000000 }, { 0x36b8000000000000UL, 0x40000000 },
                { 0x3810000000000000UL, 0x40000000 }, { 0x380ffffff0000000UL, 0x3f800000 },
                { 0x7fefffffffffffffUL, 0x7f7fffff }, { 0x0010000000000000UL, 0x00800000 },
                { 0x3ff0000000000000UL, 0x00000000 }, { 0x3ff0000000000000UL, 0x80000000 },
                { 0x0000000000000000UL, 0x00000000 }, { 0x8000000000000000UL, 0x80000000 },
                { 0x7ff0000000000000UL, 0x7f800000 }, { 0xfff0000000000000UL, 0x3f800000 },
                { 0x3ff0000000000000UL, 0x7f800000 }, { 0xbff0000000000000UL, 0x7f800000 },
                { 0x7ff8000000000001UL, 0x3f800000 }, { 0xfff8000000000002UL, 0x3f800000 },
                { 0x7ff0000000000001UL, 0x3f800000 }, { 0xfff0123456789abcUL, 0x3f800000 },
                { 0x3ff0000000000000UL, 0x7fc00001 }, { 0x3ff0000000000000UL, 0xff800002 }
            };
            var owner = new ScalarQuotients();
            for (var index = 0; index < samples.GetLength(0); index++)
            {
                var input = samples[index, 0];
                var divisorBits = (uint)samples[index, 1];
                var value = BitConverter.ToDouble(BitConverter.GetBytes(input), 0);
                var divisor = BitConverter.ToSingle(BitConverter.GetBytes(divisorBits), 0);
                owner.Value = value;
                observations.Add(Row("staticDivide", input, divisorBits, value, divisor,
                    ScalarQuotients.NarrowDivide(index, ~index, divisor, value)));
                observations.Add(Row("staticNegative", input, divisorBits, value, divisor,
                    ScalarQuotients.NegativeToPositive(index, ~index, divisor, value)));
                observations.Add(Row("instanceDivide", input, divisorBits, value, divisor,
                    owner.InstanceDivide(index, divisor, value)));
                observations.Add(Row("instanceNegative", input, divisorBits, value, divisor,
                    owner.InstanceNegativeToPositive(index, divisor, value)));
                observations.Add(Row("fieldDivide", input, divisorBits, value, divisor,
                    owner.FieldDivide(index, divisor)));
                observations.Add(Row("fieldNegative", input, divisorBits, value, divisor,
                    owner.FieldNegativeToPositive(index, divisor)));
                observations.Add(Row("aggregateNegative", input, divisorBits, value, divisor,
                    owner.AggregateNegativeToPositive(default(IgnoredOptions), divisor, value)));
            }
            var missing = (ScalarQuotients)null;
            observations.Add(ExceptionRow("instanceDivide", delegate { return missing.InstanceDivide(0, 1, 1); }));
            observations.Add(ExceptionRow("instanceNegative", delegate { return missing.InstanceNegativeToPositive(0, 1, 1); }));
            observations.Add(ExceptionRow("fieldDivide", delegate { return missing.FieldDivide(0, 1); }));
            observations.Add(ExceptionRow("fieldNegative", delegate { return missing.FieldNegativeToPositive(0, 1); }));
            observations.Add(ExceptionRow("aggregateNegative", delegate { return missing.AggregateNegativeToPositive(default(IgnoredOptions), 1, 1); }));
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path)));
            File.WriteAllText(path, ReportJson.Encode(new Dictionary<string, object>
            {
                { "unityVersion", Application.unityVersion }, { "platform", Application.platform.ToString() },
                { "stage", stage }, { "profile", "scalar-float-conversion-composition" }, { "observations", observations }
            }));
        }

        private static object Row(string method, ulong input, uint divisorBits, double value, float divisor, float result)
        {
            return new Dictionary<string, object>
            {
                { "method", method }, { "inputBits", input.ToString("x16", CultureInfo.InvariantCulture) },
                { "divisorBits", divisorBits.ToString("x8", CultureInfo.InvariantCulture) },
                { "argumentBits", BitConverter.ToUInt64(BitConverter.GetBytes(value), 0).ToString("x16", CultureInfo.InvariantCulture) },
                { "divisorArgumentBits", BitConverter.ToUInt32(BitConverter.GetBytes(divisor), 0).ToString("x8", CultureInfo.InvariantCulture) },
                { "resultBits", BitConverter.ToUInt32(BitConverter.GetBytes(result), 0).ToString("x8", CultureInfo.InvariantCulture) }
            };
        }

        private static object ExceptionRow(string method, Func<float> action)
        {
            try
            {
                action();
                return new Dictionary<string, object> { { "method", method }, { "exception", "None" } };
            }
            catch (Exception exception)
            {
                return new Dictionary<string, object> { { "method", method }, { "exception", exception.GetType().Name } };
            }
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void RunPlayer()
        {
            var arguments = Environment.GetCommandLineArgs();
            for (var index = 0; index + 1 < arguments.Length; index++)
            {
                if (arguments[index] != "--validation-report") continue;
                try
                {
                    Write(arguments[index + 1], "player");
                    Application.Quit(0);
                }
                catch (Exception exception)
                {
                    Debug.LogException(exception);
                    Application.Quit(1);
                }
                return;
            }
        }
    }
}
