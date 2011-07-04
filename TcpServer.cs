using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;

namespace Communication
{
    public class TcpServer
    {
        private Socket listenSocket;
        private int port;
        private object SyncRoot;
        private EndPoint localEndPoint;

        public event Action<TcpConnection, byte[]> OnDataReceivedCompleted, OnDataSendCompleted;
        public event Action<TcpConnection> OnConnected, OnDisconnected;
        public event Action<SocketAsyncEventArgs, AsyncUserToken> ReportReceivedProgress, ReportSendProgress;

        public EndPoint LocalEndPoint
        {
            get
            {
                if (this.localEndPoint == null)
                {
                    try { this.localEndPoint = this.listenSocket.LocalEndPoint; }
                    catch { }
                }
                return this.localEndPoint;
            }
        }

        public Dictionary<EndPoint, TcpConnection> Connections
        {
            get;
            private set;
        }

        private TcpServer()
        {
            this.port = 0;
            this.Connections = null;
            this.SyncRoot = null;
            this.OnDataReceivedCompleted = null;
            this.OnConnected = null;
            this.OnDisconnected = null;
            this.OnDataSendCompleted = null;
        }

        public TcpServer(int port)
        {
            this.port = port;
            this.Connections = null;
            this.SyncRoot = null;
            this.OnDataReceivedCompleted = null;
            this.OnConnected = null;
            this.OnDisconnected = null;
            this.OnDataSendCompleted = null;
        }

        public void Start()
        {
            this.Start(300);
        }

        public void Start(int backlog)
        {
            this.listenSocket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            this.listenSocket.Bind(new IPEndPoint(IPAddress.Any, port));
            this.listenSocket.NoDelay = true;
            this.listenSocket.SendBufferSize = 0;

            this.listenSocket.Listen(backlog);

            this.Connections = new Dictionary<EndPoint, TcpConnection>();
            this.SyncRoot = ((System.Collections.ICollection)Connections).SyncRoot;

            SocketAsyncEventArgs e = new SocketAsyncEventArgs();
            e.Completed += new EventHandler<SocketAsyncEventArgs>(this.e_Completed);
            e.AcceptSocket = null;

            if (!this.listenSocket.AcceptAsync(e))
                this.e_Completed(null, e);
        }

        public void ShutdownAll()
        {
            if (this.listenSocket.Connected)
                this.listenSocket.Shutdown(SocketShutdown.Both);

            if (this.Connections != null)
            {
                lock (this.SyncRoot)
                {
                    while (this.Connections.Count > 0)
                    {
                        try
                        {
                            foreach (var conn in this.Connections)
                            {
                                conn.Value.Shutdown();
                            }
                            this.Connections.Clear();
                        }
                        catch { }
                    }
                }
            }

            if (this.listenSocket.Connected)
            {
                this.listenSocket.Close();
            }
        }

        public bool ShutdownOne(EndPoint endPoint)
        {
            if (endPoint == null)
                throw new ArgumentNullException();
            if (this.Connections == null || this.Connections.Count == 0)
                return false;

            lock (this.SyncRoot)
            {
                TcpConnection conn = null;
                if (this.Connections.TryGetValue(endPoint, out conn))
                {
                    a_conn_OnDisconnected(conn);

                    return true;
                }
                return false;
            }
        }

        void e_Completed(object sender, SocketAsyncEventArgs e)
        {
            try
            {
                if (e.AcceptSocket.Connected)
                {
                    var a_conn = new TcpConnection(e.AcceptSocket);

                    if (this.OnConnected != null)
                        a_conn.OnConnected += new Action<TcpConnection>(this.a_conn_OnConnected);

                    if (this.OnDisconnected != null)
                        a_conn.OnDisconnected += new Action<TcpConnection>(this.a_conn_OnDisconnected);

                    if (this.OnDataReceivedCompleted != null)
                        a_conn.OnDataReceivedCompleted += new Action<TcpConnection, byte[]>(this.a_conn_OnDataReceivedCompleted);

                    if (this.OnDataSendCompleted != null)
                        a_conn.OnDataSendCompleted += new Action<TcpConnection, byte[]>(this.a_conn_OnDataSendCompleted);

                    if (this.ReportReceivedProgress != null)
                        a_conn.ReportReceivedProgress += new Action<SocketAsyncEventArgs, AsyncUserToken>(this.a_conn_ReportReceivedProgress);

                    if (this.ReportSendProgress != null)
                        a_conn.ReportSendProgress += new Action<SocketAsyncEventArgs, AsyncUserToken>(this.a_conn_ReportSendProgress);

                    lock (this.SyncRoot)
                    {
                        this.Connections.Add(a_conn.RemoteEndPoint, a_conn);
                        a_conn.StartReceive();
                    }
                }
            }
            catch { }

            e.AcceptSocket = null;
            if (!this.listenSocket.AcceptAsync(e))
                this.e_Completed(null, e);
        }

        void a_conn_OnConnected(TcpConnection a_conn)
        {
            if (this.OnConnected != null)
                this.OnConnected(a_conn);
        }

        void a_conn_OnDisconnected(TcpConnection a_conn)
        {
            Connections.Remove(a_conn.RemoteEndPoint);
            if (this.OnDisconnected != null)
                this.OnDisconnected(a_conn);
            a_conn.CloseConnection();
        }

        void a_conn_OnDataReceivedCompleted(TcpConnection a_conn, byte[] data)
        {
            if (this.OnDataReceivedCompleted != null)
                this.OnDataReceivedCompleted(a_conn, data);
        }

        void a_conn_OnDataSendCompleted(TcpConnection a_conn, byte[] data)
        {
            if (this.OnDataSendCompleted != null)
                this.OnDataSendCompleted(a_conn, data);
        }

        void a_conn_ReportReceivedProgress(SocketAsyncEventArgs e, AsyncUserToken token)
        {
            if (this.ReportReceivedProgress != null)
                this.ReportReceivedProgress(e, token);
        }

        void a_conn_ReportSendProgress(SocketAsyncEventArgs e, AsyncUserToken token)
        {
            if (this.ReportSendProgress != null)
                this.ReportSendProgress(e, token);
        }
    }
}
