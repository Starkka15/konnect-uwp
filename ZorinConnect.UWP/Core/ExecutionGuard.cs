using System;
using Windows.ApplicationModel.ExtendedExecution;

namespace ZorinConnect.Core
{
    /// <summary>
    /// Holds an unspecified-reason ExtendedExecutionSession so the app keeps running (and its
    /// network listeners stay live) after it loses foreground, instead of being suspended/terminated.
    /// Full background story is T29/T30; this is the always-on keep-alive.
    /// </summary>
    public static class ExecutionGuard
    {
        private static ExtendedExecutionSession _session;

        public static async void Request()
        {
            try
            {
                Clear();
                var session = new ExtendedExecutionSession
                {
                    Reason = ExtendedExecutionReason.Unspecified,
                    Description = "Keep KDE Connect links alive",
                };
                session.Revoked += OnRevoked;
                var result = await session.RequestExtensionAsync();
                StartupTrace.Mark($"exec-guard:{result}");
                if (result == ExtendedExecutionResult.Allowed)
                    _session = session;
                else
                    session.Dispose();
            }
            catch (Exception ex)
            {
                StartupTrace.MarkError("exec-guard", ex);
            }
        }

        private static void OnRevoked(object sender, ExtendedExecutionRevokedEventArgs args)
        {
            StartupTrace.Mark($"exec-guard-revoked:{args.Reason}");
            Clear();
            // Re-request when revoked due to a transient system-policy change (not user termination).
            if (args.Reason == ExtendedExecutionRevokedReason.SystemPolicy)
                Request();
        }

        private static void Clear()
        {
            if (_session != null)
            {
                _session.Revoked -= OnRevoked;
                _session.Dispose();
                _session = null;
            }
        }
    }
}
