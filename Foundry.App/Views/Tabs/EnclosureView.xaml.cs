using System.ComponentModel;
using System.IO;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Media3D;
using Foundry.App.ViewModels;
using HelixToolkit.Wpf;

namespace Foundry.App.Views.Tabs;

public partial class EnclosureView : UserControl
{
    public EnclosureView()
    {
        InitializeComponent();
        DataContextChanged += (_, _) => Hook();
        Loaded += (_, _) => Hook();
    }

    private EnclosureViewModel? _vm;

    private void Hook()
    {
        if (DataContext is not EnclosureViewModel vm || ReferenceEquals(vm, _vm)) return;
        if (_vm is not null) _vm.PropertyChanged -= OnVmChanged;
        _vm = vm;
        _vm.PropertyChanged += OnVmChanged;
        if (vm.StlBytes is not null) RenderMesh(vm.StlBytes);
    }

    private void OnVmChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(EnclosureViewModel.StlBytes) && _vm?.StlBytes is { } bytes)
            RenderMesh(bytes);
    }

    private void RenderMesh(byte[] stl)
    {
        try
        {
            var reader = new StLReader
            {
                DefaultMaterial = MaterialHelper.CreateMaterial(Color.FromRgb(0x3A, 0x3A, 0x46)),
            };
            using var ms = new MemoryStream(stl);
            var model = reader.Read(ms);
            if (model is null) return;
            model.Freeze();
            MeshHost.Content = model;
            Viewport.ZoomExtents(0);
        }
        catch
        {
            // leave the schematic preview in place on any read failure
        }
    }
}
