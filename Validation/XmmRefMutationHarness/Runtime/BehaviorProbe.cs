using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using UnityEngine;
using XmmRefMutationFixture;

namespace RecoveryValidation
{
    public static class BehaviorProbe
    {
        private static readonly string[][] Vectors =
        {
            new[] { "3f800000", "40000000", "40400000", "40800000" },
            new[] { "80000000", "80000000", "80000000", "80000000" },
            new[] { "00000000", "80000000", "00000000", "80000000" },
            new[] { "00000001", "00000000", "00000000", "00000000" },
            new[] { "80000001", "80000000", "80000000", "80000000" },
            new[] { "4b800000", "3f800000", "cb800000", "3f800000" },
            new[] { "7f800000", "3f800000", "40000000", "40400000" },
            new[] { "ff800000", "bf800000", "40000000", "40400000" },
            new[] { "7f800000", "ff800000", "00000000", "00000000" },
            new[] { "7fc00001", "3f800000", "40000000", "40400000" },
            new[] { "3f800000", "40000000", "7fc12345", "40400000" },
            new[] { "00000000", "00000001", "00000001", "80000001" },
            new[] { "3f800000", "33800000", "33800000", "00000000" }
        };

        public static void Write(string path, string stage)
        {
            var observations = new List<object>();
            foreach (var vector in Vectors)
                Record(observations, vector);

            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path)));
            File.WriteAllText(path, ReportJson.Encode(new Dictionary<string, object>
            {
                { "unityVersion", Application.unityVersion }, { "platform", Application.platform.ToString() },
                { "stage", stage }, { "profile", "xmm-ref-mutation" }, { "observations", observations }
            }));
        }

        private static void Record(List<object> observations, string[] vector)
        {
            var first = FromBits(vector[0]);
            var values = new FloatPack
            {
                Second = FromBits(vector[1]),
                Third = FromBits(vector[2]),
                Fourth = FromBits(vector[3])
            };
            var identityValues = values;
            var identityResult = FloatAcrossCall.Identity(ref identityValues, first);
            var sumValues = values;
            var sumResult = FloatAcrossCall.Sum(first, sumValues);

            observations.Add(new Dictionary<string, object>
            {
                { "inputBits", vector },
                { "identityResult", Result(identityResult) },
                { "identityAfterBits", FieldBits(identityValues) },
                { "sumResult", Result(sumResult) },
                { "sumInputAfterBits", FieldBits(sumValues) }
            });
        }

        private static string[] FieldBits(FloatPack values)
        {
            return new[] { Bits(values.Second), Bits(values.Third), Bits(values.Fourth) };
        }

        private static float FromBits(string bits)
        {
            var raw = uint.Parse(bits, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture);
            return BitConverter.ToSingle(BitConverter.GetBytes(raw), 0);
        }

        private static string Result(float value)
        {
            return float.IsNaN(value) ? "nan" : Bits(value);
        }

        private static string Bits(float value)
        {
            return BitConverter.ToUInt32(BitConverter.GetBytes(value), 0).ToString("x8", CultureInfo.InvariantCulture);
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void RunPlayer()
        {
            var arguments = Environment.GetCommandLineArgs();
            for (var index = 0; index + 1 < arguments.Length; index++)
            {
                if (arguments[index] != "--validation-report")
                    continue;
                try
                {
                    Write(arguments[index + 1], "player");
                    Application.Quit(0);
                }
                catch (Exception error)
                {
                    Debug.LogException(error);
                    Application.Quit(1);
                }
                return;
            }
        }
    }
}
