using System.Runtime.CompilerServices;

namespace TypedFieldAddressDiscardedResultFixture
{
    public enum ResultCode : int
    {
        Idle = 0,
        Changed = 1
    }

    public enum UpdateMode : int
    {
        Default = 0,
        Alternative = 1
    }

    public struct ResultCell
    {
        public long Value;

        [MethodImpl(MethodImplOptions.NoInlining)]
        public ResultCode ResetAndReport()
        {
            Value = 0;
            return ResultCode.Changed;
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        public ResultCode IncrementAndReport(UpdateMode mode)
        {
            Value = unchecked(Value + 1);
            return (ResultCode)mode;
        }
    }

    public sealed class AddressResultOwner
    {
        public ResultCell First;
        public ResultCell Second;
        public bool Enabled;

        [MethodImpl(MethodImplOptions.NoInlining)]
        public void ResetFirstDiscard()
        {
            First.ResetAndReport();
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        public void ResetBothDiscard()
        {
            First.ResetAndReport();
            Second.ResetAndReport();
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        public void ApplyDefault()
        {
            First.IncrementAndReport(UpdateMode.Default);
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        public void GuardedApplyDefault()
        {
            if (Enabled)
                First.IncrementAndReport(UpdateMode.Default);
        }
    }
}
