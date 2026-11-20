using System.Runtime.InteropServices;

namespace ivalice.battle.smartautobattle;

/// <summary>
/// Checks, inside the running game, that calling the hooked function still leaves r8-r11 untouched.
/// The game's own callers keep live values in those registers across the call (the compiler knew the
/// original function never writes them), so a hook that clobbers them crashes the game.
/// </summary>
internal static unsafe class RegisterSelfTest
{
    private const int SceneSize = 0x200;
    private const int SceneUnitPointer = 0x148;

    /// <summary>
    /// Calls <paramref name="function"/> once with a throwaway scene object whose unit lies outside the
    /// game's unit table, so nothing in the game is read or changed.
    /// </summary>
    /// <returns>True when r8, r9, r10 and r11 survived the call.</returns>
    public static bool RegistersSurvive(nint function)
    {
        var stub = BuildStub();
        var memory = VirtualAlloc(0, (nuint)stub.Length, 0x3000 /* commit + reserve */, 0x40 /* execute, read, write */);
        if (memory == 0) return false;

        var scene = (byte*)NativeMemory.AllocZeroed(SceneSize);
        var unit = (byte*)NativeMemory.AllocZeroed(SceneSize);
        try
        {
            Marshal.Copy(stub, 0, memory, stub.Length);
            FlushInstructionCache(GetCurrentProcess(), memory, (nuint)stub.Length);
            *(byte**)(scene + SceneUnitPointer) = unit;
            return ((delegate* unmanaged<nint, nint, int>)memory)(function, (nint)scene) == 1;
        }
        finally
        {
            NativeMemory.Free(scene);
            NativeMemory.Free(unit);
            VirtualFree(memory, 0, 0x8000 /* release */);
        }
    }

    /// <summary>
    /// int stub(void* function, void* scene): loads a marker into each of r8-r11, calls function(scene),
    /// and returns 1 only if all four markers are still there.
    /// </summary>
    private static byte[] BuildStub()
    {
        ulong[] markers = [0x1111111111111111, 0x2222222222222222, 0x3333333333333333, 0x4444444444444444];
        var code = new List<byte>();
        var jumpsToEnd = new List<int>();

        code.AddRange([0x53]);                         // push rbx
        code.AddRange([0x48, 0x83, 0xEC, 0x20]);       // sub rsp, 0x20
        code.AddRange([0x48, 0x89, 0xC8]);             // mov rax, rcx   (function)
        code.AddRange([0x48, 0x89, 0xD1]);             // mov rcx, rdx   (scene)
        for (var i = 0; i < 4; i++)
        {
            code.AddRange([0x49, (byte)(0xB8 + i)]);   // mov r8+i, marker
            code.AddRange(BitConverter.GetBytes(markers[i]));
        }
        code.AddRange([0xFF, 0xD0]);                   // call rax
        code.AddRange([0x31, 0xC0]);                   // xor eax, eax
        for (var i = 0; i < 4; i++)
        {
            code.AddRange([0x48, 0xBB]);               // mov rbx, marker
            code.AddRange(BitConverter.GetBytes(markers[i]));
            code.AddRange([0x49, 0x39, (byte)(0xD8 + i)]); // cmp r8+i, rbx
            code.AddRange([0x75, 0x00]);               // jne end
            jumpsToEnd.Add(code.Count);
        }
        code.AddRange([0xB8, 0x01, 0x00, 0x00, 0x00]); // mov eax, 1
        var end = code.Count;
        code.AddRange([0x48, 0x83, 0xC4, 0x20]);       // end: add rsp, 0x20
        code.AddRange([0x5B]);                         // pop rbx
        code.AddRange([0xC3]);                         // ret

        foreach (var after in jumpsToEnd) code[after - 1] = (byte)(end - after);
        return code.ToArray();
    }

    [DllImport("kernel32.dll")] private static extern nint VirtualAlloc(nint address, nuint size, uint type, uint protect);
    [DllImport("kernel32.dll")] private static extern bool VirtualFree(nint address, nuint size, uint type);
    [DllImport("kernel32.dll")] private static extern nint GetCurrentProcess();
    [DllImport("kernel32.dll")] private static extern bool FlushInstructionCache(nint process, nint address, nuint size);
}
