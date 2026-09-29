using System;
using System.Collections.Generic;
using System.IO;
using LayeredVirtualTailDispatchFixture;
using UnityEngine;

namespace RecoveryValidation
{
    public static class BehaviorProbe
    {
        public static void Write(string path, string stage)
        {
            var observations = new List<object>();
            var first = new LayeredNode
            {
                Marker = 3, Neighbor = 101, TagValue = 7
            };
            var second = new LayeredOverride
            {
                Marker = 5, Neighbor = 202, TagValue = 9
            };
            RecordCall(observations, "base:first", first, first, second);
            RecordCall(observations, "derived:first", second, first, second);

            first.Marker = -3;
            second.Marker = -5;
            first.TagValue = 17;
            second.TagValue = 19;
            RecordCall(observations, "base:again", first, first, second);
            RecordCall(observations, "derived:again", second, first, second);
            RecordCall(observations, "null", null, first, second);

            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path)));
            File.WriteAllText(path, ReportJson.Encode(new Dictionary<string, object>
            {
                { "unityVersion", Application.unityVersion },
                { "platform", Application.platform.ToString() },
                { "stage", stage },
                { "profile", "layered-virtual-tail-dispatch" },
                { "observations", observations }
            }));
        }

        private static void RecordCall(List<object> observations, string kind,
            LayeredNode receiver, LayeredNode first, LayeredNode second)
        {
            var exception = "none";
            try
            {
                receiver.Forward();
            }
            catch (Exception error)
            {
                exception = error.GetType().FullName;
            }
            observations.Add(new Dictionary<string, object>
            {
                { "kind", kind },
                { "exception", exception },
                { "baseMarker", first.Marker },
                { "derivedMarker", second.Marker },
                { "baseNeighbor", first.Neighbor },
                { "derivedNeighbor", second.Neighbor },
                { "baseTag", ((ITag)first).ReadTag() },
                { "derivedTag", ((ITag)second).ReadTag() }
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
