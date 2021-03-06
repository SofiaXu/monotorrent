//
// ConnectionManager.cs
//
// Authors:
//   Alan McGovern alan.mcgovern@gmail.com
//
// Copyright (C) 2006 Alan McGovern
//
// Permission is hereby granted, free of charge, to any person obtaining
// a copy of this software and associated documentation files (the
// "Software"), to deal in the Software without restriction, including
// without limitation the rights to use, copy, modify, merge, publish,
// distribute, sublicense, and/or sell copies of the Software, and to
// permit persons to whom the Software is furnished to do so, subject to
// the following conditions:
// 
// The above copyright notice and this permission notice shall be
// included in all copies or substantial portions of the Software.
// 
// THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND,
// EXPRESS OR IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF
// MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE AND
// NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS OR COPYRIGHT HOLDERS BE
// LIABLE FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY, WHETHER IN AN ACTION
// OF CONTRACT, TORT OR OTHERWISE, ARISING FROM, OUT OF OR IN CONNECTION
// WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE SOFTWARE.
//



using System;
using MonoTorrent.Common;
using System.Net.Sockets;
using System.Threading;
using MonoTorrent.Client.Encryption;
using System.Diagnostics;
using System.Collections.Generic;
using System.Net;
using System.Net.NetworkInformation;
using MonoTorrent.Client.Messages;
using MonoTorrent.Client.Connections;
using MonoTorrent.Client.Messages.FastPeer;
using MonoTorrent.Client.Messages.Standard;
using MonoTorrent.Client.Messages.Libtorrent;
using System.Threading.Tasks;

namespace MonoTorrent.Client
{
    /// <summary>
    /// Main controller class for all incoming and outgoing connections
    /// </summary>
    public class ConnectionManager
    {
        struct AsyncConnectState
        {
            public AsyncConnectState (TorrentManager manager, IConnection connection, Stopwatch timer)
            {
                Manager = manager;
                Connection = connection;
                Timer = timer;
            }

            public IConnection Connection;
            public TorrentManager Manager;
            public Stopwatch Timer;
        }

        #region Events

        public event EventHandler<AttemptConnectionEventArgs> BanPeer;

        /// <summary>
        /// Event that's fired every time a message is sent or Received from a Peer
        /// </summary>
        public event EventHandler<PeerMessageEventArgs> PeerMessageTransferred;

        #endregion


        #region Member Variables
        private ClientEngine engine;
        internal static readonly int ChunkLength = 2096 + 64;   // Download in 2kB chunks to allow for better rate limiting

        List<AsyncConnectState> PendingConnects { get; }

        /// <summary>
        /// The maximum number of half open connections
        /// </summary>
        public int MaxHalfOpenConnections
        {
            get { return this.engine.Settings.GlobalMaxHalfOpenConnections; }
        }


        /// <summary>
        /// The number of open connections
        /// </summary>
        public int OpenConnections
        {
            get
            {
                return (int) ClientEngine.MainLoop.QueueWait (() =>
                    (int) Toolbox.Accumulate<TorrentManager>(engine.Torrents, (m) =>
                        m.Peers.ConnectedPeers.Count
                    )
                );
            }
        }


        /// <summary>
        /// The maximum number of open connections
        /// </summary>
        public int MaxOpenConnections
        {
            get { return this.engine.Settings.GlobalMaxConnections; }
        }

        int TryConnectIndex {
            get; set;
        }
        #endregion


        #region Constructors

        /// <summary>
        /// 
        /// </summary>
        public ConnectionManager(ClientEngine engine)
        {
            this.engine = engine;

            PendingConnects = new List<AsyncConnectState>();
            ClientEngine.MainLoop.QueueTimeout(TimeSpan.FromSeconds(2.5), () =>  {
                CancelPendingConnects(null);
                return true;
            });
        }

        #endregion


        #region Async Connection Methods

        internal async void ConnectToPeer(TorrentManager manager, Peer peer)
        {
            // Connect to the peer.
            IConnection connection = ConnectionFactory.Create(peer.ConnectionUri);
            if (connection == null)
                return;

            var state = new AsyncConnectState(manager, connection, Stopwatch.StartNew ());
            PendingConnects.Add(state);
            manager.Peers.ConnectingToPeers.Add(peer);

            bool succeeded;
            try {
                await NetworkIO.ConnectAsync (connection);
                succeeded = true;
            } catch {
                succeeded = false;
            }

            PendingConnects.Remove (state);
            if (manager.Engine == null ||
                !manager.Mode.CanAcceptConnections) {
                connection.Dispose ();
                return;
            }

            try {
                manager.Peers.ConnectingToPeers.Remove (peer);
                if (!succeeded) {
                    Logger.Log (null, "ConnectionManager - Failed to connect{0}", peer);

                    manager.RaiseConnectionAttemptFailed (
                        new PeerConnectionFailedEventArgs (manager, peer, Direction.Outgoing, "EndCreateConnection"));

                    peer.FailedConnectionAttempts++;
                    connection.Dispose ();
                    manager.Peers.BusyPeers.Add (peer);
                } else {
                    PeerId id = new PeerId (peer, manager);
                    id.Connection = connection;
                    manager.Peers.ActivePeers.Add (peer);

                    Logger.Log (id.Connection, "ConnectionManager - Connection opened");

                    ProcessFreshConnection (id);
                }
            } catch (Exception) {
                // FIXME: Do nothing now?
            } finally {
                // Try to connect to another peer
                TryConnect ();
            }
        }


        internal async void ProcessFreshConnection(PeerId id)
        {
            // If we have too many open connections, close the connection
            if (OpenConnections > this.MaxOpenConnections)
            {
                CleanupSocket (id, "Too many connections");
                return;
            }

            try
            {
                id.ProcessingQueue = true;
                // Increase the count of the "open" connections
                var initialData = await EncryptorFactory.CheckEncryptionAsync (id, 0, new[] { id.TorrentManager.InfoHash });
                EndCheckEncryption(id, initialData);


                id.TorrentManager.Peers.ConnectedPeers.Add(id);
                id.WhenConnected.Restart ();
                // Baseline the time the last block was received
                id.LastBlockReceived.Restart ();
            }
            catch (Exception)
            {
                id.TorrentManager.RaiseConnectionAttemptFailed(
                    new PeerConnectionFailedEventArgs(id.TorrentManager, id.Peer, Direction.Outgoing, "ProcessFreshConnection: failed to encrypt"));

                id.Connection.Dispose();
                id.Connection = null;
            }
        }

        private async void EndCheckEncryption(PeerId id, byte[] initialData)
        {
            try
            {
                if (initialData != null && initialData.Length > 0)
                    throw new EncryptionException("unhandled initial data");

                EncryptionTypes e = engine.Settings.AllowedEncryption;
                if (id.Encryptor is RC4 && !Toolbox.HasEncryption(e, EncryptionTypes.RC4Full) ||
                    id.Encryptor is RC4Header && !Toolbox.HasEncryption(e, EncryptionTypes.RC4Header) ||
                    id.Encryptor is PlainTextEncryption && !Toolbox.HasEncryption(e, EncryptionTypes.PlainText))
                {
                    CleanupSocket(id, id.Encryptor.GetType().Name + " encryption is not enabled");
                }
                else
                {
                    // Create a handshake message to send to the peer
                    var handshake = new HandshakeMessage(id.TorrentManager.InfoHash, engine.PeerId, VersionInfo.ProtocolStringV100);
                    await PeerIO.SendMessageAsync (id.Connection, id.Encryptor, handshake, id.TorrentManager.UploadLimiter, id.Monitor, id.TorrentManager.Monitor);

                    // Receive their handshake
                    handshake = await PeerIO.ReceiveHandshakeAsync (id.Connection, id.Decryptor);
                    handshake.Handle(id);

                    // If there are any pending messages, send them otherwise set the queue
                    // processing as finished.
                    if (id.QueueLength > 0)
                        ProcessQueue(id);
                    else
                        id.ProcessingQueue = false;

                    ReceiveMessagesAsync(id.Connection, id.Decryptor, id.TorrentManager.DownloadLimiter, id.Monitor, id.TorrentManager, id);
                    // Alert the engine that there is a new usable connection
                    id.TorrentManager.HandlePeerConnected(id, Direction.Outgoing);
                }
            }
            catch
            {
                id.Peer.Encryption &= ~EncryptionTypes.RC4Full;
                id.Peer.Encryption &= ~EncryptionTypes.RC4Header;
                CleanupSocket(id, "Failed encryptor check");
            }
        }

        async void ReceiveMessagesAsync (IConnection connection, IEncryption decryptor, RateLimiterGroup downloadLimiter, ConnectionMonitor monitor, TorrentManager torrentManager, PeerId id)
        {
            try {
                while (true)
                {
                    var message = await PeerIO.ReceiveMessageAsync(connection, decryptor, downloadLimiter, monitor, torrentManager);
                    if (id.Disposed)
                    {
                        if (message is PieceMessage msg)
                            ClientEngine.BufferManager.FreeBuffer(msg.Data);
                    }
                    else
                    {
                        id.LastMessageReceived.Restart();

                        if (PeerMessageTransferred != null)
                            RaisePeerMessageTransferred(new PeerMessageEventArgs(id.TorrentManager, message, Direction.Incoming, id));

                        message.Handle(id);
                    }
                }
            } catch (Exception ex) {
                CleanupSocket (id, "Could not receive a message");
            }
        }

        #endregion


        #region Methods

        internal void CleanupSocket(PeerId id, string message = null)
        {
            if (id == null || id.Disposed) // Sometimes onEncryptoError will fire with a null id
                return;

            try
            {
                // We can reuse this peer if the connection says so and it's not marked as inactive
                bool canReuse = id.Connection.CanReconnect && !id.TorrentManager.InactivePeerManager.InactivePeerList.Contains(id.Uri);
                Logger.Log(id.Connection, "Cleanup Reason : " + message);

                Logger.Log(id.Connection, "*******Cleaning up*******");
                id.TorrentManager.PieceManager.Picker.CancelRequests(id);
                id.Peer.CleanedUpCount++;

                if (id.PeerExchangeManager != null)
                    id.PeerExchangeManager.Dispose();

                if (!id.AmChoking)
                    id.TorrentManager.UploadingTo--;

                id.Dispose();

                id.TorrentManager.Peers.ConnectedPeers.RemoveAll(delegate(PeerId other) { return id == other; });

                if (id.TorrentManager.Peers.ActivePeers.Contains(id.Peer))
                    id.TorrentManager.Peers.ActivePeers.Remove(id.Peer);

                // If we get our own details, this check makes sure we don't try connecting to ourselves again
                if (canReuse && id.Peer.PeerId != engine.PeerId)
                {
                    if (!id.TorrentManager.Peers.AvailablePeers.Contains(id.Peer) && id.Peer.CleanedUpCount < 5)
                        id.TorrentManager.Peers.AvailablePeers.Insert(0, id.Peer);
                }
            }

            finally
            {
                id.TorrentManager.RaisePeerDisconnected(
                    new PeerConnectionEventArgs( id.TorrentManager, id, Direction.None, message ) );
            }
        }

        internal void CancelPendingConnects (TorrentManager manager)
        {
            foreach (var pending in PendingConnects)
                if (pending.Manager == manager || pending.Timer.ElapsedMilliseconds > 10 * 1000)
                    pending.Connection.Dispose ();
        }

        /// <summary>
        /// This method is called when the ClientEngine recieves a valid incoming connection
        /// </summary>
        /// <param name="result"></param>
        public void IncomingConnectionAccepted(PeerId id)
        {
            try
            {
                bool maxAlreadyOpen = OpenConnections >= Math.Min(this.MaxOpenConnections, id.TorrentManager.Settings.MaxConnections);
                if (id.Peer.PeerId == engine.PeerId || maxAlreadyOpen)
                {
                    CleanupSocket (id, "Connection was not accepted");
                    return;
                }

                if (id.TorrentManager.Peers.ActivePeers.Contains(id.Peer))
                {
                    Logger.Log(id.Connection, "ConnectionManager - Already connected to peer");
                    id.Connection.Dispose();
                    return;
                }

                Logger.Log(id.Connection, "ConnectionManager - Incoming connection fully accepted");
                id.TorrentManager.Peers.AvailablePeers.Remove(id.Peer);
                id.TorrentManager.Peers.ActivePeers.Add(id.Peer);
                id.TorrentManager.Peers.ConnectedPeers.Add(id);
                id.WhenConnected.Restart ();
                // Baseline the time the last block was received
                id.LastBlockReceived.Restart ();

                id.TorrentManager.HandlePeerConnected(id, Direction.Incoming);

                // We've sent our handshake so begin our looping to receive incoming message
                ReceiveMessagesAsync (id.Connection, id.Decryptor, id.TorrentManager.DownloadLimiter, id.Monitor, id.TorrentManager, id);
            }
            catch (Exception e)
            {
                CleanupSocket (id, e.Message);
            }
        }

        /// <param name="id">The peer whose message queue you want to start processing</param>
        internal async void ProcessQueue(PeerId id)
        {
            while (id.QueueLength > 0) {
                try {
                    PeerMessage msg = id.Dequeue ();
                    var pm = msg as PieceMessage;

                    if (pm != null) {
                        pm.Data = ClientEngine.BufferManager.GetBuffer (pm.ByteLength);
                        await engine.DiskManager.ReadAsync (id.TorrentManager, pm.StartOffset + ((long)pm.PieceIndex * id.TorrentManager.Torrent.PieceLength), pm.Data, pm.RequestLength);
                        id.PiecesSent++;
                    }

                    await PeerIO.SendMessageAsync (id.Connection, id.Encryptor, msg, id.TorrentManager.UploadLimiter, id.Monitor, id.TorrentManager.Monitor);
                    if (msg is PieceMessage)
                        id.IsRequestingPiecesCount--;


                    if (pm != null)
                        ClientEngine.BufferManager.FreeBuffer (pm.Data);

                    // Fire the event to let the user know a message was sent
                    if (PeerMessageTransferred != null)
                        RaisePeerMessageTransferred (new PeerMessageEventArgs (id.TorrentManager, msg, Direction.Outgoing, id));

                    id.LastMessageSent.Restart ();
                } catch (Exception e) {
                    CleanupSocket (id, "Exception calling SendMessage: " + e.Message);
                    break;
                }
            }

            id.ProcessingQueue = false;
        }

        void RaisePeerMessageTransferred(PeerMessageEventArgs e)
        {
            ThreadPool.QueueUserWorkItem(delegate
            {
                EventHandler<PeerMessageEventArgs> h = PeerMessageTransferred;
                if (h == null)
                    return;

                if (!(e.Message is MessageBundle))
                {
                    h(e.TorrentManager, e);
                }
                else
                {
                    // Message bundles are only a convience for internal usage!
                    MessageBundle b = (MessageBundle)e.Message;
                    foreach (PeerMessage message in b.Messages)
                    {
                        PeerMessageEventArgs args = new PeerMessageEventArgs(e.TorrentManager, message, e.Direction, e.ID);
                        h(args.TorrentManager, args);
                    }
                }
            });
        }

        internal bool ShouldBanPeer(Peer peer)
        {
            if (BanPeer == null)
                return false;

            AttemptConnectionEventArgs e = new AttemptConnectionEventArgs(peer);
            BanPeer(this, e);
            return e.BanPeer;
        }

        internal void TryConnect()
        {
            int lastConnect = TryConnectIndex;
            bool connected = true;

            // If we have already reached our max connections globally, don't try to connect to a new peer
            while (OpenConnections < this.MaxOpenConnections && PendingConnects.Count < this.MaxHalfOpenConnections && connected) {
                connected = false;
                // Check each torrent manager in turn to see if they have any peers we want to connect to
                for (int i = lastConnect; i < engine.Torrents.Count; i ++) {
                    if (TryConnect(engine.Torrents[i])) {
                        connected = true;
                        TryConnectIndex = (i + 1) % engine.Torrents.Count;
                    }
                }

                lastConnect = 0;
            }
        }
        
        bool TryConnect (TorrentManager manager)
        {
            int i;
            Peer peer;
            if (!manager.Mode.CanAcceptConnections)
                return false;
            
            // If we have reached the max peers allowed for this torrent, don't connect to a new peer for this torrent
            if (manager.Peers.ConnectedPeers.Count >= manager.Settings.MaxConnections)
                return false;
            
            // If the torrent isn't active, don't connect to a peer for it
            if (!manager.Mode.CanAcceptConnections)
                return false;
            
            // If we are not seeding, we can connect to anyone. If we are seeding, we should only connect to a peer
            // if they are not a seeder.
            for (i = 0; i < manager.Peers.AvailablePeers.Count; i++)
                if (manager.Mode.ShouldConnect (manager.Peers.AvailablePeers[i]))
                    break;
            
            // If this is true, there were no peers in the available list to connect to.
            if (i == manager.Peers.AvailablePeers.Count)
                return false;
            
            // Remove the peer from the lists so we can start connecting to him
            peer = manager.Peers.AvailablePeers[i];
            manager.Peers.AvailablePeers.RemoveAt(i);

            // Do not try to connect to ourselves
            if (peer.ConnectionUri.Port == manager.Engine.Listener.Endpoint.Port)
            {
                if (manager.Engine.Listener.Endpoint.Address.ToString() == peer.ConnectionUri.Host)
                    return false;

                if (manager.Engine.Listener.Endpoint.Address == IPAddress.Any)
                    foreach (var intf in NetworkInterface.GetAllNetworkInterfaces())
                        if (intf.OperationalStatus == OperationalStatus.Up)
                            foreach (var ip in intf.GetIPProperties().UnicastAddresses)
                                if (ip.Address.ToString() == peer.ConnectionUri.Host)
                                    return false;
            }

            if (ShouldBanPeer(peer))
                return false;
            
            // Connect to the peer
            ConnectToPeer(manager, peer);
            return true;
        }

        #endregion
    }
}
