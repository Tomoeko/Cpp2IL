using System;
using System.Collections.Generic;
using System.IO;
using RecoveryFixture;
using UnityEngine;

namespace RecoveryValidation
{
    public static class BehaviorProbe
    {
        [Serializable]
        public sealed class Observation
        {
            public int left;
            public int right;
            public int add;
            public int select;
            public int accumulated;
            public int stored;
        }

        [Serializable]
        public sealed class Report
        {
            public string unityVersion;
            public string platform;
            public string stage;
            public Observation[] observations;
        }

        // This driver is a separate validation oracle, not part of the recovery scope.
        public static Report Observe(string stage)
        {
            var values = new[] { int.MinValue, int.MinValue + 1, -17, -1, 0, 1, 17, int.MaxValue - 1, int.MaxValue };
            var report = new Report
            {
                unityVersion = Application.unityVersion,
                platform = Application.platform.ToString(),
                stage = stage,
                observations = new Observation[values.Length * values.Length]
            };
            var instance = new RecoveryLogic();
            var index = 0;
            foreach (var left in values)
            {
                foreach (var right in values)
                {
                    instance.Value = left;
                    report.observations[index++] = new Observation
                    {
                        left = left,
                        right = right,
                        add = RecoveryLogic.Add(left, right),
                        select = RecoveryLogic.Select(left, right),
                        accumulated = instance.Accumulate(right),
                        stored = instance.Value
                    };
                }
            }
            return report;
        }

        public static void Write(string path, string stage)
        {
            var directory = Path.GetDirectoryName(Path.GetFullPath(path));
            Directory.CreateDirectory(directory);
            var report = Observe(stage);
            var observations = new List<object>();
            foreach (var item in report.observations)
            {
                observations.Add(new Dictionary<string, object>
                {
                    { "left", item.left }, { "right", item.right }, { "add", item.add },
                    { "select", item.select }, { "accumulated", item.accumulated }, { "stored", item.stored }
                });
            }
            File.WriteAllText(path, ReportJson.Encode(new Dictionary<string, object>
            {
                { "unityVersion", report.unityVersion }, { "platform", report.platform },
                { "stage", report.stage }, { "observations", observations }
            }));
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
