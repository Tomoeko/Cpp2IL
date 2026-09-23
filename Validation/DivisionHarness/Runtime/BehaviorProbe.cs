using System;
using System.Collections.Generic;
using System.IO;
using DivisionFixture;
using UnityEngine;

namespace RecoveryValidation
{
    // The driver is an independent oracle and is not in the selected recovery assembly.
    public static class BehaviorProbe
    {
        private static void Observe<T>(List<object> observations, int width, bool signed,
            T[] values, Func<T, T, T> quotient, Func<T, T, T> remainder)
        {
            foreach (var left in values)
            {
                foreach (var right in values)
                {
                    observations.Add(new Dictionary<string, object>
                    {
                        { "width", width }, { "signed", signed }, { "left", left }, { "right", right },
                        { "quotient", quotient(left, right) }, { "remainder", remainder(left, right) }
                    });
                }
            }
        }

        public static void Write(string path, string stage)
        {
            var observations = new List<object>();
            Observe(observations, 32, true,
                new[] { int.MinValue, int.MinValue + 1, -17, -2, -1, 0, 1, 2, 17, int.MaxValue },
                IntegerDivision.Quotient32, IntegerDivision.Remainder32);
            Observe(observations, 32, false,
                new uint[] { 0, 1, 2, int.MaxValue, 0x80000000U, uint.MaxValue - 1, uint.MaxValue },
                IntegerDivision.QuotientUnsigned32, IntegerDivision.RemainderUnsigned32);
            Observe(observations, 64, true,
                new[] { long.MinValue, long.MinValue + 1, -17, -2, -1, 0, 1, 2, 17, long.MaxValue },
                IntegerDivision.Quotient64, IntegerDivision.Remainder64);
            Observe(observations, 64, false,
                new ulong[] { 0, 1, 2, long.MaxValue, 0x8000000000000000UL, ulong.MaxValue - 1, ulong.MaxValue },
                IntegerDivision.QuotientUnsigned64, IntegerDivision.RemainderUnsigned64);
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path)));
            File.WriteAllText(path, ReportJson.Encode(new Dictionary<string, object>
            {
                { "unityVersion", Application.unityVersion }, { "platform", Application.platform.ToString() },
                { "stage", stage }, { "profile", "division" }, { "observations", observations }
            }));
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
