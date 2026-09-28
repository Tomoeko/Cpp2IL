using System.Runtime.CompilerServices;

namespace TripleLiteralGuardFixture
{
    public class ChoiceCell
    {
        // These neutral fields put Flag beyond the short displacement range.
        public long Pad00, Pad01, Pad02, Pad03, Pad04, Pad05, Pad06, Pad07;
        public long Pad08, Pad09, Pad10, Pad11, Pad12, Pad13, Pad14, Pad15;
        public long Pad16, Pad17, Pad18, Pad19, Pad20, Pad21, Pad22, Pad23;
        public byte Tail0, Tail1, Tail2, Tail3, Tail4;
        public bool Flag;
    }

    public class LabelNode
    {
        public long Pad00, Pad01, Pad02, Pad03, Pad04, Pad05, Pad06, Pad07, Pad08;
        public string Label;
        public ChoiceCell Choice;
        public int ChoiceGetterCount;

        [MethodImpl(MethodImplOptions.NoInlining)]
        public ChoiceCell GetChoice()
        {
            ChoiceGetterCount = unchecked(ChoiceGetterCount + 1);
            return Choice;
        }
    }

    public class LabelOwner
    {
        public LabelNode First;
        public LabelNode Second;
        public int NodeGetterCount;

        [MethodImpl(MethodImplOptions.NoInlining)]
        public LabelNode GetNode()
        {
            NodeGetterCount = unchecked(NodeGetterCount + 1);
            return NodeGetterCount == 1 ? First : Second;
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        public virtual string Compose()
        {
            return string.Concat(GetNode().Label,
                GetNode().GetChoice().Flag ? "warm" : "cool", "!ending");
        }
    }
}
