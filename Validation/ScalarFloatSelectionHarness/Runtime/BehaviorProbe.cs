using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using ScalarFloatSelectionFixture;
using UnityEngine;

namespace RecoveryValidation
{
    // Operand construction and observations are independent of the recovery assembly.
    public static class BehaviorProbe
    {
        public static void Write(string path, string stage)
        {
            var observations = new List<object>();
            var bits32 = new uint[]
            {
                0x00000000, 0x80000000, 0x3f800000, 0xbf800000, 0x00000001, 0x80000001,
                0x00800000, 0x7f7fffff, 0xff7fffff, 0x7f800000, 0xff800000, 0x7fc00001, 0xffc00002, 0x7f800001, 0xff800002
            };
            foreach (var leftBits in bits32)
            foreach (var rightBits in bits32)
            {
                var left = BitConverter.ToSingle(BitConverter.GetBytes(leftBits), 0);
                var right = BitConverter.ToSingle(BitConverter.GetBytes(rightBits), 0);
                observations.Add(Observe(32, leftBits, rightBits, Bits(left), Bits(right),
                    Bits(ScalarSelections.Minimum32(left, right)), Bits(ScalarSelections.Maximum32(left, right))));
            }
            var bits64 = new ulong[]
            {
                0x0000000000000000UL, 0x8000000000000000UL, 0x3ff0000000000000UL, 0xbff0000000000000UL,
                0x0000000000000001UL, 0x8000000000000001UL, 0x0010000000000000UL, 0x7fefffffffffffffUL,
                0xffefffffffffffffUL, 0x7ff0000000000000UL, 0xfff0000000000000UL, 0x7ff8000000000001UL,
                0xfff8000000000002UL, 0x7ff0000000000001UL, 0xfff0000000000002UL
            };
            foreach (var leftBits in bits64)
            foreach (var rightBits in bits64)
            {
                var left = BitConverter.ToDouble(BitConverter.GetBytes(leftBits), 0);
                var right = BitConverter.ToDouble(BitConverter.GetBytes(rightBits), 0);
                observations.Add(Observe(64, leftBits, rightBits, Bits(left), Bits(right),
                    Bits(ScalarSelections.Minimum64(left, right)), Bits(ScalarSelections.Maximum64(left, right))));
            }
            observations.Add(new Dictionary<string, object>
            {
                { "kind", "constructor" }, { "value32", Bits(new ScalarSelections().Value32).ToString("x8") },
                { "value64", Bits(new ScalarSelections().Value64).ToString("x16") }, { "neighbor", new ScalarSelections().Neighbor }
            });
            foreach (var bits in bits32)
            {
                var cell = new ScalarSelections { Value32 = BitConverter.ToSingle(BitConverter.GetBytes(bits), 0), Value64 = -17d, Neighbor = 31 };
                var read = Bits(cell.ReadPositive32());
                cell.StorePositive32(cell.Value32);
                observations.Add(Field(32, bits, read, Bits(cell.Value32), Bits(cell.Value64), cell.Neighbor));
            }
            foreach (var bits in bits64)
            {
                var cell = new ScalarSelections { Value64 = BitConverter.ToDouble(BitConverter.GetBytes(bits), 0), Value32 = -17f, Neighbor = 31 };
                var read = Bits(cell.ReadPositive64());
                cell.StorePositive64(cell.Value64);
                observations.Add(Field(64, bits, read, Bits(cell.Value64), Bits(cell.Value32), cell.Neighbor));
            }
            ObserveNullReceiver(observations, 32, false);
            ObserveNullReceiver(observations, 32, true);
            ObserveNullReceiver(observations, 64, false);
            ObserveNullReceiver(observations, 64, true);
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path)));
            File.WriteAllText(path, ReportJson.Encode(new Dictionary<string, object>
            {
                { "unityVersion", Application.unityVersion }, { "platform", Application.platform.ToString() },
                { "stage", stage }, { "profile", "scalar-float-selection" }, { "observations", observations }
            }));
        }

        private static ulong Bits(float value) { return BitConverter.ToUInt32(BitConverter.GetBytes(value), 0); }
        private static ulong Bits(double value) { return BitConverter.ToUInt64(BitConverter.GetBytes(value), 0); }

        private static void ObserveNullReceiver(List<object> observations, int width, bool store)
        {
            ScalarSelections receiver = null;
            string resultBits = null;
            var exception = "none";
            try
            {
                if (width == 32)
                {
                    if (store) receiver.StorePositive32(1f);
                    else resultBits = Bits(receiver.ReadPositive32()).ToString("x8", CultureInfo.InvariantCulture);
                }
                else
                {
                    if (store) receiver.StorePositive64(1d);
                    else resultBits = Bits(receiver.ReadPositive64()).ToString("x16", CultureInfo.InvariantCulture);
                }
            }
            catch (Exception error) { exception = error.GetType().FullName; }
            observations.Add(new Dictionary<string, object>
            {
                { "kind", "null-receiver" }, { "width", width }, { "operation", store ? "store" : "read" },
                { "exception", exception }, { "resultBits", resultBits }
            });
        }

        private static object Observe(int width, ulong left, ulong right, ulong leftArgument, ulong rightArgument, ulong minimum, ulong maximum)
        {
            var format = width == 32 ? "x8" : "x16";
            return new Dictionary<string, object>
            {
                { "kind", "pair" }, { "width", width }, { "leftBits", left.ToString(format, CultureInfo.InvariantCulture) },
                { "rightBits", right.ToString(format, CultureInfo.InvariantCulture) },
                { "leftArgumentBits", leftArgument.ToString(format, CultureInfo.InvariantCulture) },
                { "rightArgumentBits", rightArgument.ToString(format, CultureInfo.InvariantCulture) },
                { "minimumBits", minimum.ToString(format, CultureInfo.InvariantCulture) },
                { "maximumBits", maximum.ToString(format, CultureInfo.InvariantCulture) }
            };
        }

        private static object Field(int width, ulong input, ulong read, ulong stored, ulong other, int neighbor)
        {
            var format = width == 32 ? "x8" : "x16";
            return new Dictionary<string, object>
            {
                { "kind", "field" }, { "width", width }, { "inputBits", input.ToString(format, CultureInfo.InvariantCulture) },
                { "readBits", read.ToString(format, CultureInfo.InvariantCulture) },
                { "storedBits", stored.ToString(format, CultureInfo.InvariantCulture) },
                { "otherBits", other.ToString(width == 32 ? "x16" : "x8", CultureInfo.InvariantCulture) }, { "neighbor", neighbor }
            };
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
