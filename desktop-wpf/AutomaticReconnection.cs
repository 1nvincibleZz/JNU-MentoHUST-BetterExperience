using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

namespace MentoHUST.Desktop
{
    public enum ReconnectionDecision { None, Waiting, WaitingForLink, Retry }

    // Monotonic-time policy, independently testable without touching the real network.
    public sealed class ReconnectionPolicy
    {
        private readonly TimeSpan delay, recoveryGrace;
        private bool armed, internetObserved, physicalLoss, pending;
        private int linkMisses, internetMisses;
        private TimeSpan due, nextAttempt;
        private bool wasLinkDown;
        private bool previousEngineLoss;
        public ReconnectionPolicy(TimeSpan delay, TimeSpan recoveryGrace)
        {
            this.delay = delay; this.recoveryGrace = recoveryGrace;
        }
        public void MarkAuthenticated() { armed = true; }

        public ReconnectionDecision Observe(TimeSpan now, AuthenticationState state, bool available, NetworkObservation network)
        {
            if (!armed) return ReconnectionDecision.None;
            if (network.LinkUp == true && network.Internet == true) internetObserved = true;
            linkMisses = network.LinkUp == false ? linkMisses + 1 : 0;
            internetMisses = network.LinkUp == true && network.Internet == false && internetObserved ? internetMisses + 1 : 0;
            bool engineLost = !available || state != AuthenticationState.Connected;
            bool newlyLostEngine = engineLost && !previousEngineLoss;
            previousEngineLoss = engineLost;
            if (linkMisses >= 3) physicalLoss = true;

            bool healthy = available && state == AuthenticationState.Connected && network.LinkUp == true &&
                (network.Internet == true || (!internetObserved && !physicalLoss));
            if (healthy) {
                pending = false; wasLinkDown = false; physicalLoss = false;
                return ReconnectionDecision.None;
            }
            if (!pending && (engineLost || physicalLoss || internetMisses >= 3)) {
                pending = true;
                // Give the native engine time to recover on its own; never stack START commands.
                due = now + delay + (engineLost && available ? recoveryGrace : TimeSpan.Zero);
            }
            if (!pending) return ReconnectionDecision.None;
            if (newlyLostEngine && available && !physicalLoss) {
                var recoveryDue = now + recoveryGrace + delay;
                if (recoveryDue > due) due = recoveryDue;
            }
            if (network.LinkUp != true) {
                wasLinkDown = true;
                return ReconnectionDecision.WaitingForLink;
            }
            if (wasLinkDown) { due = now + delay; wasLinkDown = false; }
            // Unknown connectivity never triggers a retry by itself.
            bool confirmedLoss = engineLost || physicalLoss || (internetObserved && network.Internet == false);
            if (!confirmedLoss || now < due || now < nextAttempt) return ReconnectionDecision.Waiting;
            nextAttempt = now + recoveryGrace + delay;
            physicalLoss = false; linkMisses = internetMisses = 0;
            return ReconnectionDecision.Retry;
        }
    }

    public sealed class AutomaticReconnection
    {
        private readonly ReconnectionPolicy policy;
        private readonly int pollMilliseconds;
        private CancellationTokenSource cancellation;
        private Task work = Task.FromResult(0);
        public bool IsActive { get { return cancellation != null && !cancellation.IsCancellationRequested; } }
        public bool IsWaiting { get; private set; }
        public AutomaticReconnection(int delaySeconds, int pollMilliseconds = 5000, int graceSeconds = 90)
        {
            policy = new ReconnectionPolicy(TimeSpan.FromSeconds(delaySeconds), TimeSpan.FromSeconds(graceSeconds));
            this.pollMilliseconds = pollMilliseconds;
        }
        public void Start(Func<CancellationToken, Task<NetworkObservation>> probe,
            Func<AuthenticationState> state, Func<bool> available, Func<CancellationToken, Task> retry, Action<string> log, Action changed)
        {
            if (cancellation != null) return;
            policy.MarkAuthenticated(); cancellation = new CancellationTokenSource();
            work = RunAsync(probe, state, available, retry, log, changed, cancellation.Token);
        }
        private async Task RunAsync(Func<CancellationToken, Task<NetworkObservation>> probe,
            Func<AuthenticationState> state, Func<bool> available, Func<CancellationToken, Task> retry,
            Action<string> log, Action changed, CancellationToken token)
        {
            var elapsed = Stopwatch.StartNew(); int attempts = 0; bool probeFailureLogged = false;
            try {
                while (true) {
                    await Task.Delay(pollMilliseconds, token);
                    NetworkObservation observation = new NetworkObservation();
                    try { observation = await probe(token); probeFailureLogged = false; }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception error) {
                        if (!probeFailureLogged) log("暂时无法读取网卡状态，等待下次检查：" + error.Message);
                        probeFailureLogged = true;
                    }
                    token.ThrowIfCancellationRequested();
                    var decision = policy.Observe(elapsed.Elapsed, state(), available(), observation);
                    bool waiting = decision != ReconnectionDecision.None;
                    if (waiting != IsWaiting) {
                        IsWaiting = waiting;
                        log(waiting ? "检测到连接中断，等待网卡恢复并尝试自动重连" : "连接已恢复，继续监测");
                        changed();
                    }
                    if (decision == ReconnectionDecision.Retry) {
                        token.ThrowIfCancellationRequested();
                        log("正在自动重连（第 " + (++attempts) + " 次）");
                        try { await retry(token); }
                        catch (OperationCanceledException) { throw; }
                        catch (Exception error) { log("本次重连未完成：" + error.Message + "；稍后再试"); }
                    }
                }
            } catch (OperationCanceledException) { }
            catch (Exception error) { log("自动重连监测已停止：" + error.Message); cancellation.Cancel(); }
            finally { IsWaiting = false; changed(); }
        }
        public void Cancel() { if (cancellation != null) cancellation.Cancel(); }
        public async Task CancelAndWaitAsync()
        {
            Cancel(); await work;
            if (cancellation != null) { cancellation.Dispose(); cancellation = null; }
        }
    }
}
