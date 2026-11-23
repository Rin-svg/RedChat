using System.Net;
using System.Net.Sockets;
using RedChat.Core;
using Xunit;

namespace RedChat.Infrastructure.Tests;

/// <summary>Aides de test : nœuds sur la boucle locale, attentes bornées (aucun test ne peut rester bloqué).</summary>
internal static class NetHelpers
{
    public static readonly TimeSpan Limit = TimeSpan.FromSeconds(10);

    public static T Await<T>(Task<T> task)
    {
        try
        {
            return task.WaitAsync(Limit).GetAwaiter().GetResult();
        }
        catch (TimeoutException)
        {
            throw new TimeoutException("Opération trop longue.");
        }
    }

    public static void Await(Task task)
    {
        try
        {
            task.WaitAsync(Limit).GetAwaiter().GetResult();
        }
        catch (TimeoutException)
        {
            throw new TimeoutException("Opération trop longue.");
        }
    }

    /// <summary>Attend (par petits pas) qu'une condition devienne vraie ; retourne son dernier état.</summary>
    public static bool WaitUntil(Func<bool> condition, int timeoutMs = 10000)
    {
        DateTime end = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTime.UtcNow < end)
        {
            if (condition())
            {
                return true;
            }

            Thread.Sleep(20);
        }

        return condition();
    }

    public static int FreeUdpPort()
    {
        using UdpClient u = new(new IPEndPoint(IPAddress.Loopback, 0));
        return ((IPEndPoint)u.Client.LocalEndPoint!).Port;
    }

    /// <summary>Ensemble de nœuds locaux, libérés ensemble.</summary>
    internal sealed class Rig : IDisposable
    {
        private readonly List<LanNode> _nodes = new();
        private readonly List<LocalIdentity> _ids = new();

        public LanNode Add(LocalIdentity? identity = null, LanNodeOptions? options = null)
        {
            LocalIdentity id = identity ?? LocalIdentity.Create();
            if (identity is null)
            {
                _ids.Add(id);
            }

            LanNode n = new(id, options ?? new LanNodeOptions { BindAddress = IPAddress.Loopback, EnableDiscovery = false });
            n.Start();
            _nodes.Add(n);
            return n;
        }

        public LocalIdentity NewIdentity()
        {
            LocalIdentity id = LocalIdentity.Create();
            _ids.Add(id);
            return id;
        }

        public static IPEndPoint EndpointOf(LanNode n) => new(IPAddress.Loopback, n.Transport.Port);

        public static string Text(LanNode n) => $"127.0.0.1:{n.Transport.Port}";

        /// <summary>Connecte a → b et attend que les deux côtés voient la connexion.</summary>
        public static void Link(LanNode a, LanNode b, string? expected = null)
        {
            string remote = Await(a.Transport.ConnectAsync(EndpointOf(b), expected, CancellationToken.None));
            Assert.Equal(b.PeerId, remote);
            Assert.True(WaitUntil(() => a.Transport.IsConnected(remote) && b.Transport.IsConnected(a.PeerId)));
        }

        public void Dispose()
        {
            foreach (LanNode n in _nodes)
            {
                Await(n.DisposeAsync().AsTask());
            }

            foreach (LocalIdentity id in _ids)
            {
                id.Dispose();
            }
        }
    }
}
