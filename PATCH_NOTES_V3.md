# RedChat — correctifs v3

## Cycle de vie du verrou de profil Windows

- Le mutex nommé est maintenant acquis explicitement avec `WaitOne(0)`.
- Un `AbandonedMutexException` est traité comme une acquisition réussie : Windows transmet la propriété du mutex au nouveau processus après un arrêt brutal de l'ancienne instance.
- La libération du mutex est renvoyée au dispatcher WPF, car un `Mutex` Windows doit être libéré par le même thread qui l'a acquis. Le nettoyage asynchrone ne libère donc plus le mutex depuis un thread de pool.
- Le chemin `Dispose()` synchrone pompe temporairement le dispatcher pendant le nettoyage asynchrone afin d'éviter de bloquer le thread propriétaire du mutex.

## Vérification

- Le rapport fourni par l'utilisateur pour la version précédente confirme : `dotnet build -c Release -warnaserror` réussi et `dotnet test -c Release` : 426 réussis, 0 échec, 0 ignoré.
- Après le correctif v3 : audit statique `audit_csharp.py` = 0 alerte ; XML des fichiers `.csproj` et `.xaml` valide ; archive contrôlée.
- Le correctif WPF v3 n'a pas été compilé dans cet environnement (SDK .NET absent) : il doit être recompilé sur Windows avec les commandes du README.
