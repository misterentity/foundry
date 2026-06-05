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

/// <summary>Base for the workspace tab view models — all read the canonical Project.</summary>
public abstract class TabViewModelBase : ObservableObject
{
    protected TabViewModelBase(Project project) => Project = project;
    public Project Project { get; }
}
