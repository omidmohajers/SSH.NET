﻿using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using Renci.SshClient.Algorithms;
using Renci.SshClient.Channels;
using Renci.SshClient.Common;
using Renci.SshClient.Messages;
using Renci.SshClient.Messages.Connection;
using Renci.SshClient.Messages.Transport;
using Renci.SshClient.Services;

namespace Renci.SshClient
{
    internal abstract class Session : IDisposable
    {
        protected const int MAXIMUM_PACKET_SIZE = 35000;

        public static Session CreateSession(ConnectionInfo connectionInfo)
        {
            var ep = new IPEndPoint(Dns.GetHostAddresses(connectionInfo.Host)[0], connectionInfo.Port);
            var socket = new Socket(ep.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
            socket.Connect(ep);
            //socket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.KeepAlive, 1000 * 15);
            //socket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReceiveTimeout, 1000 * 5);

            //  Get server version from the server,
            //  ignore text lines which are sent before if any
            var serverVersion = string.Empty;

            using (StreamReader sr = new StreamReader(new NetworkStream(socket)))
            {
                while (true)
                {
                    serverVersion = sr.ReadLine();
                    if (serverVersion.StartsWith("SSH"))
                    {
                        break;
                    }
                }
            }

            //  TODO:   Create session based on server version
            var session = new SessionSSHv2(connectionInfo, socket, serverVersion);

            return session;
        }

        private Socket _socket;

        private int _waitTimeout = 5 * 1000;    //  Set default receive timeout to 5 seconds

        private KeyExchange _keyExhcange;

        private BackgroundWorker _messageListener;

        private EventWaitHandle _keyExhangedFinishedWaitHandle = new AutoResetEvent(false);

        private IDictionary<ServiceNames, Service> _services = new Dictionary<ServiceNames, Service>();

        private IDictionary<uint, uint> _openChannels = new Dictionary<uint, uint>();

        private EventWaitHandle _disconnectWaitHandle = new AutoResetEvent(false);

        private EventWaitHandle _exceptionWaitHandle = new AutoResetEvent(false);

        private Exception _exceptionToThrow;

        public event EventHandler<MessageReceivedEventArgs> MessageReceived;

        protected HMAC ServerMac { get; private set; }

        protected HMAC ClientMac { get; private set; }

        protected ICryptoTransform Encryption { get; private set; }

        protected ICryptoTransform Decryption { get; private set; }

        protected Compression ServerDecompression { get; private set; }

        protected Compression ClientCompression { get; private set; }

        //  TODO:   Consider refactor to make setter private
        public IEnumerable<byte> SessionId { get; set; }

        public string ServerVersion { get; private set; }

        public string ClientVersion { get; private set; }

        public ConnectionInfo ConnectionInfo { get; private set; }

        public bool IsConnected
        {
            get
            {
                //  TODO:   Add condition that user is authenticated
                //  TODO:   Add condition that connection was not terminated byt MSG_DISCONNECT
                return this._socket.Connected;
            }
        }

        protected Session(ConnectionInfo connectionInfo, Socket socket, string serverVersion)
        {
            this.ConnectionInfo = connectionInfo;
            this._socket = socket;
            //this._socket.ReceiveTimeout = this._waitTimeout;

            this.ServerVersion = serverVersion;
            this.ClientVersion = string.Format("SSH-2.0-Renci.SshClient.{0}", this.GetType().Assembly.GetName().Version);
        }

        private static IDictionary<Type, Func<Session, Channel>> _channels = new Dictionary<Type, Func<Session, Channel>>()
            {
                {typeof(ChannelSession), (session) => { return new ChannelSession(session);}},
                {typeof(ChannelSftp), (session) => { return new ChannelSftp(session);}}
            };

        public T CreateChannel<T>() where T : Channel
        {
            if (!this._socket.Connected)
            {
                throw new InvalidOperationException("Not connected");
            }
            return _channels[typeof(T)](this) as T;
        }

        public void Connect()
        {
            this.Write(Encoding.ASCII.GetBytes(string.Format("{0}\n", this.ClientVersion)));

            //  Register Transport response messages
            Message.RegisterMessageType<DisconnectMessage>(MessageTypes.Disconnect);
            Message.RegisterMessageType<IgnoreMessage>(MessageTypes.Ignore);
            Message.RegisterMessageType<UnimplementedMessage>(MessageTypes.Unimplemented);
            Message.RegisterMessageType<DebugMessage>(MessageTypes.Debug);
            Message.RegisterMessageType<ServiceAcceptMessage>(MessageTypes.ServiceAcceptRequest);
            Message.RegisterMessageType<KeyExchangeInitMessage>(MessageTypes.KeyExchangeInit);
            Message.RegisterMessageType<NewKeysMessage>(MessageTypes.NewKeys);

            //  Start incoming request listener
            this._messageListener = new BackgroundWorker();
            this._messageListener.DoWork += MessageListener_DoWork;
            this._messageListener.RunWorkerAsync();

            //  Wait for key exchange to be completed
            this.WaitHandle(this._keyExhangedFinishedWaitHandle);

            //  If sessionId is not set then its not connected
            if (this.SessionId == null)
            {
                this.Disconnect();
                return;
            }

            var authenticationService = new UserAuthenticationService(this);

            authenticationService.AuthenticateUser();

            if (!authenticationService.IsAuthenticated)
            {
                throw new InvalidOperationException(string.Format("User cannot be authenticated. Reason: {0}.", authenticationService.ErrorMessage));
            }
        }

        public void Disconnect()
        {
            //  TODO:   Change message to something more appropriate
            this.Disconnect(DisconnectReasonCodes.ByApplication, "Connection terminated by the client.");

            this.DisconnectCleanup();
        }

        internal abstract void SendMessage(Message message);

        internal void WaitHandle(EventWaitHandle waitHandle)
        {
            var waitHandles = new EventWaitHandle[]
                {
                    this._disconnectWaitHandle,
                    this._exceptionWaitHandle,
                    waitHandle,
                };

            //EventWaitHandle.WaitAny(waitHandles);

            var index = EventWaitHandle.WaitAny(waitHandles, this._waitTimeout);

            if (this._exceptionToThrow != null)
            {
                var exception = this._exceptionToThrow;
                this._exceptionToThrow = null;
                throw exception;
            }
            else if (index > waitHandles.Length)
            {
                //  TODO:   Issue timeout disconnect message if approapriate
                throw new TimeoutException();
            }
        }

        protected abstract Message ReceiveMessage();

        protected virtual void HandleMessage<T>(T message) where T : Message
        {
            //  Do nothing as message could be proccessed by other module
        }

        #region Handle transport messages

        protected virtual void HandleMessage(DisconnectMessage message)
        {
            this.DisconnectCleanup();
        }

        protected virtual void HandleMessage(IgnoreMessage message)
        {
            throw new NotImplementedException();
        }

        protected virtual void HandleMessage(UnimplementedMessage message)
        {
            throw new NotImplementedException();
        }

        protected virtual void HandleMessage(DebugMessage message)
        {
            throw new NotImplementedException();
        }

        protected virtual void HandleMessage(ServiceAcceptMessage message)
        {
        }

        protected virtual void HandleMessage(ServiceRequestMessage message)
        {
            throw new NotImplementedException();
        }

        protected virtual void HandleMessage(KeyExchangeInitMessage message)
        {
            this._keyExhangedFinishedWaitHandle.Reset();

            if (message.FirstKexPacketFollows)
            {
                //  TODO:   Expect guess packet
                throw new NotImplementedException("Guess packets are not supported.");
            }

            //  Create key exchange algorithm
            this._keyExhcange = KeyExchange.Create(message, this);

            this._keyExhcange.Failed += delegate(object sender, KeyExchangeFailedEventArgs e)
            {
                this.Disconnect(DisconnectReasonCodes.KeyExchangeFailed, e.Message);
                throw new InvalidOperationException(e.Message);
            };

            this._keyExhcange.Start(message);
        }

        protected virtual void HandleMessage(NewKeysMessage message)
        {
            this._keyExhcange.Finish();

            //  Update encryption and decryption algorithm
            this.ServerMac = this._keyExhcange.ServerMac;
            this.ClientMac = this._keyExhcange.ClientMac;
            this.Encryption = this._keyExhcange.Encryption;
            this.Decryption = this._keyExhcange.Decryption;
            this.ServerDecompression = this._keyExhcange.ServerDecompression;
            this.ClientCompression = this._keyExhcange.ClientCompression;

            this._keyExhangedFinishedWaitHandle.Set();
        }

        #endregion

        #region Handle connection messages

        protected virtual void HandleMessage(GlobalRequestMessage message)
        {
            //  TODO:   Add implemention for this message
        }

        protected virtual void HandleMessage(RequestSuccessMessage message)
        {
            //  TODO:   Add implemention for this message
        }

        protected virtual void HandleMessage(RequestFailureMessage message)
        {
            //  TODO:   Add implemention for this message
        }

        #endregion

        protected void Disconnect(DisconnectReasonCodes reasonCode, string message)
        {
            if (this._socket != null && this._socket.Connected)
            {
                this.SendMessage(new DisconnectMessage
                {
                    ReasonCode = reasonCode,
                    Description = message,
                });

                this.DisconnectCleanup();
            }
        }

        protected byte[] Read(int length)
        {
            var buffer = new byte[length];

            SocketError socketErrorCode = SocketError.Success;

            int bytesRead = 0;

            while (bytesRead == 0 && this._socket.Connected && socketErrorCode == SocketError.Success)
            {
                var asynchResult = this._socket.BeginReceive(buffer, 0, length, SocketFlags.None, null, null);

                while (!asynchResult.IsCompleted && this._socket.Connected)
                {
                    asynchResult.AsyncWaitHandle.WaitOne(this._waitTimeout);
                }
                bytesRead = this._socket.EndReceive(asynchResult, out socketErrorCode);
            }

            //  Check for errors
            if (!this._socket.Connected)
            {
                //  If socket was closed throw an exception
                throw new SocketException(995); //  WSA_OPERATION_ABORTED
            }
            if (bytesRead != length && socketErrorCode == SocketError.Success)
            {
                throw new InvalidDataException(string.Format("Data read {0}, expected {1}", bytesRead, length));
            }
            else if (socketErrorCode != SocketError.Success)
            {
                throw new SocketException((int)socketErrorCode);
            }
            else
                return buffer;
        }

        protected void Write(byte[] data)
        {
            //  TODO:   Make sure socket is connected
            SocketError socketErrorCode;

            this._socket.Send(data, 0, data.Length, SocketFlags.None, out socketErrorCode);

            //  Check for socket errors
            if (socketErrorCode != SocketError.Success)
            {
                throw new SocketException((int)socketErrorCode);
            }
            else if (!this._socket.Connected)
            {
                //  If socket was closed throw an exception
                throw new SocketException(995); //  WSA_OPERATION_ABORTED
            }
        }

        protected void RequestNewKeys()
        {
            //  TODO:   Create method to issue new keys when required
            //this._keyExhcange.Start();
        }

        private void DisconnectCleanup()
        {
            //  Stop running listener thread

            //  Close all open channels if any
            foreach (var channelId in this._openChannels.Values)
            {
                this.SendMessage(new ChannelCloseMessage
                {
                    ChannelNumber = channelId,
                });
            }

            //  Close socket connection if still open
            if (this._socket.Connected)
            {
                this._socket.Disconnect(false);
            }
        }

        private void MessageListener_DoWork(object sender, DoWorkEventArgs e)
        {
            while (this._socket.Connected)
            {
                try
                {
                    dynamic message = this.ReceiveMessage();

                    if (message == null)
                    {
                        throw new NullReferenceException("The 'message' variable cannot be null");
                    }

                    //  Handle session messages first
                    this.HandleMessage(message);

                    //  Raise an event that message received
                    this.RaiseMessageReceived(this, new MessageReceivedEventArgs(message));
                }
                catch (Exception exp)
                {
                    //  TODO:   This exception can be swolloed if it occures while running in the background, look for possible solutions

                    //  In case of error issue disconntect command
                    this.Disconnect(DisconnectReasonCodes.ByApplication, exp.ToString());

                    //  Set exception that need to be thrown by the main thread
                    this._exceptionToThrow = exp;

                    this._exceptionWaitHandle.Set();
                    //  TODO:   Set exp to some excpetion that can be thrown later by the main thread.

                    break;
                }
            }
        }

        private void RaiseMessageReceived(object sender, MessageReceivedEventArgs args)
        {
            if (this.MessageReceived != null)
            {
                this.MessageReceived(sender, args);
            }
        }

        private void HandleMessage(ChannelOpenConfirmationMessage message)
        {
            //  Keep track of open channels
            this._openChannels.Add(message.ChannelNumber, message.ServerChannelNumber);
            Debug.WriteLine("Open channel " + message.ServerChannelNumber);
        }

        private void HandleMessage(ChannelCloseMessage message)
        {
            //  Keep track of open channels
            Debug.WriteLine("Close channel " + this._openChannels[message.ChannelNumber]);
            this._openChannels.Remove(message.ChannelNumber);
        }

        #region IDisposable Members

        private bool disposed = false;

        public void Dispose()
        {
            Dispose(true);

            GC.SuppressFinalize(this);
        }

        private void Dispose(bool disposing)
        {
            // Check to see if Dispose has already been called.
            if (!this.disposed)
            {
                // If disposing equals true, dispose all managed
                // and unmanaged resources.
                if (disposing)
                {
                    // Dispose managed resources.
                    if (this._socket != null)
                    {
                        this.Disconnect();
                        this._socket.Dispose();
                    }
                }

                // Note disposing has been done.
                disposed = true;
            }
        }

        ~Session()
        {
            // Do not re-create Dispose clean-up code here.
            // Calling Dispose(false) is optimal in terms of
            // readability and maintainability.
            Dispose(false);
        }

        #endregion
    }
}