---
title: "Foundry — P0 Implementation Plan (June 2026)"
domain: p0-plan
status: archived
last-reviewed: 2026-08-01
superseded-by: "[[pcb]]"
---

# Foundry — P0 Implementation Plan

_Generated from the multi-agent critical review (2026-06-04). Grade C-. 6 P0 fixes to make the auto-PCB->fab moat trustworthy._

## Execution order

- P0-2 (build_board.py pytest slice only) — the cheapest TDD red on any box: guard `import pcbnew`, extract pure `assign_pads`, write `test_build_board.py`. It is a prerequisite refactor for P0-1's edits to the SAME two passes, so doing it first means P0-1 edits the already-extracted helper instead of fighting a second rewrite.
- P0-1 — connectivity gate. Builds directly on the P0-2 build_board.py refactor (same pass-1/pass-2 region): add header detection + unmappedPins/byPosition emission, then PcbResult/PcbDesigner gating. This is the moat's correctness floor; everything downstream (fab gate) assumes Ok actually means connected.
- P0-6 — DRC fab gate. Depends on nothing in P0-1 but shares CanExportFab + TabViewModels with P0-1, so sequence it right after P0-1 to merge the two gate flags (ConnectivityVerified + LastDrcClean) in one coherent VM pass and avoid a second conflicting edit to line 361/596.
- P0-3 — ProcessRunner extraction. The structural linchpin for all Core subprocess files; it must land before P0-4/P0-5 touch FirmwareBuilder.RunAsync and before any further edits to the 5 duplicated runners, so the later P0s edit ONE runner shape, not five.
- P0-4 — download integrity. Depends on P0-3 only for FirmwareBuilder.DownloadCliAsync coexistence (both edit that file); DownloadVerifier is otherwise standalone. Sequenced after P0-3 so the FirmwareBuilder edits stack cleanly.
- P0-5 — flash confirm + vendor-mismatch refuse. Last because it makes the largest single rewrite to FirmwareBuilder.UploadAsync + TabViewModels.Flash(), both already touched by P0-3 (tokens) and P0-4 (URL pin); doing it last means it rebases onto the final runner/URL shape instead of being clobbered.

## Shared-file coordination

- Foundry.Core/Pcb/KiCadScripts/build_board.py — touched by P0-2 (extract pure assign_pads + guard `import pcbnew`) AND P0-1 (header detection + unmappedPins/byPosition + ok reflects connectivity). SEQUENCE: P0-2 first (refactor the two passes into the pure helper), THEN P0-1 adds the header-conditional logic INSIDE that helper/build(). If P0-1 went first the helper extraction would have to re-touch every changed line. The note strings 'pin %s -> pad %s by position' and 'no free pad for net node' MUST stay byte-identical through BOTH edits (existing PcbResult/PcbTests assert them).
- Foundry.App/ViewModels/TabViewModels.cs — touched by P0-1 (ConnectivityVerified flag + CanExportFab + ExportPcb/ExportFabCore/DesignAndExportFab), P0-3 (_pcbCts/_fwCts + cancel commands + ct threading + OperationCanceledException arms across all 6 PCB actions and Flash/VerifyBuild/DetectBoards), P0-5 (Flash() confirm dialog + new UploadAsync arg order), P0-6 (LastDrcClean flag + CanExportFab + DrcCore/DesignPcb/DesignAndExportFab verdict-setting + OnLastPcbPathChanged reset). CRITICAL MERGE: CanExportFab (line 361) is rewritten by BOTH P0-1 and P0-6 — land them adjacently and produce ONE final expression `!IsExportingPcb && !string.IsNullOrEmpty(LastPcbPath) && ConnectivityVerified && LastDrcClean`. ExportPcb (line 434-442) is edited by P0-1 (set ConnectivityVerified), P0-3 (ct), P0-6 (ordering vs OnLastPcbPathChanged reset). DesignAndExportFab (657-711) is edited by all four. Do P0-1+P0-6 VM edits in one pass, then P0-3 adds tokens/cancel on top, then P0-5 rewrites only Flash(). OnIsExportingPcbChanged (344-352) gains both CancelPcbCommand.NotifyCanExecuteChanged (P0-3) — confirm no name collision with existing PcbCommands.
- Foundry.Core/Pcb/PcbDesigner.cs — touched by P0-1 (RunLoopAsync unmapped-pins short-circuit branch at 163-167) and P0-6 (DesignAndExportFabAsync line 133 -> named args `drcClean:true, drcOptions:options`). Non-overlapping methods (RunLoopAsync vs DesignAndExportFabAsync) so either order works, but P0-6's named-arg change is REQUIRED the moment P0-6's GerberExporter signature lands — they must be in the SAME commit or the build breaks (positional ct binds to drcClean).
- Foundry.Core/Pcb/PcbResult.cs — touched ONLY by P0-1 (add UnmappedPins/ByPositionCount init props, parse arrays, force Ok=false). No other P0 touches it. Positional-record additive change keeps all existing constructions/tests compiling.
- Foundry.Core/Firmware/FirmwareBuilder.cs — touched by P0-3 (delete private RunAsync 363-371 + inline blocks 119-122/182-185, route all through ProcessRunner.RunAsync with ArduinoTimeout), P0-4 (pin ArduinoCliVersion/Url/Sha256, route DownloadCliAsync through DownloadVerifier), P0-5 (new UploadAsync signature `(project, target, bool forceMismatch, ct)` + BuildFlashPlan/FqbnSource/validators + RunAsync ArgumentList overload). ORDER: P0-3 first (establishes the ProcessRunner-based runner), then P0-4 (DownloadCliAsync only — disjoint method), then P0-5 (UploadAsync rewrite — must build on P0-3's runner, and P0-5 adds its OWN ArgumentList overload of RunAsync; keep P0-3's string overload for the other callers). All three edit RunAsync-adjacent code — land sequentially, rebuild between.
- Foundry.Core/Pcb/Fab/GerberExporter.cs — touched by P0-3 (delete private RunAsync 138-150, route the two runs through ProcessRunner with KicadTimeout) and P0-6 (add drcClean/drcOptions params + DRC self-gate before work-dir + `using Foundry.Core.Pcb;`). Disjoint regions (RunAsync helper vs ExportAsync top), but P0-6's signature change must be coordinated with PcbDesigner.cs:133 in the same commit.
- Foundry.Core/Pcb/PcbRouter.cs, PcbBuilder.cs, PcbDrc.cs — each touched ONLY by P0-3 (delete their private RunAsync, route call sites through ProcessRunner). PcbBuilder.cs also has the P0-1-relevant `result with { Notes }` at line 91 which must preserve the new init props — no edit needed there, just verify after P0-1 lands.
- Foundry.Core/Pcb/KiCadInstaller.cs — touched by P0-2 indirectly (CI installs KiCad via choco; Locate() probes bin\python.exe) and P0-4 (FallbackExeSha256 + route NSIS download through DownloadVerifier + optional Authenticode before RunAsync). KiCadInstaller's own RunAsync (177-196) is the INSTALL runner — P0-3 does NOT list it for extraction (it's an exit-code-only helper, lower risk); leave it unless reviewer wants consistency. Only P0-4 edits this file's body.
- .github/workflows/release.yml — touched by P0-2 ONLY (add sibling `pcb-live` job; do NOT touch the existing `build` job's bare `dotnet test` at line 78). No conflict with other P0s.
- App.xaml.cs — touched by P0-4 ONLY (InstallerTrusted fail-closed + WinVerifyTrust via DownloadVerifier.VerifyAuthenticode + releases-page redirect at the 177-184 call site). No conflict.

## Step-by-step sequence (25 commit-sized steps)

### Step 1 — [P0-2]

Guard `import pcbnew` (try/except → pcbnew=None) and extract the two-pass pad-assignment into a pure `assign_pads(pad_names, pad_net_list, known_nets, ref, lib_id)` returning (assignments, notes) with the note strings copied VERBATIM; refactor build()'s inner loop (151-191) to derive pad_names + call assign_pads. No behavior change for headers.

- **Files:** `Foundry.Core\Pcb\KiCadScripts\build_board.py`
- **Verify:** python -c "import importlib.util,os; s=importlib.util.spec_from_file_location('bb','Foundry.Core/Pcb/KiCadScripts/build_board.py'); m=importlib.util.module_from_spec(s); s.loader.exec_module(m); print(m.assign_pads(['1','2','3'],[{'pin':'VCC','net':'+3V3'},{'pin':'AOUT','net':'SIG'},{'pin':'GND','net':'GND'}],{'+3V3','SIG','GND'},'S1','x'))" — imports WITHOUT pcbnew and prints the assignment.

### Step 2 — [P0-2]

Write test_build_board.py with the 5 pytest cases (named-pin-on-numbered-pad, exact-match-precedence, pad-count-mismatch note, unknown-net-unassigned, case-insensitive). FAILING-FIRST proof: against pre-step-1 code it fails at collection (import pcbnew + no assign_pads).

- **Files:** `Foundry.Core\Pcb\KiCadScripts\test_build_board.py`
- **Verify:** python -m pytest Foundry.Core/Pcb/KiCadScripts/test_build_board.py -q → 5 passed.

### Step 3 — [P0-1]

Write the FAILING-FIRST PcbResult test Parse_UnmappedPins_BlocksOk_AndSurfacesThem (+ Parse_ByPositionOnHeader_StaysOk, Parse_NoUnmapped_BackCompat) in PcbTests.cs PcbResultTests. It won't compile until UnmappedPins exists — that is the intended red.

- **Files:** `Foundry.Tests\PcbTests.cs`
- **Verify:** dotnet test Foundry.Tests --filter PcbResultTests → compile error / red on the new test (proves it exercises new behavior).

### Step 4 — [P0-1]

In build_board.py: add pads_are_pure_numeric() helper, per-component is_header flag, unmapped/by_position accumulators, header-conditional pass-2 (named footprints record unmapped + do NOT ordinal-map), and build() return surfaces ok=(len(unmapped)==0) + unmappedPins/byPosition. Keep 'by position'/'no free pad' strings byte-identical.

- **Files:** `Foundry.Core\Pcb\KiCadScripts\build_board.py`
- **Verify:** python -m pytest Foundry.Core/Pcb/KiCadScripts/test_build_board.py -q stays green (pure helper untouched in signature); add a quick pytest asserting pads_are_pure_numeric(['1','2','3']) is True and (['VCC','GND']) is False.

### Step 5 — [P0-1]

In PcbResult.cs: add UnmappedPins (init=[]) + ByPositionCount (init=0) props, parse unmappedPins/byPosition arrays in Parse(), force ok=false + 'Connectivity unverified' note BEFORE the file-exists check, carry new fields in the return. Update PcbBuilder.cs XML summary only (verify `with` at line 91 preserves init props — no logic change).

- **Files:** `Foundry.Core\Pcb\PcbResult.cs`, `Foundry.Core\Pcb\PcbBuilder.cs`
- **Verify:** dotnet test Foundry.Tests --filter PcbResultTests → step-3 tests now GREEN; all prior PcbResult tests still pass.

### Step 6 — [P0-1]

In PcbDesigner.RunLoopAsync (163-167): add the explicit unmapped-pins branch returning Failed('connectivity unverified (N unmapped pin(s))') before route/DRC. Add the PcbDesigner test RunLoop_BlocksOnUnmappedPins_NoRouteNoExport (fake BuildStep with UnmappedPins; assert routed==false && drcd==false).

- **Files:** `Foundry.Core\Pcb\PcbDesigner.cs`, `Foundry.Tests\PcbRoutingTests.cs`
- **Verify:** dotnet test Foundry.Tests --filter "RunLoop_BlocksOnUnmappedPins" → green; route/DRC delegates never invoked.

### Step 7 — [P0-6]

Write the FAILING-FIRST DrcReport test: rename Parse_ExitZero_NoReportFile_ReconciledToClean → Parse_ExitZero_NoReportFile_IsInconclusiveNotClean asserting !Ok && !Clean && 'could not verify'. Keep Parse_CleanBoard_ExitZero_EmptyArrays_IsClean as the positive guard.

- **Files:** `Foundry.Tests\PcbDrcTests.cs`
- **Verify:** dotnet test Foundry.Tests --filter DrcReportParseTests → the renamed test FAILS (today returns Ok=Clean=true).

### Step 8 — [P0-6]

In DrcReport.Parse: change the empty-reportJson branch so exit 0 + no report → Failed('DRC produced no report — could not verify the board.'); exit 5 stays Failed. Leave the JSON-present path untouched.

- **Files:** `Foundry.Core\Pcb\DrcReport.cs`
- **Verify:** dotnet test Foundry.Tests --filter DrcReportParseTests → step-7 test GREEN; Parse_CleanBoard_ExitZero_EmptyArrays_IsClean still GREEN.

### Step 9 — [P0-6]

In GerberExporter.ExportAsync: add `drcClean=false, drcOptions=null` params before ct, `using Foundry.Core.Pcb;`, and the DRC self-gate (run PcbDrc.CheckAsync when !drcClean; NotInstalled→NotInstalled, !Clean→Failed) after the input-exists check. In PcbDesigner.DesignAndExportFabAsync line 133, change to named args `drcClean:true, drcOptions:options, ct:ct` (SAME commit — fixes the positional break). Add FabExportResult.Failed-shape test + the KiCad-guarded ExportAsync gate tests.

- **Files:** `Foundry.Core\Pcb\Fab\GerberExporter.cs`, `Foundry.Core\Pcb\PcbDesigner.cs`, `Foundry.Tests\FabExportTests.cs`
- **Verify:** dotnet build Foundry.Core (positional break is the canary) succeeds; dotnet test Foundry.Tests --filter "FabExport|GerberExporter" → green.

### Step 10 — [P0-1+P0-6]

ONE coordinated TabViewModels pass: add ConnectivityVerified (P0-1) + LastDrcClean (P0-6) observable flags; rewrite CanExportFab ONCE to `!IsExportingPcb && !string.IsNullOrEmpty(LastPcbPath) && ConnectivityVerified && LastDrcClean`; set ConnectivityVerified in ExportPcb; set LastDrcClean in DrcCore/DesignPcb/DesignAndExportFab AFTER LastPcbPath; add OnLastPcbPathChanged reset (LastDrcClean=false) and the OnLastDrcCleanChanged/OnConnectivityVerifiedChanged notify partials; ExportFabCore top-guard on !ConnectivityVerified.

- **Files:** `Foundry.App\ViewModels\TabViewModels.cs`
- **Verify:** dotnet build Foundry.App succeeds; manual FOUNDRY_SHOT: after a miswired/dirty build the EXPORT GERBERS button is disabled; after a clean DRC pass it enables.

### Step 11 — [P0-2]

Write Foundry.Tests/PcbLiveToolchainTests.cs: KiCadSkip()/ITestOutputHelper idiom mirroring Avr8jsLiveSmokeTest; 3-part ESP32+BME280+cap fixture with NAMED pins; build→(optional route)→DRC→Gerber/drill; ParsePadNets readback asserting +3V3/GND/SDA/SCL landed on the right refs; closed Edge.Cuts; DRC verdict-vs-counts consistency; zip marker checks. Skips cleanly on bare boxes.

- **Files:** `Foundry.Tests\PcbLiveToolchainTests.cs`
- **Verify:** dotnet test Foundry.Tests --filter PcbLiveToolchainTests → on a bare box: writes skip reason + passes (no red); on a KiCad box: pad->net readback passes.

### Step 12 — [P0-2]

Add the sibling `pcb-live` job to release.yml (choco install kicad + setup-python + pip pytest + `kicad-cli version` smoke + pytest + `dotnet test --filter PcbLiveToolchainTests`). Do NOT modify the existing build job's bare `dotnet test`.

- **Files:** `.github\workflows\release.yml`
- **Verify:** workflow_dispatch (or throwaway tag) → pcb-live job installs KiCad, pytest green, C# live test runs-or-cleanly-skips; build job unchanged and still bare.

### Step 13 — [P0-3]

Write FAILING-FIRST Foundry.Tests/ProcessRunnerTests.cs (Windows-guarded): RunAsync_KillsAndReportsTimeout_OnSlowChild (#1, ping -n 30 with 500ms timeout, assert TimedOut + <5s), fast-child, non-zero-exit, caller-cancel-throws, LargeStderr-no-deadlock (#5), sane-defaults. Won't compile until ProcessRunner exists.

- **Files:** `Foundry.Tests\ProcessRunnerTests.cs`
- **Verify:** dotnet test Foundry.Tests --filter ProcessRunnerTests → red (type missing) — the intended failing-first state.

### Step 14 — [P0-3]

Create Foundry.Core/Diagnostics/ProcessRunner.cs: ProcRun record + RunAsync(exe,args,timeout,ct) that starts both stream reads before awaiting (deadlock fix), linked CTS with CancelAfter (watchdog), Kill(entireProcessTree:true) on timeout/cancel, OCE rethrow for caller cancel, TimedOut+ExitCode=-1 for watchdog; KicadTimeout/RouterTimeout/ArduinoTimeout constants.

- **Files:** `Foundry.Core\Diagnostics\ProcessRunner.cs`
- **Verify:** dotnet test Foundry.Tests --filter ProcessRunnerTests → all 6 green (#1 timeout <5s, #5 stderr>10000 no deadlock).

### Step 15 — [P0-3]

Route the 4 Core PCB files' call sites through ProcessRunner and DELETE their private RunAsync: PcbRouter (3 sites, RouterTimeout for FreeRouting + KicadTimeout for DSN/SES), PcbBuilder (build+measure, KicadTimeout), PcbDrc (KicadTimeout), GerberExporter (gerbers+drill, KicadTimeout). Translate TimedOut→the existing Failed factories.

- **Files:** `Foundry.Core\Pcb\PcbRouter.cs`, `Foundry.Core\Pcb\PcbBuilder.cs`, `Foundry.Core\Pcb\PcbDrc.cs`, `Foundry.Core\Pcb\Fab\GerberExporter.cs`
- **Verify:** dotnet build Foundry.Core succeeds (no private RunAsync left in these 4); dotnet test Foundry.Tests → all existing PcbRouting/Pcb/PcbDrc/FabExport NotInstalled tests still green.

### Step 16 — [P0-3]

FirmwareBuilder: delete private RunAsync (363-371) + inline blocks (119-122, 182-185); route compile/image/board-list/core/upload/download through ProcessRunner.RunAsync(ArduinoTimeout); timeout→didn't-compile BuildResult/CompiledImage and upload→UploadResult fail.

- **Files:** `Foundry.Core\Firmware\FirmwareBuilder.cs`
- **Verify:** dotnet build Foundry.Core; dotnet test Foundry.Tests --filter FlashTests → ParseBoardList/Fqbn tests still green.

### Step 17 — [P0-3]

TabViewModels: add _pcbCts + CancelPcb [RelayCommand(CanExecute=CanCancelPcb)] and _fwCts + CancelBuild; create fresh CTS at the top of each PCB action + Flash/VerifyBuild/DetectBoards; thread ct into every Core call; add OperationCanceledException arms; wire CancelPcbCommand.NotifyCanExecuteChanged into OnIsExportingPcbChanged. (Rebases on step-10's VM edits.) Flag XAML CANCEL buttons to reviewer (out of scope).

- **Files:** `Foundry.App\ViewModels\TabViewModels.cs`
- **Verify:** dotnet build Foundry.App succeeds; no CommunityToolkit source-gen name collisions (CancelPcbCommand/CancelBuildCommand).

### Step 18 — [P0-4]

Write FAILING-FIRST Foundry.Tests/DownloadVerifierTests.cs: ExtractZipSafe_RejectsZipSlipEntry, VerifyFileSha256_Mismatch_Throws, DownloadVerifiedAsync_Mismatch_DeletesPartAndThrows (in-proc HttpMessageHandler), DownloadVerifiedAsync_Match_WritesFile, pinned-hash 64-hex format tests, ArduinoCliUrl_IsVersionPinned. Won't compile until DownloadVerifier/IntegrityException exist.

- **Files:** `Foundry.Tests\DownloadVerifierTests.cs`
- **Verify:** dotnet test Foundry.Tests --filter DownloadVerifierTests → red (types missing).

### Step 19 — [P0-4]

Create Foundry.Core/Provisioning/DownloadVerifier.cs: IntegrityException, DownloadVerifiedAsync (stream+IncrementalHash+.part+Move), VerifyFileSha256, VerifyAuthenticode (WinVerifyTrust P/Invoke), ExtractZipSafe (zip-slip guard).

- **Files:** `Foundry.Core\Provisioning\DownloadVerifier.cs`
- **Verify:** dotnet test Foundry.Tests --filter DownloadVerifierTests → green (zip-slip rejected, mismatch deletes .part, match writes file).

### Step 20 — [P0-4]

Pin SHA-256 constants (computed via Get-FileHash on the real pinned artifacts) and route every installer through DownloadVerifier/ExtractZipSafe: RenodeInstaller, OpenScadInstaller, FreeRoutingInstaller (jar=hash; JRE=post-extract Authenticode fail-closed), FirmwareBuilder.DownloadCliAsync (version-pinned URL + hash), KiCadInstaller NSIS exe (hash before RunAsync).

- **Files:** `Foundry.Core\Simulation\RenodeInstaller.cs`, `Foundry.Core\Cad\OpenScadInstaller.cs`, `Foundry.Core\Pcb\FreeRoutingInstaller.cs`, `Foundry.Core\Firmware\FirmwareBuilder.cs`, `Foundry.Core\Pcb\KiCadInstaller.cs`
- **Verify:** dotnet test Foundry.Tests → pinned-hash 64-hex format tests + ArduinoCliUrl_IsVersionPinned green; existing ProvisioningTests (idempotent/url-shape) still green.

### Step 21 — [P0-4]

App.xaml.cs: make InstallerTrusted fail CLOSED — unsigned running app returns false (open releases page, don't auto-run); signed path requires DownloadVerifier.VerifyAuthenticode (full WinVerifyTrust chain) THEN publisher pin. Change the 177-184 call site to Yes/No 'open releases page'. Add VerifyAuthenticode_UnsignedStub_ReturnsFalse to UpdaterTests.

- **Files:** `Foundry.App\App.xaml.cs`, `Foundry.Tests\UpdaterTests.cs`
- **Verify:** dotnet build Foundry.App; dotnet test Foundry.Tests --filter UpdaterTests → unsigned-stub-returns-false green.

### Step 22 — [P0-5]

Write FAILING-FIRST FlashTests case BuildFlashPlan_InferredEsp32_DetectedAvr_FlagsVendorMismatch (+ the full planner/validator suite: matching-vendors-prefers-detected, unidentified-falls-back, exact-match-source, IsValidFqbn/IsValidPort/VendorOf theories, MicroPython-refuses). Won't compile until BuildFlashPlan/FlashPlan/FqbnSource exist.

- **Files:** `Foundry.Tests\FlashTests.cs`
- **Verify:** dotnet test Foundry.Tests --filter FlashTests → red on the new BuildFlashPlan tests (types missing).

### Step 23 — [P0-5]

FirmwareBuilder: add FqbnSource enum, FlashPlan record (with ConfirmText), IsValidFqbn/IsValidPort/VendorOf validators, BuildFlashPlan planner (prefer detected FQBN; flag cross-vendor mismatch); change UploadAsync signature to (project, target, bool forceMismatch=false, ct=default) — refuse multi-port ambiguity, validate port/fqbn, refuse mismatch unless forced; add ArgumentList RunAsync overload and switch the upload invocation to it (drop manual quotes on buildDir).

- **Files:** `Foundry.Core\Firmware\FirmwareBuilder.cs`
- **Verify:** dotnet test Foundry.Tests --filter FlashTests → all planner/validator tests green; existing ParseBoardList/Fqbn green.

### Step 24 — [P0-5]

TabViewModels.Flash(): rewrite to build the FlashPlan, force a pick on multi-port, show OKCancel confirm (default Cancel) naming port+board+resolved FQBN (separate VENDOR-MISMATCH variant that sets force=true), then call UploadAsync(Project, board, force, ct). Keep the _fwCts token from step 17. Optional FirmwareView.xaml FLASH tooltip tweak.

- **Files:** `Foundry.App\ViewModels\TabViewModels.cs`, `Foundry.App\Views\Tabs\FirmwareView.xaml`
- **Verify:** dotnet build Foundry.App; manual FOUNDRY_SHOT: FLASH shows a confirm dialog naming port/board/FQBN; Cancel aborts; two ports without a pick shows the multi-port message; mismatch shows the brick warning.

### Step 25 — [ALL]

Full regression + build gate: dotnet build (Core + App) and dotnet test Foundry.Tests; run the pytest; confirm release.yml build job still bare. Confirm all FAILING-FIRST tests from steps 2/3/7/13/18/22 are now green and no existing assertion changed (esp. build_board.py note strings byte-identical).

- **Files:** `Foundry.Tests`, `Foundry.Core\Pcb\KiCadScripts\test_build_board.py`
- **Verify:** dotnet build && dotnet test Foundry.Tests → 0 failures; python -m pytest Foundry.Core/Pcb/KiCadScripts/test_build_board.py -q → all green.

## Global risks

1. build_board.py is edited TWICE (P0-2 extract, then P0-1 header logic). The note strings 'component %s: pin \'%s\' -> pad \'%s\' by position' and 'no free pad for net node' are asserted by existing C# PcbResult/PcbTests AND by the new pytest — they must stay byte-identical across both edits or both test layers break. Diff produced notes before/after on a real build.
2. Shared-file write ordering on TabViewModels.cs is the single biggest conflict surface (4 P0s). If P0-3's token threading lands before the P0-1+P0-6 gate-flag pass, CanExportFab gets rewritten twice and ExportPcb/DesignAndExportFab edits collide. Enforce the step order: P0-1+P0-6 VM pass (step 10) → P0-3 tokens (step 17) → P0-5 Flash rewrite (step 24). One editor, sequential, rebuild between.
3. GerberExporter.ExportAsync signature change (P0-6) and its only positional caller PcbDesigner.cs:133 MUST be in the same commit; a positional 3rd→4th-arg shift binds ct to drcClean and silently changes behavior. The build break is the canary — never split these.
4. P0-4 ships pinned SHA-256 constants that, if wrong/placeholder, fail-close 100% of installs. The 64-hex format test catches empties but NOT a wrong-but-valid hash — each constant must be computed from the actual pinned artifact (Renode 1.16.1, OpenSCAD 2021.01, freerouting 2.2.4 jar, the chosen arduino-cli version, kicad 10.0.3 exe) and cross-checked against publisher checksums where available.
5. P0-4 makes the updater fail-CLOSED for unsigned (default) builds — auto-update stops working until builds are Authenticode-signed. Intended, but a user-visible regression; the releases-page redirect mitigates it. Must be in release notes.
6. P0-5 + P0-3 both rewrite FirmwareBuilder's runner area. P0-5 adds a NEW ArgumentList RunAsync overload while P0-3 moved the existing calls to ProcessRunner. Keep both: ProcessRunner for the string-arg callers, the ArgumentList overload only for upload. Verify no ambiguous-overload resolution.
7. Live tests (P0-2 PcbLiveToolchainTests, P0-3 ProcessRunnerTests #1/#5) spawn real processes (kicad-cli/python, cmd.exe/ping). They are Windows-bound and must SKIP cleanly (P0-2) or be Windows-guarded (P0-3) so the bare-box suite stays green; a CI Linux runner would red-fail otherwise. The existing build job's bare `dotnet test` must remain bare — KiCad only in the new pcb-live job.
8. Worst-case wall time after P0-3: maxIterations × RouterTimeout (3 × 10min = 30min) if FreeRouting repeatedly times out. Acceptable vs infinite hang, but flag for a product decision on RouterTimeout / cumulative loop budget.
9. CommunityToolkit.Mvvm source-gen: P0-3's [RelayCommand] CancelPcb/CancelBuild and P0-6/P0-1's new [ObservableProperty] flags generate members at compile — confirm no collision with existing PcbCommands/observable names and that NotifyCanExecuteChanged references resolve.
10. P0-5 changes FQBN resolution to always PREFER the detected board's FQBN (not just when inferred==uno). Within-vendor variant cases (infer uno, detect nano) now flash 'nano' — more correct but a semantic change; firmware was compiled for inferred, so an upload may still fail. Acceptable vs cross-vendor brick.
11. XAML CANCEL buttons (P0-3) are explicitly OUT of the logic edits — without them the user cannot cancel from the UI even though the plumbing exists. Must be done alongside to fully close P0-3; flag to reviewer.

## Done criteria

1. P0-1: `dotnet test Foundry.Tests` — Parse_UnmappedPins_BlocksOk_AndSurfacesThem is GREEN (Ok forced false, UnmappedPins populated, 'Connectivity unverified' note); Parse_ByPositionOnHeader_StaysOk + Parse_NoUnmapped_BackCompat GREEN; RunLoop_BlocksOnUnmappedPins_NoRouteNoExport proves route/DRC delegates never invoked on unmapped pins.
2. P0-1: a NAMED footprint with an unmatched net pin yields ok:false + unmappedPins from build_board.py (not a silent ordinal miswire); a pure-numeric header still ordinal-maps and stays ok:true (pads_are_pure_numeric pytest True for ['1','2','3'], False for ['VCC','GND']).
3. P0-2: `python -m pytest Foundry.Core/Pcb/KiCadScripts/test_build_board.py -q` → all 5 cases pass AND build_board.py imports WITHOUT pcbnew on a bare box; the pcb-live CI job installs KiCad, runs pytest green, and runs-or-cleanly-skips PcbLiveToolchainTests while the existing build job's `dotnet test` stays bare.
4. P0-2: on a KiCad box, PcbLiveToolchainTests asserts +3V3/GND/SDA/SCL each landed on the correct component refs by reading back the saved .kicad_pcb, Edge.Cuts forms a closed loop, DRC verdict is internally consistent with its counts, and the fab zip's Gerber/Excellon files contain the expected RS-274X/Excellon markers.
5. P0-3: ProcessRunnerTests all green — RunAsync_KillsAndReportsTimeout_OnSlowChild returns TimedOut in <5s (proves timeout+tree-kill), RunAsync_LargeStderr_DoesNotDeadlock completes (proves concurrent-read fix), RunAsync_CallerCancel_Throws (OCE preserved). No private RunAsync remains in PcbRouter/PcbBuilder/PcbDrc/GerberExporter/FirmwareBuilder (single ProcessRunner.RunAsync).
6. P0-4: DownloadVerifierTests green — ExtractZipSafe throws IntegrityException on a '..\evil' entry and writes nothing outside target; DownloadVerifiedAsync deletes the .part and throws on hash mismatch; every pinned SHA-256 matches `^[0-9A-Fa-f]{64}$`; ArduinoCliUrl contains the pinned version and NOT '_latest_'; VerifyAuthenticode returns false for an unsigned stub; InstallerTrusted returns false for an unsigned running app.
7. P0-5: FlashTests green — BuildFlashPlan_InferredEsp32_DetectedAvr_FlagsVendorMismatch shows VendorMismatch=true, Fqbn=='arduino:avr:uno' (physical board wins), Source==PortPreferredOverInferred, MismatchWarning contains 'brick'; IsValidFqbn/IsValidPort reject injection strings; UploadAsync(project,null) with >1 port returns Ok=false (no FirstOrDefault auto-flash).
8. P0-5: manual FOUNDRY_SHOT confirms FLASH always shows an OKCancel confirm naming port+board+resolved FQBN (default Cancel), a two-port state without a pick refuses, and a cross-vendor mismatch shows the brick warning and only proceeds on explicit confirm.
9. P0-6: DrcReportParseTests green — Parse_ExitZero_NoReportFile_IsInconclusiveNotClean (!Ok && !Clean && 'could not verify') AND Parse_CleanBoard_ExitZero_EmptyArrays_IsClean both pass (false-clean removed, real-clean preserved); the old Parse_ExitZero_NoReportFile_ReconciledToClean no longer exists.
10. P0-6: GerberExporter.ExportAsync compiles with the new signature and PcbDesigner.cs:133 named-arg call (no positional break); the standalone EXPORT GERBERS path self-runs DRC and returns Failed (no zip) on a non-clean board; TabViewModels CanExportFab requires BOTH ConnectivityVerified (P0-1) AND LastDrcClean (P0-6).
11. Trust capstone: `dotnet build` (Core+App) and `dotnet test Foundry.Tests` → 0 failures; pytest green; every FAILING-FIRST test (steps 2/3/7/13/18/22) was demonstrably red before its fix and green after; the moat now refuses to fab a board that is miswired (P0-1), DRC-unverified (P0-6), or built by an untested toolchain (P0-2), and refuses to flash/auto-update without explicit verified consent (P0-4/P0-5), with no subprocess able to hang the app (P0-3).


---

# Appendix — Detailed per-P0 plans

## P0-1: Stop silent ordinal pad mapping; gate fab on verified connectivity

**Effort:** M | **dependsOn:** none

### Current state

build_board.py assigns unmatched (pin,net) pairs to free pads purely by ordinal position and treats the board as correct.

Pass 1 (build_board.py:164-177) matches by pad name case-insensitively; anything unmatched is deferred:
```
# pass 1: exact pad-name match (case-insensitive)
deferred = []
for item in pad_net_list:
    ...
    if not matched:
        deferred.append((pin, net_name))
```
Pass 2 (build_board.py:179-191) ALWAYS ordinal-falls-back and only appends an informational note:
```
# pass 2: ordinal — assign each unmatched (pin, net) to the next free pad in order
free = [i for i in range(len(pads)) if i not in used]
fi = 0
for pin, net_name in deferred:
    if fi >= len(free):
        notes.append("component %s: no free pad for net node '%s' (%s has %d pads)" % ...)
        continue
    i = free[fi]; fi += 1
    if assign(pads[i], net_name):
        used.add(i)
    if pads[i].GetName().lower() != pin.lower():
        notes.append("component %s: pin '%s' -> pad '%s' by position" % (comp["ref"], pin, pads[i].GetName()))
```
build() then unconditionally returns `{"ok": True, ...}` (build_board.py:211) regardless of how many pins were placed by position. So an ESP32 net like U1.GPIO34→SIG, when the footprint's pads are named "1".."38" but the WROOM uses named pads or the pin name doesn't match, lands on whatever the next free pad happens to be — wrong copper, then routes/DRCs/exports as if correct.

PcbResult.Parse (PcbResult.cs:34-79) reads `ok`/`error`/`components`/`nets`/`notes` only. It has no awareness of "by position" notes or any unmapped-pin list, so `ok` stays true and `KicadPcbPath` is returned.

PcbDesigner.RunLoopAsync (PcbDesigner.cs:161-167) only bails when `!built.Ok || string.IsNullOrEmpty(built.KicadPcbPath)`:
```
var built = await build(plan, knobs, ct);
...
if (!built.Ok || string.IsNullOrEmpty(built.KicadPcbPath))
{ trace.Add(...); return PcbDesignResult.Failed(...); }
```
A board with silent ordinal mapping has `built.Ok == true`, so the loop proceeds to route→DRC→export. A DRC-clean-but-miswired board then passes the gate.

DesignAndExportFabAsync (PcbDesigner.cs:124-135) only gates fab on `design.Ok`:
```
var design = await DesignAsync(...);
if (!design.Ok || string.IsNullOrEmpty(design.KicadPcbPath)) { ...Failed... }
var fabResult = await GerberExporter.ExportAsync(design.KicadPcbPath!, outputDir, fabOptions, ct);
```

The standalone fab path in the UI is even looser: `CanExportFab => !IsExportingPcb && !string.IsNullOrEmpty(LastPcbPath)` (TabViewModels.cs:361) and `ExportFab`→`ExportFabCore`→`GerberExporter.ExportAsync` (TabViewModels.cs:611-650) export Gerbers from any built board, with no connectivity check at all.

FootprintMap.PadNetList (FootprintMap.cs:260-272) and PcbJobComponent.PadNetList (PcbJob.cs:25) already carry the ordered (pin,net) list, but only build_board.py (which loads the real footprint) knows the real pad names — so the named-vs-pure-numeric decision must be made there. v2.3.1's design note (FootprintMap.cs:255-259) explicitly documents the ordinal fallback as intended for headers; this P0 narrows it to ONLY pure-numeric footprints.

### File edits

#### `Foundry.Core/Pcb/KiCadScripts/build_board.py`

Make the ordinal fallback conditional on the footprint having pure-numeric pad names (a real header, pads exactly the set {"1".."N"}). For named-pad footprints, do NOT ordinal-map: collect the unmatched (pin,net,ref,libId) as unmapped pins and return ok:false with an 'unmappedPins' list. Always record any by-position assignment so the C# side can detect it even on headers.

1) Add a helper after footprint_loader():

```
def pads_are_pure_numeric(pads):
    """True iff the footprint's pad names are exactly the ordinal set 1..N (a generic header) —
    the ONLY case where ordinal pin->pad fallback is semantically safe."""
    names = [p.GetName().strip() for p in pads if p.GetName().strip()]
    if not names:
        return False
    nums = []
    for nm in names:
        if not nm.isdigit():
            return False
        nums.append(int(nm))
    return sorted(set(nums)) == list(range(1, len(set(nums)) + 1))
```

2) In build(), before the components loop add accumulators:
```
unmapped = []        # [{"ref","pin","net","footprint"}] — named footprints we refused to ordinal-map
by_position = []      # [{"ref","pin","pad","footprint"}] — header pins placed by ordinal (recorded, allowed)
```

3) Compute the header flag once per component, right after `pads = list(fp.Pads())` (build_board.py:155):
```
is_header = pads_are_pure_numeric(pads)
```

4) Replace pass 2 (build_board.py:179-191) so it only ordinal-maps headers; for named footprints it records the deferred items as unmapped and does NOT assign:
```
# pass 2: ordinal fallback — ONLY for pure-numeric (header) footprints. For footprints with
# meaningful pad names (VCC/SDA/GPIOx/...), refuse to guess: an unmatched pin means the netlist
# pin name doesn't exist on the part, so record it as unmapped and fail the build (no silent miswire).
if deferred and not is_header:
    for pin, net_name in deferred:
        unmapped.append({"ref": comp["ref"], "pin": pin, "net": net_name or "", "footprint": lib_id})
    notes.append("component %s: %d net pin(s) have no matching pad on %s (named footprint — not ordinal-mapped): %s"
                 % (comp["ref"], len(deferred), lib_id, ", ".join(p for p, _ in deferred)))
else:
    free = [i for i in range(len(pads)) if i not in used]
    fi = 0
    for pin, net_name in deferred:
        if fi >= len(free):
            notes.append("component %s: no free pad for net node '%s' (%s has %d pads)" % (comp["ref"], pin, lib_id, len(pads)))
            unmapped.append({"ref": comp["ref"], "pin": pin, "net": net_name or "", "footprint": lib_id})
            continue
        i = free[fi]; fi += 1
        if assign(pads[i], net_name):
            used.add(i)
        if pads[i].GetName().lower() != pin.lower():
            notes.append("component %s: pin '%s' -> pad '%s' by position" % (comp["ref"], pin, pads[i].GetName()))
            by_position.append({"ref": comp["ref"], "pin": pin, "pad": pads[i].GetName(), "footprint": lib_id})
```

5) Change the build() return (build_board.py:209-211) to surface connectivity status. The board is still Save()d (so a partial board can be inspected), but ok reflects verified connectivity:
```
out_path = job["outPath"]
board.Save(out_path)
ok = len(unmapped) == 0
return {"ok": ok, "out": out_path, "components": placed, "nets": len(nets),
        "unmappedPins": unmapped, "byPosition": by_position, "notes": notes}
```

Note: keep the existing 'by position' note string EXACTLY ("pin '%s' -> pad '%s' by position") so any existing scrape and the new C# substring check both keep working. The header path still emits ok:true.

#### `Foundry.Core/Pcb/PcbResult.cs`

Parse the new unmappedPins/byPosition arrays from the script JSON, carry counts/flags on PcbResult, and force Ok=false when there are unmapped pins. This is the single point that turns 'silent ordinal mapping' into a hard build failure.

1) Add two fields to the record (additive; default 0 so all existing constructions/tests compile — records with positional ctor: append optional params with defaults):
```
public sealed record PcbResult(bool Installed, bool Ok, string Summary, string? KicadPcbPath, IReadOnlyList<string> Notes)
{
    /// <summary>Net pins that could not be mapped to a real pad on a NAMED footprint (no ordinal guess made).
    /// Non-zero ⇒ connectivity is UNVERIFIED and the board must not be routed/exported/fabbed.</summary>
    public IReadOnlyList<string> UnmappedPins { get; init; } = Array.Empty<string>();
    /// <summary>Header pins assigned to a pad by ordinal position (allowed for pure-numeric footprints only).</summary>
    public int ByPositionCount { get; init; }
    ...
}
```
2) In Parse() after the notes loop (PcbResult.cs:56-58), read the new arrays:
```
var unmapped = new List<string>();
if (root.TryGetProperty("unmappedPins", out var up) && up.ValueKind == JsonValueKind.Array)
    foreach (var u in up.EnumerateArray())
    {
        string r = Str(u, "ref"), pin = Str(u, "pin"), net = Str(u, "net"), fp = Str(u, "footprint");
        unmapped.Add($"{r}.{pin} -> net {net}: no pad on {fp}");
    }
int byPos = 0;
if (root.TryGetProperty("byPosition", out var bp) && bp.ValueKind == JsonValueKind.Array)
    byPos = bp.GetArrayLength();
```
with a small local helper `static string Str(JsonElement e, string n) => e.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";`
3) Force failure + a clear note when anything is unmapped, BEFORE the file-exists check so a saved-but-miswired board never becomes Ok:
```
if (unmapped.Count > 0)
{
    ok = false;
    notes.Add($"Connectivity unverified: {unmapped.Count} net pin(s) have no matching pad — " +
              "fix the part's pin names or footprint before routing/fab.");
    notes.AddRange(unmapped);
}
```
4) Update the final return (PcbResult.cs:78) to carry the new fields:
```
return new PcbResult(true, ok, summary, ok ? path : null, notes)
    { UnmappedPins = unmapped, ByPositionCount = byPos };
```
5) Summary when failing for this reason: keep the generic "Couldn't build the PCB." — the explicit note covers detail; or branch to $"Couldn't build the PCB — {unmapped.Count} unmapped pin(s)." when unmapped.Count>0 for legibility.

#### `Foundry.Core/Pcb/PcbBuilder.cs`

Preserve the new PcbResult fields when folding job diagnostics into notes (the `result with { Notes = ... }` rewrite currently drops nothing today, but the new init-only fields must survive the `with`). Also detect the legacy 'by position' note on NON-header parts as a defensive belt-and-suspenders failure, in case an older embedded script is run.

In BuildAsync (PcbBuilder.cs:88-91), the `result = result with { Notes = notes }` already preserves init-only fields (record `with` copies UnmappedPins/ByPositionCount). No change strictly required there, but verify by keeping the line as-is.

No new logic is needed in PcbBuilder beyond confirming the embedded script resource is the updated one (it is embedded via ScriptResource, PcbBuilder.cs:19, and rebuilt with the project). The substantive gate lives in PcbResult.Parse and PcbDesigner. (List this file only to confirm the `with` preserves the new fields and to update the XML summary mentioning 'no DRC/connectivity gate'.)

#### `Foundry.Core/Pcb/PcbDesigner.cs`

Treat a build with unmapped pins as a hard failure that does NOT proceed to route/DRC/export, and surface it in the result Notes. Because PcbResult.Parse already forces Ok=false when UnmappedPins is non-empty, the existing `!built.Ok` guard now catches it — but add an explicit, well-messaged branch so the trace/notes name the connectivity problem rather than a generic 'build failed', and so DesignAndExportFabAsync's fab gate is never reached for an unverified board.

In RunLoopAsync (PcbDesigner.cs:163-167), expand the build-failure branch:
```
if (!built.Ok || string.IsNullOrEmpty(built.KicadPcbPath))
{
    if (built.UnmappedPins.Count > 0)
    {
        trace.Add($"attempt {attempt}: connectivity unverified — {built.UnmappedPins.Count} unmapped pin(s)");
        return PcbDesignResult.Failed(
            $"PCB build blocked: connectivity unverified ({built.UnmappedPins.Count} unmapped pin(s)). Fix part pin names/footprints before fab.",
            trace, built.Notes);
    }
    trace.Add($"attempt {attempt}: build failed — {built.Summary}");
    return PcbDesignResult.Failed($"PCB build failed: {built.Summary}", trace, built.Notes);
}
```
This keeps DesignAndExportFabAsync (PcbDesigner.cs:124-135) unchanged: a blocked build returns design.Ok==false, so the existing `if (!design.Ok ...)` path returns FabExportResult.Failed and ExportAsync is never called. Add a one-line XML-doc note on RunLoopAsync that connectivity-unverified builds short-circuit before route/export.

#### `Foundry.App/ViewModels/TabViewModels.cs`

Block the standalone EXPORT GERBERS / fab path when the last build's connectivity was unverified. Today CanExportFab only checks LastPcbPath, so a board built with silent ordinal mapping (or, after this change, a board whose build was blocked) could still be Gerber-exported. Track a connectivity-verified flag set from the build result and require it for fab.

1) Add an observable flag near LastPcbPath (around TabViewModels.cs:359) and fold it into CanExportFab/CanOrderFab:
```
[ObservableProperty]
[NotifyPropertyChangedFor(nameof(CanExportFab))]
private bool _connectivityVerified;
...
public bool CanExportFab => !IsExportingPcb && !string.IsNullOrEmpty(LastPcbPath) && ConnectivityVerified;
```
Also add `OnPropertyChanged(nameof(CanExportFab)); ExportFabCommand.NotifyCanExecuteChanged();` to OnConnectivityVerifiedChanged (partial method) mirroring the existing OnIsExportingPcbChanged pattern at TabViewModels.cs:347-351.
2) In ExportPcb (TabViewModels.cs:434-442), set the flag from the build result — only a build with zero unmapped pins is fab-eligible:
```
ConnectivityVerified = result.Ok && result.UnmappedPins.Count == 0;
if (result.Ok && result.KicadPcbPath is not null) { LastPcbPath = ...; ... }
else if (result.UnmappedPins.Count > 0)
    PcbStatus = $"Connectivity unverified — {result.UnmappedPins.Count} pin(s) couldn't be mapped to a pad. Fab is blocked.";
```
   Note: result.Ok is now already false when UnmappedPins>0 (PcbResult change), so LastPcbPath won't be set on a miswire and CanExportFab stays false; the flag is belt-and-suspenders + drives the message.
3) In ExportFabCore (TabViewModels.cs:623-650) add a guard at the top so even a programmatic call can't export an unverified board:
```
if (!ConnectivityVerified)
{
    PcbSeverity = "fail";
    PcbStatus = "Fab export blocked — board connectivity is unverified. Rebuild after fixing unmapped pins.";
    return;
}
```
4) In DesignAndExportFab (TabViewModels.cs:657-709) set ConnectivityVerified = design.Ok after DesignAndExportFabAsync returns (a blocked build yields design.Ok==false), so the standalone EXPORT GERBERS button stays disabled after a blocked one-shot run.
5) Optional: when the build is blocked, do NOT auto-continue into RouteCore (ExportPcb already guards `if (result.Ok ...)`, so this is already correct once Ok is forced false).

### Test plan

TDD — write the FAILING test first against today's code.

FAILING-FIRST test (PcbResult layer, the cheapest place the bug is observable):
File: Foundry.Tests/PcbTests.cs, class PcbResultTests.
```
[Fact]
public void Parse_UnmappedPins_BlocksOk_AndSurfacesThem()
{
    // build_board.py (after the fix) emits unmappedPins for a named footprint whose net pin had no pad.
    var json = \"{\\\"ok\\\":true,\\\"out\\\":\\\"x.kicad_pcb\\\",\\\"components\\\":1,\\\"nets\\\":1,\\\"unmappedPins\\\":[{\\\"ref\\\":\\\"U1\\\",\\\"pin\\\":\\\"GPIO34\\\",\\\"net\\\":\\\"SIG\\\",\\\"footprint\\\":\\\"RF_Module:ESP32-WROOM-32\\\"}],\\\"byPosition\\\":[],\\\"notes\\\":[]}\";
    var r = PcbResult.Parse(json, \"\", 0, \"x.kicad_pcb\");
    Assert.False(r.Ok);                         // FAILS today (Parse ignores unmappedPins, Ok stays true)
    Assert.Null(r.KicadPcbPath);
    Assert.Single(r.UnmappedPins);              // FAILS today (field doesn't exist)
    Assert.Contains(r.Notes, n => n.Contains(\"Connectivity unverified\"));
}
```
This compiles only after the PcbResult.UnmappedPins field is added, and the Ok assertion fails until Parse forces Ok=false — exactly the regression we are closing.

Additional PcbResult tests:
- Parse_ByPositionOnHeader_StaysOk: json with `ok:true`, empty unmappedPins, byPosition=[{ref:J1,pin:VCC,pad:1,...}], file exists ⇒ r.Ok==true, r.ByPositionCount==1 (headers are still allowed).
- Parse_NoUnmapped_BackCompat: existing happy-path JSON without the new keys ⇒ r.Ok==true, r.UnmappedPins empty (proves additive/back-compat; existing Parse_Ok_RequiresExistingFile keeps passing).

PcbDesigner layer (Foundry.Tests/PcbRoutingTests.cs or a new PcbDesignerTests):
- RunLoop_BlocksOnUnmappedPins_NoRouteNoExport: fake BuildStep returns `PcbResult` (Installed:true, Ok:false, UnmappedPins:[\"U1.GPIO34 -> net SIG: no pad on RF_Module:ESP32-WROOM-32\"]). RouteStep/DrcStep set a `bool routed=false/drcd=false` flag when invoked. Assert result.Ok==false, summary contains \"connectivity unverified\", routed==false, drcd==false (route/DRC never ran). FAILS today because today's RunLoopAsync would still return Failed with a generic message but more importantly there's no UnmappedPins field — and the dedicated message branch is the assertion.
- DesignAndExportFab_BlockedBuild_DoesNotExport: drive DesignAndExportFabAsync via a fake — simpler to assert at the RunLoop level since ExportAsync needs KiCad; assert that with a build that has UnmappedPins, the returned design.Ok is false (which is the precondition the fab gate at PcbDesigner.cs:124 relies on).

build_board.py layer (pure-Python unit test, no KiCad — extract pads_are_pure_numeric to be importable, or test via a tiny fake pad object exposing GetName()):
File: new Foundry.Core/Pcb/KiCadScripts/test_build_board.py (pytest), run only where python is available; guard import of pcbnew so the helper tests don't require it (move `import pcbnew` usage so pads_are_pure_numeric has no pcbnew dependency):
- test_pure_numeric_header_true: names [\"1\",\"2\",\"3\"] ⇒ True.
- test_named_pads_false: names [\"VCC\",\"GND\",\"SDA\"] ⇒ False.
- test_mixed_false: [\"1\",\"VCC\"] ⇒ False.
- test_gaps_false: [\"1\",\"3\"] ⇒ False (not contiguous 1..N).
(If adding a pytest file is out of scope for CI, cover the header-vs-named decision indirectly through the PcbResult contract tests above, which assert the JSON the script must produce.)

UI layer (only if a ViewModel test harness exists — check; PcbPlacementTests etc. are core-only): if there is no WPF VM test infra, document the CanExportFab gating as covered by manual verification + the core gate. Do NOT add a WPF test project just for this.

Full run: `dotnet test Foundry.Tests` — all existing PcbTests / PcbRealPlacementTests / PcbRoutingTests must stay green (the PcbResult change is additive with defaults; the build_board.py change is a no-op for headers, the common case in those tests).

### Risks



---

## P0-2: Real end-to-end PCB/Gerber/DRC test that RUNS with KiCad and asserts pad->net intent (plus a pytest for build_board.py pad assignment, and a CI lane with KiCad)

**Effort:** L | **dependsOn:** none

### Current state

Today the positive (KiCad-present) path of the entire Track B toolchain is NEVER exercised by any test, because every toolchain test early-returns the moment KiCad is found:

- FabExportTests.cs:287 `if (KiCadInstaller.Locate() is not null) return;   // guard: real install present, skip` (ExportAsync_ReturnsNotInstalled_WhenKiCadAbsent); same at :310 (NotInstalled_TakesPrecedenceOverMissingInput) and :365 (PcbDesignerFabExportTests.DegradesToNotInstalled_WhenKiCadAbsent).
- PcbDrcTests.cs:276 `if (KiCadInstaller.Locate() is not null) return;   // guard: real install present, skip` (CheckAsync_ReturnsNotInstalled_WhenKiCadAbsent); same at :293.
- PcbTests.cs:378 `if (KiCadInstaller.Locate() is not null) return;` (PcbBuilderTests.BuildAsync_ReturnsNotInstalled_WhenKiCadAbsent).
- PcbRoutingTests.cs:288-291 defines `FullyAvailable()` = KiCad+Java+jar present, then :297 `if (FullyAvailable()) return;` and :315 — so a fully provisioned box skips routing entirely.

The net effect: on a developer/CI box WITH KiCad, all these tests no-op. There is zero coverage that a real board builds, routes, DRCs clean, and exports parseable Gerbers/drill — and zero coverage that named pad->net intent actually lands on the right physical pad in the saved .kicad_pcb. The release.yml `Test` step (.github/workflows/release.yml:77-78 `- name: Test` / `run: dotnet test Foundry.Tests -c Release`) runs on windows-latest with NO KiCad install step anywhere in the job (steps are checkout, setup-dotnet, publish, verify-integrity, sign, test, setup-python, pyinstaller, innosetup, build installer) — so the positive path could never run in CI even if the guards were inverted.

The pure machinery that the positive path would exercise is real and well-factored:
- PcbBuilder.BuildAsync (PcbBuilder.cs:52-103) locates KiCad (line 56-57 `var kicad = KiCadInstaller.Locate(); if (kicad is null) return PcbResult.NotInstalled();`), builds a PcbJob, writes build_board.py + job.json to a temp dir (lines 80-83), runs `kicad.PythonPath "<script>" "<jobPath>"` (line 86), and parses via PcbResult.Parse. PcbBuilder.ReadScript() (lines 183-190) is public and returns the embedded script text.
- build_board.py does the pad->net assignment in two passes: pass 1 exact pad-name match case-insensitive (lines 165-177), pass 2 ordinal fallback to the next free pad (lines 179-191), appending notes `"component %s: pin '%s' -> pad '%s' by position"` (line 191) and `"component %s: no free pad for net node '%s' (%s has %d pads)"` (line 184). `import pcbnew` is at module top (line 22), so the file cannot currently be imported by pytest without KiCad on the path.
- PcbJob.Build (PcbJob.cs:59-125) resolves footprints, builds PadNets (name-keyed) + PadNetList (ordered), and emits a closed rectangular Edge.Cuts outline (OutlineSegmentsMm). Net naming (KiCadNetlist.cs:158-168 NetName) renames pins: GND/GROUND/VSS -> "GND"; 3V3/VCC/VDD/5V/VIN... -> "+3V3"/"+VCC"/...; SDA/SCL kept as-is.
- PcbDrc.CheckAsync (PcbDrc.cs:21-55) runs `kicad-cli pcb drc --format json` and parses DrcReport. GerberExporter.ExportAsync (GerberExporter.cs:78-134) runs `pcb export gerbers` then `pcb export drill`, validates via FabFileSet.Validate, and zips to `<name>-fab.zip`. PcbDesigner.DesignAndExportFabAsync (PcbDesigner.cs:120-135) is the full capstone (design loop -> fab zip).

Test idiom for guarded live tests already exists: Avr8jsLiveSmokeTest.cs uses a `private static string? SkipReason()` (lines 74-83) that returns a reason string when the toolchain is absent, and the [Fact] writes it to ITestOutputHelper and `return`s (lines 88-93) — a graceful skip that keeps the suite green on bare machines. There is no xunit Skip/SkippableFact package referenced (Foundry.Tests.csproj:12-16 has only xunit 2.5.3, test sdk, coverlet) and no existing pytest infrastructure for the project (the only conftest/test_*.py files are inside sidecar/.venv site-packages). Foundry.Core.csproj has `InternalsVisibleTo Foundry.Tests` (line 33) and embeds the three KiCadScripts .py files (lines 26-30).

### File edits

#### `Foundry.Tests/PcbLiveToolchainTests.cs`

NEW FILE. The C# live end-to-end test that RUNS only when the real toolchain is present and asserts pad->net intent by reading back the saved .kicad_pcb, DRC verdict, and Gerber/drill parseability. This is the inversion of today's guards: instead of `if (Locate() is not null) return;` it does `var skip = SkipReason(); if (skip is not null) { _out.WriteLine(skip); return; }` and then runs the positive path. Mirror Avr8jsLiveSmokeTest.cs's SkipReason()/ITestOutputHelper idiom exactly.

Namespace Foundry.Tests; usings: System.IO.Compression, Foundry.Core.Pcb, Foundry.Core.Pcb.Fab, Foundry.Core.Project, Foundry.Core.Kb, Xunit.Abstractions.

class PcbLiveToolchainTests { private readonly ITestOutputHelper _out; ctor(output)=>_out=output; }

// Skip helper — KiCad is the floor; routing is checked separately so DRC/Gerber still run with KiCad alone.
private static string? KiCadSkip() => KiCadInstaller.Locate() is null ? "KiCad not installed — skipping live PCB toolchain test." : (KiCadInstaller.Locate()!.PythonPath is var py && !File.Exists(py) ? "KiCad python.exe missing — skipping." : null);
private static bool RouterAvailable() => KiCadInstaller.Locate() is not null && FreeRoutingInstaller.LocateJava() is not null && FreeRoutingInstaller.JarPresent;

// Known-good 3-part fixture: ESP32 + BME280 sensor + decoupling cap, NAMED pins so pad->net intent is checkable.
private static Project Fixture() => new() {
  Title = "LiveFixture",
  Components = new() {
    new ComponentSpec { Alias="U1", Ref="esp32", Name="ESP32-WROOM-32" },
    new ComponentSpec { Alias="U2", Ref="bme280", Name="BME280" },
    new ComponentSpec { Alias="C1", Ref="cap", Name="100nF capacitor" },
  },
  Connections = new() {
    new Connection { From="U1.3V3", To="U2.VCC", Net="power" },   // names -> net "+3V3"
    new Connection { From="U2.VCC", To="C1.1",  Net="power" },
    new Connection { From="U1.GND", To="U2.GND", Net="ground" },  // -> net "GND"
    new Connection { From="U2.GND", To="C1.2",  Net="ground" },
    new Connection { From="U1.GPIO21", To="U2.SDA", Net="i2c" }, // -> net "SDA"
    new Connection { From="U1.GPIO22", To="U2.SCL", Net="i2c" }, // -> net "SCL"
  },
};

[Fact] public async Task LiveBoard_BuildsRoutesDrcsAndExports_WithPadNetIntent():
  1) var skip = KiCadSkip(); if (skip is not null) { _out.WriteLine(skip); return; }
  2) var outDir = Path.Combine(Path.GetTempPath(), "foundry_live_"+Guid.NewGuid().ToString("N")[..8]); try { ... } finally { Directory.Delete(outDir, true) if exists }.
  3) BUILD: var build = await PcbBuilder.BuildAsync(Fixture(), outDir);  Assert.True(build.Installed); Assert.True(build.Ok, build.Summary); Assert.NotNull(build.KicadPcbPath); Assert.True(File.Exists(build.KicadPcbPath!));
  4) (a) PAD->NET READBACK — read the saved board text and assert named intent landed on a physical pad. var pcb = File.ReadAllText(build.KicadPcbPath!); Use a small private parser ParsePadNets(pcb) (see specifics below) that returns the set of (footprintRef, padName, netName). Assert that net "+3V3" connects to a pad on U1 AND U2 AND C1; "GND" connects to U1,U2,C1; "SDA" connects to U1 and U2; "SCL" connects to U1 and U2. Concretely: Assert.Contains(("U2","SDA"), padNetsByRef) i.e. assert there exists a pad on U2 carrying net SDA, a pad on U1 carrying SDA, etc. Also assert NO net node was dropped: every expected net name {"+3V3","GND","SDA","SCL"} appears in the parsed pad-net set. This is the load-bearing 'pad->net intent' assertion the P0 demands.
  5) (c-partial) EDGE.CUTS CLOSED — assert the saved board has >=4 Edge.Cuts gr_line/segment entries forming a closed loop: parse the (gr_line ... (layer "Edge.Cuts")) start/end points, assert the multiset of endpoints has every vertex appearing an even number of times (closed polygon) and >=4 segments. (Mirror PcbTests.cs Build_ProducesRectangularOutline closure check but on the real file.)
  6) ROUTE (optional): if (RouterAvailable()) { var routed = await PcbRouter.RouteAsync(build.KicadPcbPath!); Assert.True(routed.Installed); if (routed.Ok) boardForDrc = routed.RoutedPcbPath!; else boardForDrc = build.KicadPcbPath!; } else boardForDrc = build.KicadPcbPath!.  _out.WriteLine the route summary.
  7) (b) DRC VERDICT MATCHES REALITY: var drc = await PcbDrc.CheckAsync(boardForDrc); Assert.True(drc.Installed); Assert.True(drc.Ok, drc.Summary);  // ran cleanly (exit 0 or 5, parsed). Assert that drc.Clean implies drc.ErrorCount==0 && drc.UnconnectedCount==0, and !drc.Clean implies (ErrorCount>0 || UnconnectedCount>0) — i.e. the verdict is internally consistent with the counts (the 'matches reality' check that DrcReport.Clean is not lying). Do NOT hard-require Clean on the unrouted board (an unrouted board legitimately has unconnected nets); instead: if boardForDrc is the routed board and routed.FullyRouted, Assert.True(drc.Clean, drc.Summary). If unrouted, Assert.True(drc.UnconnectedCount > 0, "an unrouted board must report unconnected nets") — proving DRC actually sees the pad->net membership we wrote.
  8) (c) GERBER/DRILL EXPORT + PARSE: var fab = await GerberExporter.ExportAsync(boardForDrc, outDir); Assert.True(fab.Installed); Assert.True(fab.Ok, fab.Summary); Assert.NotNull(fab.ZipPath); using var zip = ZipFile.OpenRead(fab.ZipPath!); Assert.True(zip.Entries.Count >= 5). For each entry: read its text; if name ends ".gtl"/".gbl"/".gm1" (Gerber) Assert it contains the RS-274X format header "%FSLAX" or starts with a G-code line and contains "M02*" (end-of-file); if name ends ".drl" (Excellon) Assert it contains "M48" (header) and "M30" (end). Assert the Edge.Cuts gerber ("-Edge_Cuts.gm1" or matching) is non-empty (Length>0) and contains at least one aperture flash/draw — proving a closed outline was plotted.

// Private readback parser — pad->net intent from a saved KiCad 9/10 s-expr board.
private static List<(string Ref,string Pad,string Net)> ParsePadNets(string pcb):
  KiCad saves each footprint as `(footprint "lib:fp" ... (property "Reference" "U1" ...) ... (pad "23" smd ... (net 3 "SDA")) ...)`. Implement a tolerant scanner: split into footprint blocks by locating `(footprint` tokens with brace-depth tracking; within each block capture the Reference property value via regex `\(property \"Reference\" \"([^\"]+)\"`; capture every pad via regex over `\(pad \"([^\"]+)\"[\s\S]*?\(net \d+ \"([^\"]+)\"\)` (non-greedy, but bounded to the pad's own parens — simplest robust approach is a regex that finds `(pad "<name>"` then within the next ~400 chars looks for `(net <code> "<net>")`). Return (ref, padName, netName) for every pad that has a net. NOTE: pads with no assigned net have no `(net ...)` child or `(net 0 "")` — skip net code 0 / empty. This regex approach is acceptable here because we only assert presence of expected (ref,net) pairs, not exhaustive structure.

IMPORTANT BEHAVIOR NOTE: The TWO EXISTING negative tests that today say `if (Locate() is not null) return;` (FabExportTests ExportAsync_ReturnsNotInstalled_WhenKiCadAbsent etc.) are LEFT AS-IS — they correctly cover the degrade path on bare boxes. We are ADDING the positive-path coverage, not deleting the negative-path coverage. The 'invert the guards' direction from the review is realized as a NEW guarded-positive test rather than mutating the existing negative tests (keeps both paths covered).

#### `Foundry.Core/Pcb/KiCadScripts/build_board.py`

Make the pure pad-assignment logic importable by pytest WITHOUT KiCad on the path. Today `import pcbnew` at module top (line 22) makes the whole module unimportable off a KiCad box, so the pad-assignment algorithm cannot be unit-tested in isolation. Extract the two-pass assignment into a pure helper `assign_pads(pad_names, pad_net_list, known_nets)` that takes plain strings (no pcbnew objects) and returns (assignments, notes), then have build() call it. Guard the pcbnew import so importing the module for the pure helper does not require pcbnew.

1) Replace the top-level `import pcbnew` (line 22) with a lazy/guarded import so the module imports without KiCad:
   try:
       import pcbnew
   except Exception:  # noqa: BLE001 — pure helpers (assign_pads) are importable without KiCad for unit tests
       pcbnew = None
2) Add a NEW pure function (no pcbnew use) that encodes EXACTLY the current two-pass algorithm so pytest can assert it:
   def assign_pads(pad_names, pad_net_list, known_nets, ref='?', lib_id='?'):
       """Pure pad->net resolver mirroring build()'s two passes. pad_names: list[str] (footprint pad names in order, may repeat e.g. '1','2' or '23'..); pad_net_list: list[{pin,net}] in netlist order; known_nets: set/dict of valid net names. Returns (assignments, notes) where assignments is a list aligned to pad_names of net-name-or-None, and notes are the same strings build() appends."""
   Logic (lift verbatim from lines 158-191, but operating on pad NAMES not pad objects):
     used=set(); assignments=[None]*len(pad_names); notes=[]
     def can(n): return bool(n) and n in known_nets
     # pass 1: exact pad-name match (case-insensitive)
     deferred=[]
     for item in pad_net_list:
         pin=str(item.get('pin','')); net=item.get('net'); matched=False
         for i,pn in enumerate(pad_names):
             if i not in used and pn.lower()==pin.lower():
                 if can(net): assignments[i]=net; used.add(i)
                 matched=True; break
         if not matched: deferred.append((pin,net))
     # pass 2: ordinal
     free=[i for i in range(len(pad_names)) if i not in used]; fi=0
     for pin,net in deferred:
         if fi>=len(free):
             notes.append("component %s: no free pad for net node '%s' (%s has %d pads)" % (ref,pin,lib_id,len(pad_names))); continue
         i=free[fi]; fi+=1
         if can(net): assignments[i]=net; used.add(i)
         if pad_names[i].lower()!=pin.lower():
             notes.append("component %s: pin '%s' -> pad '%s' by position" % (ref,pin,pad_names[i]))
     return assignments, notes
3) Refactor build()'s inner loop (current lines 151-191) to delegate to assign_pads: derive pad_names = [p.GetName() for p in pads]; known_nets = set(nets.keys()); assignments,notes2 = assign_pads(pad_names, pad_net_list, known_nets, comp['ref'], lib_id); notes.extend(notes2); then apply: for i,net_name in enumerate(assignments): if net_name: pads[i].SetNet(nets[net_name]). This keeps runtime behavior byte-identical (same notes, same assignment order) while making the algorithm testable. Keep the `assign(pad, net_name)` closure removed (its role is now inside assign_pads' can()).
No signature change to main()/build()/measure(). The note strings MUST remain identical so PcbResult/PcbBuilder note-folding is unaffected.

#### `Foundry.Core/Pcb/KiCadScripts/test_build_board.py`

NEW FILE. The pytest the P0 asks for: pure unit tests of build_board.py pad assignment (named-pin-on-numbered-pad + pad-count-mismatch). Imports the module via importlib without requiring pcbnew (thanks to the guarded import above).

import importlib.util, os, sys
# Load the sibling build_board.py by path so the test runs from anywhere (CI/dev), no package install.
_HERE=os.path.dirname(os.path.abspath(__file__)); _SPEC=importlib.util.spec_from_file_location('build_board', os.path.join(_HERE,'build_board.py')); bb=importlib.util.module_from_spec(_SPEC); _SPEC.loader.exec_module(bb)

def test_named_pin_on_numbered_pad_assigns_by_ordinal():
    # Generic fallback header: pads named '1'..'3'; netlist addresses by NAME (VCC/AOUT/GND).
    pads=['1','2','3']; pad_net_list=[{'pin':'VCC','net':'+3V3'},{'pin':'AOUT','net':'SIG'},{'pin':'GND','net':'GND'}]; nets={'+3V3','SIG','GND'}
    a,notes=bb.assign_pads(pads,pad_net_list,nets,'S1','Connector:Header')
    assert a==['+3V3','SIG','GND']            # ordinal fallback wired every node
    assert any('by position' in n for n in notes)  # each named pin landed by position, noted

def test_exact_pad_name_match_takes_precedence_over_ordinal():
    pads=['VCC','GND','AOUT']; pad_net_list=[{'pin':'AOUT','net':'SIG'},{'pin':'VCC','net':'+3V3'},{'pin':'GND','net':'GND'}]; nets={'+3V3','SIG','GND'}
    a,notes=bb.assign_pads(pads,pad_net_list,nets,'U1','lib:fp')
    assert a==['+3V3','GND','SIG']            # matched by NAME not order
    assert not any('by position' in n for n in notes)  # all exact-matched, no positional notes

def test_pad_count_mismatch_more_nodes_than_pads_records_no_free_pad_note():
    pads=['1','2']; pad_net_list=[{'pin':'A','net':'N1'},{'pin':'B','net':'N2'},{'pin':'C','net':'N3'}]; nets={'N1','N2','N3'}
    a,notes=bb.assign_pads(pads,pad_net_list,nets,'X1','lib:fp')
    assert a==['N1','N2']                     # only two pads got nets
    assert any("no free pad for net node 'C'" in n and 'has 2 pads' in n for n in notes)

def test_unknown_net_name_leaves_pad_unassigned():
    pads=['1']; pad_net_list=[{'pin':'1','net':'GHOST'}]; nets={'REAL'}
    a,notes=bb.assign_pads(pads,pad_net_list,nets,'X1','lib:fp')
    assert a==[None]                          # net not in known_nets -> not assigned (mirrors can())

def test_case_insensitive_pad_name_match():
    pads=['Sda','Scl']; pad_net_list=[{'pin':'SDA','net':'SDA'},{'pin':'scl','net':'SCL'}]; nets={'SDA','SCL'}
    a,_=bb.assign_pads(pads,pad_net_list,nets,'U1','lib:fp')
    assert a==['SDA','SCL']

#### `.github/workflows/release.yml`

Add a SEPARATE CI lane (a new job) that installs KiCad + Python and runs BOTH the C# live positive-path test AND the pytest, so the positive path can't rot. Do NOT add KiCad to the existing `build` job (that job's `dotnet test` at line 78 must stay fast and bare-box-clean as the degrade-path guarantee). Add the new job to run on push tags and workflow_dispatch alongside build (it does not gate the release publish, but surfaces red when the positive path breaks).

Add a top-level job after the `build:` job (sibling under `jobs:`):

  pcb-live:
    name: PCB live toolchain (KiCad)
    runs-on: windows-latest
    steps:
      - uses: actions/checkout@v4
      - uses: actions/setup-dotnet@v4
        with: { dotnet-version: '8.0.x' }
      - uses: actions/setup-python@v5
        with: { python-version: '3.12' }
      - name: Install KiCad
        run: choco install kicad -y --no-progress    # lands kicad-cli + bundled python where KiCadInstaller.Locate() probes (Program Files\KiCad\<ver>\bin)
      - name: Install pytest
        run: python -m pip install --upgrade pytest
      - name: Pytest build_board.py pad assignment
        run: python -m pytest Foundry.Core/Pcb/KiCadScripts/test_build_board.py -q
      - name: C# live PCB toolchain test
        run: dotnet test Foundry.Tests -c Release --filter "FullyQualifiedName~PcbLiveToolchainTests"

Notes for the implementer:
- choco's `kicad` package installs to Program Files\KiCad\<ver>\bin which is exactly KiCadInstaller.KiCadRoots()[0]; verify the version dir contains kicad-cli.exe + python.exe (KiCadInstaller.Locate requires python.exe at bin\python.exe, line 48). If choco lands a layout without bundled python.exe, the C# SkipReason will gracefully skip (test stays green) — acceptable, but log it; preferred is to assert KiCad located by adding a quick `kicad-cli version` smoke step before the dotnet test.
- The pytest path uses forward slashes which work in PowerShell on windows-latest.
- Do not set `continue-on-error`; we WANT this lane red when the positive path breaks. It is a separate job so it does not block the existing release publish steps (which live in `build`).

### Test plan

TDD — write the FAILING test FIRST, in this order:

1) FAILING-FIRST pytest (fastest, no KiCad): Add Foundry.Core/Pcb/KiCadScripts/test_build_board.py with the five cases above. Run `python -m pytest Foundry.Core/Pcb/KiCadScripts/test_build_board.py -q`. Against TODAY's build_board.py it FAILS at COLLECTION: the module's top-level `import pcbnew` (line 22) raises ModuleNotFoundError, and `bb.assign_pads` does not exist (AttributeError). This proves the test exercises new behavior. Then apply the build_board.py edit (guarded import + extracted assign_pads) and the same command goes GREEN. This is the primary TDD loop because it runs on any box.

2) C# live positive-path test: Add Foundry.Tests/PcbLiveToolchainTests.cs. On a box WITHOUT KiCad it skips (writes KiCadSkip() reason, returns) — so it never red-fails the bare suite. On a box WITH KiCad it must pass. To demonstrate it is load-bearing, temporarily break build_board.py's pass-1 match (e.g. compare pin to wrong field) and confirm the pad->net readback assertion (step 4) fails — then revert. Concrete assertions (from fileEdits step (a)/(b)/(c)):
   - (a) pad->net: parsed (ref,net) set from the saved .kicad_pcb contains a U1+U2+C1 pad on \"+3V3\", U1+U2+C1 on \"GND\", U1+U2 on \"SDA\", U1+U2 on \"SCL\"; and every expected net name appears (no node dropped).
   - Edge.Cuts: >=4 Edge.Cuts segments, endpoints form a closed loop (each vertex even-degree).
   - (b) DRC consistency: drc.Installed && drc.Ok; drc.Clean <=> (ErrorCount==0 && UnconnectedCount==0); routed+FullyRouted => Clean; unrouted => UnconnectedCount>0.
   - (c) Gerber/drill: zip opens, >=5 entries; each .gtl/.gbl/.gm1 contains RS-274X markers (\"%FSLAX\"/\"M02*\"); each .drl contains Excellon \"M48\"+\"M30\"; Edge.Cuts gerber non-empty.

3) Regression guard for existing suite: run full `dotnet test Foundry.Tests -c Release` on the dev box. All existing pure tests (FabExportTests, PcbDrcTests, PcbTests, PcbRealPlacementTests, PcbRoutingTests) MUST still pass unchanged — the build_board.py refactor must keep note strings byte-identical (PcbResultTests/PcbTests assert specific note substrings via PcbResult.Parse). Verify no PcbResult.Parse-driven assertion changed.

4) CI lane: push a throwaway tag (or workflow_dispatch) to confirm the new pcb-live job installs KiCad via choco, runs pytest green, and either runs-and-passes or cleanly-skips the C# live test. Confirm `build` job is unchanged and still bare (no KiCad).

### Risks

- build_board.py refactor could change a note string and break existing C# assertions: PcbResultTests/PcbTests assert exact note substrings parsed by PcbResult.Parse (e.g. PcbTests.cs:279 expects a 'warning' generic-footprint fallback; Build_EmitsErrorWhenNodeHasNoPad). The 'by position' and 'no free pad' notes (build_board.py:184,191) must stay byte-identical — copy them verbatim into assign_pads. Mitigate by diffing the produced notes before/after on a real build.
- The .kicad_pcb readback parser is regex-based against KiCad's s-expression format, which differs subtly across KiCad 8/9/10 (pad net stored as `(net <code> "<name>")` inside each pad; Reference stored as `(property "Reference" "U1")` in KiCad 7+ but `(fp_text reference U1 ...)` in older saves). The CI lane pins KiCad 10 via choco, but a dev box on KiCad 9 could parse differently. Mitigate: make the parser tolerant of both Reference forms; if it cannot find any Reference, _out.WriteLine and skip rather than false-fail.
- An unrouted board's DRC will report unconnected nets — the test must NOT assert Clean on the unrouted board, or it will red-fail on KiCad-only boxes (no Java/jar). The plan asserts UnconnectedCount>0 in that case instead, which is actually a STRONGER proof that pad->net membership was written. If FreeRouting IS present, the routed board may still not be FullyRouted on a dense ESP32 fixture — only assert Clean when routed.FullyRouted is true.
- choco's kicad package layout: if it does not place python.exe under bin (KiCadInstaller.Locate requires it, line 48), the C# test will skip and provide no positive coverage in CI even though the lane is green. Add a `kicad-cli version` + python.exe existence smoke step to make a misconfigured install fail loudly rather than silently skip.
- Gerber/Excellon marker assertions are format-version sensitive: KiCad's protel-extension + X2 output (kept per GerberExporter, no --no-x2) uses %FSLAX and M02* / Excellon M48..M30 — stable across 8/9/10, but if a future KiCad changes EOF tokens the assertion could break. Keep marker checks minimal (presence of header + EOF + non-empty) rather than full RS-274X parsing.
- The live C# test spawns real kicad-cli/python and can be slow (10-40s) and could hang if a process blocks; rely on the existing RunAsync (no explicit timeout) — consider passing a CancellationToken with a deadline in the test to avoid CI hangs.
- Adding pcb-live as a separate job that does not gate publish means a broken positive path won't block a release tag; this is intentional (the review wants it 'can't rot', i.e. visible red) but a stricter team may want `needs: pcb-live` on a future gating job — out of scope here.

---

## P0-3: Timeout + process-tree kill + UI cancellation on all PCB/firmware subprocess runs

**Effort:** L | **dependsOn:** none

### Current state

All five subprocess sites use the SAME byte-identical private `RunAsync` helper, and every one reads stdout to completion, THEN stderr to completion, THEN waits for exit — the classic pipe-buffer deadlock (a child that fills stderr while we block on stdout's ReadToEndAsync hangs forever), with no timeout and no kill-on-cancel.

PcbRouter.cs:93-105 — `private static async Task<(string stdout, string stderr, int code)> RunAsync(string exe, string args, CancellationToken ct)`: `using var p = Process.Start(psi)!; var o = await p.StandardOutput.ReadToEndAsync(ct); var e = await p.StandardError.ReadToEndAsync(ct); await p.WaitForExitAsync(ct); return (o, e, p.ExitCode);`. This is invoked 3x in RouteAsync (lines 55, 64, 78) including the long FreeRouting run (line 64). RouteAsync's outer `catch (OperationCanceledException) { throw; }` (line 84) rethrows but the child is NEVER killed.

PcbBuilder.cs:168-180 — identical private `RunAsync`, called at line 86 (build_board.py) and line 136 (measure).

PcbDrc.cs:87-99 — identical private `RunAsync`, called at line 40 (kicad-cli pcb drc).

GerberExporter.cs:138-150 — identical private `RunAsync`, called at lines 97 (gerbers) and 98 (drill).

FirmwareBuilder.cs has TWO copies of the same pattern: the private `RunAsync` at 363-371 (used by ListBoardsAsync/EnsureCoreAsync/UploadAsync/DownloadCliAsync), PLUS inline duplicates at 119-122 (CompileAsync) and 182-185 (CompileToImageAsync): `using var proc = Process.Start(psi)!; var stdout = await proc.StandardOutput.ReadToEndAsync(ct); var stderr = await proc.StandardError.ReadToEndAsync(ct); await proc.WaitForExitAsync(ct);`. The upload (line 327) and compile runs have no timeout.

PcbDesigner.cs LOOPS the router: `RunLoopAsync` (143-231) runs build→route→drc up to `maxIterations` (default 3, from DrcOptions.MaxIterations:9). Each iteration calls `build` (line 161 → PcbBuilder.BuildAsync), `route` (line 169 → PcbRouter.RouteAsync), `drc` (line 175 → PcbDrc.CheckAsync). It threads `ct` correctly and keeps a BEST board (`bestPath`/`bestReport`, IsBetter at 234-241) even when exhausted (lines 222-231). DesignAsync (79-110) and DesignAndExportFabAsync (120-135) pass `ct` through. So one hung router with no timeout hangs the entire loop forever.

WiringViewModel (Foundry.App/ViewModels/TabViewModels.cs:316) passes NO CancellationToken to ANY PCB call and has NO cancel command:
- ExportPcb (412-446): `PcbBuilder.BuildAsync(Project, dir)` (423) then RouteCore.
- RoutePcb (453-462) / RouteCore (465-497): `PcbRouter.RouteAsync(pcbPath)` (477), DrcCore.
- DrcCore (503-537): `PcbDrc.CheckAsync(boardPath)` (506).
- DesignPcb (545-603): `PcbDesigner.DesignAsync(Project, dir, ai, model, options)` (564).
- ExportFab (611-620)/ExportFabCore (623-649): `GerberExporter.ExportAsync(boardPath, dir)` (629).
- DesignAndExportFab (656-711): `PcbDesigner.DesignAndExportFabAsync(Project, dir, ai, model, options)` (674).
Every one guards re-entry with `if (IsExportingPcb) return;` then `IsExportingPcb = true; ... finally { IsExportingPcb = false; }` — but there is no way to interrupt a stuck run.

The firmware tab is the SAME: VerifyBuild (1308) `CompileAsync(Project)`, FixBuild (1334), Flash (1398) `UploadAsync(Project, SelectedBoard)`, DetectBoards (1373) — all call without a token; guarded by IsBuilding/IsFlashing only.

The pattern that ALREADY EXISTS and we should mirror: SimulationViewModel has `_cts` (876) + RUN (915, `_cts = new CancellationTokenSource();` 924, passes `_cts.Token` 927, `catch (OperationCanceledException) { Status = "Start cancelled."; }` 946) + STOP (965-980, `_cts?.Cancel();`). EnclosureViewModel has `_scadCts` (1045) + `[RelayCommand] private void CancelScad() => _scadCts?.Cancel();` (1046) and threads `_scadCts.Token` (1138) with `catch (OperationCanceledException) { ScadStatus = "Render cancelled."; }` (1160). These are the exact idioms to copy for `_pcbCts`.

Tests use xUnit (`[Fact]`), net8.0, guard-skip when toolchain present (PcbRoutingTests.cs:297 `if (FullyAvailable()) return;`). Pure helpers are public and directly unit-tested (BuildArgs, Parse, ReadScript). There is currently NO test that exercises process timeout/kill because RunAsync is private and bound to a real process.

### File edits

#### `Foundry.Core/Diagnostics/ProcessRunner.cs`

NEW FILE. Extract the five duplicated private RunAsync helpers into ONE shared, public, testable runner that (a) fixes the pipe-buffer deadlock by starting BOTH stream reads before awaiting either, (b) enforces a timeout via a linked CTS with CancelAfter, and (c) kills the whole process tree in finally on timeout/cancel. Public so it is directly unit-testable (matches the codebase's public-helper convention: BuildArgs/Parse/ReadScript).

```csharp
using System.Diagnostics;
namespace Foundry.Core.Diagnostics;

/// <summary>Outcome of a child-process run. <see cref="TimedOut"/> distinguishes a watchdog kill
/// (CancelAfter) from a caller cancel; on either, <see cref="ExitCode"/> is non-zero (we killed it).</summary>
public readonly record struct ProcRun(string Stdout, string Stderr, int ExitCode, bool TimedOut);

public static class ProcessRunner
{
    /// <summary>Default timeouts by call-site class. kicad-cli/pcbnew are fast; FreeRouting is the long pole.</summary>
    public static readonly TimeSpan KicadTimeout = TimeSpan.FromSeconds(120);
    public static readonly TimeSpan RouterTimeout = TimeSpan.FromMinutes(10);
    public static readonly TimeSpan ArduinoTimeout = TimeSpan.FromMinutes(5); // core install/compile/upload

    /// <summary>Start <paramref name="exe"/> with <paramref name="args"/>, drain stdout+stderr concurrently
    /// (no pipe-buffer deadlock), bounded by <paramref name="timeout"/> AND <paramref name="ct"/>. On timeout
    /// or cancel the ENTIRE process tree is killed before returning/throwing. Caller cancel -> throws
    /// OperationCanceledException (preserves existing `catch(OCE){throw;}` semantics). Watchdog timeout ->
    /// returns ProcRun with TimedOut=true, ExitCode=-1 (callers translate to a Failed result + keep best board).</summary>
    public static async Task<ProcRun> RunAsync(string exe, string args, TimeSpan timeout, CancellationToken ct = default)
    {
        var psi = new ProcessStartInfo
        {
            FileName = exe, Arguments = args,
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true,
        };
        using var p = Process.Start(psi)!;
        // Distinguish the watchdog (timeout) from a real caller cancel.
        using var timeoutCts = new CancellationTokenSource(timeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);
        // Start BOTH reads first (no await) so neither pipe buffer can fill and block the other.
        var outTask = p.StandardOutput.ReadToEndAsync(linked.Token);
        var errTask = p.StandardError.ReadToEndAsync(linked.Token);
        try
        {
            await p.WaitForExitAsync(linked.Token);
            var o = await outTask;
            var e = await errTask;
            return new ProcRun(o, e, p.ExitCode, false);
        }
        catch (OperationCanceledException)
        {
            // Kill the whole tree (FreeRouting/arduino-cli spawn children) then drain best-effort output.
            try { if (!p.HasExited) p.Kill(entireProcessTree: true); } catch { }
            string o = "", e = "";
            try { o = await outTask; } catch { }
            try { e = await errTask; } catch { }
            bool timedOut = timeoutCts.IsCancellationRequested && !ct.IsCancellationRequested;
            if (timedOut)
            {
                AppLog.Warn("proc", $"timed out after {timeout.TotalSeconds:0}s, killed process tree: {System.IO.Path.GetFileName(exe)}");
                return new ProcRun(o, e + $"\n[timed out after {timeout.TotalSeconds:0}s — process killed]", -1, true);
            }
            ct.ThrowIfCancellationRequested(); // caller cancel -> propagate (existing OCE contract)
            return new ProcRun(o, e, -1, true); // defensive (shouldn't reach)
        }
    }
}
```
Note: `Process.Kill(bool)` exists since .NET Core 3.0 — already used implicitly nowhere, but valid on net8.0. ReadToEndAsync(token) cancels cleanly so the kill happens promptly.

#### `Foundry.Core/Pcb/PcbRouter.cs`

Delete the private RunAsync (lines 93-105); route all three call sites through ProcessRunner with RouterTimeout for the FreeRouting run and KicadTimeout for the two pcbnew (export DSN / import SES) runs. Translate a watchdog timeout into RouteResult.Failed so the loop keeps the best board instead of hanging.

Add `using Foundry.Core.Diagnostics;` (already present at line 3). Replace the 3 calls:
- line 55: `var er = await ProcessRunner.RunAsync(kicad.PythonPath, $"\"{exportScript}\" \"{exportJob}\"", ProcessRunner.KicadTimeout, ct); if (er.TimedOut) return RouteResult.Failed("DSN export timed out."); var (exo, exe, exc) = (er.Stdout, er.Stderr, er.ExitCode);`
- line 64: `var rr = await ProcessRunner.RunAsync(routing.JavaPath, args, ProcessRunner.RouterTimeout, ct); if (rr.TimedOut) return RouteResult.Failed($"FreeRouting timed out after {ProcessRunner.RouterTimeout.TotalMinutes:0} min.", new[] { Trimmed(rr.Stdout, rr.Stderr) }); var (fro, fre, frc) = (rr.Stdout, rr.Stderr, rr.ExitCode);`
- line 78: `var ir = await ProcessRunner.RunAsync(kicad.PythonPath, $"\"{importScript}\" \"{importJob}\"", ProcessRunner.KicadTimeout, ct); if (ir.TimedOut) return RouteResult.Failed("SES import timed out."); var (imo, ime, imc) = (ir.Stdout, ir.Stderr, ir.ExitCode);`
Keep the existing `catch (OperationCanceledException) { throw; }` (line 84): caller cancel still propagates, and the finally still deletes work dir. Delete lines 93-105 entirely.

#### `Foundry.Core/Pcb/PcbBuilder.cs`

Delete private RunAsync (168-180); route the build (line 86) and measure (line 136) through ProcessRunner with KicadTimeout. Build timeout -> PcbResult.Failed; measure timeout -> empty map (degrade to approximations, matching the existing catch at 140-144).

`using Foundry.Core.Diagnostics;` (already line 4). 
- line 86: `var run = await ProcessRunner.RunAsync(kicad.PythonPath, $"\"{scriptPath}\" \"{jobPath}\"", ProcessRunner.KicadTimeout, ct); if (run.TimedOut) return PcbResult.Failed($"PCB build timed out after {ProcessRunner.KicadTimeout.TotalSeconds:0}s.", job.Diagnostics.Select(d => d.Message)); var (stdout, stderr, code) = (run.Stdout, run.Stderr, run.ExitCode);`
- line 136 (MeasureAsync): `var run = await ProcessRunner.RunAsync(kicad.PythonPath, $"\"{scriptPath}\" measure \"{jobPath}\"", ProcessRunner.KicadTimeout, ct); if (run.TimedOut) { AppLog.Warn("pcb", "footprint measure timed out (using approximations)", null); return empty; } return ParseSizes(run.Stdout);`
Delete lines 168-180.

#### `Foundry.Core/Pcb/PcbDrc.cs`

Delete private RunAsync (87-99); route the kicad-cli drc run (line 40) through ProcessRunner with KicadTimeout; timeout -> DrcReport.Failed (so PcbDesigner's RunLoopAsync sees a non-clean report and keeps the best board / continues, never hangs).

`using Foundry.Core.Diagnostics;` (already line 2). Replace line 40: `var run = await ProcessRunner.RunAsync(kicad.KicadCliPath, args, ProcessRunner.KicadTimeout, ct); if (run.TimedOut) return DrcReport.Failed($"DRC timed out after {ProcessRunner.KicadTimeout.TotalSeconds:0}s."); var (stdout, stderr, code) = (run.Stdout, run.Stderr, run.ExitCode);` (DrcReport.Failed returns Installed=true, Ok/Clean=false — RunLoopAsync line 176 only short-circuits on !report.Installed, so a timeout correctly flows as a failed-but-installed iteration). Delete lines 87-99.

#### `Foundry.Core/Pcb/Fab/GerberExporter.cs`

Delete private RunAsync (138-150); route the gerber (97) and drill (98) runs through ProcessRunner with KicadTimeout. A timeout sets a non-zero exit so the existing exitsOk gate (line 107) already declines to package; additionally short-circuit to FabExportResult.Failed for a clear message.

`using Foundry.Core.Diagnostics;` (already line 3). Replace lines 97-98: `var gr = await ProcessRunner.RunAsync(kicad.KicadCliPath, BuildGerberArgs(kicadPcbPath, work, options), ProcessRunner.KicadTimeout, ct); if (gr.TimedOut) return FabExportResult.Failed($"Gerber export timed out after {ProcessRunner.KicadTimeout.TotalSeconds:0}s."); var dr = await ProcessRunner.RunAsync(kicad.KicadCliPath, BuildDrillArgs(kicadPcbPath, work, options), ProcessRunner.KicadTimeout, ct); if (dr.TimedOut) return FabExportResult.Failed($"Drill export timed out after {ProcessRunner.KicadTimeout.TotalSeconds:0}s."); var (gOut, gErr, gCode) = (gr.Stdout, gr.Stderr, gr.ExitCode); var (dOut, dErr, dCode) = (dr.Stdout, dr.Stderr, dr.ExitCode);`
Delete lines 138-150. The finally (133) still deletes work.

#### `Foundry.Core/Firmware/FirmwareBuilder.cs`

Delete private RunAsync (363-371) and the two INLINE process blocks in CompileAsync (119-122) and CompileToImageAsync (182-185); route everything (compile, image build, board list, core list/update/install, upload, post-download index) through ProcessRunner with ArduinoTimeout. Compile/image timeouts -> a 'didn't compile' BuildResult/CompiledImage; upload timeout -> UploadResult fail.

`using Foundry.Core.Diagnostics;` add (file currently uses fully-qualified Diagnostics.AppLog — keep that style or add the using; either compiles). 
- CompileAsync 119-122: `var run = await ProcessRunner.RunAsync(cli, psi.Arguments!, ProcessRunner.ArduinoTimeout, ct);` — but psi is built inline; simplest: keep building `psi` for nothing OR call ProcessRunner with the same FileName/Arguments. Refactor to: `var run = await ProcessRunner.RunAsync(cli, $"compile --fqbn {fqbn} --format json --warnings none \"{sketchDir}\"", ProcessRunner.ArduinoTimeout, ct); if (run.TimedOut) return new BuildResult(true, true, false, $"Compile timed out after {ProcessRunner.ArduinoTimeout.TotalMinutes:0} min.", Array.Empty<BuildDiagnostic>()); var (ok, diags) = Parse(run.Stdout, run.Stderr, run.ExitCode);` (drop the local `psi`/`proc`).
- CompileToImageAsync 182-185: same shape with the `--output-dir` args string; timeout -> `return new CompiledImage(false, fqbn, null, null, null, outputDir, new[] { new BuildDiagnostic("error", "", 0, "Compile timed out.") });`
- UploadAsync line 327: `var run = await ProcessRunner.RunAsync(cli, args, ProcessRunner.ArduinoTimeout, ct); if (run.TimedOut) return new UploadResult(true, false, $"Flash timed out for {board.Port}.", ""); var (stdout, stderr, code) = (run.Stdout, run.Stderr, run.ExitCode);`
- EnsureCoreAsync 354/357/358, ListBoardsAsync 259, DownloadCliAsync 387: replace `await RunAsync(cli, "...", ct)` with `await ProcessRunner.RunAsync(cli, "...", ProcessRunner.ArduinoTimeout, ct)` and read `.Stdout`/`.ExitCode` (EnsureCoreAsync reads `.stdout` -> `.Stdout`; ListBoardsAsync uses the tuple's stdout -> `run.Stdout`). Delete the private RunAsync 363-371.

#### `Foundry.App/ViewModels/TabViewModels.cs`

Add a `_pcbCts` field + a CANCEL command to WiringViewModel, mirroring the existing _scadCts/CancelScad (1045-1046) and _cts/Stop (876/965) idioms; create a fresh CTS at the top of each PCB action, thread its token through every PcbBuilder/PcbRouter/PcbDrc/GerberExporter/PcbDesigner call, and add `catch (OperationCanceledException)` arms. Also thread tokens through the firmware Compile/Upload calls (new _fwCts + CancelBuild/CancelFlash, or reuse IsBuilding/IsFlashing guards).

In WiringViewModel (after line 333 with the other PCB fields) add:
```csharp
private System.Threading.CancellationTokenSource? _pcbCts;
public bool IsExportingPcbCancellable => IsExportingPcb;
[RelayCommand] private void CancelPcb() { _pcbCts?.Cancel(); PcbStatus = "Cancelling…"; }
```
At the START of each guarded PCB body, after `IsExportingPcb = true;`, add: `_pcbCts?.Cancel(); _pcbCts = new System.Threading.CancellationTokenSource(); var ct = _pcbCts.Token;` and pass `ct` to the core call:
- ExportPcb 423: `PcbBuilder.BuildAsync(Project, dir, ct: ct)` then `await RouteCore(result.KicadPcbPath, ct)`.
- RouteCore signature -> `private async Task RouteCore(string pcbPath, CancellationToken ct)`; line 477 `PcbRouter.RouteAsync(pcbPath, ct: ct)`; line 494 `await DrcCore(route.RoutedPcbPath, ct)`.
- DrcCore signature -> `(string boardPath, CancellationToken ct)`; line 506 `PcbDrc.CheckAsync(boardPath, ct: ct)`.
- RoutePcb 459: `await RouteCore(LastPcbPath, ct)`.
- DesignPcb 564: `PcbDesigner.DesignAsync(Project, dir, ai, model, options, ct)`.
- ExportFab/ExportFabCore: ExportFabCore -> `(string boardPath, CancellationToken ct)`; line 629 `GerberExporter.ExportAsync(boardPath, dir, ct: ct)`.
- DesignAndExportFab 674: `PcbDesigner.DesignAndExportFabAsync(Project, dir, ai, model, options, ct: ct)`.
In each body's catch list ADD (before the generic Exception catch): `catch (OperationCanceledException) { PcbSeverity = "info"; PcbStatus = "PCB run cancelled."; }`.
In `OnIsExportingPcbChanged` (344-352) add `CancelPcbCommand.NotifyCanExecuteChanged();` and add a `CanCancelPcb => IsExportingPcb` so the XAML CANCEL button enables only while running (decorate CancelPcb with `[RelayCommand(CanExecute = nameof(CanCancelPcb))]`).
Firmware tab (FirmwareViewModel): add `private System.Threading.CancellationTokenSource? _fwCts;` and `[RelayCommand] private void CancelBuild() => _fwCts?.Cancel();`. In VerifyBuild 1316 / CompileAsync, FixBuild, Flash 1405 / UploadAsync, DetectBoards 1380: create `_fwCts = new(); var ct = _fwCts.Token;` and pass `ct` (`CompileAsync(Project, ct)`, `UploadAsync(Project, SelectedBoard, ct)`, `DetectPortsAsync(Project, ct)`); add `catch (OperationCanceledException) { BuildStatus = "Build cancelled."; }` / `FlashStatus = "Flash cancelled.";`.
NOTE: a XAML CANCEL button must be added to WiringView.xaml / FirmwareView.xaml bound to CancelPcbCommand/CancelBuildCommand (visible when IsExportingPcb/IsBuilding) — out of scope for logic but required for the user-facing affordance; flag to reviewer.

### Test plan

TDD — write the FAILING test FIRST against a NEW public ProcessRunner (the test cannot fail today because RunAsync is private and process-bound; introducing ProcessRunner is the unit under test). New file Foundry.Tests/ProcessRunnerTests.cs (xUnit, [Fact], net8.0, Windows — the app is Windows-only; spawn cmd.exe):\n\n1. RunAsync_KillsAndReportsTimeout_OnSlowChild (THE first failing test):\n   - `var sw = Stopwatch.StartNew();`\n   - `var r = await ProcessRunner.RunAsync(\"cmd.exe\", \"/c ping -n 30 127.0.0.1\", TimeSpan.FromMilliseconds(500));` (ping sleeps ~29s; cmd spawns a child so it also proves tree-kill).\n   - `sw.Stop();`\n   - Assert `r.TimedOut` is true; `Assert.NotEqual(0, r.ExitCode);` (we killed it -> -1); `Assert.True(sw.Elapsed < TimeSpan.FromSeconds(5));` (proves it didn't run to completion — fails today since there is no timeout at all).\n\n2. RunAsync_ReturnsOutputAndZeroExit_OnFastChild:\n   - `var r = await ProcessRunner.RunAsync(\"cmd.exe\", \"/c echo hello\", TimeSpan.FromSeconds(10));`\n   - Assert `!r.TimedOut`; `r.ExitCode == 0`; `r.Stdout.Contains(\"hello\")`.\n\n3. RunAsync_NonZeroExit_IsReportedNotTimedOut:\n   - `var r = await ProcessRunner.RunAsync(\"cmd.exe\", \"/c exit 3\", TimeSpan.FromSeconds(10));`\n   - Assert `!r.TimedOut`; `r.ExitCode == 3`.\n\n4. RunAsync_CallerCancel_Throws_NotTimeout (distinguishes cancel from watchdog):\n   - `using var cts = new CancellationTokenSource(); cts.CancelAfter(300);`\n   - `await Assert.ThrowsAsync<OperationCanceledException>(() => ProcessRunner.RunAsync(\"cmd.exe\", \"/c ping -n 30 127.0.0.1\", TimeSpan.FromMinutes(5), cts.Token));`\n   - (Proves caller cancel propagates as OCE — preserving existing `catch(OCE){throw;}` contracts — and is NOT swallowed as a TimedOut result.)\n\n5. RunAsync_LargeStderr_DoesNotDeadlock (regression for the pipe-buffer bug):\n   - Spawn a child that writes a large volume to BOTH stdout and stderr concurrently. Cross-platform-simple option: `cmd.exe /c \"for /L %i in (1,1,2000) do @echo line%i 1>&2\"` (floods stderr) plus stdout via a second echo loop; bounded by a 30s timeout. Assert `!r.TimedOut` and `r.Stderr.Length > 10000`. With today's sequential read this would block once a pipe fills; with concurrent reads it completes.\n\nDefaults/wiring tests:\n6. ProcessRunner_Timeouts_HaveSaneDefaults: Assert `ProcessRunner.RouterTimeout >= TimeSpan.FromMinutes(5)`, `ProcessRunner.KicadTimeout <= TimeSpan.FromMinutes(3)`, `KicadTimeout > TimeSpan.Zero`.\n\nDegrade/translation tests (extend existing files, no toolchain needed — they exercise the timeout->Failed translation by guard-skipping when toolchain present, same as PcbRoutingTests.cs:297):\n7. In PcbRoutingTests/PcbTests/PcbDrcTests/FabExportTests: keep the existing NotInstalled tests green (they prove the not-installed gate still short-circuits before any ProcessRunner call). Add a comment-anchored assertion that RouteResult.Failed/PcbResult.Failed/DrcReport.Failed/FabExportResult.Failed carry the timeout summary string when fed (these factories already exist and are pure — assert e.g. `RouteResult.Failed(\"FreeRouting timed out after 10 min.\").Summary.Contains(\"timed out\")`).\n\nRun: `dotnet test Foundry.Tests` — test #1 and #5 fail before the ProcessRunner exists / before the deadlock fix; all pass after. Verify build: `dotnet build` (all 5 callers compile against the new ProcRun shape).

### Risks

- Behavior change: a stuck subprocess now returns a Failed result after the timeout instead of hanging — DesignPcb/RunLoopAsync will, on a router timeout, treat that iteration as failed-but-installed and continue to the next iteration / keep the best board (correct), but total worst-case wall time becomes maxIterations * RouterTimeout (3 * 10min = 30min). If that is too long, lower RouterTimeout or cap the loop's cumulative budget — flag for product decision.
- Process.Kill(entireProcessTree:true) can throw if the process already exited or on a race; wrapped in try/catch but on Windows killing a tree mid-flush can leave partial temp files — the existing `finally { Directory.Delete(work, true); }` in each caller still cleans the work dir, so no leak.
- ReadToEndAsync(token) cancellation: when the linked token fires we await the read tasks again in the catch; if the killed process closed pipes, those awaits may throw — wrapped in try/catch returning best-effort partial output. Verify no unobserved-task warnings.
- Caller-cancel vs watchdog ambiguity: the code distinguishes via `timeoutCts.IsCancellationRequested && !ct.IsCancellationRequested`. If BOTH fire near-simultaneously the caller-cancel path wins (throws OCE) — acceptable; the UI shows 'cancelled' rather than 'timed out'.
- FirmwareBuilder.EnsureCoreAsync runs `core install` which legitimately can take minutes on a slow network on first use; ArduinoTimeout=5min may be too tight for a cold ESP32 core install. Consider a longer dedicated CoreInstallTimeout (e.g. 8-10min) to avoid spurious failures.
- CommunityToolkit.Mvvm source generator: the new [RelayCommand] CancelPcb/CancelBuild generate CancelPcbCommand/CancelBuildCommand — referencing them in OnIsExportingPcbChanged before the generator runs is fine (generated at compile), but the field/command must be inside the partial class and not collide with existing names. Confirm no existing CancelPcb member.
- XAML: CANCEL buttons are not added by these logic edits; without them the user still cannot cancel from the UI even though the plumbing exists. The XAML wiring (WiringView.xaml, FirmwareView.xaml) is required to fully close P0-3 and must be done alongside — explicitly out of the StructuredOutput's code edits but called out.
- Tests #1/#5 spawn real cmd.exe/ping — Windows-only and depend on ping being on PATH (it is, on Windows). On a CI Linux runner these would fail; the suite is already Windows-bound (arduino-cli.exe etc.), but mark these [Fact] with a Windows guard (`if (!OperatingSystem.IsWindows()) return;`) to be safe.

---

## P0-4: Verify integrity of every on-demand tool download; fail closed on updater for unsigned builds

**Effort:** L | **dependsOn:** none

### Current state

Every on-demand installer streams a remote artifact to disk and immediately extracts/executes it with ZERO integrity check, and the updater's "trusted" gate is a no-op when the running app is unsigned (the default).

1) RenodeInstaller.DownloadAsync (Foundry.Core/Simulation/RenodeInstaller.cs:47-62): `var bytes = await http.GetByteArrayAsync(PortableUrl, ct); await System.IO.File.WriteAllBytesAsync(zip, bytes, ct); ZipFile.ExtractToDirectory(zip, ToolsDir, overwriteFiles: true);` — no hash, and extraction is plain ZipFile (no zip-slip guard). URL is version-pinned (v1.16.1, line 17-18).

2) OpenScadInstaller.DownloadAsync (Foundry.Core/Cad/OpenScadInstaller.cs:40-54): identical pattern — `GetByteArrayAsync(PortableUrl)` → `WriteAllBytesAsync(zip)` → `ZipFile.ExtractToDirectory(zip, ToolsDir, overwriteFiles: true)` (line 46-49). URL pinned (OpenSCAD-2021.01, line 13).

3) FreeRoutingInstaller.DownloadJreAsync (Foundry.Core/Pcb/FreeRoutingInstaller.cs:102-117): `GetByteArrayAsync(JreUrl)` → write → `ZipFile.ExtractToDirectory(zip, JavaToolsDir, overwriteFiles: true)`. JreUrl (line 24) is the Adoptium "latest" redirect endpoint — UNPINNABLE by hash (content changes per GA). DownloadJarAsync (line 120-132): `GetByteArrayAsync(JarUrl)` → `WriteAllBytesAsync(JarPath)`, then only checks `File.Exists` — no hash. JarUrl pinned (v2.2.4, line 18).

4) FirmwareBuilder.DownloadCliAsync (Foundry.Core/Firmware/FirmwareBuilder.cs:374-390): `GetByteArrayAsync(\"https://downloads.arduino.cc/arduino-cli/arduino-cli_latest_Windows_64bit.zip\")` → write → `ZipFile.ExtractToDirectory(zip, dir, overwriteFiles: true)`. URL is the \"_latest_\" alias — NOT version-pinned, so not directly hash-pinnable as written.

5) KiCadInstaller.InstallAsync NSIS fallback (Foundry.Core/Pcb/KiCadInstaller.cs:160-167): `GetByteArrayAsync(FallbackExeUrl)` → `WriteAllBytesAsync(exe)` → `await RunAsync(exe, \"/S\", ct)` — downloads an unsigned-unverified .exe and runs it elevated/silent. FallbackExeUrl pinned (10.0.3, line 22-23).

6) Updater App.xaml.cs InstallerTrusted (Foundry.App/App.xaml.cs:200-218): when the running app is unsigned, `appCert is null` → logs a warning and `return true` (line 204-207) — fail-OPEN, runs the downloaded installer unverified. When signed, it only does a thumbprint string compare of the leaf cert (line 214) via SignerCert, which calls X509Certificate.CreateFromSignedFile (line 220-229) — it never validates the chain, never calls WinVerifyTrust, and a thumbprint compare can be satisfied by any cert chaining to the same leaf without trust validation. GitHubUpdater.DownloadAsync (Foundry.Core/Update/GitHubUpdater.cs:85-104) streams the installer to %TEMP% with no hash (the GitHub release JSON has no hash to compare anyway).

All 5 download methods are reached from ToolchainProvisioner.InstallAsync (Foundry.Core/Provisioning/ToolchainProvisioner.cs:90-118) and directly from TabViewModels.cs (lines 472, 991, 1231, 1364) and SettingsViewModel.cs:95. Tests live in Foundry.Tests (xunit); ProvisioningTests.cs and UpdaterTests.cs are the existing idiom. Foundry.Core has InternalsVisibleTo Foundry.Tests (Foundry.Core.csproj:33).

### File edits

#### `Foundry.Core/Provisioning/DownloadVerifier.cs`

NEW shared helper that all five installers call instead of inlining GetByteArrayAsync/WriteAllBytes/ZipFile.ExtractToDirectory. Centralizes (a) streaming download, (b) SHA-256 verification against a pinned hash with fail-closed behavior, (c) zip-slip-safe extraction. This avoids duplicating the same fix in 5 files and gives one testable surface.

namespace Foundry.Core.Provisioning; public static class DownloadVerifier.

Thrown on mismatch: public sealed class IntegrityException : Exception (ctor takes message). 

A) public static async Task DownloadVerifiedAsync(HttpClient http, string url, string destPath, string expectedSha256Hex, CancellationToken ct):
  - stream to disk like GitHubUpdater.DownloadAsync (HttpCompletionOption.ResponseHeadersRead, ReadAsStreamAsync, 81920 buffer) into destPath + ".part";
  - compute SHA-256 while streaming using System.Security.Cryptography.IncrementalHash.CreateHash(HashAlgorithmName.SHA256) (hash each buffer chunk as written so we never re-read);
  - after the loop, var actual = Convert.ToHexString(hash.GetHashAndReset()); if (!actual.Equals(expectedSha256Hex, StringComparison.OrdinalIgnoreCase)) { delete .part; throw new IntegrityException($"{Path.GetFileName(destPath)}: SHA-256 mismatch (expected {expectedSha256Hex}, got {actual}) — refusing to use download"); }
  - on success File.Move(destPath+".part", destPath, overwrite:true). Wrap so the .part file is always deleted on any exception (try/catch that deletes then rethrows).

B) public static void VerifyFileSha256(string path, string expectedSha256Hex): used for the unpinnable Adoptium JRE after extraction — open the file, SHA256.HashData over a FileStream, compare; throw IntegrityException on mismatch. (Helper reused by the JRE Authenticode fallback caller below.)

C) public static bool VerifyAuthenticode(string path): wrap WinVerifyTrust for the JRE java.exe path. P/Invoke WinVerifyTrust (wintrust.dll) with WINTRUST_ACTION_GENERIC_VERIFY_V2 GUID and a WINTRUST_DATA pointing at a WINTRUST_FILE_INFO for `path`; return result == 0 (S_OK). Add the minimal structs/GUID/[DllImport] privately. (Used only when a pinned JRE hash is unavailable; see FreeRoutingInstaller below.)

D) public static void ExtractZipSafe(string zipPath, string targetDir, bool overwrite): replaces ZipFile.ExtractToDirectory. Open with ZipFile.OpenRead; var root = Path.GetFullPath(targetDir + Path.DirectorySeparatorChar); foreach entry: var full = Path.GetFullPath(Path.Combine(targetDir, entry.FullName)); if (!full.StartsWith(root, StringComparison.OrdinalIgnoreCase)) throw new IntegrityException($"zip entry escapes target: {entry.FullName}"); skip directory entries (entry.Name == ""); Directory.CreateDirectory(Path.GetDirectoryName(full)!); entry.ExtractToFile(full, overwrite). This is the zip-slip guard the current ZipFile.ExtractToDirectory calls lack.

Keep file under 500 lines; no comments beyond the existing terse style. Diagnostics via Foundry.Core.Diagnostics.AppLog where the installers already log.

#### `Foundry.Core/Simulation/RenodeInstaller.cs`

Add a pinned SHA-256 constant for the v1.16.1 portable zip and route DownloadAsync through DownloadVerifier (verified download + zip-slip-safe extract). Fail closed on mismatch.

Add: public const string PortableSha256 = "<sha256 of renode-1.16.1.windows-portable.zip>"; (placeholder to be filled by computing the hash of the pinned artifact during implementation — fetch the file once, run Get-FileHash -Algorithm SHA256, paste hex). 
Rewrite DownloadAsync body (lines 49-58): keep Directory.CreateDirectory(ToolsDir) and zip path; replace the `using (var http...) { GetByteArrayAsync; WriteAllBytesAsync }` block + `ZipFile.ExtractToDirectory(...)` with:
  using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(30) };
  await DownloadVerifier.DownloadVerifiedAsync(http, PortableUrl, zip, PortableSha256, ct);
  DownloadVerifier.ExtractZipSafe(zip, ToolsDir, overwrite:true);
Keep the `File.Delete(zip)` and Locate() postcondition. Add `using Foundry.Core.Provisioning;`. Behavior change: throws IntegrityException (a normal Exception) on hash mismatch — caller in TabViewModels.cs:991 already catches Exception and shows Status.

#### `Foundry.Core/Cad/OpenScadInstaller.cs`

Pin SHA-256 for OpenSCAD-2021.01-x86-64.zip; route DownloadAsync through DownloadVerifier.

Add: public const string PortableSha256 = "<sha256 of OpenSCAD-2021.01-x86-64.zip>";
Rewrite DownloadAsync (lines 42-50): replace the http GetByteArrayAsync/WriteAllBytesAsync block and ZipFile.ExtractToDirectory with:
  using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
  await DownloadVerifier.DownloadVerifiedAsync(http, PortableUrl, zip, PortableSha256, ct);
  DownloadVerifier.ExtractZipSafe(zip, ToolsDir, overwrite:true);
Keep File.Delete(zip) + Locate() postcondition. Add using Foundry.Core.Provisioning;. Note: PortableUrl is currently private const (line 13) — leave private; only PortableSha256 needs to be public if a test asserts it's non-empty (recommend public to enable the format test below).

#### `Foundry.Core/Pcb/FreeRoutingInstaller.cs`

Pin SHA-256 for the FreeRouting 2.2.4 jar (pinned URL). For the UNPINNABLE Adoptium 'latest' JRE: switch verification to post-extract Authenticode validation of the resolved java.exe (fail closed if WinVerifyTrust fails), since the rolling 'latest' endpoint has no stable hash. Route both downloads through DownloadVerifier and zip-slip-safe extract.

JAR (pinnable): Add public const string JarSha256 = "<sha256 of freerouting-2.2.4.jar>";. Rewrite DownloadJarAsync (lines 122-131): replace GetByteArrayAsync/WriteAllBytesAsync + File.Exists check with:
  using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
  await DownloadVerifier.DownloadVerifiedAsync(http, JarUrl, JarPath, JarSha256, ct);
(DownloadVerifiedAsync already throws if missing/mismatch; keep AppLog.Info success line.)

JRE (unpinnable 'latest' redirect): Rewrite DownloadJreAsync (lines 104-116). Stream the zip to JavaToolsDir\jre.zip via a raw stream (NOT DownloadVerifiedAsync since there is no hash) using the same streaming loop, then DownloadVerifier.ExtractZipSafe(zip, JavaToolsDir, overwrite:true); File.Delete(zip); var java = LocateJava() ?? throw ...; then: if (!DownloadVerifier.VerifyAuthenticode(java)) { try { Directory.Delete(JavaToolsDir, recursive:true); } catch {} throw new Provisioning.IntegrityException("downloaded JRE java.exe failed Authenticode verification — refusing to use it"); } AppLog.Info(...); return java. This fails closed: an unsigned/tampered JRE is deleted and the install throws. Add using Foundry.Core.Provisioning;.

(Optional hardening, document in comment: if you instead want a pinned-hash JRE, change JreUrl from the /latest/ endpoint to a specific Adoptium asset URL like .../v3/assets/version/jdk-25... and add JreSha256, then use DownloadVerifiedAsync — but the Authenticode path avoids re-pinning on every Temurin patch.)

#### `Foundry.Core/Firmware/FirmwareBuilder.cs`

arduino-cli is downloaded from a '_latest_' alias URL with no hash. Change the URL to a version-pinned asset and add a pinned SHA-256, route through DownloadVerifier with zip-slip-safe extract. Fail closed on mismatch.

Add at the top of FirmwareBuilder a pinned version+url+hash (the current inline literal is the only place the URL lives, line 381):
  public const string ArduinoCliVersion = "<e.g. 1.x.y current GA>";
  public const string ArduinoCliUrl = $"https://downloads.arduino.cc/arduino-cli/arduino-cli_{ArduinoCliVersion}_Windows_64bit.zip";  // version-pinned, NOT the _latest_ alias
  public const string ArduinoCliSha256 = "<sha256 of that pinned zip>";
Rewrite DownloadCliAsync (lines 378-385): replace the inline-URL GetByteArrayAsync/WriteAllBytesAsync + ZipFile.ExtractToDirectory with:
  using var http = new System.Net.Http.HttpClient { Timeout = TimeSpan.FromMinutes(5) };
  await Provisioning.DownloadVerifier.DownloadVerifiedAsync(http, ArduinoCliUrl, zip, ArduinoCliSha256, ct);
  Provisioning.DownloadVerifier.ExtractZipSafe(zip, dir, overwrite:true);
Keep the File.Delete(zip), the File.Exists(LocalToolPath) postcondition throw, and the `core update-index` warm-up. Behavior change: switching from '_latest_' to a pinned version means the bundled core/CLI version is now fixed and must be bumped intentionally; this is the intended trade for verifiability.

#### `Foundry.Core/Pcb/KiCadInstaller.cs`

Verify the downloaded NSIS installer's SHA-256 before running it elevated/silent. The exe is version-pinned (10.0.3) so it IS hashable. Fail closed: do not RunAsync the installer on mismatch.

Add: public const string FallbackExeSha256 = "<sha256 of kicad-10.0.3-x86_64.exe>";
In InstallAsync, rewrite the NSIS fallback download block (lines 159-167): replace the `using (var http...) { GetByteArrayAsync(FallbackExeUrl); WriteAllBytesAsync(exe) }` with:
  using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(30) };
  await Provisioning.DownloadVerifier.DownloadVerifiedAsync(http, FallbackExeUrl, exe, FallbackExeSha256, ct);
This throws IntegrityException (an Exception subclass) BEFORE the `await RunAsync(exe, "/S", ct)` line (167), so a tampered installer is never executed. The existing try/catch chain in ToolchainProvisioner.InstallAsync / SettingsViewModel surfaces the message. Keep the File.Delete(exe) in finally-style cleanup (line 168). Add using where needed. The winget path is unchanged (winget verifies its own package signatures).

Defense-in-depth (recommended, low cost): after the (signed) RunAsync completes, the existing Locate() postcondition stays; optionally also call DownloadVerifier.VerifyAuthenticode(exe) BEFORE RunAsync as a second gate so even a hash collision can't run an unsigned exe — order: hash-verify (DownloadVerifiedAsync) then Authenticode-verify, then RunAsync.

#### `Foundry.App/App.xaml.cs`

Make the updater fail CLOSED. (1) When the running app is unsigned, do NOT auto-run the downloaded installer — open the releases page instead. (2) When signed, validate the full X509 chain via WinVerifyTrust (Authenticode), not just a thumbprint string compare.

Change InstallerTrusted (lines 200-218) semantics and the CheckForUpdatesAsync call site (lines 176-184).

InstallerTrusted: 
  - Replace the unsigned-app branch (lines 204-207): instead of `return true`, return false (fail closed). Log: AppLog.Warn("update", "running app is unsigned — cannot verify update publisher; not auto-running, will open releases page").
  - Replace the thumbprint compare path (lines 208-217): first require the file to pass Authenticode: if (!Foundry.Core.Provisioning.DownloadVerifier.VerifyAuthenticode(path)) { AppLog.Error("update", "downloaded installer failed Authenticode/WinVerifyTrust — refusing to run"); return false; } THEN (still) pin to the same publisher as the running app via fileCert/appCert. Keep SignerCert for the publisher pin, but the trust decision now rests on WinVerifyTrust (full chain) + publisher match, not a bare thumbprint equality. (Reuse DownloadVerifier.VerifyAuthenticode rather than re-implementing P/Invoke in the App project.)

CheckForUpdatesAsync call site (line 177): when InstallerTrusted(path) is false, instead of only showing a blocking warning, ALSO offer/redirect to the releases page so the user has a path forward — change the MessageBox to a Yes/No 'could not be verified… Open the releases page to download manually?' and on Yes call OpenUrl(info.ReleaseUrl); never Process.Start the unverified path. This is the 'fail closed → open releases page, don't auto-run' behavior from the plan.

Note: DownloadVerifier lives in Foundry.Core (already referenced by Foundry.App via Foundry.Core using-directives at top of App.xaml.cs, e.g. using Foundry.Core.Update). Add using Foundry.Core.Provisioning; if not pulled in transitively.

### Test plan

TDD — write the FAILING test first, then implement.

FIRST failing test (proves zip-slip + hash gaps exist today). New file Foundry.Tests/DownloadVerifierTests.cs:
- ExtractZipSafe_RejectsZipSlipEntry: build an in-memory/temp zip containing an entry whose name is \"..\\\\evil.txt\" (use ZipArchive + CreateEntry with a traversal name), then Assert.Throws<Foundry.Core.Provisioning.IntegrityException>(() => DownloadVerifier.ExtractZipSafe(zipPath, targetDir, true)); and assert no file was written outside targetDir. This FAILS to even compile today (DownloadVerifier/IntegrityException don't exist) — exactly the required failing-first state. After implementation it passes; the old ZipFile.ExtractToDirectory would have written outside the dir.
- VerifyFileSha256_Mismatch_Throws: write a temp file with known bytes; compute its real hash; assert VerifyFileSha256 passes with the right hash and Assert.Throws<IntegrityException> with a wrong hash.
- DownloadVerifiedAsync_Mismatch_DeletesPartAndThrows: stand up a tiny in-proc HttpMessageHandler (subclass DelegatingHandler/HttpMessageHandler returning a fixed byte[] body) wired into an HttpClient; call DownloadVerifiedAsync with a deliberately wrong expected hash; Assert.Throws<IntegrityException>; assert neither destPath nor destPath+\".part\" exists afterward.
- DownloadVerifiedAsync_Match_WritesFile: same handler, pass the correct SHA-256 (computed from the same bytes); assert the file exists with exact bytes.

Pinned-hash constant tests (cheap, no network) in DownloadVerifierTests.cs or extend ProvisioningTests.cs:
- Pinned hashes are 64 hex chars: Assert.Matches(\"^[0-9A-Fa-f]{64}$\", RenodeInstaller.PortableSha256) and likewise OpenScadInstaller.PortableSha256, FreeRoutingInstaller.JarSha256, FirmwareBuilder.ArduinoCliSha256, KiCadInstaller.FallbackExeSha256. This guards against an empty/placeholder hash shipping.
- ArduinoCliUrl_IsVersionPinned_NotLatestAlias: Assert.DoesNotContain(\"_latest_\", FirmwareBuilder.ArduinoCliUrl) and Assert.Contains(FirmwareBuilder.ArduinoCliVersion, ArduinoCliUrl).

Updater fail-closed tests — App.xaml.cs InstallerTrusted/VerifyAuthenticode is hard to unit-test in WPF, so:
- Move the unsigned-app decision into a pure testable helper if feasible, OR add Foundry.Tests/UpdaterTests.cs cases against DownloadVerifier.VerifyAuthenticode: VerifyAuthenticode_UnsignedFile_ReturnsFalse (write a random .exe stub → expect false) and (if a signed Windows system exe path is available, e.g. notepad) VerifyAuthenticode_SystemSignedExe_ReturnsTrue guarded by File.Exists so it's skippable on CI. The unsigned-stub-returns-false case is the load-bearing fail-closed assertion.

Regression: run the full `dotnet test` (Foundry.Tests) — existing ProvisioningTests (InstallAsync_AlreadyInstalled idempotent path, JreUrl/JarPath shape, KiCad url shape) must stay green; the JreUrl shape test still holds because we keep the Adoptium URL (Authenticode path). Confirm `dotnet build` succeeds for Foundry.Core and Foundry.App.

How to obtain the real pinned hashes during implementation: download each pinned artifact once and `Get-FileHash -Algorithm SHA256 <file>`; paste the hex into the constants. (Renode v1.16.1 portable zip, OpenSCAD-2021.01-x86-64.zip, freerouting-2.2.4.jar, the chosen pinned arduino-cli zip, kicad-10.0.3-x86_64.exe.)

### Risks

- Wrong/placeholder SHA-256 constants will fail-close 100% of installs. The 64-hex-format test catches empties but NOT a wrong-but-valid-format hash — hashes MUST be computed from the actual pinned artifacts, ideally cross-checked against the publisher's published checksums where available (Adoptium publishes .sha256 sidecars; KiCad/Renode/OpenSCAD vary).
- arduino-cli: switching from the '_latest_' alias to a version-pinned URL freezes the bundled CLI version. If the chosen version's asset path differs from the assumed `arduino-cli_{ver}_Windows_64bit.zip` scheme, the download 404s. Verify the exact pinned asset URL exists before pinning, and document that bumping requires updating both URL and hash.
- Adoptium JRE Authenticode path depends on Temurin java.exe being Authenticode-signed by Eclipse Adoptium. If a given build ships an unsigned java.exe, installs will fail-close. Verify current Temurin 25 Windows JRE java.exe is signed; if not, fall back to pinning a specific asset URL + published SHA-256 instead of the /latest/ redirect.
- WinVerifyTrust P/Invoke must be correct (WINTRUST_DATA layout, GUID, dwUIChoice=NONE, dwStateAction OPEN then CLOSE to free state). A buggy interop could either crash or wrongly return trusted. Keep dwUIChoice=WTD_UI_NONE and revocation flags conservative; close the state handle in a second call.
- Behavior change: the updater now fails CLOSED for unsigned (default) builds — auto-update stops working until builds are Authenticode-signed. This is intended but is a user-visible regression; the releases-page redirect mitigates it. Communicate in release notes.
- Zip-slip ExtractZipSafe changes extraction semantics from ZipFile.ExtractToDirectory: ensure directory-only entries and nested paths still create folders correctly (the Renode portable zip extracts into a versioned subfolder that Locate() globs for; OpenSCAD extracts into a subdir; verify Locate() still finds the exe after ExtractZipSafe).
- Streaming-hash-then-Move uses a .part temp file; ensure overwrite semantics and that a leftover .part from a prior aborted run is overwritten, and that File.Move(overwrite:true) is used (net8 supports it).
- KiCad NSIS exe hash gate runs before RunAsync, but the winget path is unverified-by-Foundry (relies on winget's own signature checks) — acceptable but note it so a reviewer doesn't expect a hash there.

---

## P0-5: Require explicit confirm before flashing firmware; refuse on FQBN/port mismatch

**Effort:** M | **dependsOn:** none

### Current state

Flashing is the only irreversible hardware action with NO confirmation and a vendor-mismatch brick risk.

1) Auto-flash with zero confirmation. `FirmwareViewModel.Flash()` (Foundry.App/ViewModels/TabViewModels.cs:1397-1418) calls `UploadAsync(Project, SelectedBoard)` immediately:
```
1404:            var result = await Foundry.Core.Firmware.FirmwareBuilder.UploadAsync(Project, SelectedBoard);
```
Contrast the fab-order handoff `PlaceFabOrder()` which DOES confirm (TabViewModels.cs:797-803):
```
797:        var confirm = System.Windows.MessageBox.Show(
...
802:            System.Windows.MessageBoxButton.OKCancel, System.Windows.MessageBoxImage.Information);
803:        if (confirm != System.Windows.MessageBoxResult.OK) return;
```

2) Silent FirstOrDefault even with multiple ports. `FirmwareBuilder.UploadAsync` (Foundry.Core/Firmware/FirmwareBuilder.cs:293-307):
```
301:        var board = target;
302:        if (board is null)
303:        {
304:            var detected = await DetectPortsAsync(project, ct);
305:            board = detected.FirstOrDefault();
306:            if (board is null) return UploadResult.NoBoard();
307:        }
```
When `target` is null and >1 port is connected, it silently flashes the first (DetectPortsAsync only reorders, it does not de-ambiguate).

3) Inferred FQBN wins on mismatch — the brick path. FirmwareBuilder.cs:309-311:
```
309:        // Prefer the project's inferred FQBN; fall back to whatever the port reported.
310:        var fqbn = Fqbn(project);
311:        if (fqbn == "arduino:avr:uno" && board.Fqbn is not null) fqbn = board.Fqbn;   // trust the port over the safe default
```
The detected board's real FQBN is only adopted when the inferred value is exactly the default `arduino:avr:uno`. If `Fqbn(project)` infers a NON-default value (e.g. `esp32:esp32:esp32` because the prompt/title contains "esp32" — see Fqbn() at FirmwareBuilder.cs:68-84) but the physically connected board reports `arduino:avr:uno`, the inferred ESP32 FQBN is used to write to an AVR board. That is the mismatch / brick risk.

4) Unvalidated, string-interpolated CLI args. The upload command is built by string interpolation and run via a shared helper that sets `Arguments` (not `ArgumentList`):
```
325:            var args = $"upload -p {board.Port} --fqbn {fqbn} --input-dir \"{buildDir}\" --format json";
...
363:    private static async Task<(string stdout, string stderr, int code)> RunAsync(string cli, string args, CancellationToken ct)
364:    {
365:        var psi = new ProcessStartInfo { FileName = cli, Arguments = args, ... };
```
`board.Port` and `fqbn` flow from `DetectedBoard` / arduino-cli JSON; there is no regex validation before they reach the process command line.

Supporting shapes: `DetectedBoard(string Port, string? Fqbn, string Label)` (FirmwareBuilder.cs:31). `UploadResult(bool Installed, bool Ok, string Summary, string Detail)` with factories `NotInstalled()`/`NoBoard()` (FirmwareBuilder.cs:34-40). VM exposes `Boards` (ObservableCollection), `SelectedBoard`, `HasBoardChoices => Boards.Count > 1` (TabViewModels.cs:1289-1291). XAML port picker ComboBox is bound to `Boards`/`SelectedBoard` and only visible when `HasBoardChoices` (FirmwareView.xaml:77-86). VMs use `System.Windows.MessageBox.Show` directly throughout (no dialog abstraction). Tests are xUnit calling static pure FirmwareBuilder methods directly (Foundry.Tests/FlashTests.cs).

### File edits

#### `Foundry.Core/Firmware/FirmwareBuilder.cs`

Add pure, testable helpers (vendor extraction, FQBN/port validation, and a 'resolve the FQBN to physically write + classify mismatch' planner), then rewire UploadAsync to (a) refuse to auto-pick when ambiguous, (b) prefer the connected board's concrete FQBN, (c) refuse on vendor mismatch unless explicitly forced, and (d) build the upload command via ArgumentList with validated tokens.

ADD `using System.Text.RegularExpressions;` at top.

1) NEW provenance record + plan record (place near the other records, after DetectedBoard at ~line 31):
```
/// <summary>How the FQBN that will be physically written was chosen.</summary>
public enum FqbnSource { InferredOnly, PortReported, PortPreferredOverInferred }

/// <summary>
/// A vetted, ready-to-flash decision: which port, which FQBN we will actually write, where it came from,
/// and whether the inferred-vs-detected vendors disagree (brick risk). Pure — no I/O. UI shows this in the
/// confirm dialog; <see cref="UploadAsync"/> consumes it.
/// </summary>
public sealed record FlashPlan(string Port, string Fqbn, string BoardLabel, string InferredFqbn, string? DetectedFqbn, FqbnSource Source, bool VendorMismatch, string? MismatchWarning)
{
    public string ConfirmText =>
        $"Port: {Port}\nBoard: {BoardLabel}\nWill flash FQBN: {Fqbn}" +
        (VendorMismatch ? $"\n\nWARNING: {MismatchWarning}" : "");
}
```

2) NEW validation helpers (private/internal static; expose as `internal` so the test project — which already references Foundry.Core — can assert them; add `[assembly: InternalsVisibleTo("Foundry.Tests")]` if not already present, OR make them `public` to match the existing public-static test surface like Fqbn/ParseBoardList — prefer PUBLIC for consistency with the file's tested API):
```
// FQBN: exactly vendor:arch:board, each segment [A-Za-z0-9_.-]+ (arduino-cli grammar), optional :opts appended.
private static readonly Regex FqbnRx = new(@"^[A-Za-z0-9_.\-]+:[A-Za-z0-9_.\-]+:[A-Za-z0-9_.\-]+(?::[A-Za-z0-9_.\-=,]+)?$", RegexOptions.Compiled);
// Port: COM<n> (Windows) or a /dev/... path; no spaces/shell metachars.
private static readonly Regex PortRx = new(@"^(COM[0-9]+|/dev/[A-Za-z0-9_./\-]+)$", RegexOptions.Compiled);
public static bool IsValidFqbn(string? f) => !string.IsNullOrWhiteSpace(f) && FqbnRx.IsMatch(f);
public static bool IsValidPort(string? p) => !string.IsNullOrWhiteSpace(p) && PortRx.IsMatch(p);
public static string VendorOf(string? fqbn) => string.IsNullOrEmpty(fqbn) ? "" : fqbn.Split(':') is { Length: >= 1 } s ? s[0].ToLowerInvariant() : "";
```

3) NEW pure planner — the heart of the fix (public static so it is unit-testable without hardware):
```
/// <summary>
/// Decide which FQBN to physically write to <paramref name="board"/> for <paramref name="project"/>, and flag a
/// vendor mismatch. Rules: prefer the connected board's concrete FQBN (it knows what it physically is); fall back
/// to the inferred FQBN only for unidentified ports (board.Fqbn == null). A mismatch is when BOTH vendors are known
/// and differ (e.g. inferred esp32 vs detected arduino) — that is the brick path and must be refused/forced.
/// </summary>
public static FlashPlan BuildFlashPlan(Project.Project project, DetectedBoard board)
{
    var inferred = Fqbn(project);
    var detected = board.Fqbn;
    string write; FqbnSource src;
    if (IsValidFqbn(detected))
    {
        write = detected!;
        src = string.Equals(detected, inferred, StringComparison.OrdinalIgnoreCase) ? FqbnSource.PortReported : FqbnSource.PortPreferredOverInferred;
    }
    else { write = inferred; src = FqbnSource.InferredOnly; }

    var vi = VendorOf(inferred); var vd = VendorOf(detected);
    bool mismatch = vi.Length > 0 && vd.Length > 0 && vi != vd;
    string? warn = mismatch
        ? $"This project's firmware was built for '{inferred}' ({vi}) but the connected board reports '{detected}' ({vd}). Flashing a {vi} image to a {vd} board can brick it. Foundry will flash the board's own FQBN '{write}', which will almost certainly fail because the firmware was compiled for {vi}."
        : null;
    return new FlashPlan(board.Port, write, board.Label, inferred, detected, src, mismatch, warn);
}
```

4) CHANGE `UploadAsync` signature to add an explicit force flag and to refuse ambiguity. New signature:
`public static async Task<UploadResult> UploadAsync(Project.Project project, DetectedBoard? target, bool forceMismatch = false, CancellationToken ct = default)`
Replace the body lines 301-311 with:
```
var board = target;
if (board is null)
{
    var detected = await DetectPortsAsync(project, ct);
    if (detected.Count == 0) return UploadResult.NoBoard();
    if (detected.Count > 1)
        return new UploadResult(true, false, "Multiple boards connected — pick the target board, then flash.", "");  // never auto-pick FirstOrDefault
    board = detected[0];
}

if (!IsValidPort(board.Port))
    return new UploadResult(true, false, $"Refusing to flash — unsafe port '{board.Port}'.", "");

var plan = BuildFlashPlan(project, board);
if (plan.VendorMismatch && !forceMismatch)
    return new UploadResult(true, false, $"Refusing to flash — board/firmware vendor mismatch.", plan.MismatchWarning ?? "");
if (!IsValidFqbn(plan.Fqbn))
    return new UploadResult(true, false, $"Refusing to flash — invalid FQBN '{plan.Fqbn}'.", "");
var fqbn = plan.Fqbn;
```
Then further down, EnsureCoreAsync already takes `fqbn` (line 323) — unchanged. The summaries at lines 320/330/335 already interpolate `fqbn`/`board.Port` — keep.

5) CHANGE the upload invocation (lines 324-327) from string Arguments to ArgumentList. Add an overload of RunAsync that accepts `params string[]` (or `IEnumerable<string>`) and fills `psi.ArgumentList`:
```
private static async Task<(string stdout, string stderr, int code)> RunAsync(string cli, CancellationToken ct, params string[] argv)
{
    var psi = new ProcessStartInfo { FileName = cli, UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
    foreach (var a in argv) psi.ArgumentList.Add(a);
    using var p = Process.Start(psi)!;
    var o = await p.StandardOutput.ReadToEndAsync(ct);
    var e = await p.StandardError.ReadToEndAsync(ct);
    await p.WaitForExitAsync(ct);
    return (o, e, p.ExitCode);
}
```
Replace lines 325-327 with:
```
Diagnostics.AppLog.Info("flash", $"flashing {fqbn} -> {board.Port}" + (plan.VendorMismatch ? " (FORCED over vendor mismatch)" : ""));
var (stdout, stderr, code) = await RunAsync(cli, ct, "upload", "-p", board.Port, "--fqbn", fqbn, "--input-dir", buildDir, "--format", "json");
```
(Note: with ArgumentList the buildDir is added as a single argument WITHOUT the surrounding quotes that were in the old interpolated string.)
Keep the existing string-based `RunAsync(cli, args, ct)` for the other callers (board list / core install) to minimize blast radius, OR optionally migrate those too — not required for P0-5.

#### `Foundry.App/ViewModels/TabViewModels.cs`

Make Flash() build the FlashPlan, show an explicit OKCancel confirm dialog (mirroring PlaceFabOrder at line 797) that names the exact port + board label + resolved FQBN, hard-refuse on multi-port ambiguity, and surface/forced-confirm vendor mismatch before calling UploadAsync.

Replace the body of `Flash()` (TabViewModels.cs:1397-1418). New logic:
```
[RelayCommand]
private async Task Flash()
{
    if (IsFlashing) return;

    // 1) Resolve an unambiguous target. If none picked and multiple/zero detected, force a pick.
    var board = SelectedBoard;
    if (board is null)
    {
        await DetectBoards();                 // populates Boards + SelectedBoard
        board = SelectedBoard;
        if (Boards.Count > 1) { FlashSeverity = "info"; FlashStatus = "Multiple boards connected — pick the target board, then click FLASH."; return; }
        if (board is null) { FlashSeverity = "info"; FlashStatus = "No board detected — plug in your board over USB and scan again."; return; }
    }

    // 2) Build the human-readable plan and confirm (mirrors PlaceFabOrder at ~797).
    var plan = Foundry.Core.Firmware.FirmwareBuilder.BuildFlashPlan(Project, board);
    var force = false;
    if (plan.VendorMismatch)
    {
        var go = System.Windows.MessageBox.Show(
            plan.ConfirmText + "\n\nThis is very likely to fail or damage the board. Flash anyway?",
            "Foundry — VENDOR MISMATCH",
            System.Windows.MessageBoxButton.OKCancel, System.Windows.MessageBoxImage.Warning,
            System.Windows.MessageBoxResult.Cancel);   // default to Cancel
        if (go != System.Windows.MessageBoxResult.OK) { FlashSeverity = "fail"; FlashStatus = "Flash cancelled — vendor mismatch."; return; }
        force = true;
    }
    else
    {
        var go = System.Windows.MessageBox.Show(
            "Foundry will compile and write firmware to your board over USB. This overwrites whatever is on it.\n\n" +
            plan.ConfirmText + "\n\nFlash now?",
            "Foundry — flash firmware",
            System.Windows.MessageBoxButton.OKCancel, System.Windows.MessageBoxImage.Warning,
            System.Windows.MessageBoxResult.Cancel);   // default to Cancel
        if (go != System.Windows.MessageBoxResult.OK) { FlashSeverity = "info"; FlashStatus = "Flash cancelled."; return; }
    }

    // 3) Flash the confirmed target.
    IsFlashing = true;
    FlashSeverity = "info"; FlashStatus = $"Compiling and flashing {plan.Fqbn} to {plan.Port}…";
    try
    {
        var result = await Foundry.Core.Firmware.FirmwareBuilder.UploadAsync(Project, board, force);
        if (!result.Installed) { FlashSeverity = "info"; FlashStatus = result.Summary; return; }
        FlashSeverity = result.Ok ? "pass" : "fail";
        FlashStatus = string.IsNullOrEmpty(result.Detail) ? result.Summary : $"{result.Summary}\n{result.Detail}";
        if (result.Ok) Foundry.Core.Diagnostics.AppLog.Info("flash", result.Summary);
        else Foundry.Core.Diagnostics.AppLog.Warn("flash", result.Summary);
    }
    catch (Exception ex) { FlashSeverity = "fail"; FlashStatus = $"Flash failed: {ex.Message}"; }
    finally { IsFlashing = false; }
}
```
Note the new UploadAsync arg order is `(project, target, forceMismatch, ct)`; the `ct` default still applies. No other callers of UploadAsync exist (verified by grep — only this VM call site).

#### `Foundry.App/Views/Tabs/FirmwareView.xaml`

No structural change required; the existing port-picker ComboBox + DETECT BOARDS button (lines 77-91) already drive Boards/SelectedBoard. Optionally clarify the FLASH button tooltip to set the confirm expectation.

Optional (cosmetic, not load-bearing): update the header FLASH button ToolTip at line 31 from `"Compile and upload the firmware to a USB-connected board"` to `"Compile and upload firmware — asks you to confirm the port and board first"`. No binding changes; the confirm and refusal logic live entirely in the VM/Core. Leave the panel ComboBox/DETECT BOARDS (lines 77-91) and Visibility on HasBoardChoices unchanged — they already provide the disambiguation UI the refusal path tells users to use.

### Test plan

All tests go in Foundry.Tests/FlashTests.cs (xUnit, matches existing style — pure static FirmwareBuilder calls, no hardware). UploadAsync itself spawns arduino-cli so it stays UI/integration-tested manually; the unit tests target the new pure planner/validators, which is where the brick logic lives.

TDD — WRITE THIS FIRST (must FAIL against today's code because BuildFlashPlan does not exist; it encodes the exact brick scenario the current line-311 logic mishandles):
```
[Fact]
public void BuildFlashPlan_InferredEsp32_DetectedAvr_FlagsVendorMismatch()
{
    var p = new Foundry.Core.Project.Project {
        Title = \"ESP32 weather station\",   // forces inferred esp32:esp32:esp32 via Fqbn()
        Components = new() { new ComponentSpec { Ref = \"mcu\", Alias = \"MCU\", Name = \"ESP32 DevKit\" } },
    };
    var board = new DetectedBoard(\"COM3\", \"arduino:avr:uno\", \"Arduino Uno (COM3)\");
    var plan = FirmwareBuilder.BuildFlashPlan(p, board);

    Assert.True(plan.VendorMismatch);
    Assert.Equal(\"arduino:avr:uno\", plan.Fqbn);        // prefer the PHYSICAL board's FQBN, never write esp32 to AVR
    Assert.Equal(FqbnSource.PortPreferredOverInferred, plan.Source);
    Assert.Contains(\"brick\", plan.MismatchWarning, StringComparison.OrdinalIgnoreCase);
    Assert.Contains(\"COM3\", plan.ConfirmText);
    Assert.Contains(\"arduino:avr:uno\", plan.ConfirmText);
}
```
(This fails today: BuildFlashPlan/FlashPlan/FqbnSource are undefined → compile error, the strongest possible red.)

Then the full suite:
1) BuildFlashPlan_MatchingVendors_NoMismatch_PrefersDetected: project infers arduino:avr:uno, board reports arduino:avr:nano → VendorMismatch false, Fqbn == \"arduino:avr:nano\" (board wins), Source == PortPreferredOverInferred.
2) BuildFlashPlan_UnidentifiedPort_FallsBackToInferred: board.Fqbn == null → Fqbn == Fqbn(project), Source == InferredOnly, VendorMismatch == false (detected vendor unknown).
3) BuildFlashPlan_ExactMatch_SourcePortReported: inferred == detected (both arduino:avr:uno) → Source == PortReported, VendorMismatch false.
4) IsValidFqbn_Theory: valid \"arduino:avr:uno\", \"esp32:esp32:esp32\", \"rp2040:rp2040:rpipico\" → true; invalid \"\", \"arduino:avr\", \"arduino avr uno\", \"a:b:c; rm -rf\", null → false.
5) IsValidPort_Theory: \"COM3\", \"COM12\", \"/dev/ttyACM0\", \"/dev/cu.usbserial-1410\" → true; \"COM 3\", \"COM3 && calc\", \"\", \"3\", null → false.
6) VendorOf_Theory: \"esp32:esp32:esp32\"→\"esp32\", \"arduino:avr:uno\"→\"arduino\", \"\"/null→\"\".
7) UploadAsync_PythonPlatform_Refuses (extends existing pattern): Project with Firmware.Platform=\"MicroPython\" → still returns the existing MicroPython message regardless of new args (regression guard on the early-return at FirmwareBuilder.cs:298).
8) Mismatch-not-forced refusal is covered by the planner test + a code-review check on UploadAsync; if InternalsVisibleTo is added, optionally assert UploadAsync(project, mismatchBoard, forceMismatch:false) returns Ok==false with \"vendor mismatch\" in Summary WITHOUT spawning the CLI — but only when arduino-cli is absent (Locate()==null short-circuits first), so prefer to keep this as the planner-level assertion to stay hardware-independent.

Run: `dotnet test Foundry.Tests` — the new red test must fail first, then pass after the Core edits. Manual UI verification (FOUNDRY_SHOT per project memory): launch app, Firmware tab, click FLASH → confirm dialog appears naming port+board+FQBN; Cancel aborts; with two simulated ports the picker forces a choice and FLASH without a pick shows the multi-port message.

### Risks

- Behavior change: FLASH no longer flashes on a single click — it now always shows a confirm dialog. This is the intended P0 fix but changes the one-click UX and will break any automated/headless flash flow that relied on silent UploadAsync (none found by grep; only the VM call site exists).
- Behavior change: when >1 port is connected and no SelectedBoard, UploadAsync now REFUSES instead of flashing FirstOrDefault. Users must pick. The VM mitigates by auto-running DetectBoards and surfacing the picker, but a caller invoking UploadAsync(project, null) directly with multiple ports now gets a non-Ok result.
- Behavior change: the resolved FQBN now PREFERS the detected board's FQBN in all identified-board cases (not just when inferred==arduino:avr:uno). For a matching-vendor-but-different-variant case (infer uno, board nano) it will now flash 'nano' where it previously flashed 'uno'. This is more correct (the physical board knows itself) but is a semantic change worth calling out; the firmware was compiled for the inferred FQBN, so a variant mismatch within the same vendor could still fail to upload — acceptable vs. cross-vendor bricking.
- ArgumentList migration drops the manual quotes around buildDir (line 325). Correct for ArgumentList (it quotes per-arg), but a stray manual quote would now corrupt the path — ensure NO surrounding quotes are passed.
- Regex strictness: PortRx/FqbnRx could reject a legitimate but unusual arduino-cli value (e.g. a Bluetooth/network port address, or an FQBN with menu options like ':opts'). FqbnRx includes an optional :opts segment; PortRx covers COM* and /dev/* only. If arduino-cli ever returns a network 'port' (e.g. an OTA address), it would be refused — acceptable for a USB-flash feature, but note it loudly in the refusal Summary so users aren't confused.
- If the test project does not already have InternalsVisibleTo, keep the new helpers PUBLIC (consistent with the file's already-public tested API: Fqbn, ParseBoardList, Parse) rather than adding assembly attributes.
- MessageBox in the VM is hard to unit-test (UI thread); the confirm gating itself isn't covered by automated tests — relies on manual FOUNDRY_SHOT verification per project memory note about UI changes needing visual verify.

---

## P0-6: Gate Gerber/fab export on a real DRC-clean result; stop reconciling exit-0+no-report to clean

**Effort:** M | **dependsOn:** none

### Current state

Today fab export packages ANY board with zero DRC enforcement, and the DRC parser can manufacture a fake "clean" verdict.

1) GerberExporter.ExportAsync runs NO DRC. It only runs the two export verbs and packages on "both exits 0 + file set validates" (GerberExporter.cs:104-115): `bool exitsOk = gCode == 0 && dCode == 0; if (exitsOk && FabFileSet.Validate(produced).Ok) { ... ZipFile.CreateFromDirectory(...) }`. There is no call to PcbDrc.CheckAsync anywhere in the file (confirmed: file uses only KiCadInstaller, FabFileSet, ZipFile, AppLog). A DRC-failing board exports a perfectly valid fab ZIP.

2) The standalone EXPORT GERBERS button reaches that directly. TabViewModels.cs:611-649: `[RelayCommand(CanExecute = nameof(CanExportFab))] private async Task ExportFab()` → `ExportFabCore(LastPcbPath)` → `var fab = await Foundry.Core.Pcb.Fab.GerberExporter.ExportAsync(boardPath, dir);` (line 629). No DRC is consulted in that path.

3) The VM gate is DRC-blind. `public bool CanExportFab => !IsExportingPcb && !string.IsNullOrEmpty(LastPcbPath);` (TabViewModels.cs:361). LastPcbPath is set whenever a board exists, including the best-effort (DRC-FAIL) board: DesignPcb sets it unconditionally when a path exists even though result.Ok is false — `PcbStatus = result.Ok ? ... : verdict; if (result.KicadPcbPath is not null) { LastPcbPath = result.KicadPcbPath; ... }` (TabViewModels.cs:593-596). So a user who just saw "DRC FAIL — 3 error(s)" still has EXPORT GERBERS enabled.

4) DrcReport.Parse fabricates clean on exit-0 + no report file (DrcReport.cs:88-97): `if (string.IsNullOrWhiteSpace(reportJson)) { if (exitCode == 0) return new DrcReport(true, true, "DRC clean — 0 errors, fully connected.", ...empty..., 0,0,0, ...); return Failed("DRC reported violations but produced no readable report.", ...); }`. PcbDrc.CheckAsync always passes kicad-cli the `--exit-code-violations` flag and `--output "<reportPath>"` (PcbDrc.cs:64-83, 37), then reads the file: `var reportText = File.Exists(reportPath) ? await ...ReadAllTextAsync(...) : null;` (PcbDrc.cs:42). So a board that was never really checked (cli exits 0 but writes no JSON) is reported `Clean == true`. The existing test that locks this in is PcbDrcTests.cs:124-130 `Parse_ExitZero_NoReportFile_ReconciledToClean` asserting `r.Ok` and `r.Clean`.

5) The one-shot DESIGN+GERBERS path is already gated correctly at the orchestration layer (PcbDesigner.DesignAndExportFabAsync, PcbDesigner.cs:120-135 refuses export unless design.Ok) — but that gate lives only in the orchestrator, NOT in ExportAsync itself, so the standalone button bypasses it. The fix must put the gate inside ExportAsync (defense in depth) AND in the VM CanExportFab, AND remove the parser's false-clean.

### File edits

#### `Foundry.Core/Pcb/DrcReport.cs`

Stop treating kicad-cli exit 0 + missing report file as 'clean'. Only an explicit JSON document with empty violations maps to Clean; a missing report is inconclusive and must map to Failed ('DRC produced no report — could not verify').

In Parse, change the `string.IsNullOrWhiteSpace(reportJson)` branch (currently lines 88-97). Replace the `if (exitCode == 0) return new DrcReport(true, true, "DRC clean ...", ...)` clean-fabrication with a Failed for BOTH exit codes. New body:

```
if (string.IsNullOrWhiteSpace(reportJson))
{
    // A missing report file is NEVER trustworthy as 'clean' — we cannot verify connectivity/
    // clearance without the JSON. Exit 0 + no report is inconclusive (fab must not proceed);
    // exit 5 + no report is the same IO failure. Only explicit empty-violations JSON is clean.
    var note = string.IsNullOrWhiteSpace(stderr) ? null : new[] { stderr!.Trim() };
    return exitCode == 0
        ? Failed("DRC produced no report — could not verify the board.", note)
        : Failed("DRC reported violations but produced no readable report.", note);
}
```

Keep the rest of Parse unchanged: the `exitCode != 0 && exitCode != 5` infra-error guard (lines 81-86) stays first; the JSON-present path (lines 99-139) is untouched, so a real `{"violations":[],"unconnected_items":[]}` on exit 0 still yields Clean via the existing `bool clean = errors == 0 && unconnectedCount == 0;` logic. No signature change. Note: Failed() already returns Installed=true, Ok=false, Clean=false (DrcReport.cs:44,51-53), which is exactly the inconclusive verdict we want.

#### `Foundry.Core/Pcb/Fab/GerberExporter.cs`

ExportAsync must require a fresh DRC-clean verdict for the input board before it exports/packages anything. Add an explicit drcClean provenance parameter so callers that ALREADY ran DRC (the orchestrator) can pass the verdict without re-running kicad-cli, while callers that have NOT verified (the standalone button) cause ExportAsync to run PcbDrc.CheckAsync itself. A non-clean (or inconclusive) board returns FabExportResult.Failed carrying the DRC summary — never a ZIP.

Add a parameter and a gate at the top of ExportAsync.

New signature (append optional params; existing 4-arg call sites keep compiling):
```
public static async Task<FabExportResult> ExportAsync(string kicadPcbPath, string outputDir,
    FabOptions? options = null, bool drcClean = false, DrcOptions? drcOptions = null,
    CancellationToken ct = default)
```
Note: the existing call in PcbDesigner passes `(design.KicadPcbPath!, outputDir, fabOptions, ct)` positionally — adding `drcClean`/`drcOptions` before `ct` BREAKS that positional call, so PcbDesigner.cs must be updated (see its edit). The standalone VM call `ExportAsync(boardPath, dir)` keeps working (defaults drcClean=false → ExportAsync self-verifies).

Gate logic, inserted AFTER the NotInstalled check (line 84) and AFTER the input-exists check (lines 86-87), BEFORE creating the work dir (line 90):
```
// FAB GATE: never package a board that isn't proven DRC-clean. Callers that already hold a
// fresh clean verdict pass drcClean:true; otherwise we run the gate ourselves here.
if (!drcClean)
{
    var drc = await PcbDrc.CheckAsync(kicadPcbPath, drcOptions ?? DrcOptions.Default, ct);
    if (!drc.Installed) return FabExportResult.NotInstalled();
    if (!drc.Clean)
        return FabExportResult.Failed($"Fab export blocked — board is not DRC-clean: {drc.Summary}",
            drc.Notes);
}
```
This requires a `using Foundry.Core.Pcb;` add at the top of the file (currently usings are System.Diagnostics, System.IO.Compression, Foundry.Core.Diagnostics — GerberExporter is in namespace Foundry.Core.Pcb.Fab so PcbDrc/DrcReport/DrcOptions are in the parent namespace and need the using). Everything below the gate (work dir, two RunAsync calls, FabFileSet.Validate, zip, FabExportResult.Parse) is unchanged. Rationale for the drcClean flag: the orchestrator already ran DRC on the SAME board and knows it is clean (PcbDesigner returns design.Ok only when report.Clean) — re-running kicad-cli pcb drc there would double the toolchain cost; the flag lets it skip the redundant run while the standalone path stays safe by default.

#### `Foundry.Core/Pcb/PcbDesigner.cs`

Update DesignAndExportFabAsync's call to ExportAsync for the new signature, passing drcClean:true since DesignAsync only returns Ok (and a KicadPcbPath worth fabbing) when the board passed DRC. This both fixes the positional-arg break and avoids a redundant second DRC run.

Line 133 currently: `var fabResult = await GerberExporter.ExportAsync(design.KicadPcbPath!, outputDir, fabOptions, ct);`. Change to named args so the new `drcClean`/`drcOptions` slot is unambiguous:
```
var fabResult = await GerberExporter.ExportAsync(design.KicadPcbPath!, outputDir, fabOptions,
    drcClean: true, drcOptions: options, ct: ct);
```
This is sound because the guard at lines 125-131 already returns early unless `design.Ok && !string.IsNullOrEmpty(design.KicadPcbPath)`, and design.Ok is true only when the kept board's report was Clean (RunLoopAsync sets Ok via report.Clean at lines 191-196 / 229). Passing the loop's same `options` (DrcOptions) as drcOptions keeps strictness consistent if it were ever re-run. No other change to this file.

#### `Foundry.App/ViewModels/TabViewModels.cs`

Track the last DRC verdict in a new LastDrcClean observable, gate CanExportFab on it, set it true/false from every path that produces or fails a board (DrcCore, DesignPcb), reset it whenever a new unverified board path is set, and notify the command when it changes.

1) Add an observable that re-evaluates the gate and command (place beside _lastFabZipPath, ~line 357):
```
[ObservableProperty]
[NotifyPropertyChangedFor(nameof(CanExportFab))]
private bool _lastDrcClean;
partial void OnLastDrcCleanChanged(bool value) => ExportFabCommand.NotifyCanExecuteChanged();
```
2) Tighten the gate (line 361):
```
public bool CanExportFab => !IsExportingPcb && !string.IsNullOrEmpty(LastPcbPath) && LastDrcClean;
```
3) In DrcCore (lines 503-537), record the verdict so the standalone EXPORT+ROUTE path sets it. After computing report, add: in the NotInstalled early-return set `LastDrcClean = false;` before returning (after line 513); in the `if (report.Clean)` branch set `LastDrcClean = true;` (after line 520); in the else branch set `LastDrcClean = false;` (after line 526).
4) In DesignPcb (lines 586-599): the design loop verdict is authoritative. After `PcbSeverity = result.Ok ? "pass" : "fail";` (line 586) set `LastDrcClean = result.Ok && result.Report?.Clean == true;`. Important: keep LastPcbPath assignment as-is (line 596) so the best-effort board is still revealed, but with LastDrcClean false the fab button stays disabled.
5) In DesignAndExportFab (lines 657-711): after `if (design.KicadPcbPath is not null) LastPcbPath = design.KicadPcbPath;` (line 688) set `LastDrcClean = design.Ok && design.Report?.Clean == true;`. The early `if (!design.Ok)` return (lines 691-696) then naturally leaves the button disabled.
6) Reset on any unverified board source: in OnIsExportingPcbChanged is not the right spot; instead add `partial void OnLastPcbPathChanged(string? value) { LastDrcClean = false; OnPropertyChanged(nameof(CanExportFab)); ExportFabCommand.NotifyCanExecuteChanged(); }` so that setting a new board path defaults to 'unverified' until DRC/design explicitly marks it clean. Then ensure the clean-marking assignments in steps 3-5 happen AFTER LastPcbPath is set in their respective flows (they already do: DrcCore is called after RouteCore sets nothing to LastPcbPath in that path — verify ordering in ExportPcb→RouteCore→DrcCore; LastPcbPath is set at line 438 before RouteCore, DrcCore runs inside RouteCore at line 494, so the order is LastPcbPath then DrcCore: correct). For DesignPcb, set LastDrcClean AFTER line 596 (after LastPcbPath) — adjust placement so the OnLastPcbPathChanged reset doesn't clobber it. Concretely, move the `LastDrcClean = result.Ok ...` assignment to just after `LastPcbPath = result.KicadPcbPath;` inside the `if (result.KicadPcbPath is not null)` block.

### Test plan

TDD — write the failing parser test FIRST (it fails against today's code because Parse currently returns Clean):

A) Replace/repurpose the now-wrong existing test in Foundry.Tests/PcbDrcTests.cs:124-130. The current `Parse_ExitZero_NoReportFile_ReconciledToClean` asserts `r.Ok` and `r.Clean` — that is the bug. Rename it and invert the assertions (this is the first-failing test):
```
[Fact]
public void Parse_ExitZero_NoReportFile_IsInconclusiveNotClean()
{
    var r = DrcReport.Parse(null, 0, null);
    Assert.True(r.Installed);
    Assert.False(r.Ok);
    Assert.False(r.Clean);
    Assert.Contains("could not verify", r.Summary, StringComparison.OrdinalIgnoreCase);
}
```
Run before the Parse edit → FAILS (today returns Ok=Clean=true). After the DrcReport.cs edit → PASSES.

B) Add a positive guard that explicit empty-violations JSON is STILL clean (regression guard for the narrowing):
```
[Fact]
public void Parse_ExplicitEmptyViolations_ExitZero_StaysClean()
{
    var r = DrcReport.Parse("{\"violations\":[],\"unconnected_items\":[]}", 0, null);
    Assert.True(r.Clean);
}
```
This already exists in spirit as Parse_CleanBoard_ExitZero_EmptyArrays_IsClean (PcbDrcTests.cs:110-122) — keep that one; it confirms the narrowing didn't over-reach.

C) GerberExporter.ExportAsync gate — add to Foundry.Tests/FabExportTests.cs in GerberExporterExportTests. These must guard on KiCad presence like the existing degrade tests (skip when KiCadInstaller.Locate() is not null):
  - ExportAsync_WhenKiCadAbsent_AndNotDrcClean_DoesNotPackage: with no KiCad, calling ExportAsync(tmp, outDir) (drcClean defaults false) returns Installed=false (the self-DRC short-circuits to NotInstalled), ZipPath null. (Mirrors existing ExportAsync_ReturnsNotInstalled_WhenKiCadAbsent but proves the DRC self-check runs first.)
  - ExportAsync_DrcCleanTrue_SkipsSelfDrc: cannot fully assert without KiCad, so assert behavior parity — with drcClean:true and KiCad absent it should still reach the export attempt and degrade via the export run's NotInstalled (KiCadInstaller.Locate() check at line 84). Guard-skip when KiCad present. (This documents that drcClean:true bypasses the gate.)
Note: a true end-to-end 'dirty board → Failed, no ZIP' test requires a real KiCad + a deliberately failing board, which the suite already avoids (all kicad-cli tests guard-skip). Cover the gate logic at the unit level via the Parse tests (A) plus a new pure check below.

D) Add a pure FabExportResult.Failed-shape assertion proving the blocked verdict carries the DRC summary (Foundry.Tests/FabExportTests.cs, FabExportResultFactoryTests):
```
[Fact]
public void Failed_FromDrcBlock_CarriesDrcSummaryAndNotIsOk()
{
    var r = FabExportResult.Failed("Fab export blocked — board is not DRC-clean: DRC found 3 errors.", new[] {"detail"});
    Assert.False(r.Ok); Assert.True(r.Installed); Assert.Null(r.ZipPath);
    Assert.Contains("not DRC-clean", r.Summary);
}
```

E) Build verification: `dotnet build` then `dotnet test Foundry.Tests` — confirm the PcbDesigner.cs named-arg change compiles (positional break is the canary) and all PcbDrc/FabExport tests pass. The VM (TabViewModels.cs) has no unit tests in the suite; verify it builds and manually note CanExportFab now requires LastDrcClean.

### Risks

- The existing test PcbDrcTests.cs:124-130 (Parse_ExitZero_NoReportFile_ReconciledToClean) encodes the OLD behavior and WILL fail after the DrcReport edit — it must be updated/renamed (covered in test plan A). Leaving it asserts the bug.
- Behavior change: any real board where kicad-cli exits 0 but writes no JSON report now reports DRC inconclusive (Ok=false). If a supported KiCad version legitimately omits the report on a genuinely clean board, those boards will no longer auto-pass and fab will be blocked. Mitigation: PcbDrc.CheckAsync always passes --output "<reportPath>" and --format json (PcbDrc.cs:37,64-83), and modern kicad-cli writes the file on clean boards, so this should be the unverified-edge case the gate is meant to catch, not normal operation.
- Signature change to GerberExporter.ExportAsync inserts drcClean/drcOptions before the ct param — the ONLY positional 4-arg caller is PcbDesigner.cs:133, updated in this plan. Confirmed via grep there are exactly two call sites (PcbDesigner.cs:133, TabViewModels.cs:629); the VM uses the 2-arg overload which is unaffected. Any external/test caller passing ct positionally as the 4th arg would now bind ct to drcClean — none found in the suite (FabExportTests.cs:295,311 use 2-arg).
- VM ordering risk: OnLastPcbPathChanged resets LastDrcClean=false, so every flow that sets LastPcbPath must set LastDrcClean AFTER it. DesignPcb sets LastPcbPath at line 596; the LastDrcClean assignment must move to immediately after that line, not before, or the reset clobbers it. Same care in ExportPcb→RouteCore→DrcCore (LastPcbPath set at 438, DrcCore runs later inside RouteCore — safe).
- DesignAndExportFab path: gate is now enforced in THREE places (parser, ExportAsync via drcClean:true skip, orchestrator's existing design.Ok guard). With drcClean:true the orchestrator trusts design.Ok; if a future refactor sets design.Ok without report.Clean, the self-DRC is skipped. Low risk today since RunLoopAsync ties Ok to report.Clean (PcbDesigner.cs:191-196,229).
- Self-running DRC inside ExportAsync for the standalone button adds a kicad-cli pcb drc invocation (a few seconds) to EXPORT GERBERS that previously skipped it — acceptable cost for correctness, and it only runs when drcClean is false.

---

