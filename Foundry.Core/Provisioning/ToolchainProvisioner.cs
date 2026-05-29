using Foundry.Core.Cad;
using Foundry.Core.Diagnostics;
using Foundry.Core.Firmware;
using Foundry.Core.Pcb;
using Foundry.Core.Simulation;

namespace Foundry.Core.Toolchain;

/// <summary>Stable id for an optional external tool (used as the registry key and UI key).</summary>
public enum ToolId { ArduinoCli, OpenScad, Renode, FreeRouting, JavaJre, KiCad }

/// <summary>A coarse progress update for the Settings panel. Percent is null when unknown (download
/// size unknown / indeterminate step); the UI shows an indeterminate bar in that case.</summary>
public sealed record ToolProgress(string Stage, int? Percent = null);

/// <summary>Static identity + why-string for a tool (no I/O).</summary>
public sealed record ToolDescriptor(ToolId Id, string Name, string Purpose);

/// <summary>One optional tool's identity + live state for the "Optional tools" panel.</summary>
public sealed record ToolStatus(ToolId Id, string Name, string Purpose, bool Installed, string? Location);

/// <summary>
/// Single entry point the Settings "Optional tools" panel binds to. Wraps the existing on-demand
/// installers (OpenScadInstaller / RenodeInstaller / FreeRoutingInstaller / FirmwareBuilder) plus the
/// new Java JRE and KiCad provisioning behind one locate → IsInstalled → InstallAsync(progress) shape.
/// Installs land in %LocalAppData%/Foundry/tools (portable, no UAC) except KiCad, which is provisioned
/// via winget per-user (fallback: silent NSIS exe). <see cref="GetStatus"/>/<see cref="Snapshot"/> never
/// throw; <see cref="InstallAsync"/> reports stages via <see cref="IProgress{T}"/> + AppLog and throws on failure.
/// </summary>
public static class ToolchainProvisioner
{
    /// <summary>All optional tools in display order, with their one-line purpose.</summary>
    public static IReadOnlyList<ToolDescriptor> Tools { get; } = new[]
    {
        new ToolDescriptor(ToolId.ArduinoCli,  "Arduino CLI",   "Compile and flash generated firmware to your board."),
        new ToolDescriptor(ToolId.OpenScad,    "OpenSCAD",      "Render parametric SCAD enclosures to mesh."),
        new ToolDescriptor(ToolId.Renode,      "Renode",        "Headless firmware simulation (RP2040 etc.)."),
        new ToolDescriptor(ToolId.FreeRouting, "FreeRouting",   "Auto-route the PCB (needs Java)."),
        new ToolDescriptor(ToolId.JavaJre,     "Java (JRE 25)", "Runs the FreeRouting auto-router jar."),
        new ToolDescriptor(ToolId.KiCad,       "KiCad",         "Design and export the PCB and run DRC/fab."),
    };

    /// <summary>Resolved exe/jar/launcher path when installed, else null. Never throws.</summary>
    private static string? Locate(ToolId id)
    {
        try
        {
            return id switch
            {
                ToolId.ArduinoCli  => FirmwareBuilder.Locate(),
                ToolId.OpenScad    => OpenScadInstaller.Locate(),
                ToolId.Renode      => RenodeInstaller.Locate(),
                ToolId.FreeRouting => FreeRoutingInstaller.JarPresent ? FreeRoutingInstaller.JarPath : null,
                ToolId.JavaJre     => FreeRoutingInstaller.LocateJava(),
                ToolId.KiCad       => KiCadInstaller.Locate()?.BinDir,
                _ => null,
            };
        }
        catch { return null; }
    }

    /// <summary>Is this tool already installed/located? Never throws.</summary>
    public static bool IsInstalled(ToolId id) => Locate(id) is not null;

    /// <summary>Current install state for one tool (runs the tool's Locate()). Never throws.</summary>
    public static ToolStatus GetStatus(ToolId id)
    {
        var d = Tools.First(t => t.Id == id);
        var loc = Locate(id);
        return new ToolStatus(d.Id, d.Name, d.Purpose, loc is not null, loc);
    }

    /// <summary>Snapshot of every tool's state for the panel (calls GetStatus for each).</summary>
    public static IReadOnlyList<ToolStatus> Snapshot() => Tools.Select(t => GetStatus(t.Id)).ToList();

    /// <summary>
    /// Install/download the tool on demand. Reports coarse progress via <paramref name="progress"/>
    /// (also written to AppLog). Idempotent: a no-op returning the current status when already installed.
    /// Throws on failure (caller shows the message); never leaves a half-extracted dir on success.
    /// </summary>
    public static async Task<ToolStatus> InstallAsync(
        ToolId id, IProgress<ToolProgress>? progress = null, CancellationToken ct = default)
    {
        if (IsInstalled(id))
        {
            progress?.Report(new ToolProgress("Installed"));
            return GetStatus(id);
        }

        switch (id)
        {
            case ToolId.ArduinoCli:
                progress?.Report(new ToolProgress("Downloading…"));
                await FirmwareBuilder.DownloadCliAsync(ct);
                break;
            case ToolId.OpenScad:
                progress?.Report(new ToolProgress("Downloading…"));
                await OpenScadInstaller.DownloadAsync(ct);
                break;
            case ToolId.Renode:
                progress?.Report(new ToolProgress("Downloading…"));
                await RenodeInstaller.DownloadAsync(ct);
                break;
            case ToolId.FreeRouting:
                progress?.Report(new ToolProgress("Downloading…"));
                await FreeRoutingInstaller.DownloadJarAsync(ct);
                break;
            case ToolId.JavaJre:
                progress?.Report(new ToolProgress("Downloading…"));
                await FreeRoutingInstaller.DownloadJreAsync(ct);
                break;
            case ToolId.KiCad:
                await KiCadInstaller.InstallAsync(
                    new Progress<string>(s => progress?.Report(new ToolProgress(s))), ct);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(id), id, "Unknown tool.");
        }

        progress?.Report(new ToolProgress("Verifying…"));
        var status = GetStatus(id);
        if (!status.Installed)
            throw new InvalidOperationException($"{status.Name} not found after install.");
        progress?.Report(new ToolProgress("Installed"));
        AppLog.Info("provision", $"{status.Name} installed ({status.Location}).");
        return status;
    }
}
