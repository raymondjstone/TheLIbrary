using Hangfire.Dashboard;

namespace TheLibrary.Server.Services.Scheduling;

// Hangfire's default authorization filter denies non-local requests, which
// breaks dashboard access from any other machine on the LAN. This app is a
// self-hosted, single-user tool on a trusted network, so the dashboard is
// always authorized. Swap this for something stricter if the host ever moves
// to a shared environment.
public sealed class AllowAllDashboardAuthorizationFilter : IDashboardAuthorizationFilter
{
    public bool Authorize(DashboardContext context) => true;
}
