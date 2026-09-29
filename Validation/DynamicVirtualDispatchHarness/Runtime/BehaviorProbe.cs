using System;
using System.Collections.Generic;
using System.IO;
using DynamicVirtualDispatchFixture;
using UnityEngine;

namespace RecoveryValidation
{
    public static class BehaviorProbe
    {
        private static readonly int[] Inputs =
        {
            int.MinValue, int.MinValue + 1, -65536, -32769, -32768, -129, -128, -1,
            0, 1, 2, 127, 128, 255, 256, 32767, 32768, 65535, 65536,
            int.MaxValue - 1, int.MaxValue
        };
        private static readonly string[] Kinds = { "base", "override", "inherited", "shadow" };
        private static readonly string[] Routes = { "virtual", "virtual-store" };

        public static void Write(string path, string stage)
        {
            var observations = new List<object>();
            foreach (var kind in Kinds)
            {
                var receiver = Create(kind);
                observations.Add(new Dictionary<string, object>
                {
                    { "kind", "constructor" }, { "owner", kind }, { "counter", Counter(receiver) },
                    { "neighbor", Neighbor(receiver) }, { "referenceNull", Reference(receiver) == null }
                });
            }
            var freshMarker = new Marker();
            observations.Add(new Dictionary<string, object>
            {
                { "kind", "constructor" }, { "owner", "marker" }, { "value", freshMarker.Value },
                { "neighbor", freshMarker.Neighbor }, { "referenceNull", freshMarker.Reference == null }
            });
            foreach (var input in Inputs)
            foreach (var counter in new[] { -3, 0, int.MaxValue })
            foreach (var referenceNull in new[] { false, true })
            {
                foreach (var route in Routes)
                foreach (var kind in Kinds)
                {
                    observations.Add(Invoke(kind, route, input, counter, referenceNull, false, false));
                    if (route.EndsWith("store", StringComparison.Ordinal))
                        observations.Add(Invoke(kind, route, input, counter, referenceNull, false, true));
                }
                observations.Add(Invoke("shadow", "concrete-shadow", input, counter, referenceNull, false, false));
            }
            foreach (var input in Inputs)
            foreach (var route in Routes)
            foreach (var markerNull in new[] { false, true })
                observations.Add(Invoke("missing", route, input, 0, false, true, markerNull));
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path)));
            File.WriteAllText(path, ReportJson.Encode(new Dictionary<string, object>
            {
                { "unityVersion", Application.unityVersion }, { "platform", Application.platform.ToString() },
                { "stage", stage }, { "profile", "dynamic-virtual-dispatch" }, { "observations", observations }
            }));
        }

        private static Dictionary<string, object> Invoke(string kind, string route, int input, int counter,
            bool referenceNull, bool receiverNull, bool markerNull)
        {
            var receiver = receiverNull ? null : Create(kind);
            var reference = referenceNull ? null : new object();
            if (receiver is BaseState state)
            {
                state.Counter = counter; state.Neighbor = -137; state.Reference = reference;
            }
            var markerReference = referenceNull ? null : new object();
            var marker = markerNull ? null : new Marker { Value = -101, Neighbor = 307, Reference = markerReference };
            object value = null;
            var failure = "none";
            try
            {
                switch (route)
                {
                    case "virtual": value = Dispatcher.CallVirtual((BaseState)receiver, input); break;
                    case "virtual-store": value = Dispatcher.CallVirtualThenStore((BaseState)receiver, input, marker); break;
                    case "concrete-shadow": value = ((ShadowState)receiver).Apply(input); break;
                    default: throw new InvalidOperationException("Unknown neutral dispatch route.");
                }
            }
            catch (Exception error) { failure = error.GetType().FullName; }
            return new Dictionary<string, object>
            {
                { "kind", "dispatch" }, { "owner", kind }, { "route", route }, { "input", input },
                { "counterBefore", counter }, { "referenceNull", referenceNull },
                { "receiverNull", receiverNull }, { "markerNull", markerNull },
                { "value", value }, { "failure", failure },
                { "counterAfter", receiverNull ? (object)null : Counter(receiver) },
                { "neighborAfter", receiverNull ? (object)null : Neighbor(receiver) },
                { "referenceSame", receiverNull || ReferenceEquals(reference, Reference(receiver)) },
                { "markerAfter", markerNull ? (object)null : marker.Value },
                { "markerNeighborAfter", markerNull ? (object)null : marker.Neighbor },
                { "markerReferenceSame", markerNull || ReferenceEquals(markerReference, marker.Reference) }
            };
        }

        private static object Create(string kind)
        {
            switch (kind)
            {
                case "base": return new BaseState();
                case "override": return new OverrideState();
                case "inherited": return new InheritedState();
                case "shadow": return new ShadowState();
                default: throw new InvalidOperationException("Unknown neutral receiver kind.");
            }
        }

        private static int Counter(object value)
        {
            return ((BaseState)value).Counter;
        }

        private static int Neighbor(object value)
        {
            return ((BaseState)value).Neighbor;
        }

        private static object Reference(object value)
        {
            return ((BaseState)value).Reference;
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void RunPlayer()
        {
            var arguments = Environment.GetCommandLineArgs();
            for (var index = 0; index + 1 < arguments.Length; index++)
            {
                if (arguments[index] != "--validation-report") continue;
                try { Write(arguments[index + 1], "player"); Application.Quit(0); }
                catch (Exception error) { Debug.LogException(error); Application.Quit(1); }
                return;
            }
        }
    }
}
