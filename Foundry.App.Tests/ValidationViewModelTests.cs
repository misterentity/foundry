using Foundry.App.ViewModels;
using Foundry.Core.Project;

namespace Foundry.App.Tests;

// The report card is the one screen a user reads to decide whether to apply power, so its grade and
// verdict are load-bearing text. Grading on fails and warns alone meant a project whose checks could
// not be COMPLETED still scored "A" and was told it was safe to power on — the engine certifying
// exactly what it had never looked at.
public class ValidationViewModelTests
{
    private static Project WithFindings(params (string Severity, string Code)[] findings)
    {
        var p = DemoData.CreateSoilMoistureProject();
        p.Findings = findings
            .Select(f => new Finding { Severity = f.Severity, Code = f.Code, Title = f.Code })
            .ToList();
        return p;
    }

    private static ValidationViewModel Vm(Project p) => new(p);

    [Fact]
    public void UnprovenChecks_AreCounted()
    {
        var vm = Vm(WithFindings(("unproven", "FIT-UNK"), ("unproven", "X"), ("pass", "OK")));
        Assert.Equal(2, vm.UnprovenCount);
    }

    // The core regression: no failures, no warnings, but a check the engine could not complete.
    [Fact]
    public void AnUnprovenCheck_BlocksTheTopGradeAndTheSafeToPowerOnVerdict()
    {
        var vm = Vm(WithFindings(("pass", "OK"), ("unproven", "FIT-UNK")));

        Assert.Equal(0, vm.FailCount);
        Assert.Equal(0, vm.WarnCount);
        Assert.NotEqual("A", vm.Grade);
        Assert.Equal("?", vm.Grade);
        Assert.Equal("unproven", vm.GradeSeverity);
        Assert.Equal("UNPROVEN", vm.OverallStatus);
        Assert.DoesNotContain("safe to power on", vm.Verdict, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("couldn't be completed", vm.Verdict, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Verdict_PluralisesTheUnprovenCount()
    {
        Assert.Contains("1 check couldn't", Vm(WithFindings(("unproven", "A"))).Verdict);
        Assert.Contains("2 checks couldn't", Vm(WithFindings(("unproven", "A"), ("unproven", "B"))).Verdict);
    }

    // Severity precedence: a real defect always outranks "I couldn't tell".
    [Fact]
    public void AFailureStillOutranksAnUnprovenCheck()
    {
        var vm = Vm(WithFindings(("fail", "FIT-XY"), ("unproven", "FIT-UNK")));
        Assert.Equal("F", vm.Grade);
        Assert.Equal("fail", vm.GradeSeverity);
        Assert.Equal("FAIL", vm.OverallStatus);
        Assert.Contains("resolve the failures", vm.Verdict, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void AWarningOutranksAnUnprovenCheck()
    {
        var vm = Vm(WithFindings(("warn", "PIN-04"), ("unproven", "FIT-UNK")));
        Assert.Equal("warn", vm.GradeSeverity);
        Assert.Equal("WARN", vm.OverallStatus);
    }

    // A genuinely clean design must still be able to earn an A — the guard must not be a blanket downgrade.
    [Fact]
    public void ACleanDesignStillGradesA()
    {
        var vm = Vm(WithFindings(("pass", "OK"), ("pass", "OK2")));
        Assert.Equal("A", vm.Grade);
        Assert.Equal("PASS", vm.OverallStatus);
        Assert.Contains("safe to power on", vm.Verdict, StringComparison.OrdinalIgnoreCase);
    }

    // `unproven` findings route to guidance, not to an "Apply & re-run" button an AI edit cannot satisfy.
    [Fact]
    public void UnprovenFindings_AreAdvisory()
    {
        Assert.True(new Finding { Severity = "unproven", Code = "FIT-UNK" }.Advisory);
        Assert.False(new Finding { Severity = "fail", Code = "FIT-XY" }.Advisory);
    }
}
