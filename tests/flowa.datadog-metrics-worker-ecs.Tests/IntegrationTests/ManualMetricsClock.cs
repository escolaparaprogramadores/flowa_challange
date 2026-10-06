namespace Flowa.DatadogMetrics.Tests;

// A clock that only moves when the test says so: the metrics PeriodicTimer asks for its timer here.
public sealed class ManualMetricsClock : TimeProvider
{
    private ManualMetricsTimer? metricsTimer;

    public TimeSpan MetricsTimerDueTime => metricsTimer!.DueTime;
    public TimeSpan MetricsTimerPeriod => metricsTimer!.Period;

    public override ITimer CreateTimer(TimerCallback timerCallback, object? timerState, TimeSpan dueTime, TimeSpan period) =>
        metricsTimer = new ManualMetricsTimer(timerCallback, timerState, dueTime, period);

    public void TickMetricsTimer() => metricsTimer!.RunMetricsTimerCallback();

    private sealed class ManualMetricsTimer(TimerCallback timerCallback, object? timerState, TimeSpan dueTime, TimeSpan period) : ITimer
    {
        public TimeSpan DueTime { get; private set; } = dueTime;
        public TimeSpan Period { get; private set; } = period;

        public void RunMetricsTimerCallback() => timerCallback(timerState);

        public bool Change(TimeSpan newDueTime, TimeSpan newPeriod)
        {
            DueTime = newDueTime;
            Period = newPeriod;
            return true;
        }

        public void Dispose() { }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
