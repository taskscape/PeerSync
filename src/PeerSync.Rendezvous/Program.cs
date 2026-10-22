using PeerSync.Rendezvous.Hosting;

if (args is ["diagnostics", .. var diagnosticArgs])
    return RendezvousDiagnosticsCommand.Run(diagnosticArgs);
if (args is ["--help"] or ["-h"])
{
    Console.WriteLine("PeerSync Rendezvous service");
    Console.WriteLine("  PeerSync.Rendezvous.exe diagnostics --output <new-zip> [--logs <directory>] [--include-network-addresses]");
    return 0;
}
await RendezvousHost.RunAsync(args);
return 0;
