using System;
using System.Collections.Generic;
using System.IO;
using SharedAbiFixture;
using UnityEngine;

namespace RecoveryValidation
{
    public static class BehaviorProbe
    {
        public static void Write(string path, string stage)
        {
            var observations = new List<object>();
            foreach (var value in new[] { int.MinValue, -17, 0, 19, int.MaxValue })
            {
                var slots = new StackSlots();
                var returned = slots.Forward(value, -31, 37, -41, 43);
                observations.Add(new Dictionary<string, object>
                {
                    { "kind", "call" }, { "value", value }, { "returned", returned },
                    { "fields", new[] { slots.A, slots.B, slots.C, slots.D, slots.E } }
                });
                slots.TailForward(47, -53, 59, -61, value);
                observations.Add(new Dictionary<string, object>
                {
                    { "kind", "tail" }, { "value", value },
                    { "fields", new[] { slots.A, slots.B, slots.C, slots.D, slots.E } }
                });
                observations.Add(new Dictionary<string, object>
                {
                    { "kind", "sixth" }, { "value", value },
                    { "returned", StackSlots.ForwardSixth(67, -71, 73, -79, 83, value) }
                });
                var integer = new IntegerSink();
                var integerResult = integer.Forward(value);
                var bits = value == 0 ? unchecked((int)0x80000000) : value;
                var floating = new FloatSink();
                var twin = new FloatTwin();
                var sample = BitConverter.ToSingle(BitConverter.GetBytes(bits), 0);
                var floatResult = floating.Forward(sample);
                var twinResult = twin.Forward(sample);
                observations.Add(new Dictionary<string, object>
                {
                    { "kind", "shared" }, { "value", value }, { "exception", "none" },
                    { "integer", new[] { integerResult, integer.A, integer.H } },
                    { "floating", new[] { Bits(floatResult), Bits(floating.A), Bits(floating.H) } },
                    { "twin", new[] { Bits(twinResult), Bits(twin.A), Bits(twin.H) } },
                    { "neighbors", new[] { floating.GapA, floating.GapG, twin.GapA, twin.GapG } }
                });
            }
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path)));
            File.WriteAllText(path, ReportJson.Encode(new Dictionary<string, object>
            {
                { "unityVersion", Application.unityVersion },
                { "platform", Application.platform.ToString() },
                { "stage", stage }, { "profile", "shared-abi" },
                { "observations", observations }
            }));
        }

        private static int Bits(float value) => BitConverter.ToInt32(BitConverter.GetBytes(value), 0);

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
