using System.Runtime.InteropServices;

namespace ivalice.party.reincarnation;

/// <summary>The registers a call did not hand back as it found them.</summary>
[Flags]
internal enum LostRegisters
{
    None = 0,
    Rax = 1 << 0,
    Rcx = 1 << 1,
    Rdx = 1 << 2,
    R8 = 1 << 3,
    R9 = 1 << 4,
    R10 = 1 << 5,
    R11 = 1 << 6,
    Xmm0 = 1 << 7,
    Xmm1 = 1 << 8,
    Xmm2 = 1 << 9,
    Xmm3 = 1 << 10,
    Xmm4 = 1 << 11,
    Xmm5 = 1 << 12,
    /// <summary>The probe itself could not run (no executable memory).</summary>
    ProbeFailed = 1 << 30,
}

/// <summary>
/// Checks that a hook stub hands every volatile register back unchanged. This game's compiler keeps live
/// values in those registers across calls to its own small functions, so a hook that lets managed code
/// overwrite them crashes the game (see docs/smart-auto-battle.md). The mod puts its real hook stub on a
/// throwaway function that does nothing, calls it through this probe, and only hooks the game when
/// nothing was lost.
/// </summary>
internal static unsafe class RegisterSelfTest
{
    private const int FirstXmmBit = 7;
    private const int XmmCount = 6;

    /// <summary>A function made of padding and a return: room for a hook, and no register use of its own.</summary>
    public static nint AllocateQuietFunction()
    {
        var code = new byte[32];
        Array.Fill(code, (byte)0x90, 0, 31); // nop
        code[31] = 0xC3;                     // ret
        return AllocateCode(code);
    }

    /// <summary>Copies machine code into executable memory. Returns 0 when Windows refuses the memory.</summary>
    public static nint AllocateCode(byte[] code)
    {
        var memory = VirtualAlloc(0, (nuint)code.Length, 0x3000 /* commit + reserve */, 0x40 /* execute, read, write */);
        if (memory == 0) return 0;
        Marshal.Copy(code, 0, memory, code.Length);
        FlushInstructionCache(GetCurrentProcess(), memory, (nuint)code.Length);
        return memory;
    }

    public static void Free(nint memory)
    {
        if (memory != 0) VirtualFree(memory, 0, 0x8000 /* release */);
    }

    /// <summary>
    /// Calls <paramref name="function"/> with a marker value in rcx, rdx, r8 to r11 and xmm0 to xmm5, and
    /// reports which of them, or rax, came back different. The function must change no register itself.
    /// </summary>
    public static LostRegisters Probe(nint function)
    {
        if (function == 0) return LostRegisters.ProbeFailed;
        var stub = AllocateCode(BuildStub());
        if (stub == 0) return LostRegisters.ProbeFailed;
        try
        {
            return (LostRegisters)((delegate* unmanaged<nint, int>)stub)(function);
        }
        finally
        {
            Free(stub);
        }
    }

    /// <summary>
    /// int stub(void* function): loads the markers, calls the function, and returns one bit for every
    /// register that no longer holds its marker (rax must still hold the function's address).
    /// </summary>
    internal static byte[] BuildStub()
    {
        // Integer markers for rcx, rdx, r8, r9, r10, r11, then the low halves of xmm0 to xmm5.
        ulong[] markers =
        [
            0x1111111111111111, 0x2222222222222222, 0x3333333333333333, 0x4444444444444444,
            0x5555555555555555, 0x6666666666666666,
            0xA0A0A0A0A0A0A0A0, 0xA1A1A1A1A1A1A1A1, 0xA2A2A2A2A2A2A2A2, 0xA3A3A3A3A3A3A3A3,
            0xA4A4A4A4A4A4A4A4, 0xA5A5A5A5A5A5A5A5,
        ];
        byte[][] loadInteger = [[0x48, 0xB9], [0x48, 0xBA], [0x49, 0xB8], [0x49, 0xB9], [0x49, 0xBA], [0x49, 0xBB]];
        byte[][] compareWithRbx =
        [
            [0x48, 0x39, 0xD9], [0x48, 0x39, 0xDA], [0x49, 0x39, 0xD8], [0x49, 0x39, 0xD9], [0x49, 0x39, 0xDA], [0x49, 0x39, 0xDB],
        ];

        var code = new List<byte>();
        void Emit(params byte[] bytes) => code.AddRange(bytes);
        void EmitValue(ulong value) => code.AddRange(BitConverter.GetBytes(value));
        void MarkLostUnlessEqual(int bit)
        {
            Emit(0x74, 0x06);                                      // je over the next instruction
            Emit(0x81, 0xCF);                                      // or edi, 1 << bit
            code.AddRange(BitConverter.GetBytes(1 << bit));
        }

        Emit(0x53);                                                // push rbx
        Emit(0x56);                                                // push rsi
        Emit(0x57);                                                // push rdi
        Emit(0x48, 0x83, 0xEC, 0x20);                              // sub rsp, 0x20   (stack is 16-byte aligned here)
        Emit(0x48, 0x89, 0xCE);                                    // mov rsi, rcx    (function)
        Emit(0x31, 0xFF);                                          // xor edi, edi    (lost registers)

        for (var i = 0; i < loadInteger.Length; i++)
        {
            Emit(loadInteger[i]);                                  // mov <register>, marker
            EmitValue(markers[i]);
        }
        for (var i = 0; i < XmmCount; i++)
        {
            Emit(0x48, 0xBB);                                      // mov rbx, marker
            EmitValue(markers[loadInteger.Length + i]);
            Emit(0x66, 0x48, 0x0F, 0x6E, (byte)(0xC3 | (i << 3))); // movq xmm<i>, rbx
        }

        Emit(0x48, 0x89, 0xF0);                                    // mov rax, rsi
        Emit(0xFF, 0xD0);                                          // call rax

        Emit(0x48, 0x39, 0xF0);                                    // cmp rax, rsi
        MarkLostUnlessEqual(0);
        for (var i = 0; i < loadInteger.Length; i++)
        {
            Emit(0x48, 0xBB);                                      // mov rbx, marker
            EmitValue(markers[i]);
            Emit(compareWithRbx[i]);                               // cmp <register>, rbx
            MarkLostUnlessEqual(1 + i);
        }
        for (var i = 0; i < XmmCount; i++)
        {
            Emit(0x66, 0x48, 0x0F, 0x7E, (byte)(0xC0 | (i << 3))); // movq rax, xmm<i>
            Emit(0x48, 0xBB);                                      // mov rbx, marker
            EmitValue(markers[loadInteger.Length + i]);
            Emit(0x48, 0x39, 0xD8);                                // cmp rax, rbx
            MarkLostUnlessEqual(FirstXmmBit + i);
        }

        Emit(0x89, 0xF8);                                          // mov eax, edi
        Emit(0x48, 0x83, 0xC4, 0x20);                              // add rsp, 0x20
        Emit(0x5F);                                                // pop rdi
        Emit(0x5E);                                                // pop rsi
        Emit(0x5B);                                                // pop rbx
        Emit(0xC3);                                                // ret
        return code.ToArray();
    }

    [DllImport("kernel32.dll")] private static extern nint VirtualAlloc(nint address, nuint size, uint type, uint protect);
    [DllImport("kernel32.dll")] private static extern bool VirtualFree(nint address, nuint size, uint type);
    [DllImport("kernel32.dll")] private static extern nint GetCurrentProcess();
    [DllImport("kernel32.dll")] private static extern bool FlushInstructionCache(nint process, nint address, nuint size);
}
