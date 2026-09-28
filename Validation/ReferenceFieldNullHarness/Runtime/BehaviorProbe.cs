using System;
using System.Collections.Generic;
using System.IO;
using ReferenceFieldNullFixture;
using UnityEngine;

namespace RecoveryValidation
{
    public static class BehaviorProbe
    {
        public static void Write(string path, string stage)
        {
            var observations = new List<object>();
            var owner = new ReferenceOwner { Neighbor = 23 };
            Record(observations, "initial-null", owner);
            owner.Current = new object();
            owner.Text = new string('a', 3);
            owner.Trap = new OperatorTrap();
            Record(observations, "assigned-values", owner);
            owner.Current = null;
            owner.Text = null;
            owner.Trap = null;
            Record(observations, "cleared-values", owner);
            var derived = new DerivedOwner { Neighbor = -31 };
            RecordDerived(observations, "inherited-null", derived);
            derived.Current = new object();
            RecordDerived(observations, "inherited-value", derived);
            Record(observations, "null-receiver", null);

            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path)));
            File.WriteAllText(path, ReportJson.Encode(new Dictionary<string, object>
            {
                { "unityVersion", Application.unityVersion }, { "platform", Application.platform.ToString() },
                { "stage", stage }, { "profile", "reference-field-null" },
                { "observations", observations }
            }));
        }

        private static void Record(List<object> observations, string kind, ReferenceOwner owner)
        {
            string failure = null;
            bool? isNull = null;
            bool? hasCurrent = null;
            bool? textNull = null;
            bool? trapNull = null;
            bool? trapOperatorNull = null;
            try
            {
                isNull = owner.IsCurrentNull();
                hasCurrent = owner.HasCurrent();
                textNull = owner.IsTextNull();
                trapNull = owner.IsTrapReferenceNull();
                trapOperatorNull = owner.Trap == null;
            }
            catch (Exception error)
            {
                failure = error.GetType().Name;
            }
            observations.Add(new Dictionary<string, object>
            {
                { "kind", kind }, { "isNull", isNull }, { "hasCurrent", hasCurrent },
                { "textNull", textNull }, { "failure", failure },
                { "trapNull", trapNull }, { "trapOperatorNull", trapOperatorNull },
                { "neighbor", owner == null ? (int?)null : owner.Neighbor }
            });
        }

        private static void RecordDerived(List<object> observations, string kind, DerivedOwner owner)
        {
            observations.Add(new Dictionary<string, object>
            {
                { "kind", kind }, { "inheritedNull", owner.IsInheritedCurrentNull() },
                { "neighbor", owner.Neighbor }
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
