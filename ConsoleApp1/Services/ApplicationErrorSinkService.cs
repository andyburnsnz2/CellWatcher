using BatteryEMU.Data;
using Microsoft.Extensions.Hosting;

namespace BatteryEMU.Services;

// Drains ApplicationErrorChannel and persists each entry via MariaDbService. Runs as its own
// hosted service (rather than writing inline from DatabaseErrorLogger) so a slow or unreachable
// database never blocks whatever code path just logged the error.
public sealed class ApplicationErrorSinkService(MariaDbService db) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await foreach (var record in ApplicationErrorChannel.Instance.Reader.ReadAllAsync(stoppingToken))
        {
            try
            {
                await db.SaveApplicationErrorAsync(record, stoppingToken);
            }
            catch
            {
                // Deliberately swallowed and not logged: logging a failure here would re-enter
                // the same channel via DatabaseErrorLoggerProvider and loop.
            }
        }
    }
}
