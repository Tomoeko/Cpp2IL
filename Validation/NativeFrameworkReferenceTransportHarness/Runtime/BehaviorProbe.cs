using System;
using System.Collections.Generic;
using System.IO;
using System.Linq.Expressions;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Xml;
using NativeFrameworkReferenceTransportFixture;
using UnityEngine;

namespace RecoveryValidation
{
    public static class BehaviorProbe
    {
        public static void Write(string path, string stage)
        {
            var rows = new List<object>();
            Declarations(rows);
            Framework(rows, "regex", "Pattern", "RegexIdentity", typeof(Regex));
            Framework(rows, "expression", "Projection", "ExpressionIdentity", typeof(Expression));
            Framework(rows, "xml", "Document", "XmlIdentity", typeof(XmlDocument));
            var owner = new Probe();
            rows.Add(new Dictionary<string, object>
            {
                { "kind", "defaults" }, { "patternNull", owner.Pattern == null },
                { "projectionNull", owner.Projection == null }, { "documentNull", owner.Document == null }
            });
            var firstRegex = new Regex("^a+$");
            var secondRegex = new Regex("^b+$");
            Cases(rows, "regex", firstRegex, secondRegex, Probe.RegexIdentity,
                value => owner.Pattern = value, () => owner.Pattern);
            var firstExpression = Expression.Constant(17);
            var secondExpression = Expression.Constant(-5);
            Cases<Expression>(rows, "expression", firstExpression, secondExpression, Probe.ExpressionIdentity,
                value => owner.Projection = value, () => owner.Projection);
            var firstDocument = new XmlDocument();
            firstDocument.LoadXml("<root value=\"17\" />");
            var secondDocument = new XmlDocument();
            secondDocument.LoadXml("<other value=\"-5\" />");
            Cases(rows, "xml", firstDocument, secondDocument, Probe.XmlIdentity,
                value => owner.Document = value, () => owner.Document);
            rows.Add(new Dictionary<string, object>
            {
                { "kind", "frameworkBehavior" }, { "regexAccept", firstRegex.IsMatch("aaa") },
                { "regexReject", firstRegex.IsMatch("bbb") }, { "expressionKind", (int)firstExpression.NodeType },
                { "expressionValue", (int)firstExpression.Value }, { "xmlName", firstDocument.DocumentElement.Name },
                { "xmlValue", firstDocument.DocumentElement.GetAttribute("value") }
            });
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path)));
            File.WriteAllText(path, ReportJson.Encode(new Dictionary<string, object>
            {
                { "unityVersion", Application.unityVersion }, { "platform", Application.platform.ToString() },
                { "stage", stage }, { "profile", "native-framework-reference-transport" }, { "observations", rows }
            }));
        }

        private static void Declarations(List<object> rows)
        {
            var type = typeof(Probe);
            var fields = new List<object>();
            var methods = new List<object>();
            foreach (var name in new[] { "Pattern", "Projection", "Document" })
            {
                var field = type.GetField(name, BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly);
                fields.Add(new Dictionary<string, object> { { "name", field.Name }, { "type", field.FieldType.FullName } });
            }
            foreach (var name in new[] { "RegexIdentity", "ExpressionIdentity", "XmlIdentity" })
            {
                var method = type.GetMethod(name, BindingFlags.Public | BindingFlags.Static | BindingFlags.DeclaredOnly);
                var parameters = method.GetParameters();
                methods.Add(new Dictionary<string, object>
                {
                    { "name", method.Name }, { "static", method.IsStatic }, { "return", method.ReturnType.FullName },
                    { "parameterCount", parameters.Length }, { "parameter", parameters[0].ParameterType.FullName }
                });
            }
            rows.Add(new Dictionary<string, object>
            {
                { "kind", "declarations" }, { "type", type.FullName },
                { "visibility", (int)(type.Attributes & TypeAttributes.VisibilityMask) }, { "sealed", type.IsSealed },
                { "constructors", type.GetConstructors(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly).Length },
                { "fields", fields }, { "methods", methods }
            });
        }

        private static void Framework(List<object> rows, string family, string fieldName, string methodName, Type expected)
        {
            var field = typeof(Probe).GetField(fieldName);
            var method = typeof(Probe).GetMethod(methodName);
            var identity = field.FieldType.Assembly.GetName();
            var token = identity.GetPublicKeyToken();
            var text = "";
            foreach (var value in token) text += value.ToString("x2");
            rows.Add(new Dictionary<string, object>
            {
                { "kind", "frameworkIdentity" }, { "family", family }, { "name", identity.Name },
                { "version", identity.Version.ToString() }, { "culture", identity.CultureName ?? "" }, { "token", text },
                { "signatureBound", field.FieldType == expected && method.ReturnType == expected &&
                    method.GetParameters()[0].ParameterType == expected }
            });
        }

        private static void Cases<T>(List<object> rows, string family, T first, T second, Func<T, T> call,
            Action<T> store, Func<T> load) where T : class
        {
            store(first);
            One(rows, family, "null", null, first, second, call, load);
            One(rows, family, "first", first, first, second, call, load);
            One(rows, family, "second", second, first, second, call, load);
            One(rows, family, "reused", first, first, second, call, load);
            store(second);
            One(rows, family, "stored", load(), first, second, call, load);
            One(rows, family, "afterReplacement", first, first, second, call, load);
        }

        private static void One<T>(List<object> rows, string family, string name, T incoming, T first, T second,
            Func<T, T> call, Func<T> load) where T : class
        {
            var stored = load();
            var result = call(incoming);
            rows.Add(new Dictionary<string, object>
            {
                { "kind", "identity" }, { "family", family }, { "case", name }, { "resultNull", result == null },
                { "returnedIncoming", ReferenceEquals(result, incoming) }, { "matchesFirst", ReferenceEquals(result, first) },
                { "matchesSecond", ReferenceEquals(result, second) }, { "fieldUnchanged", ReferenceEquals(stored, load()) }
            });
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
