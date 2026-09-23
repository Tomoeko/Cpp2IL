using System;
using System.Collections.Generic;
using System.IO;
using EnumPassthroughFixture;
using UnityEngine;

namespace RecoveryValidation
{
    public static class BehaviorProbe
    {
        public static void Write(string path, string stage)
        {
            var observations = new List<object>();
            var receiver = new EnumReceiver { Neighbor = 123456789L };
            var owner = new EnumForwarder { Receiver = receiver };
            observations.Add(new Dictionary<string, object>
            {
                { "kind", "constructor" }, { "receiverCreated", receiver != null },
                { "ownerCreated", owner != null }, { "lastAfter", receiver.Last },
                { "neighborAfter", receiver.Neighbor }
            });
            Record(observations, "minimum", owner, receiver, int.MinValue);
            Record(observations, "negative", owner, receiver, -17);
            Record(observations, "zero", owner, receiver, 0);
            Record(observations, "positive", owner, receiver, 17);
            Record(observations, "maximum", owner, receiver, int.MaxValue);
            Record(observations, "null-receiver", new EnumForwarder(), null, -1);
            var untouched = new EnumReceiver { Last = 41, Neighbor = 123456789L };
            Record(observations, "null-owner", null, untouched, -1);
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path)));
            File.WriteAllText(path, ReportJson.Encode(new Dictionary<string, object>
            {
                { "unityVersion", Application.unityVersion }, { "platform", Application.platform.ToString() },
                { "stage", stage }, { "profile", "enum-passthrough" }, { "observations", observations }
            }));
        }

        private static void Record(List<object> observations, string kind, EnumForwarder owner,
            EnumReceiver receiver, int value)
        {
            var exception = "none";
            try { owner.Forward((Mode)value); }
            catch (Exception error) { exception = error.GetType().FullName; }
            observations.Add(new Dictionary<string, object>
            {
                { "kind", kind }, { "value", value }, { "exception", exception },
                { "lastAfter", receiver == null ? null : (object)receiver.Last },
                { "neighborAfter", receiver == null ? null : (object)receiver.Neighbor }
            });
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
