using System;
using System.Collections.Generic;
using System.IO;
using NullableDelegateFieldTailFixture;
using UnityEngine;

namespace RecoveryValidation
{
    public static class BehaviorProbe
    {
        private static int _staticCount;
        private static string _trace;

        private sealed class Receiver
        {
            public int Count;

            public void Record()
            {
                Count++;
                _trace += "I";
            }

            public void Throw()
            {
                Count++;
                _trace += "T";
                throw new InvalidOperationException();
            }
        }

        private static void RecordStatic()
        {
            _staticCount++;
            _trace += "S";
        }

        public static void Write(string path, string stage)
        {
            _staticCount = 0;
            _trace = string.Empty;
            var receiver = new Receiver();
            var direct = new DirectSignalNode();
            var padded = new PaddedSignalNode();
            var observations = new List<object>
            {
                new Dictionary<string, object>
                {
                    { "kind", "constructors:defaults" },
                    { "exception", "none" },
                    { "instanceCount", receiver.Count },
                    { "staticCount", _staticCount },
                    { "trace", _trace },
                    { "directNeighbor", direct.Neighbor == 0 },
                    { "paddedNeighbor", padded.Neighbor == 0 },
                    { "paddingIntact", padded.Padding00 == 0 &&
                        padded.Padding01 == 0 && padded.Padding02 == 0 &&
                        padded.Padding03 == 0 && padded.Padding04 == 0 &&
                        padded.Padding05 == 0 && padded.Padding06 == 0 &&
                        padded.Padding07 == 0 && padded.Padding08 == 0 &&
                        padded.Padding09 == 0 && padded.Padding10 == 0 &&
                        padded.Padding11 == 0 && padded.Padding12 == 0 &&
                        padded.Padding13 == 0 && padded.Padding14 == 0 &&
                        padded.Padding15 == 0 },
                    { "directCallbackNull", direct.Callback == null },
                    { "paddedCallbackNull", padded.Callback == null }
                }
            };
            direct.Neighbor = 101;
            padded.Padding00 = 11;
            padded.Padding15 = 26;
            padded.Neighbor = 202;

            Record(observations, "direct:null", direct, padded, receiver, false);
            direct.Callback = receiver.Record;
            Record(observations, "direct:instance", direct, padded, receiver, false);
            Record(observations, "direct:repeat", direct, padded, receiver, false);
            direct.Callback = RecordStatic;
            Record(observations, "direct:static", direct, padded, receiver, false);
            direct.Callback = (Action)receiver.Record + (Action)RecordStatic;
            Record(observations, "direct:multicast", direct, padded, receiver, false);

            Record(observations, "padded:null", direct, padded, receiver, true);
            padded.Callback = receiver.Record;
            Record(observations, "padded:instance", direct, padded, receiver, true);
            padded.Callback = (Action)RecordStatic + (Action)receiver.Record;
            Record(observations, "padded:multicast", direct, padded, receiver, true);
            padded.Callback = receiver.Throw;
            Record(observations, "padded:throws", direct, padded, receiver, true);
            direct.Callback = null;
            Record(observations, "direct:null-again", direct, padded, receiver, false);
            Record(observations, "direct:null-owner", direct, padded, receiver,
                false, true);
            Record(observations, "padded:null-owner", direct, padded, receiver,
                true, true);

            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path)));
            File.WriteAllText(path, ReportJson.Encode(new Dictionary<string, object>
            {
                { "unityVersion", Application.unityVersion },
                { "platform", Application.platform.ToString() },
                { "stage", stage },
                { "profile", "nullable-delegate-field-tail" },
                { "observations", observations }
            }));
        }

        private static void Record(List<object> observations, string kind,
            DirectSignalNode direct, PaddedSignalNode padded, Receiver receiver,
            bool usePadded, bool nullOwner = false)
        {
            var exception = "none";
            try
            {
                if (usePadded)
                {
                    if (nullOwner)
                        ((PaddedSignalNode)null).FireIfPresent();
                    else
                        padded.FireIfPresent();
                }
                else
                {
                    if (nullOwner)
                        ((DirectSignalNode)null).FireIfPresent();
                    else
                        direct.FireIfPresent();
                }
            }
            catch (Exception error)
            {
                exception = error.GetType().FullName;
            }
            observations.Add(new Dictionary<string, object>
            {
                { "kind", kind },
                { "exception", exception },
                { "instanceCount", receiver.Count },
                { "staticCount", _staticCount },
                { "trace", _trace },
                { "directNeighbor", 101 == direct?.Neighbor },
                { "paddedNeighbor", 202 == padded?.Neighbor },
                { "paddingIntact", padded == null ||
                    padded.Padding00 == 11 && padded.Padding15 == 26 },
                { "directCallbackNull", direct?.Callback == null },
                { "paddedCallbackNull", padded?.Callback == null }
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
