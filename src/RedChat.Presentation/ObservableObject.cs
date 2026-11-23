using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace RedChat.Presentation;

/// <summary>Base des objets observables utilisés par le binding MVVM, sans dépendance WPF.</summary>
public abstract class ObservableObject : INotifyPropertyChanged
{
    /// <summary>Notifie la vue qu'une propriété a changé.</summary>
    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>Met à jour un champ et notifie uniquement si sa valeur change.</summary>
    protected bool SetProperty<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return false;
        }

        field = value;
        OnPropertyChanged(propertyName);
        return true;
    }

    /// <summary>Notifie explicitement un changement de propriété calculée.</summary>
    protected void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
