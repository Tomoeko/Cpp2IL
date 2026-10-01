using System.Linq.Expressions;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using System.Xml;

namespace NativeFrameworkReferenceTransportFixture
{
    public sealed class Probe
    {
        public Regex Pattern;
        public Expression Projection;
        public XmlDocument Document;

        [MethodImpl(MethodImplOptions.NoInlining)]
        public static Regex RegexIdentity(Regex value)
        {
            return value;
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        public static Expression ExpressionIdentity(Expression value)
        {
            return value;
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        public static XmlDocument XmlIdentity(XmlDocument value)
        {
            return value;
        }
    }
}
