using System;
using System.Collections.Generic;
using System.IO;
using EmptyObjectConstructorFixture;
using UnityEngine;

namespace RecoveryValidation
{
    // This driver is outside the recovery scope.
    public static class BehaviorProbe
    {
        public static void Write(string path, string stage)
        {
            var observations = new List<object>();
            var first = new EmptyCell();
            var second = new EmptyCell();
            observations.Add(Cell("first-default", first, first, second));
            observations.Add(Cell("second-default", second, first, second));

            var payload = new object();
            first.Number = 73;
            first.Flag = true;
            first.Payload = payload;
            var changed = Cell("first-changed", first, first, second);
            changed.Add("payloadSame", ReferenceEquals(first.Payload, payload));
            observations.Add(changed);
            observations.Add(Cell("second-unchanged", second, first, second));

            var third = new EmptyCell();
            observations.Add(Cell("third-default", third, first, second));

            var sibling = new EmptySibling();
            var otherSibling = new EmptySibling();
            observations.Add(Sibling("sibling-default", sibling,
                ReferenceEquals(sibling, otherSibling)));
            sibling.Count = long.MaxValue;
            sibling.Label = "changed";
            observations.Add(Sibling("sibling-changed", sibling,
                ReferenceEquals(sibling, otherSibling)));
            observations.Add(Sibling("other-sibling-unchanged", otherSibling,
                ReferenceEquals(otherSibling, sibling)));

            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path)));
            File.WriteAllText(path, ReportJson.Encode(new Dictionary<string, object>
            {
                { "unityVersion", Application.unityVersion },
                { "platform", Application.platform.ToString() },
                { "stage", stage },
                { "profile", "empty-object-constructor" },
                { "observations", observations }
            }));
        }

        private static Dictionary<string, object> Cell(string kind, EmptyCell value,
            EmptyCell first, EmptyCell second)
        {
            return new Dictionary<string, object>
            {
                { "kind", kind }, { "number", value.Number },
                { "flag", value.Flag }, { "payloadNull", value.Payload == null },
                { "sameFirst", ReferenceEquals(value, first) },
                { "sameSecond", ReferenceEquals(value, second) }
            };
        }

        private static Dictionary<string, object> Sibling(string kind, EmptySibling value,
            bool sameOther)
        {
            return new Dictionary<string, object>
            {
                { "kind", kind }, { "count", value.Count },
                { "label", value.Label }, { "sameOther", sameOther }
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
