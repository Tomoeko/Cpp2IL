using System;
using System.Collections.Generic;
using System.IO;
using FalseBooleanVirtualTailFixture;
using UnityEngine;

namespace RecoveryValidation
{
    public static class BehaviorProbe
    {
        private static string _events;

        private sealed class OverrideNode : DispatchNode
        {
            public int Calls;
            public bool Last;

            public override void Mark(bool value)
            {
                Calls++;
                Last = value;
                _events += value ? "T" : "F";
                Marker = value ? 23 : 29;
            }
        }

        public static void Write(string path, string stage)
        {
            _events = string.Empty;
            var first = new DispatchNode();
            var second = new OverrideNode();
            var observations = new List<object>
            {
                Snapshot("constructors:defaults", "none", first, second)
            };

            first.Marker = 3;
            first.Neighbor = 101;
            second.Marker = 7;
            second.Neighbor = 202;

            Record(observations, "base:first", first, first, second);
            Record(observations, "override:first", second, first, second);
            Record(observations, "override:direct-true", second, first, second, true);
            Record(observations, "override:again", second, first, second);

            first.Marker = 4;
            Record(observations, "base:again", first, first, second);
            second.Marker = 6;
            Record(observations, "override:after-reset", second, first, second);
            Record(observations, "null-owner", null, first, second);

            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path)));
            File.WriteAllText(path, ReportJson.Encode(new Dictionary<string, object>
            {
                { "unityVersion", Application.unityVersion },
                { "platform", Application.platform.ToString() },
                { "stage", stage },
                { "profile", "false-boolean-virtual-tail" },
                { "observations", observations }
            }));
        }

        private static void Record(List<object> observations, string kind,
            DispatchNode receiver, DispatchNode first, OverrideNode second,
            bool direct = false)
        {
            _events += "[";
            var exception = "none";
            try
            {
                if (direct)
                    receiver.Mark(true);
                else
                    receiver.ForwardFalse();
            }
            catch (Exception error)
            {
                exception = error.GetType().FullName;
            }
            _events += "]";
            observations.Add(Snapshot(kind, exception, first, second));
        }

        private static Dictionary<string, object> Snapshot(string kind,
            string exception, DispatchNode first, OverrideNode second)
        {
            return new Dictionary<string, object>
            {
                { "kind", kind },
                { "exception", exception },
                { "baseMarker", first.Marker },
                { "baseNeighbor", first.Neighbor },
                { "overrideMarker", second.Marker },
                { "overrideNeighbor", second.Neighbor },
                { "overrideCalls", second.Calls },
                { "overrideLast", second.Last },
                { "events", _events }
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
