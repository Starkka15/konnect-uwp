using System;
using System.Linq;
using System.Threading.Tasks;
using Windows.ApplicationModel.Background;
using ZorinConnect.Core;

namespace ZorinConnect.Core
{
    /// <summary>
    /// Always-on infrastructure (SPEC T29/T30). Registers an in-process SocketActivityTrigger
    /// background task so the OS socket broker wakes us on incoming network activity even in
    /// connected-standby (the "Outlook stays connected" mechanism). The transferred sockets are
    /// the UDP discovery socket + TCP listener; on wake we reclaim + process.
    /// </summary>
    public static class BackgroundManager
    {
        public const string SocketTaskName = "ZorinConnect.SocketActivity";

        public static Guid SocketTaskId { get; private set; }
        public static bool Registered { get; private set; }

        public static async Task EnsureRegisteredAsync()
        {
            try
            {
                // Lock-screen / background access — required for reliable background execution.
                var status = await BackgroundExecutionManager.RequestAccessAsync();
                StartupTrace.Mark($"bg-access:{status}");

                var existing = BackgroundTaskRegistration.AllTasks.Values
                    .FirstOrDefault(t => t.Name == SocketTaskName);
                if (existing != null)
                {
                    SocketTaskId = existing.TaskId;
                    Registered = true;
                    StartupTrace.Mark($"bg-task-existing:{SocketTaskId}");
                    return;
                }

                var builder = new BackgroundTaskBuilder { Name = SocketTaskName };
                builder.SetTrigger(new SocketActivityTrigger());
                // In-process task: no SetTaskEntryPoint -> handled in App.OnBackgroundActivated.
                var reg = builder.Register();
                SocketTaskId = reg.TaskId;
                Registered = true;
                StartupTrace.Mark($"bg-task-registered:{SocketTaskId}");
            }
            catch (Exception e)
            {
                StartupTrace.MarkError("bg-register", e);
            }
        }
    }
}
