using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using RedChat.Presentation;
using RedChat.Wpf;
using UserControl = System.Windows.Controls.UserControl;
using Button = System.Windows.Controls.Button;

namespace RedChat.Wpf.Views;

public partial class HomeLandingPage : UserControl
{
    private bool _isEntering;

    /// <summary>Initialise l'écran d'accueil et son décor 3D animé.</summary>
    public HomeLandingPage()
    {
        InitializeComponent();
        Loaded += OnLandingLoaded;
        Unloaded += OnLandingUnloaded;
        try
        {
            LandingScene3D.Initialize(HeroScene);
        }
        catch (Exception exception)
        {
            HeroScene.Visibility = Visibility.Collapsed;
            AppLogger.Write(exception);
        }
    }

    private void OnLandingLoaded(object sender, RoutedEventArgs e)
    {
        LandingRoot.Opacity = 0;
        LandingRoot.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(700))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
        });
        LandingEntranceTransform.BeginAnimation(TranslateTransform.YProperty,
            new DoubleAnimation(18, 0, TimeSpan.FromMilliseconds(700))
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
            });

        AnimateFloat(ChatPreviewCard, -5, -7, 3.8);
        AnimateFloat(PrivateConnectionCard, 5, 6, 4.4);
        AnimateFloat(MomentsCard, 4, -5, 3.5);
        HeroLogoTilt.BeginAnimation(RotateTransform.AngleProperty,
            new DoubleAnimation(-2, 2, TimeSpan.FromSeconds(5.5))
            {
                AutoReverse = true,
                RepeatBehavior = RepeatBehavior.Forever,
                EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut },
            });
        AnimatePulse(OnlineDot, 0.45, 1, 1.4);
        AnimatePulse(HeroGlow, 0.72, 1, 3.2);
    }

    private void OnLandingUnloaded(object sender, RoutedEventArgs e)
    {
        LandingRoot.BeginAnimation(OpacityProperty, null);
        LandingEntranceTransform.BeginAnimation(TranslateTransform.YProperty, null);
        StopFloat(ChatPreviewCard);
        StopFloat(PrivateConnectionCard);
        StopFloat(MomentsCard);
        HeroLogoTilt.BeginAnimation(RotateTransform.AngleProperty, null);
        OnlineDot.BeginAnimation(OpacityProperty, null);
        HeroGlow.BeginAnimation(OpacityProperty, null);
        LoadingSpinnerRotation.BeginAnimation(RotateTransform.AngleProperty, null);
        LoadingProgress.BeginAnimation(WidthProperty, null);
        LoadingOverlay.BeginAnimation(OpacityProperty, null);
    }

    private static void AnimateFloat(Border element, double angle, double distance, double seconds)
    {
        TranslateTransform translate = new();
        TransformGroup transforms = new();
        transforms.Children.Add(new RotateTransform(angle));
        transforms.Children.Add(translate);
        element.RenderTransformOrigin = new System.Windows.Point(0.5, 0.5);
        element.RenderTransform = transforms;
        translate.BeginAnimation(TranslateTransform.YProperty,
            new DoubleAnimation(-distance, distance, TimeSpan.FromSeconds(seconds))
            {
                AutoReverse = true,
                RepeatBehavior = RepeatBehavior.Forever,
                EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut },
            });
    }

    private static void StopFloat(Border element)
    {
        if (element.RenderTransform is not TransformGroup transforms)
        {
            return;
        }

        foreach (TranslateTransform translate in transforms.Children.OfType<TranslateTransform>())
        {
            translate.BeginAnimation(TranslateTransform.YProperty, null);
        }
    }

    private static void AnimatePulse(UIElement element, double from, double to, double seconds) =>
        element.BeginAnimation(OpacityProperty,
            new DoubleAnimation(from, to, TimeSpan.FromSeconds(seconds))
            {
                AutoReverse = true,
                RepeatBehavior = RepeatBehavior.Forever,
                EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut },
            });

    /// <summary>Déclenché lorsque l'utilisateur demande à ouvrir ses discussions.</summary>
    public event EventHandler? EnterRequested;

    /// <summary>Déclenché pour démarrer ou arrêter la collecte des annonces du réseau local.</summary>
    public event EventHandler? ScanToggleRequested;

    /// <summary>Déclenché lorsqu’un utilisateur demande une connexion vers un pair annoncé.</summary>
    public event Action<DiscoveredPeerViewModel>? ConnectPeerRequested;

    /// <summary>Actualise le libellé de commande selon l’état d’écoute du scan.</summary>
    public void UpdateScanButtonLabel(bool isActive) =>
        ScanButtonLabel.Text = isActive ? "Arrêter le scan" : "Scanner le réseau";

    private async void Enter_Click(object sender, RoutedEventArgs e)
    {
        if (_isEntering)
        {
            return;
        }

        _isEntering = true;
        LoadingOverlay.Visibility = Visibility.Visible;
        LoadingOverlay.BeginAnimation(OpacityProperty,
            new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(220))
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
            });
        LoadingSpinnerRotation.BeginAnimation(RotateTransform.AngleProperty,
            new DoubleAnimation(0, 360, TimeSpan.FromSeconds(1.1))
            {
                RepeatBehavior = RepeatBehavior.Forever,
            });
        LoadingProgress.BeginAnimation(WidthProperty,
            new DoubleAnimation(36, 220, TimeSpan.FromMilliseconds(900))
            {
                AutoReverse = true,
                RepeatBehavior = RepeatBehavior.Forever,
                EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut },
            });

        await Task.Delay(1100);
        EnterRequested?.Invoke(this, EventArgs.Empty);
    }

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
