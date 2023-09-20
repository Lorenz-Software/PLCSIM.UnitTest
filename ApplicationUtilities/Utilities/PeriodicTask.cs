using System;
using System.Threading;
using System.Threading.Tasks;

namespace ApplicationUtilities.Utilities
{
    public class PeriodicTask
    {
        private TimeSpan period;
        public TimeSpan Period { get => period; set => period = value; }

        private CancellationTokenSource stopToken = new CancellationTokenSource();

        private Task task;

        private bool isRunning = false;
        public bool IsRunning { get => isRunning; }

        public event EventHandler<bool> OnStatusChanged;

        public PeriodicTask(TimeSpan period)
        {
            this.period = period;
        }

        public void Start(Action action)
        {
            stopToken = new CancellationTokenSource();
            task = Task.Run(() => RunAsync(action));
            UpdateStatus(true);
            task.GetAwaiter().OnCompleted(() => UpdateStatus(false));
        }

        public void Stop()
        {
            stopToken.Cancel();
        }

        private async Task RunAsync(Action action)
        {
            while (!stopToken.IsCancellationRequested)
            {
                if (!stopToken.IsCancellationRequested)
                    action();
                await Task.Delay(period, stopToken.Token);
            }
        }

        protected void UpdateStatus(bool value)
        {
            isRunning = value;
            EventHandler<bool> handler = OnStatusChanged;
            handler?.Invoke(this, value);
        }

    }
}
