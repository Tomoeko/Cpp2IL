using System;
using System.Linq;
using Cpp2IL.Core.ISIL;
using Cpp2IL.Core.Model.Contexts;

namespace Cpp2IL.Core.Tests;

public partial class IlGeneratorParameterTests
{
    [TestCase(false)]
    [TestCase(true)]
    public void IncomingBooleanArrayStorePreservesValueNeighborAndExceptionalEffects(bool instance)
    {
        var arrayType = new SzArrayTypeAnalysisContext(_app.SystemTypes.SystemBooleanType);
        var (context, definition, parameters) = CreateMethod("SetBooleanParameter",
            _app.SystemTypes.SystemVoidType,
            [arrayType, _app.SystemTypes.SystemInt32Type, _app.SystemTypes.SystemBooleanType], instance);
        Emit(context, definition,
        [
            new(0, OpCode.Move, new ArrayAccess(parameters[0], parameters[1]), parameters[2]),
            new(1, OpCode.Return),
        ]);
        if (instance) AddDefaultConstructor();
        Assert.That(definition.CilMethodBody!.Instructions.Count(instruction =>
            instruction.OpCode == AsmResolver.PE.DotNet.Cil.CilOpCodes.Stelem_I1), Is.EqualTo(1));
        using var runtime = Load();
        var method = runtime.Type.GetMethod("SetBooleanParameter")!;
        var receiver = instance ? Activator.CreateInstance(runtime.Type) : null;
        foreach (var value in new[] { false, true })
        {
            var array = new[] { true, false, true };
            method.Invoke(receiver, [array, 1, value]);
            Assert.That(array, Is.EqualTo(new[] { true, value, true }));
            foreach (var index in new[] { int.MinValue, -1, 3, int.MaxValue })
            {
                Assert.That(() => method.Invoke(receiver, [array, index, !value]),
                    Throws.TypeOf<System.Reflection.TargetInvocationException>()
                        .With.InnerException.TypeOf<IndexOutOfRangeException>());
                Assert.That(array, Is.EqualTo(new[] { true, value, true }));
            }
            Assert.That(() => method.Invoke(receiver, [null, int.MinValue, value]),
                Throws.TypeOf<System.Reflection.TargetInvocationException>()
                    .With.InnerException.TypeOf<NullReferenceException>());
        }
    }
}
