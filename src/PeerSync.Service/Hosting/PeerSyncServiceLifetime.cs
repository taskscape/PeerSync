using System.Runtime.Versioning;
using System.ServiceProcess;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Hosting.WindowsServices;
using Microsoft.Extensions.Options;

namespace PeerSync.Service.Hosting;

[SupportedOSPlatform("windows")]
public sealed class PeerSyncServiceLifetime : WindowsServiceLifetime
{
    private readonly PowerEvents power;

    public PeerSyncServiceLifetime(IHostEnvironment environment, IHostApplicationLifetime application,
        ILoggerFactory loggers, IOptions<HostOptions> hostOptions,
        IOptions<WindowsServiceLifetimeOptions> serviceOptions, PowerEvents power)
        : base(environment, application, loggers, hostOptions, serviceOptions)
    {
        CanHandlePowerEvent = true;
        this.power = power;
    }

    protected override bool OnPowerEvent(PowerBroadcastStatus powerStatus)
    {
        switch (powerStatus)
        {
            case PowerBroadcastStatus.Suspend:
                power.RaiseSuspending();
                break;
            case PowerBroadcastStatus.ResumeAutomatic:
            case PowerBroadcastStatus.ResumeSuspend:
                power.RaiseResumed();
                break;
        }
        return true;
    }
}
