using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace Cpp2IL.Core.Reporting;

public sealed class MethodRecoveryResult
{
    public int Identity { get; private set; }
    public string AssemblyName { get; private set; }
    public string TypeName { get; private set; }
    public string MethodName { get; private set; }
    public string Signature { get; private set; }
    public uint Token { get; private set; }
    public bool IsInputMethod { get; private set; }
    public bool HasNativeBody { get; private set; }
    public string[] Reasons { get; private set; }
    public string ManagedIlValidation { get; private set; }

    public MethodRecoveryDisposition Disposition { get; }

    public bool IsExcluded => Disposition is MethodRecoveryDisposition.ExcludedReferenceAssembly
        or MethodRecoveryDisposition.NoManagedBody or MethodRecoveryDisposition.ExcludedInjectedMethod;

    public bool IsUnresolved => !IsExcluded && Disposition != MethodRecoveryDisposition.Emitted;

    public MethodRecoveryResult(int identity, string assemblyName, string typeName, string methodName,
        string signature, uint token, bool isInputMethod, bool hasNativeBody,
        MethodRecoveryDisposition disposition, IEnumerable<string>? reasons = null)
    {
        Identity = identity;
        AssemblyName = assemblyName;
        TypeName = typeName;
        MethodName = methodName;
        Signature = signature;
        Token = token;
        IsInputMethod = isInputMethod;
        HasNativeBody = hasNativeBody;
        Disposition = disposition;
        Reasons = reasons?.Distinct(StringComparer.Ordinal).ToArray() ?? [];
        ManagedIlValidation = disposition is MethodRecoveryDisposition.Emitted or MethodRecoveryDisposition.Partial
            ? "LabelsAndStackDepthChecked" : "NotRun";
    }

    internal MethodRecoveryResult WithDisposition(MethodRecoveryDisposition disposition, IEnumerable<string> reasons)
        => new(Identity, AssemblyName, TypeName, MethodName, Signature, Token, IsInputMethod, HasNativeBody, disposition, reasons);

    internal string ToJson() => "{" +
        "\"Identity\":" + Identity.ToString(CultureInfo.InvariantCulture) + "," +
        "\"AssemblyName\":" + JsonText.Quote(AssemblyName) + "," +
        "\"TypeName\":" + JsonText.Quote(TypeName) + "," +
        "\"MethodName\":" + JsonText.Quote(MethodName) + "," +
        "\"Signature\":" + JsonText.Quote(Signature) + "," +
        "\"Token\":" + Token.ToString(CultureInfo.InvariantCulture) + "," +
        "\"IsInputMethod\":" + (IsInputMethod ? "true" : "false") + "," +
        "\"HasNativeBody\":" + (HasNativeBody ? "true" : "false") + "," +
        "\"Disposition\":" + JsonText.Quote(Disposition.ToString()) + "," +
        "\"Reasons\":" + JsonText.Array(Reasons) + "," +
        "\"ManagedIlValidation\":" + JsonText.Quote(ManagedIlValidation) + "}";
}
