namespace ivalice.party.reincarnation;

/// <summary>
/// The assembly that runs at the top of the hooked game function, before the game's own instructions.
/// This game's compiler keeps live values in volatile registers across calls to its own functions, and
/// managed code is free to overwrite them, so every such register is saved and restored around the call
/// into the mod (see "Why the hook saves every register" in docs/smart-auto-battle.md).
/// </summary>
internal static class HookStub
{
    /// <param name="callManaged">The instruction that calls the mod's managed callback.</param>
    public static string[] Build(string callManaged) =>
    [
        "use64",
        // At a function's entry the stack is 8 bytes off 16-byte alignment; seven pushes put it right.
        "push rax", "push rcx", "push rdx", "push r8", "push r9", "push r10", "push r11",
        "sub rsp, 0x80", // 0x20 shadow space + six 16-byte slots
        "movdqu [rsp+0x20], xmm0", "movdqu [rsp+0x30], xmm1", "movdqu [rsp+0x40], xmm2",
        "movdqu [rsp+0x50], xmm3", "movdqu [rsp+0x60], xmm4", "movdqu [rsp+0x70], xmm5",
        callManaged,
        "movdqu xmm0, [rsp+0x20]", "movdqu xmm1, [rsp+0x30]", "movdqu xmm2, [rsp+0x40]",
        "movdqu xmm3, [rsp+0x50]", "movdqu xmm4, [rsp+0x60]", "movdqu xmm5, [rsp+0x70]",
        "add rsp, 0x80",
        "pop r11", "pop r10", "pop r9", "pop r8", "pop rdx", "pop rcx", "pop rax",
    ];
}
