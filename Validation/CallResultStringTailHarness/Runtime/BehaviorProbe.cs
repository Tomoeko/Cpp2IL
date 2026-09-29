using System;
using System.Collections.Generic;
using System.IO;
using CallResultStringTailFixture;
using UnityEngine;

namespace RecoveryValidation
{
    public static class BehaviorProbe
    {
        public static void Write(string path, string stage)
        {
            var observations = new List<object> { Constructor() };
            foreach (var scenario in new[]
                { "value", "missing-node", "null-string", "host-null", "other-node" })
                observations.Add(Single(scenario));
            observations.AddRange(Sequence());

            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path)));
            File.WriteAllText(path, ReportJson.Encode(new Dictionary<string, object>
            {
                { "unityVersion", Application.unityVersion },
                { "platform", Application.platform.ToString() },
                { "stage", stage },
                { "profile", "call-result-string-tail" },
                { "observations", observations }
            }));
        }

        private static object Constructor()
        {
            var host = new Host();
            var node = new TextNode();
            return new Dictionary<string, object>
            {
                { "kind", "constructor" },
                { "nodeNull", host.Node == null },
                { "valueNull", node.Value == null },
                { "textCalls", node.TextCalls }
            };
        }

        private static object Single(string scenario)
        {
            var witness = new TextNode { Value = "alpha", TextCalls = 4 };
            var other = new TextNode { Value = "beta", TextCalls = 7 };
            Host host = scenario == "host-null" ? null : new Host
            {
                Node = scenario == "missing-node" ? null :
                    scenario == "other-node" ? other : witness
            };
            if (scenario == "null-string") witness.Value = null;
            return Invoke("single", scenario, host, witness, other, null);
        }

        private static List<object> Sequence()
        {
            var witness = new TextNode { Value = "first", TextCalls = 7 };
            var first = new Host { Node = witness };
            var second = new Host { Node = witness };
            var rows = new List<object>
            {
                Invoke("sequence", "first-host", first, witness, null, second),
                Invoke("sequence", "second-host", second, witness, null, first)
            };
            witness.Value = "second";
            rows.Add(Invoke("sequence", "changed-value", first, witness, null, second));
            second.Node = null;
            rows.Add(Invoke("sequence", "missing-node", second, witness, null, first));
            second.Node = witness;
            rows.Add(Invoke("sequence", "restored", second, witness, null, first));
            witness.Value = null;
            rows.Add(Invoke("sequence", "null-string", first, witness, null, second));
            return rows;
        }

        private static object Invoke(string kind, string scenario, Host host,
            TextNode witness, TextNode other, Host peer)
        {
            string result = null;
            var failure = "none";
            try { result = host.ReadText(); }
            catch (Exception error) { failure = error.GetType().FullName; }
            return new Dictionary<string, object>
            {
                { "kind", kind }, { "scenario", scenario },
                { "failure", failure }, { "result", result },
                { "textCalls", witness.TextCalls },
                { "otherTextCalls", other == null ? null : (object)other.TextCalls },
                { "nodeSameWitness", host == null ? null :
                    (object)ReferenceEquals(host.Node, witness) },
                { "hostsShareNode", peer == null ? null :
                    (object)ReferenceEquals(host.Node, peer.Node) },
                { "value", witness.Value },
                { "hostNull", host == null }
            };
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
