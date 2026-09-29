using System;
using System.Collections.Generic;
using System.IO;
using InstanceParameterReferenceReadFixture;
using UnityEngine;

namespace RecoveryValidation
{
    public static class BehaviorProbe
    {
        public static void Write(string path, string stage)
        {
            var reader = new Reader();
            var box = new ValueBox { Neighbor = 91 };
            var text = new string('t', 3);
            var payload = new object();
            var numbers = new[] { int.MinValue, 0, int.MaxValue };
            var observations = new List<object>();

            box.Text = text;
            box.Payload = payload;
            box.Numbers = numbers;
            Record(observations, reader, box, "filled", text, payload, numbers);

            box.Text = string.Empty;
            box.Payload = text;
            box.Numbers = Array.Empty<int>();
            Record(observations, reader, box, "empty", string.Empty, text, box.Numbers);

            box.Text = null;
            box.Payload = null;
            box.Numbers = null;
            Record(observations, reader, box, "null-values", null, null, null);
            Record(observations, reader, null, "null-box", null, null, null);

            box.Text = text;
            box.Payload = payload;
            box.Numbers = numbers;
            Record(observations, null, box, "null-reader", text, payload, numbers);

            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path)));
            File.WriteAllText(path, ReportJson.Encode(new Dictionary<string, object>
            {
                { "unityVersion", Application.unityVersion },
                { "platform", Application.platform.ToString() },
                { "stage", stage },
                { "profile", "instance-parameter-reference-read" },
                { "observations", observations }
            }));
        }

        private static void Record(List<object> observations, Reader reader, ValueBox box,
            string kind, string text, object payload, int[] numbers)
        {
            var result = new Dictionary<string, object>
            {
                { "kind", kind },
                { "neighbor", box == null ? (int?)null : box.Neighbor }
            };
            try
            {
                var actual = reader.ReadText(box);
                result["textFailure"] = "none";
                result["textSame"] = ReferenceEquals(actual, text);
                result["textValue"] = actual;
            }
            catch (Exception error)
            {
                result["textFailure"] = error.GetType().Name;
                result["textSame"] = null;
                result["textValue"] = null;
            }
            try
            {
                var actual = reader.ReadPayload(box);
                result["payloadFailure"] = "none";
                result["payloadSame"] = ReferenceEquals(actual, payload);
            }
            catch (Exception error)
            {
                result["payloadFailure"] = error.GetType().Name;
                result["payloadSame"] = null;
            }
            try
            {
                var actual = reader.ReadNumbers(box);
                result["numbersFailure"] = "none";
                result["numbersSame"] = ReferenceEquals(actual, numbers);
                result["numbersLength"] = actual == null ? (int?)null : actual.Length;
                result["numbersFirst"] = actual == null || actual.Length == 0 ? (int?)null : actual[0];
            }
            catch (Exception error)
            {
                result["numbersFailure"] = error.GetType().Name;
                result["numbersSame"] = null;
                result["numbersLength"] = null;
                result["numbersFirst"] = null;
            }
            result["neighborAfter"] = box == null ? (int?)null : box.Neighbor;
            observations.Add(result);
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
