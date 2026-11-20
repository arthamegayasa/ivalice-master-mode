namespace ivalice.party.stattuning;

/// <summary>
/// The assembly that runs at a hooked place in the game, before the game's own instructions there.
/// This game's compiler keeps live values in volatile registers across calls to its own functions, and
/// managed code is free to overwrite them, so every such register is saved and restored around the call
/// into the mod (see "Why the hook saves every register" in docs/smart-auto-battle.md).
/// </summary>
internal static class HookStub
{
    /// <param name="callManaged">The instruction that calls the mod's managed callback.</param>
    /// <param name="atFunctionEntry">
    /// True for a hook on a function's first instruction, where the stack is 8 bytes off 16-byte
    /// alignment; false for a hook in the middle of a function body, where it is aligned.
    /// </param>
    /// <param name="unitRegister">The register that holds the unit record at the hooked place; the callback takes it as its argument.</param>
    public static string[] Build(string callManaged, bool atFunctionEntry, string unitRegister = "rbx")
    {
        // Seven pushes take 56 bytes. The rest must leave the stack 16-byte aligned for the call and
        // hold 0x20 bytes of shadow space plus six 16-byte slots.
        var frame = atFunctionEntry ? "0x80" : "0x88";
        return
        [
            "use64",
            "push rax", "push rcx", "push rdx", "push r8", "push r9", "push r10", "push r11",
            $"sub rsp, {frame}",
            "movdqu [rsp+0x20], xmm0", "movdqu [rsp+0x30], xmm1", "movdqu [rsp+0x40], xmm2",
            "movdqu [rsp+0x50], xmm3", "movdqu [rsp+0x60], xmm4", "movdqu [rsp+0x70], xmm5",
            $"mov rcx, {unitRegister}",
            callManaged,
            "movdqu xmm0, [rsp+0x20]", "movdqu xmm1, [rsp+0x30]", "movdqu xmm2, [rsp+0x40]",
            "movdqu xmm3, [rsp+0x50]", "movdqu xmm4, [rsp+0x60]", "movdqu xmm5, [rsp+0x70]",
            $"add rsp, {frame}",
            "pop r11", "pop r10", "pop r9", "pop r8", "pop rdx", "pop rcx", "pop rax",
        ];
    }
}
