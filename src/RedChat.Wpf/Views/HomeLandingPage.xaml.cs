using System.Windows;
using System.Windows.Controls;
using RedChat.Presentation;
using RedChat.Wpf;
using UserControl = System.Windows.Controls.UserControl;
using Button = System.Windows.Controls.Button;

namespace RedChat.Wpf.Views;

public partial class HomeLandingPage : UserControl
{
    /// <summary>Initialise l'écran d'accueil et son décor 3D animé.</summary>
    public HomeLandingPage()
    {
        InitializeComponent();
        LandingScene3D.Initialize(HeroScene);
    }

    /// <summary>Déclenché lorsque l'utilisateur demande à ouvrir ses discussions.</summary>
    public event EventHandler? EnterRequested;

    /// <summary>Déclenché pour démarrer ou arrêter la collecte des annonces du réseau local.</summary>
    public event EventHandler? ScanToggleRequested;

    /// <summary>Déclenché lorsqu’un utilisateur demande une connexion vers un pair annoncé.</summary>
    public event Action<DiscoveredPeerViewModel>? ConnectPeerRequested;

    /// <summary>Actualise le libellé de commande selon l’état d’écoute du scan.</summary>
    public void UpdateScanButtonLabel(bool isActive) =>
        ScanButtonLabel.Text = isActive ? "Arrêter le scan" : "Scanner le réseau";

    private void Enter_Click(object sender, RoutedEventArgs e) =>
        EnterRequested?.Invoke(this, EventArgs.Empty);

    private void ScanToggle_Click(object sender, RoutedEventArgs e) =>
        ScanToggleRequested?.Invoke(this, EventArgs.Empty);

    private void ConnectPeer_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: DiscoveredPeerViewModel peer })
        {
            ConnectPeerRequested?.Invoke(peer);
        }
    }
}
