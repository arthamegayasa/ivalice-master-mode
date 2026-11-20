using ivalice.party.reincarnation;

namespace Ivalice.Party.Tests;

/// <summary>
/// Runs the mod's register probe as real machine code against small native functions, so the probe that
/// guards the game hook is itself known to catch every register it claims to watch.
/// </summary>
public class RegisterSelfTestTests
{
    private const byte Ret = 0xC3;

    private static LostRegisters ProbeCode(params byte[] code)
    {
        var function = RegisterSelfTest.AllocateCode(code);
        Assert.NotEqual(0, function);
        try
        {
            return RegisterSelfTest.Probe(function);
        }
        finally
        {
            RegisterSelfTest.Free(function);
        }
    }

    [Fact]
    public void A_function_that_touches_nothing_loses_nothing()
    {
        var function = RegisterSelfTest.AllocateQuietFunction();
        try
        {
            Assert.Equal(LostRegisters.None, RegisterSelfTest.Probe(function));
        }
        finally
        {
            RegisterSelfTest.Free(function);
        }
    }

    [Theory]
    [InlineData((int)LostRegisters.Rax, new byte[] { 0x31, 0xC0, Ret })]             // xor eax, eax
    [InlineData((int)LostRegisters.Rcx, new byte[] { 0x31, 0xC9, Ret })]             // xor ecx, ecx
    [InlineData((int)LostRegisters.Rdx, new byte[] { 0x31, 0xD2, Ret })]             // xor edx, edx
    [InlineData((int)LostRegisters.R8, new byte[] { 0x45, 0x31, 0xC0, Ret })]        // xor r8d, r8d
    [InlineData((int)LostRegisters.R9, new byte[] { 0x45, 0x31, 0xC9, Ret })]        // xor r9d, r9d
    [InlineData((int)LostRegisters.R10, new byte[] { 0x45, 0x31, 0xD2, Ret })]       // xor r10d, r10d
    [InlineData((int)LostRegisters.R11, new byte[] { 0x45, 0x31, 0xDB, Ret })]       // xor r11d, r11d
    [InlineData((int)LostRegisters.Xmm0, new byte[] { 0x66, 0x0F, 0xEF, 0xC0, Ret })] // pxor xmm0, xmm0
    [InlineData((int)LostRegisters.Xmm1, new byte[] { 0x66, 0x0F, 0xEF, 0xC9, Ret })]
    [InlineData((int)LostRegisters.Xmm2, new byte[] { 0x66, 0x0F, 0xEF, 0xD2, Ret })]
    [InlineData((int)LostRegisters.Xmm3, new byte[] { 0x66, 0x0F, 0xEF, 0xDB, Ret })]
    [InlineData((int)LostRegisters.Xmm4, new byte[] { 0x66, 0x0F, 0xEF, 0xE4, Ret })]
    [InlineData((int)LostRegisters.Xmm5, new byte[] { 0x66, 0x0F, 0xEF, 0xED, Ret })]
    public void A_function_that_overwrites_one_register_is_caught_for_exactly_that_register(int expected, byte[] code)
    {
        Assert.Equal((LostRegisters)expected, ProbeCode(code));
    }

    [Fact]
    public void A_function_that_overwrites_several_registers_is_caught_for_each()
    {
        // xor r8d, r8d; xor r11d, r11d; pxor xmm5, xmm5; ret
        var lost = ProbeCode(0x45, 0x31, 0xC0, 0x45, 0x31, 0xDB, 0x66, 0x0F, 0xEF, 0xED, Ret);

        Assert.Equal(LostRegisters.R8 | LostRegisters.R11 | LostRegisters.Xmm5, lost);
    }

    [Fact]
    public void A_missing_function_is_reported_as_a_failed_probe()
    {
        Assert.Equal(LostRegisters.ProbeFailed, RegisterSelfTest.Probe(0));
    }
}
