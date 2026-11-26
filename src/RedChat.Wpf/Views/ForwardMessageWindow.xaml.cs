using System.ComponentModel;
using System.Windows;
using System.Windows.Input;
using RedChat.Presentation;

namespace RedChat.Wpf.Views;

/// <summary>Dialogue de choix de la conversation de destination d'un message transféré.</summary>
public partial class ForwardMessageWindow : Window
{
    private readonly ForwardMessageViewModel _viewModel;

    /// <summary>Crée le dialogue pour le ViewModel fourni.</summary>
    public ForwardMessageWindow(ForwardMessageViewModel viewModel)
    {
        InitializeComponent();
        _viewModel = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
        DataContext = viewModel;
        _viewModel.PropertyChanged += OnViewModelPropertyChanged;
        Closed += OnClosed;
    }

    private void OnTargetDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (_viewModel.ForwardCommand.CanExecute(null))
        {
            _viewModel.ForwardCommand.Execute(null);
        }
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ForwardMessageViewModel.ForwardedMessage) && _viewModel.ForwardedMessage is not null)
        {
            DialogResult = true;
        }
    }

    private void OnClosed(object? sender, EventArgs e) => _viewModel.PropertyChanged -= OnViewModelPropertyChanged;
}