// A keyboard shortcut goes by the window in front, a mouse gesture by the window under the pointer (#450): with
// Explorer focused and the pointer resting on the taskbar or another window, the shortcut was ignored.

using Filee.App.Services.Triggers;
using Filee.Core.Platform;
using Filee.Core.Settings;
using Microsoft.Extensions.Logging.Abstractions;

namespace Filee.App.Tests;

public sealed class TriggerScopeTests
{
    private static readonly TriggerGesture Shortcut = new() { Kind = TriggerKind.KeyChord, Modifiers = ModifierKeys.Ctrl | ModifierKeys.Alt, Key = "Space" };
    private static readonly TriggerGesture Drag = new() { Kind = TriggerKind.Drag, Modifiers = ModifierKeys.Ctrl, Button = TriggerMouseButton.Left };

    [Fact]
    public void A_shortcut_fires_for_the_file_manager_in_front_wherever_the_pointer_is()
    {
        var platform = new ScopePlatform { Front = new ForegroundWindowInfo("explorer", true), Pointer = ("notepad", false) };
        var service = new TriggerService(platform, NullLogger<TriggerService>.Instance);

        Assert.True(service.InScope(Shortcut, 10, 10));
        Assert.False(service.InScope(Drag, 10, 10)); // the pointer still decides for the mouse
    }

    [Fact]
    public void A_shortcut_is_ignored_in_another_app_in_front_even_with_the_pointer_over_explorer()
    {
        var platform = new ScopePlatform { Front = new ForegroundWindowInfo("notepad", false), Pointer = ("explorer", true) };
        var service = new TriggerService(platform, NullLogger<TriggerService>.Instance);

        Assert.False(service.InScope(Shortcut, 10, 10));
        Assert.True(service.InScope(Drag, 10, 10));
    }

    [Fact]
    public void An_excluded_app_in_front_blocks_a_shortcut_set_to_anywhere()
    {
        var platform = new ScopePlatform { Front = new ForegroundWindowInfo("Photoshop", false), Pointer = ("explorer", true) };
        var service = new TriggerService(platform, NullLogger<TriggerService>.Instance);
        var anywhere = Shortcut.Clone();
        anywhere.Scope = TriggerScope.Anywhere;
        Assert.True(service.InScope(anywhere, 10, 10));

        var settings = new AppSettings();
        settings.ExcludedProcesses.Add("Photoshop.exe");
        service.Apply(settings);

        Assert.False(service.InScope(anywhere, 10, 10));
    }

    [Fact]
    public void Without_a_window_in_front_the_pointer_decides_as_before()
    {
        var platform = new ScopePlatform { Front = null, Pointer = ("explorer", true) };
        var service = new TriggerService(platform, NullLogger<TriggerService>.Instance);

        Assert.True(service.InScope(Shortcut, 10, 10));
        platform.Pointer = ("notepad", false);
        Assert.False(service.InScope(Shortcut, 10, 10));
    }

    private sealed class ScopePlatform : IPlatformServices
    {
        public ForegroundWindowInfo? Front { get; set; }
        public (string Process, bool IsFileManager) Pointer { get; set; }
        public ForegroundWindowInfo? GetForegroundWindow() => Front;
        public bool IsFileManagerAt(int x, int y) => Pointer.IsFileManager;
        public string? ProcessNameAt(int x, int y) => Pointer.Process;
        public IReadOnlyList<string> GetFileManagerSelection() => [];
        public int SystemDragThreshold => 4;
        public bool PrefersReducedMotion => false;
        public void SetStartWithSystem(bool enabled, string executablePath) { }
        public void SetContextMenu(bool enabled, string executablePath, string label) { }
        public ModernContextMenuState GetModernContextMenuState(string executablePath) => ModernContextMenuState.Unsupported;
        public Task<ModernContextMenuResult> SetModernContextMenuAsync(bool enabled, string executablePath) =>
            Task.FromResult(new ModernContextMenuResult(false));
        public void RevealInFileManager(string path) { }
        public void OpenFile(string path) { }
        public void RaiseTopmost(nint windowHandle) { }
    }
}
