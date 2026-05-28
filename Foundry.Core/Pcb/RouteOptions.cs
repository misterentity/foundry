namespace Foundry.Core.Pcb;

/// <summary>
/// Knobs for a FreeRouting headless run (v2.4). <see cref="Passes"/> maps to <c>-mp</c> (max optimization
/// passes) and <see cref="Threads"/> to <c>-mt</c> (router threads). Defaults are sane for small boards.
/// </summary>
public sealed record RouteOptions(int Passes = 10, int Threads = 1)
{
    public static RouteOptions Default => new();
}
