using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Foundry.Core.Config;
using Foundry.Core.Export;
using Foundry.Core.Firmware;
using Foundry.Core.Project;
using Foundry.Core.Simulation;
using Foundry.Core.Sourcing;
using Foundry.Core.Validation;
using Microsoft.Win32;

namespace Foundry.App.ViewModels;

// ---------------- Wiring · PCB / fab pipeline (Track B) — split from WiringViewModel.cs ----------------
public sealed partial class WiringViewModel : TabViewModelBase
{
    /// <summary>
    /// Build a <c>.kicad_pcb</c> from the netlist (footprints + grid placement + ratsnest) and reveal it.
    /// Degrades gracefully when KiCad isn't installed — surfaces install guidance, never throws (PRD Track B v2.2).
    /// </summary>
    [RelayCommand]
    private async Task ExportPcb()
    {
        if (IsExportingPcb) return;
        IsExportingPcb = true;
        _pcbCts?.Dispose();
        _pcbCts = new CancellationTokenSource();
        var ct = _pcbCts.Token;
        PcbNotes.Clear();
        PcbSeverity = "info"; PcbStatus = "Building the PCB from the netlist…";
        try
        {
            var dir = ConfigStore.Load().OutputFolder;
            Directory.CreateDirectory(dir);
            var result = await Foundry.Core.Pcb.PcbBuilder.BuildAsync(Project, dir, ct: ct);

            foreach (var n in result.Notes) PcbNotes.Add(n);

            if (!result.Installed)
            {
                PcbSeverity = "info";
                PcbStatus = $"KiCad isn't installed — install it from {Foundry.Core.Pcb.KiCadInstaller.DownloadUrl} to export a PCB.";
                return;
            }

            PcbSeverity = result.Ok ? "pass" : "fail";
            PcbStatus = result.Summary;
            if (result.Ok && result.KicadPcbPath is not null)
            {
                LastPcbPath = result.KicadPcbPath;
                ConnectivityVerified = true;   // result.Ok ⇒ build_board mapped every net pin to a real pad (no unmapped/ordinal-guess)
                Foundry.Core.Diagnostics.AppLog.Info("export", $"KiCad PCB → {result.KicadPcbPath}");
                // Continue straight into routing — copper tracks on the placed board (v2.4).
                await RouteCore(result.KicadPcbPath, ct);
            }
        }
        catch (OperationCanceledException) { PcbSeverity = "info"; PcbStatus = "Cancelled."; }
        catch (Exception ex) { PcbSeverity = "fail"; PcbStatus = $"PCB export failed: {ex.Message}"; }
        finally { IsExportingPcb = false; _pcbCts?.Dispose(); _pcbCts = null; }
    }

    /// <summary>
    /// Route the last-built <c>.kicad_pcb</c> with FreeRouting, writing a <c>.routed.kicad_pcb</c> beside it.
    /// Degrades gracefully when KiCad / Java (JRE 21+) / the FreeRouting jar are missing — surfaces install
    /// guidance, downloads the single jar on demand, and never throws (PRD Track B v2.4).
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanRoutePcb))]
    private async Task RoutePcb()
    {
        if (IsExportingPcb || string.IsNullOrEmpty(LastPcbPath)) return;
        IsExportingPcb = true;
        _pcbCts?.Dispose();
        _pcbCts = new CancellationTokenSource();
        PcbNotes.Clear();
        try { await RouteCore(LastPcbPath, _pcbCts.Token); }
        catch (OperationCanceledException) { PcbSeverity = "info"; PcbStatus = "Cancelled."; }
        catch (Exception ex) { PcbSeverity = "fail"; PcbStatus = $"PCB routing failed: {ex.Message}"; }
        finally { IsExportingPcb = false; _pcbCts?.Dispose(); _pcbCts = null; }
    }

    /// <summary>Shared routing step used by both EXPORT+ROUTE and the standalone ROUTE affordance.</summary>
    private async Task RouteCore(string pcbPath, CancellationToken ct)
    {
        // Java is locate-only; the FreeRouting jar can be fetched on demand (~58 MB, one-time).
        if (Foundry.Core.Pcb.FreeRoutingInstaller.LocateJava() is not null
            && !Foundry.Core.Pcb.FreeRoutingInstaller.JarPresent)
        {
            PcbSeverity = "info"; PcbStatus = "Downloading FreeRouting (~58 MB, one-time)…";
            try { await Foundry.Core.Pcb.FreeRoutingInstaller.DownloadJarAsync(); }
            catch (Exception ex) { PcbSeverity = "fail"; PcbStatus = $"FreeRouting download failed: {ex.Message}"; return; }
        }

        PcbSeverity = "info"; PcbStatus = "Routing the PCB with FreeRouting…";
        var route = await Foundry.Core.Pcb.PcbRouter.RouteAsync(pcbPath, ct: ct);

        foreach (var n in route.Notes) PcbNotes.Add(n);

        if (!route.Installed)
        {
            PcbSeverity = "info";
            PcbStatus = route.Summary;  // KiCad + Java (JRE 21+) + jar install guidance
            return;
        }

        PcbSeverity = route.Ok ? "pass" : "fail";
        PcbStatus = route.Summary;
        if (route.Ok && route.RoutedPcbPath is not null)
        {
            Foundry.Core.Diagnostics.AppLog.Info("export", $"routed PCB → {route.RoutedPcbPath}");
            // v2.5: gate the routed board with DRC before revealing it.
            await DrcCore(route.RoutedPcbPath, ct);
            Process.Start(new ProcessStartInfo { FileName = route.RoutedPcbPath, UseShellExecute = true });
        }
    }

    /// <summary>
    /// Run the v2.5 DRC gate on a routed board and surface the verdict in the PCB status block: PASS (green)
    /// or N violations (severity-colored). Degrades to clear install guidance when KiCad is absent. Never throws.
    /// </summary>
    private async Task DrcCore(string boardPath, CancellationToken ct)
    {
        PcbSeverity = "info"; PcbStatus = "Running DRC on the routed board…";
        var report = await Foundry.Core.Pcb.PcbDrc.CheckAsync(boardPath, ct: ct);

        foreach (var n in report.Notes) PcbNotes.Add(n);

        if (!report.Installed)
        {
            LastDrcClean = false;
            PcbSeverity = "info";
            PcbStatus = report.Summary;  // "DRC needs KiCad — install it from … to run kicad-cli pcb drc."
            return;
        }

        if (report.Clean)
        {
            LastDrcClean = true;
            PcbSeverity = "pass";
            PcbStatus = $"DRC PASS — {report.Summary}";
        }
        else
        {
            LastDrcClean = false;
            // Errors fail the gate; warning-only with no unconnected nets is a softer "warn".
            PcbSeverity = report.ErrorCount > 0 || report.UnconnectedCount > 0 ? "fail" : "warn";
            PcbStatus = report.Summary;
            // List the top few violations so the user sees what to fix.
            foreach (var v in report.Violations.Where(v => !v.Excluded).Take(8))
            {
                var where = v.Location is { } p ? $" @ ({p.X:0.##}, {p.Y:0.##})" : "";
                PcbNotes.Add($"· [{v.Severity}] {v.Type}: {v.Description}{where}");
            }
            foreach (var u in report.Unconnected.Where(u => !u.Excluded).Take(4))
                PcbNotes.Add($"· [unconnected] {u.Description}");
        }
        Foundry.Core.Diagnostics.AppLog.Info("export", $"DRC {(report.Clean ? "pass" : "fail")} → {boardPath}");
    }

    /// <summary>
    /// Run the full v2.5 build→route→DRC fix loop (<see cref="Foundry.Core.Pcb.PcbDesigner"/>): the
    /// deterministic gate plus the bounded AI/clearance remediation loop. Reports the final verdict and
    /// per-iteration progress ("iteration 2/3: …"). Uses the stored Anthropic key for AI placement advice when
    /// present (geometry stays deterministic). Degrades to install guidance when KiCad is absent; never throws.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanDesignPcb))]
    private async Task DesignPcb()
    {
        if (IsExportingPcb) return;
        IsExportingPcb = true;
        _pcbCts?.Dispose();
        _pcbCts = new CancellationTokenSource();
        var ct = _pcbCts.Token;
        PcbNotes.Clear();
        PcbSeverity = "info"; PcbStatus = "Designing the PCB — build → route → DRC fix loop…";
        try
        {
            var dir = ConfigStore.Load().OutputFolder;
            Directory.CreateDirectory(dir);

            // AI placement advice is opt-in: use the stored key when present (the placer/router own geometry).
            var key = new Foundry.Core.Security.CredentialStore().Read(Foundry.Core.Security.CredentialStore.AnthropicTarget);
            Foundry.Core.Ai.IAnthropicClient? ai =
                string.IsNullOrWhiteSpace(key) ? null : new Foundry.Core.Ai.AnthropicClient(key!);
            var model = ConfigStore.Load().ModelId;
            var options = Foundry.Core.Pcb.DrcOptions.Default;

            var result = await Foundry.Core.Pcb.PcbDesigner.DesignAsync(Project, dir, ai, model, options, ct);

            // Per-iteration progress: each Core trace line starts "attempt N: …"; re-badge it as
            // "iteration N/M:" for the UI (drop the redundant "attempt N:" prefix) so the error-count
            // delta the loop emits — e.g. "19 → 4 errors" — reads cleanly.
            int n = 0;
            foreach (var line in result.Trace)
            {
                var body = line.StartsWith("attempt ", StringComparison.Ordinal) && line.IndexOf(": ", StringComparison.Ordinal) is var i && i > 0
                    ? line[(i + 2)..]
                    : line;
                PcbNotes.Add($"iteration {++n}/{options.MaxIterations}: {body}");
            }
            foreach (var note in result.Notes) PcbNotes.Add(note);

            if (!result.Installed)
            {
                PcbSeverity = "info";
                PcbStatus = $"KiCad isn't installed — install it from {Foundry.Core.Pcb.KiCadInstaller.DownloadUrl} to run the DRC fix loop.";
                return;
            }

            PcbSeverity = result.Ok ? "pass" : "fail";
            // Final verdict spelled out: PASS, or the explicit N errors / N unrouted breakdown of the best board.
            var verdict = result.Report is { } rpt && !rpt.Clean
                ? $"DRC FAIL — {rpt.ErrorCount} error(s)"
                  + (rpt.UnconnectedCount > 0 ? $", {rpt.UnconnectedCount} net(s) unrouted" : "")
                  + $" after {result.Iterations} iteration(s)."
                : result.Summary;
            PcbStatus = result.Ok ? $"DRC PASS — {result.Summary}" : verdict;
            if (result.KicadPcbPath is not null)
            {
                LastPcbPath = result.KicadPcbPath;   // resets the gate flags; set them from the verdict below
                ConnectivityVerified = result.Ok;    // the loop returns Ok only when it never hit an unmapped pin
                LastDrcClean = result.Ok && result.Report?.Clean == true;
                Foundry.Core.Diagnostics.AppLog.Info("export", $"PCB design {(result.Ok ? "passed" : "best-effort")} after {result.Iterations} iter → {result.KicadPcbPath}");
                Process.Start(new ProcessStartInfo { FileName = result.KicadPcbPath, UseShellExecute = true });
            }
        }
        catch (OperationCanceledException) { PcbSeverity = "info"; PcbStatus = "Cancelled."; }
        catch (Exception ex) { PcbSeverity = "fail"; PcbStatus = $"PCB design failed: {ex.Message}"; }
        finally { IsExportingPcb = false; _pcbCts?.Dispose(); _pcbCts = null; }
    }

    /// <summary>
    /// v2.6 capstone: export the standard 2-layer fab file set (Gerbers + Excellon drill) from the last
    /// DRC-clean board and bundle it into a single <c>&lt;name&gt;-fab.zip</c> a board house (JLCPCB/PCBWay)
    /// accepts as-is. Reveals the ZIP and reports the file set on success. Degrades to clear install guidance
    /// when KiCad is absent (mirrors the not-installed UX). Never throws.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanExportFab))]
    private async Task ExportFab()
    {
        if (IsExportingPcb || string.IsNullOrEmpty(LastPcbPath)) return;
        IsExportingPcb = true;
        _pcbCts?.Dispose();
        _pcbCts = new CancellationTokenSource();
        PcbNotes.Clear();
        try { await ExportFabCore(LastPcbPath, _pcbCts.Token); }
        catch (OperationCanceledException) { PcbSeverity = "info"; PcbStatus = "Cancelled."; }
        catch (Exception ex) { PcbSeverity = "fail"; PcbStatus = $"Fab export failed: {ex.Message}"; }
        finally { IsExportingPcb = false; _pcbCts?.Dispose(); _pcbCts = null; }
    }

    /// <summary>Shared fab-export step used by EXPORT GERBERS and the one-shot DESIGN + GERBERS path.</summary>
    private async Task ExportFabCore(string boardPath, CancellationToken ct)
    {
        // Defense in depth (the command gate already requires this): never package an unverified board.
        if (!ConnectivityVerified || !LastDrcClean)
        {
            PcbSeverity = "fail";
            PcbStatus = "Refusing to export fab files — run DESIGN/DRC and get a clean, fully-connected board first.";
            return;
        }
        PcbSeverity = "info"; PcbStatus = "Exporting Gerbers + drill and packaging the fab ZIP…";
        var dir = ConfigStore.Load().OutputFolder;
        Directory.CreateDirectory(dir);

        var fab = await Foundry.Core.Pcb.Fab.GerberExporter.ExportAsync(boardPath, dir, ct: ct);

        foreach (var n in fab.Notes) PcbNotes.Add(n);

        if (!fab.Installed)
        {
            PcbSeverity = "info";
            PcbStatus = fab.Summary;  // "Fab export needs KiCad — install it from … to run kicad-cli pcb export."
            return;
        }

        PcbSeverity = fab.Ok ? "pass" : "fail";
        PcbStatus = fab.Summary;
        if (fab.Ok && fab.ZipPath is not null)
        {
            LastFabZipPath = fab.ZipPath;
            foreach (var f in fab.Files) PcbNotes.Add($"· {f}");
            Foundry.Core.Diagnostics.AppLog.Info("export", $"fab ZIP → {fab.ZipPath}");
            Process.Start(new ProcessStartInfo { FileName = dir, UseShellExecute = true });
        }
    }

    /// <summary>
    /// The v2.6 one-shot path: build → place → route → DRC-fix loop → Gerbers + drill ZIP in a single action
    /// (<see cref="Foundry.Core.Pcb.PcbDesigner.DesignAndExportFabAsync"/>). Reports per-iteration progress, the
    /// DRC verdict, then the fab package. Degrades to install guidance when KiCad is absent; never throws.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanDesignPcb))]
    private async Task DesignAndExportFab()
    {
        if (IsExportingPcb) return;
        IsExportingPcb = true;
        _pcbCts?.Dispose();
        _pcbCts = new CancellationTokenSource();
        var ct = _pcbCts.Token;
        PcbNotes.Clear();
        PcbSeverity = "info"; PcbStatus = "Designing the PCB and exporting fab files — build → route → DRC → Gerbers…";
        try
        {
            var dir = ConfigStore.Load().OutputFolder;
            Directory.CreateDirectory(dir);

            var key = new Foundry.Core.Security.CredentialStore().Read(Foundry.Core.Security.CredentialStore.AnthropicTarget);
            Foundry.Core.Ai.IAnthropicClient? ai =
                string.IsNullOrWhiteSpace(key) ? null : new Foundry.Core.Ai.AnthropicClient(key!);
            var model = ConfigStore.Load().ModelId;
            var options = Foundry.Core.Pcb.DrcOptions.Default;

            var (design, fab) = await Foundry.Core.Pcb.PcbDesigner.DesignAndExportFabAsync(Project, dir, ai, model, options, ct: ct);

            int n = 0;
            foreach (var line in design.Trace)
                PcbNotes.Add($"iteration {++n}/{options.MaxIterations}: {line}");
            foreach (var note in design.Notes) PcbNotes.Add(note);

            if (!design.Installed)
            {
                PcbSeverity = "info";
                PcbStatus = $"KiCad isn't installed — install it from {Foundry.Core.Pcb.KiCadInstaller.DownloadUrl} to design + export fab files.";
                return;
            }

            if (design.KicadPcbPath is not null)
            {
                LastPcbPath = design.KicadPcbPath;   // resets gate flags; set them from the verdict
                ConnectivityVerified = design.Ok;
                LastDrcClean = design.Ok && design.Report?.Clean == true;
            }

            // The DRC gate must pass before there's a board worth fabbing.
            if (!design.Ok)
            {
                PcbSeverity = "fail";
                PcbStatus = design.Summary;
                return;
            }

            foreach (var fn in fab.Notes) PcbNotes.Add(fn);
            PcbSeverity = fab.Ok ? "pass" : "fail";
            PcbStatus = fab.Summary;
            if (fab.Ok && fab.ZipPath is not null)
            {
                LastFabZipPath = fab.ZipPath;
                foreach (var f in fab.Files) PcbNotes.Add($"· {f}");
                Foundry.Core.Diagnostics.AppLog.Info("export", $"design + fab ZIP after {design.Iterations} iter → {fab.ZipPath}");
                Process.Start(new ProcessStartInfo { FileName = dir, UseShellExecute = true });
            }
        }
        catch (OperationCanceledException) { PcbSeverity = "info"; PcbStatus = "Cancelled."; }
        catch (Exception ex) { PcbSeverity = "fail"; PcbStatus = $"Design + fab export failed: {ex.Message}"; }
        finally { IsExportingPcb = false; _pcbCts?.Dispose(); _pcbCts = null; }
    }

    /// <summary>Reveal the last-produced fab ZIP in Explorer (selects the file).</summary>
    [RelayCommand(CanExecute = nameof(HasFabZip))]
    private void RevealFabZip()
    {
        if (string.IsNullOrEmpty(LastFabZipPath) || !File.Exists(LastFabZipPath)) return;
        try { Process.Start(new ProcessStartInfo { FileName = "explorer.exe", Arguments = $"/select,\"{LastFabZipPath}\"", UseShellExecute = true }); }
        catch (Exception ex) { Status = $"Couldn't reveal the fab ZIP: {ex.Message}"; }
    }

    partial void OnLastFabZipPathChanged(string? value)
    {
        RevealFabZipCommand.NotifyCanExecuteChanged();
        QuoteFabCommand.NotifyCanExecuteChanged();
        PlaceFabOrderCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(CanOrderFab));
    }

    // ---- Track B v2.7: get a quote / prepare an order with a board house ----
    // Opt-in, user-keyed, explicit-confirm. Quoting and order-prep NEVER submit or pay — PLACE ORDER only
    // opens the fab's upload page (http(s) via OpenUrl) with the params copied and the ZIP ready; the user
    // finishes on the fab's site. Mirrors the PCB status block's not-installed / degrade-gracefully UX.
    [ObservableProperty] private bool _isFabbing;
    [ObservableProperty] private string _fabOrderStatus = "";
    [ObservableProperty] private string _fabQuoteText = "";
    public ObservableCollection<string> FabOrderNotes { get; } = new();
    public bool HasFabOrderStatus => !string.IsNullOrEmpty(FabOrderStatus);
    partial void OnFabOrderStatusChanged(string value) => OnPropertyChanged(nameof(HasFabOrderStatus));

    /// <summary>Which house we'll quote/order with (e.g. "PCBWAY · live quotes" or "offline · estimate + handoff").</summary>
    public string FabProviderLabel
    {
        get
        {
            var svc = Foundry.Core.Pcb.Fab.FabService.Shared;
            return svc.IsLive ? $"{svc.ProviderName} · live quotes"
                 : svc.NeedsApiKey ? $"{svc.ProviderName} · estimate + handoff"
                 : "no API key — estimate + assisted handoff (add a key in Settings)";
        }
    }

    public bool CanOrderFab => !IsFabbing && HasFabZip;

    partial void OnIsFabbingChanged(bool value)
    {
        OnPropertyChanged(nameof(CanOrderFab));
        QuoteFabCommand.NotifyCanExecuteChanged();
        PlaceFabOrderCommand.NotifyCanExecuteChanged();
    }

    /// <summary>Fetch a price/lead-time quote for the last fab ZIP (clearly labelled Estimate vs Live). Never orders.</summary>
    [RelayCommand(CanExecute = nameof(CanOrderFab))]
    private async Task QuoteFab()
    {
        if (IsFabbing || string.IsNullOrEmpty(LastFabZipPath)) return;
        IsFabbing = true;
        FabOrderNotes.Clear();
        FabOrderStatus = $"Getting a quote from {Foundry.Core.Pcb.Fab.FabService.Shared.ProviderName}…";
        FabQuoteText = "";
        try
        {
            var svc = Foundry.Core.Pcb.Fab.FabService.Shared;
            var spec = Foundry.Core.Pcb.Fab.FabService.BuildSpec(LastFabZipPath, LastPcbPath);
            var quote = await svc.QuoteAsync(spec);
            var tag = quote.Source == Foundry.Core.Pcb.Fab.FabQuoteSource.Live ? "LIVE" : "ESTIMATE";
            var price = quote.Price is { } p ? $"{p:0.00} {quote.Currency}" : "price n/a";
            var lead = quote.LeadTimeDays is { } d ? $" · ~{d} day lead" : "";
            FabQuoteText = $"[{tag}] {quote.Provider} · {price}{lead}";
            FabOrderStatus = quote.Summary;
            foreach (var n in quote.Notes) FabOrderNotes.Add($"· {n}");
            Foundry.Core.Diagnostics.AppLog.Info("fab", $"quote ({tag}) · {FabQuoteText}");
        }
        catch (Exception ex) { FabOrderStatus = $"Quote failed: {ex.Message}"; }
        finally { IsFabbing = false; }
    }

    /// <summary>
    /// Explicit-confirm order prep: prepares an assisted handoff (never auto-submits), copies the order params,
    /// reveals the ZIP, and opens the fab's upload page so the user finishes the order themselves on the fab's site.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanOrderFab))]
    private async Task PlaceFabOrder()
    {
        if (IsFabbing || string.IsNullOrEmpty(LastFabZipPath)) return;

        var confirm = System.Windows.MessageBox.Show(
            "This board is a DESIGN AID, not a verified manufacturable spec — open the Gerbers in a viewer and " +
            "check footprints, pin assignments and clearances before you spend money on it.\n\n" +
            "Foundry will open the board house's order page in your browser with the order details copied to your " +
            "clipboard and the fab ZIP ready to upload.\n\nFoundry does NOT submit the order and does NOT pay — you " +
            "review the price and place the order yourself on the fab's site.\n\nContinue?",
            "Foundry — prepare PCB order",
            System.Windows.MessageBoxButton.OKCancel, System.Windows.MessageBoxImage.Warning);
        if (confirm != System.Windows.MessageBoxResult.OK) return;

        IsFabbing = true;
        FabOrderNotes.Clear();
        FabOrderStatus = "Preparing the order handoff…";
        try
        {
            var svc = Foundry.Core.Pcb.Fab.FabService.Shared;
            var spec = Foundry.Core.Pcb.Fab.FabService.BuildSpec(LastFabZipPath, LastPcbPath);
            var handoff = await svc.PrepareOrderAsync(spec);

            foreach (var n in handoff.Notes) FabOrderNotes.Add($"· {n}");
            FabOrderNotes.Add($"· order params copied to clipboard: {handoff.ClipboardParams}");
            try { System.Windows.Clipboard.SetText(handoff.ClipboardParams); } catch { /* clipboard best effort */ }

            // Reveal the ZIP so the user can drag-drop it on the fab's upload page.
            if (File.Exists(handoff.ZipPath))
                try { Process.Start(new ProcessStartInfo { FileName = "explorer.exe", Arguments = $"/select,\"{handoff.ZipPath}\"", UseShellExecute = true }); }
                catch { /* best effort */ }

            // Open the fab portal — http(s) only via the safe OpenUrl. This is the only outward action, and the
            // user explicitly confirmed it; nothing is submitted or paid.
            OpenUrl(handoff.PortalUrl);

            FabOrderStatus = handoff.Summary;
            Foundry.Core.Diagnostics.AppLog.Info("fab", $"order handoff opened · {handoff.Provider} · {handoff.PortalUrl} (not submitted)");
        }
        catch (Exception ex) { FabOrderStatus = $"Order prep failed: {ex.Message}"; }
        finally { IsFabbing = false; }
    }
}
