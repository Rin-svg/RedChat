using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using RedChat.Presentation;
using CheckBox = System.Windows.Controls.CheckBox;

namespace RedChat.Wpf.Views;

/// <summary>Dialogue de sélection des membres et de création d'un groupe.</summary>
public partial class CreateGroupWindow : Window
{
    private readonly CreateGroupViewModel _viewModel;

    /// <summary>Crée le dialogue pour le ViewModel fourni.</summary>
    public CreateGroupWindow(CreateGroupViewModel viewModel)
    {
        InitializeComponent();
        _viewModel = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
        DataContext = viewModel;
        _viewModel.PropertyChanged += OnViewModelPropertyChanged;
        Closed += OnClosed;
    }

    private void OnMemberSelectionChanged(object sender, RoutedEventArgs e)
    {
        if (sender is CheckBox { Tag: string peerId, IsChecked: bool selected })
        {
            _viewModel.SetSelected(peerId, selected);
        }
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(CreateGroupViewModel.CreatedGroup) && _viewModel.CreatedGroup is not null)
        {
            DialogResult = true;
        }
    }

    private void OnClosed(object? sender, EventArgs e) => _viewModel.PropertyChanged -= OnViewModelPropertyChanged;
}
