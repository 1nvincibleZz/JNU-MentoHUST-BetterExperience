using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.NetworkInformation;
using System.Threading;
using System.Threading.Tasks;
using MentoHUST.Desktop;

public static class ReconnectionChecks
{
    private static void Require(bool value, string message) { if (!value) throw new Exception(message); }
    private static NetworkObservation Network(bool? link, bool? internet)
    { return new NetworkObservation { LinkUp = link, Internet = internet }; }
    private static ReconnectionDecision Tick(ReconnectionPolicy policy, int seconds, bool? link, bool? internet,
        AuthenticationState state = AuthenticationState.Connected, bool available = true)
    { return policy.Observe(TimeSpan.FromSeconds(seconds), state, available, Network(link, internet)); }
    private static ReconnectionPolicy Armed()
    {
        var policy = new ReconnectionPolicy(TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(90));
        policy.MarkAuthenticated(); return policy;
    }
    public static int Main(string[] args)
    {
        try {
            if (args.Length == 1 && args[0] == "--probe") {
                foreach (var nic in NetworkInterface.GetAllNetworkInterfaces().Where(n => n.OperationalStatus == OperationalStatus.Up)) {
                    var result = WindowsNetworkStatus.Read(nic.Id);
                    Console.WriteLine("READ_ONLY_PROBE: " + nic.Name + "; Link=" + result.LinkUp + "; Internet=" + result.Internet);
                }
                return 0;
            }
            PolicyChecks(); AsyncChecks().GetAwaiter().GetResult();
            Console.WriteLine("RECONNECTION_CHECKS_PASS: unarmed; native grace; debounce; adapter link; unknown/NCSI baseline; retry spacing; recovery; serialized retries; cancellation before/during retry.");
            return 0;
        } catch (Exception error) { Console.Error.WriteLine(error); return 1; }
    }
    private static void PolicyChecks()
    {
        var unarmed = new ReconnectionPolicy(TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(90));
        Require(Tick(unarmed, 500, true, false, AuthenticationState.Failed) == ReconnectionDecision.None, "First failed login must never be auto retried");
        var p = Armed(); Tick(p, 0, true, true);
        Require(Tick(p, 5, true, false) == ReconnectionDecision.None && Tick(p, 10, true, false) == ReconnectionDecision.None,
            "Two NCSI misses must not trigger a retry");
        Require(Tick(p, 15, true, true) == ReconnectionDecision.None, "Transient loss recovered");
        Tick(p, 20, true, false); Tick(p, 25, true, false);
        Require(Tick(p, 30, true, false) == ReconnectionDecision.Waiting, "Third miss begins wait");
        Require(Tick(p, 59, true, false) == ReconnectionDecision.Waiting && Tick(p, 60, true, false) == ReconnectionDecision.Retry,
            "Retry exactly after configured delay");
        Require(Tick(p, 61, true, false) == ReconnectionDecision.Waiting && Tick(p, 179, true, false) == ReconnectionDecision.Waiting &&
            Tick(p, 180, true, false) == ReconnectionDecision.Retry, "Retry commands must be spaced by native recovery grace plus delay");
        Require(Tick(p, 185, true, true) == ReconnectionDecision.None, "Internet recovery cancels pending retry");

        p = Armed(); Tick(p, 0, true, null);
        Tick(p, 5, false, null); Tick(p, 10, false, null);
        Require(Tick(p, 15, false, null) == ReconnectionDecision.WaitingForLink && Tick(p, 500, false, null) == ReconnectionDecision.WaitingForLink,
            "Never retry while unplugged");
        Require(Tick(p, 505, true, null) == ReconnectionDecision.Waiting && Tick(p, 534, true, null) == ReconnectionDecision.Waiting &&
            Tick(p, 535, true, null) == ReconnectionDecision.Retry, "A cable restoration waits before retry even if stale engine still says Connected");
        Require(Tick(p, 540, true, null) == ReconnectionDecision.None, "Successful retry without NCSI reports no ongoing physical loss");

        p = Armed();
        for (int n = 0; n < 10; n++) Require(Tick(p, n * 500, true, false) == ReconnectionDecision.None, "NCSI which never reported Internet must not cause an infinite loop");
        Tick(p, 5005, true, true); Tick(p, 5010, true, false); Tick(p, 5015, true, false); Tick(p, 5020, true, false);
        Require(Tick(p, 6000, true, null) == ReconnectionDecision.Waiting, "Unknown Internet must not itself trigger pending retry");
        Require(Tick(p, 6005, null, null) == ReconnectionDecision.WaitingForLink, "Unknown adapter status must not start packets");
        Require(Tick(p, 6010, true, true) == ReconnectionDecision.None, "Known recovery resets loss");

        p = Armed(); Tick(p, 0, true, true);
        Require(Tick(p, 5, true, true, AuthenticationState.Starting) == ReconnectionDecision.Waiting &&
            Tick(p, 124, true, true, AuthenticationState.Starting) == ReconnectionDecision.Waiting,
            "Native reconnect has recovery grace");
        Require(Tick(p, 125, true, true, AuthenticationState.Starting) == ReconnectionDecision.Retry, "Stuck native recovery is restarted once");
        Require(Tick(p, 130, true, true) == ReconnectionDecision.None, "Native success cancels retry");
        p = Armed(); Tick(p, 0, true, true);
        Tick(p, 5, true, false); Tick(p, 10, true, false); Tick(p, 15, true, false);
        Require(Tick(p, 20, true, false, AuthenticationState.Starting) == ReconnectionDecision.Waiting &&
            Tick(p, 45, true, false, AuthenticationState.Starting) == ReconnectionDecision.Waiting,
            "A native recovery beginning after NCSI loss receives grace");
        Require(Tick(p, 50, true, true) == ReconnectionDecision.None, "Core recovery wins without additional START");
        p = Armed(); Tick(p, 0, true, true);
        Tick(p, 5, true, null, AuthenticationState.Failed, false);
        Require(Tick(p, 35, true, null, AuthenticationState.Failed, false) == ReconnectionDecision.Retry, "A lost backend can be recreated after wait");
    }
    private static async Task AsyncChecks()
    {
        int probes = 0, starts = 0, simultaneous = 0, maximum = 0;
        var monitor = new AutomaticReconnection(0, 5, 0);
        var retryEntered = new TaskCompletionSource<bool>();
        monitor.Start(token => { probes++; return Task.FromResult(Network(true, null)); }, () => AuthenticationState.Failed, () => true,
            async token => {
                starts++; maximum = Math.Max(maximum, ++simultaneous); retryEntered.TrySetResult(true);
                try { await Task.Delay(10000, token); } finally { simultaneous--; }
            }, message => { }, () => { });
        await Bounded(retryEntered.Task);
        await monitor.CancelAndWaitAsync();
        Require(starts == 1 && maximum == 1 && simultaneous == 0 && !monitor.IsActive, "Cancel during retry must wait for its single in-flight command");
        await Task.Delay(25); Require(starts == 1, "No retry after user disconnect");
        monitor = new AutomaticReconnection(0, 5, 0);
        var probeEntered = new TaskCompletionSource<bool>();
        monitor.Start(async token => { probeEntered.TrySetResult(true); await Task.Delay(10000, token); return Network(true, false); },
            () => AuthenticationState.Failed, () => true, token => { starts++; return Task.FromResult(0); }, message => { }, () => { });
        await Bounded(probeEntered.Task); await monitor.CancelAndWaitAsync();
        Require(starts == 1 && !monitor.IsActive, "Cancel while probing prevents START");

        monitor = new AutomaticReconnection(0, 5, 0); int failures = 0;
        var twice = new TaskCompletionSource<bool>();
        monitor.Start(token => Task.FromResult(Network(true, null)), () => AuthenticationState.Failed, () => false,
            async token => {
                maximum = Math.Max(maximum, ++simultaneous);
                try { await Task.Delay(15, token); if (++failures == 2) twice.TrySetResult(true); throw new InvalidOperationException("fake backend refused"); }
                finally { simultaneous--; }
            }, message => { }, () => { });
        await Bounded(twice.Task); await monitor.CancelAndWaitAsync();
        Require(failures == 2 && maximum == 1 && simultaneous == 0, "Failure retries remain serialized and cancellation stops them");
        monitor = new AutomaticReconnection(30, 5); probes = 0;
        monitor.Start(token => { probes++; return Task.FromResult(Network(true, true)); }, () => AuthenticationState.Connected, () => true,
            token => { starts++; return Task.FromResult(0); }, message => { }, () => { });
        await Task.Delay(30); await monitor.CancelAndWaitAsync();
        Require(probes > 0 && starts == 1, "Healthy monitor never reconnects");
        monitor = new AutomaticReconnection(0, 5, 0); probes = 0;
        var healthyProbe = new TaskCompletionSource<bool>();
        monitor.Start(token => {
            if (++probes <= 2) throw new InvalidOperationException("temporary NLM read error");
            healthyProbe.TrySetResult(true);
            return Task.FromResult(Network(true, true));
        }, () => AuthenticationState.Connected, () => true,
            token => { starts++; return Task.FromResult(0); }, message => { }, () => { });
        await Bounded(healthyProbe.Task);
        Require(probes > 2 && monitor.IsActive && starts == 1, "A temporary probe error must keep monitoring without causing START");
        await monitor.CancelAndWaitAsync();
    }
    private static async Task Bounded(Task task)
    { if (await Task.WhenAny(task, Task.Delay(2000)) != task) throw new Exception("Mock test timed out"); await task; }
}
