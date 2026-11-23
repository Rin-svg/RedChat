using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using RedChat.Core;

namespace RedChat.Infrastructure.Tests;

/// <summary>
/// Protecteur de test : masque les octets (XOR) et ajoute une somme de contrôle, pour que les tests puissent vérifier que rien
/// n'est écrit « en clair » et détecter les altérations. Permet aussi d'injecter des pannes à la demande.
/// </summary>
internal sealed class TestProtector : ISecretProtector
{
    private static readonly byte[] Header = "TP1"u8.ToArray();
    private readonly byte _key;
    private int _protectCalls;

    public TestProtector(byte key = 0xA5) => _key = key;

    /// <summary>Nombre d'appels à Protect (= nombre d'écritures protégées tentées).</summary>
    public int ProtectCalls => Volatile.Read(ref _protectCalls);

    /// <summary>Si renseigné : appelé avec le texte clair ; l'exception retournée est levée (panne simulée).</summary>
    public Func<byte[], Exception?>? FailProtect { get; set; }

    /// <summary>Si vrai : Protect renvoie un résultat démesuré (pour tester la limite de taille).</summary>
    public bool OversizedOutput { get; set; }

    public byte[] Protect(byte[] plaintext)
    {
        _ = Interlocked.Increment(ref _protectCalls);
        Exception? failure = FailProtect?.Invoke(plaintext);
        if (failure is not null)
        {
            throw failure;
        }

        if (OversizedOutput)
        {
            return new byte[ProfileStore.MaxHistoryFileBytes + 1];
        }

        byte[] check = SHA256.HashData(plaintext);
        byte[] result = new byte[Header.Length + plaintext.Length + check.Length];
        Header.CopyTo(result, 0);
        for (int i = 0; i < plaintext.Length; i++)
        {
            result[Header.Length + i] = (byte)(plaintext[i] ^ _key);
        }

        check.CopyTo(result, Header.Length + plaintext.Length);
        return result;
    }

    public byte[] Unprotect(byte[] protectedData)
    {
        if (protectedData.Length < Header.Length + SHA256.HashSizeInBytes || !protectedData.AsSpan(0, Header.Length).SequenceEqual(Header))
        {
            throw new CryptographicException("Données non reconnues.");
        }

        int length = protectedData.Length - Header.Length - SHA256.HashSizeInBytes;
        byte[] plain = new byte[length];
        for (int i = 0; i < length; i++)
        {
            plain[i] = (byte)(protectedData[Header.Length + i] ^ _key);
        }

        if (!CryptographicOperations.FixedTimeEquals(SHA256.HashData(plain), protectedData.AsSpan(Header.Length + length)))
        {
            throw new CryptographicException("Données altérées.");
        }

        return plain;
    }
}

/// <summary>Dossier temporaire supprimé à la fin du test.</summary>
internal sealed class TempRoot : IDisposable
{
    public TempRoot() => Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "redchat-tests-" + Guid.NewGuid().ToString("N"));

    public string Path { get; }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(Path))
            {
                Directory.Delete(Path, recursive: true);
            }
        }
        catch (IOException)
        {
            return; // nettoyage au mieux : un résidu dans le dossier temporaire du système est sans conséquence.
        }
        catch (UnauthorizedAccessException)
        {
            return;
        }
    }
}

/// <summary>Horloge et minuteur pilotés à la main : l'anti-rafale se teste sans attendre, donc sans test instable.</summary>
internal sealed class ManualTimeProvider : TimeProvider
{
    public static readonly DateTimeOffset Start = new(2026, 11, 15, 12, 0, 0, TimeSpan.Zero);

    public override DateTimeOffset GetUtcNow() => Start;

    public ManualTimer? Timer { get; private set; }

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        Timer = new ManualTimer(callback, state);
        return Timer;
    }

    internal sealed class ManualTimer : ITimer
    {
        private readonly TimerCallback _callback;
        private readonly object? _state;

        public ManualTimer(TimerCallback callback, object? state)
        {
            _callback = callback;
            _state = state;
        }

        public int ChangeCalls { get; private set; }

        public TimeSpan LastDue { get; private set; }

        public bool Armed { get; private set; }

        public bool Disposed { get; private set; }

        public bool Change(TimeSpan dueTime, TimeSpan period)
        {
            ChangeCalls++;
            LastDue = dueTime;
            Armed = dueTime != Timeout.InfiniteTimeSpan;
            return true;
        }

        /// <summary>Simule l'échéance du minuteur.</summary>
        public void Fire()
        {
            Armed = false;
            _callback(_state);
        }

        public void Dispose() => Disposed = true;

        public ValueTask DisposeAsync()
        {
            Disposed = true;
            return ValueTask.CompletedTask;
        }
    }
}

/// <summary>Transport toujours hors ligne : tout reste en file d'attente.</summary>
internal sealed class OfflineTransport : ITransport
{
    public bool TrySend(string peerId, Packet packet) => false;
}

internal static class Fixtures
{
    public static string Self => new('a', 64);

    public static string B => new('b', 64);

    public static string C => new('c', 64);

    public static ChatService NewService() => new(Self, new OfflineTransport(), new ManualTimeProvider());

    /// <summary>Un service avec un peu de tout : messages, réponse, réaction, édition, groupe, message entrant.</summary>
    public static ChatService Rich()
    {
        ChatService s = NewService();
        ChatMessage m1 = s.SendText(B, "salut");
        _ = s.SendText(B, "deuxième", m1.Id);
        s.React(B, m1.Id, "👍");
        s.EditMessage(B, m1.Id, "salut (modifié)");
        ConversationInfo g = s.CreateGroup("Les amis", new[] { B, C });
        _ = s.SendText(g.Id, "bonjour le groupe");
        s.Receive(B, new Packet
        {
            Kind = PacketKind.Text,
            Id = Guid.NewGuid().ToString("D"),
            ConversationId = Self,
            SentAtUnixMs = ManualTimeProvider.Start.ToUnixTimeMilliseconds(),
            Text = "coucou",
        });
        return s;
    }

    /// <summary>Description complète et déterministe de l'état (pour comparer avant/après restauration).</summary>
    public static string Describe(ChatService s)
    {
        StringBuilder sb = new();
        foreach (ConversationInfo c in s.GetConversations().OrderBy(x => x.Id, StringComparer.Ordinal))
        {
            _ = sb.Append(CultureInfo.InvariantCulture, $"C {c.Id} group={c.IsGroup} title={c.Title} members={string.Join(',', c.Members)} creator={c.CreatorId}\n");
            foreach (ChatMessage m in s.GetMessages(c.Id))
            {
                string rx = string.Join(',', m.Reactions.OrderBy(k => k.Key, StringComparer.Ordinal).Select(k => k.Key + ":" + k.Value));
                string rc = string.Join(',', m.Receipts.OrderBy(k => k.Key, StringComparer.Ordinal).Select(k => k.Key + ":" + k.Value));
                _ = sb.Append(CultureInfo.InvariantCulture, $"  M {m.Id} from={m.SenderId} text={m.Text} at={m.SentAtUnixMs} out={m.IsOutgoing} reply={m.ReplyToId} edited={m.IsEdited} deleted={m.IsDeleted} read={m.IsReadByMe} rx=[{rx}] rc=[{rc}] st={m.Status}\n");
            }
        }

        foreach (OutboxSnapshot o in s.ExportState().Outbox.OrderBy(x => x.PeerId, StringComparer.Ordinal))
        {
            _ = sb.Append(CultureInfo.InvariantCulture, $"O {o.PeerId}: {string.Join(',', o.Packets.Select(p => p.Kind + "/" + p.Id))}\n");
        }

        return sb.ToString();
    }
}
