# RedChat v19 — interface d'appels non modale (correctif)

## Problèmes corrigés
1. **Boîtes de dialogue modales en cascade** : chaque état WebRTC, « invitation envoyée », « invitation acceptée » et la réponse à un appel entrant ouvraient un `MessageBox`. Tout passe maintenant par un **bandeau non modal** au-dessus de la conversation (état, erreur, Accepter / Refuser, Masquer).
2. **Boutons toujours visibles** : micro, caméra et raccrocher n'apparaissent que si l'état l'exige (micro : appel accepté ; caméra : appel vidéo accepté ; raccrocher : appel en cours). Les boutons d'appel sont désactivés sans conversation privée ou pendant un appel (protège du double-clic).
3. **Logique d'appel dans la fenêtre** : déplacée dans `CallViewModel` (Presentation), testable sans WPF.

## Fiabilisation du moteur média (`WebRtcMediaEngine`)
- Micro/caméra coupés **avant** la connexion : l'intention est mémorisée et appliquée au démarrage des périphériques.
- Démarrage/arrêt des périphériques et bascules micro/caméra **sérialisés** (`SemaphoreSlim`).
- `DisposeAsync` idempotent et thread-safe (`Interlocked`).
- Le moteur implémente `ICallMediaControls`, consommé par le ViewModel.

## Nouveaux fichiers
- `src/RedChat.Infrastructure/ICallController.cs` (implémenté par `CallSignalingService`)
- `src/RedChat.Presentation/ICallMediaControls.cs`, `CallViewModel.cs`
- `tests/RedChat.Presentation.Tests/CallViewModelTests.cs` (27 tests)

## Vérification (honnêteté)
- Compilé en mode strict (`TreatWarningsAsErrors`, analyseurs) et testé dans l'environnement de préparation : Core, Infrastructure, Presentation, **27/27 tests CallViewModel** ; test de mutation manuel : 9 mutants sur 10 tués, le 10e était une protection redondante (supprimée).
- **Non compilé** : `RedChat.Wpf` (WPF + SIPSorcery indisponibles hors Windows/NuGet). Relu et vérifié syntaxiquement ; le XAML est bien formé.
- Suite complète (454 tests existants + 27) à exécuter sous Windows.
- Les appels restent à tester entre deux appareils ; aucun STUN/TURN (réseau local uniquement).

```powershell
dotnet restore .\RedChat.sln
dotnet build .\RedChat.sln -c Release -warnaserror
dotnet test .\RedChat.sln -c Release
dotnet run --project .\src\RedChat.Wpf\RedChat.Wpf.csproj
```
Total de tests attendu : 454 + 27 = 481 (plus les tests de signalisation v19 étape 1 éventuellement non comptés dans 454).
