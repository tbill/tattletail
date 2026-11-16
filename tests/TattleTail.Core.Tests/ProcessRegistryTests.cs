using TattleTail.Core;
using Xunit;

namespace TattleTail.Core.Tests;

public class ProcessRegistryTests
{
    /// <summary>Stands in for the operating system's list of running processes.</summary>
    private readonly Dictionary<int, ProcessDetails> _running = new();
    private int _lookups;

    private ProcessRegistry Create() => new(pid =>
    {
        _lookups++;
        return _running.GetValueOrDefault(pid);
    });

    [Fact]
    public void SameProcessId_GivesTheSameInstanceAndLooksItUpOnce()
    {
        _running[10] = new ProcessDetails(10, "chrome", @"C:\Apps\chrome.exe");
        var registry = Create();

        var first = registry.GetInstance(10);
        var second = registry.GetInstance(10);

        Assert.Equal(first, second);
        Assert.Equal(@"C:\Apps\chrome.exe", registry.GetDetails(first).Path);
        registry.GetDetails(first);
        Assert.Equal(1, _lookups);
        Assert.False(registry.HasExited(first));
    }

    [Fact]
    public void ReusedProcessId_GetsANewInstanceAndTheOldOneKeepsItsName()
    {
        _running[10] = new ProcessDetails(10, "chrome", @"C:\Apps\chrome.exe");
        var registry = Create();
        var chrome = registry.GetInstance(10);
        registry.GetDetails(chrome);

        registry.ProcessStopped(10);
        _running[10] = new ProcessDetails(10, "notepad", @"C:\Windows\notepad.exe");
        registry.ProcessStarted(10, "notepad.exe");
        var notepad = registry.GetInstance(10);

        Assert.NotEqual(chrome, notepad);
        Assert.Equal("chrome", registry.GetDetails(chrome).Name);
        Assert.True(registry.HasExited(chrome));
        Assert.Equal("notepad", registry.GetDetails(notepad).Name);
        Assert.False(registry.HasExited(notepad));
    }

    [Fact]
    public void StartWithoutAStopFirst_StillRetiresTheOldProgram()
    {
        var registry = Create();
        var old = registry.GetInstance(10);

        registry.ProcessStarted(10, "new.exe");

        Assert.True(registry.HasExited(old));
        Assert.NotEqual(old, registry.GetInstance(10));
    }

    [Fact]
    public void ProgramThatExitedBeforeAnyoneLooked_IsNamedFromItsStartEvent()
    {
        var registry = Create();
        registry.ProcessStarted(77, "curl.exe");
        var curl = registry.GetInstance(77);
        registry.ProcessStopped(77);

        var details = registry.GetDetails(curl);

        Assert.Equal("curl", details.Name);
        Assert.Equal(77, details.ProcessId);
        Assert.Null(details.Path);
        Assert.Equal(0, _lookups); // its process id may already belong to something else
    }

    [Fact]
    public void LastPacketsAfterExit_StillBelongToTheExitedProgram()
    {
        var registry = Create();
        registry.ProcessStarted(77, "curl.exe");
        var curl = registry.GetInstance(77);

        registry.ProcessStopped(77);

        Assert.Equal(curl, registry.GetInstance(77));
    }

    [Fact]
    public void RunningProgram_PrefersTheOperatingSystemsNameAndPath()
    {
        _running[5] = new ProcessDetails(5, "SearchProtocolHost", @"C:\Windows\System32\SearchProtocolHost.exe");
        var registry = Create();
        registry.ProcessStarted(5, "SearchProtocol");

        var details = registry.GetDetails(registry.GetInstance(5));

        Assert.Equal("SearchProtocolHost", details.Name);
        Assert.NotNull(details.Path);
    }

    [Fact]
    public void UnknownAndGone_IsShownAsExited()
    {
        var registry = Create();
        var instance = registry.GetInstance(999);

        Assert.Equal(ProcessRegistry.UnknownName, registry.GetDetails(instance).Name);
        Assert.True(registry.HasExited(instance));
    }

    [Fact]
    public void ProcessSeen_NamesAProgramWithoutRetiringIt()
    {
        var registry = Create();
        var instance = registry.GetInstance(8);

        registry.ProcessSeen(8, @"\Device\HarddiskVolume3\Windows\System32\svchost.exe");

        Assert.Equal(instance, registry.GetInstance(8));
        Assert.Equal("svchost", registry.GetDetails(instance).Name);
    }

    [Fact]
    public void Retain_ForgetsExitedProgramsNobodyUses()
    {
        var registry = Create();
        registry.ProcessStarted(1, "kept.exe");
        registry.ProcessStarted(2, "dropped.exe");
        registry.ProcessStarted(3, "running.exe");
        var kept = registry.GetInstance(1);
        var dropped = registry.GetInstance(2);
        var running = registry.GetInstance(3);
        registry.ProcessStopped(1);
        registry.ProcessStopped(2);

        registry.Retain(new HashSet<ProcessInstance> { kept });

        Assert.Equal("kept", registry.GetDetails(kept).Name);
        Assert.Equal(ProcessRegistry.UnknownName, registry.GetDetails(dropped).Name);
        Assert.NotEqual(dropped, registry.GetInstance(2));
        Assert.Equal(running, registry.GetInstance(3));
    }

    [Fact]
    public void MarkExitedExcept_RetiresMissingProcessIds()
    {
        _running[1] = new ProcessDetails(1, "a", null);
        _running[2] = new ProcessDetails(2, "b", null);
        var registry = Create();
        var a = registry.GetInstance(1);
        var b = registry.GetInstance(2);

        registry.MarkExitedExcept(new HashSet<int> { 1 });

        Assert.False(registry.HasExited(a));
        Assert.True(registry.HasExited(b));
        Assert.NotEqual(b, registry.GetInstance(2));
    }

    [Fact]
    public void IsThreadSafe()
    {
        var registry = Create();
        Parallel.For(0, 20_000, i =>
        {
            var pid = i % 50;
            switch (i % 5)
            {
                case 0: registry.ProcessStarted(pid, "p.exe"); break;
                case 1: registry.ProcessStopped(pid); break;
                case 2: registry.Retain(new HashSet<ProcessInstance>()); break;
                default: registry.GetDetails(registry.GetInstance(pid)); break;
            }
        });
    }
}
