using System;
using System.Net;
using System.Net.Sockets;
using System.Threading;

namespace Communication
{
    public struct AsyncUserToken
    {
        public bool IsHead;
        public int BytesExpected;
        public int BytesReceivedDynamic;
        public int BytesReceivedStatic;

        public AsyncUserToken(bool isHead, int bytesExpected, int bytesReceivedDynamic, int bytesReceivedStatic)
        {
            this.IsHead = isHead;
            this.BytesExpected = bytesExpected;
            this.BytesReceivedDynamic = bytesReceivedDynamic;
            this.BytesReceivedStatic = bytesReceivedStatic;
        }
    }

    public class TcpConnection
    {
        /// <summary>
        /// default receive buffer size, 4MB 
        /// </summary>
        const int ReceiveBufferSize = 4;

        //TcpClient client_;
        Socket clientSocket;
        byte[] receiveBuffer;
        bool closeGraceful;
        Thread heartbeater;

        int onDisconnectedRaised;

        private EndPoint remoteEndPoint;
        private EndPoint localEndPoint;

        public bool Connected
        {
            get { return this.clientSocket != null && this.clientSocket.Connected; }
        }

        public EndPoint RemoteEndPoint
        {
            get
            {
                if (remoteEndPoint == null)
                {
                    try { remoteEndPoint = clientSocket.RemoteEndPoint; }
                    catch { }
                }
                return remoteEndPoint;

                //try { return clientSocket.RemoteEndPoint; }
                //catch { }
                //return null;
            }
        }

        public EndPoint LocalEndPoint
        {
            get
            {
                if (localEndPoint == null)
                {
                    try { localEndPoint = clientSocket.LocalEndPoint; }
                    catch { }
                }
                return localEndPoint;

                //try { return clientSocket.LocalEndPoint; }
                //catch { }
                //return null;
            }
        }

        public event Action<TcpConnection, byte[]> OnDataReceivedCompleted, OnDataSendCompleted;
        public event Action<TcpConnection> OnConnected, OnDisconnected;
        public event Action<SocketAsyncEventArgs, AsyncUserToken> ReportReceivedProgress, ReportSendProgress;
         
        TcpConnection() { }

        internal TcpConnection(Socket clientSocket)
        {
            this.clientSocket = clientSocket;
            this.closeGraceful = false;
            this.onDisconnectedRaised = 0;
        }

        public TcpConnection(string serverIPAddress, int port)
        {
            try
            {
                this.clientSocket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
                this.remoteEndPoint = new IPEndPoint(IPAddress.Parse(serverIPAddress), port);
                this.closeGraceful = false;
                this.onDisconnectedRaised = 0;
            }
            catch (SocketException ex) { throw ex; }
            catch (ArgumentOutOfRangeException ex) { throw ex; }
        }

        ~TcpConnection()
        {
            if (this.clientSocket !=null && this.clientSocket.Connected)
            {
                this.CloseConnection();
            }

            this.receiveBuffer = null;

            if (this.heartbeater != null && this.heartbeater.IsAlive)
            {
                try
                {
                    this.heartbeater.Abort();
                    this.heartbeater.Join(1000);
                }
                catch { }
            }
        }

        public void Start()
        {
            try
            {
                this.clientSocket.Connect(remoteEndPoint);
            }
            catch (SocketException)
            {
                int tries = 3;
                do
                {
                    try { this.clientSocket.Connect(remoteEndPoint); }
                    catch { }
                } while (--tries > 0);
            }
            catch (Exception ex) { throw ex; }

            try
            {
                this.StartReceive();
            }
            catch (Exception ex) { throw ex; }
        }

        internal void StartReceive()
        {
            ////
            this.heartbeater = new Thread(new ThreadStart(CheckConnection));
            this.heartbeater.Priority = ThreadPriority.Lowest;
            this.heartbeater.Start();

            this.clientSocket.Blocking = false;
            // set send buffer
            this.clientSocket.SendBufferSize = 0;
            this.clientSocket.NoDelay = true;

            // set receive buffer
            this.receiveBuffer = new byte[ReceiveBufferSize];

            SocketAsyncEventArgs e = new SocketAsyncEventArgs();
            e.Completed += new EventHandler<SocketAsyncEventArgs>(e_Completed);
            e.SetBuffer(receiveBuffer, 0, receiveBuffer.Length);

            if (this.clientSocket.Connected)
                this.OnConnected(this);

            e.UserToken = new AsyncUserToken(true, 0, 0, 0);

            if (!this.clientSocket.ReceiveAsync(e))
                this.ProcessReceived(e);
        }

        void CheckConnection()
        {
            byte[] emptyMessage = new byte[0];
            int sleepMilliseconds = 1000;
            while (true)
            {
                try
                {
                    this.clientSocket.Send(emptyMessage);

                    Thread.Sleep(sleepMilliseconds);
                }
                catch (ArgumentNullException) { emptyMessage = new byte[0]; }
                catch (ArgumentOutOfRangeException) { sleepMilliseconds = 1000; }
                catch (SocketException ex)
                {
                    if (ex.SocketErrorCode != SocketError.WouldBlock &&
                        ex.SocketErrorCode != SocketError.IOPending)
                    {
                        if (ex.SocketErrorCode != SocketError.ConnectionReset &&
                           ex.SocketErrorCode != SocketError.NotConnected)
                        {
                            Console.WriteLine(ex.SocketErrorCode);
                            Console.ReadLine();
                        }
                        break;
                    }
                }
                catch (ObjectDisposedException) { break; }
                catch (ThreadInterruptedException) { break; }
                catch (ThreadAbortException) { break; }
            }

            CloseConnection();
        }

        void e_Completed(object sender, SocketAsyncEventArgs e)
        {
            try
            {
                switch (e.LastOperation)
                {
                    case  SocketAsyncOperation.Connect:
                        ProcessConnected(e);
                        break;
                    case SocketAsyncOperation.Disconnect:
                        ProcessDisconnected(e);
                        break;
                    case SocketAsyncOperation.Receive:
                        ProcessReceived(e);
                        break;
                    case SocketAsyncOperation.Send:
                        ProcessSend(e);
                        break;
                    default:
                        break;
                }
            }
            catch { }
        }

        void ProcessSend(SocketAsyncEventArgs e)
        {
            if (e.BytesTransferred > 0 && e.SocketError == SocketError.Success)
            {
                AsyncUserToken token = (AsyncUserToken)e.UserToken;
                token.BytesReceivedDynamic = e.BytesTransferred;
                token.BytesReceivedStatic += e.BytesTransferred;
                // send not completed
                if (e.BytesTransferred < token.BytesExpected)
                {
                    if (ReportSendProgress != null)
                        ReportSendProgress(e, token);

                    e.SetBuffer(e.BytesTransferred, token.BytesExpected - e.BytesTransferred);
                    e.UserToken = token;
                    clientSocket.SendAsync(e);
                }
                else // send has completed
                {
                    if (OnDataSendCompleted == null)
                        return;
                    OnDataSendCompleted(this, e.Buffer);
                }
            }
        }

        void ProcessReceived(SocketAsyncEventArgs e)
        {
            //..
            if (e.BytesTransferred > 0 && e.SocketError == SocketError.Success)
            {
                AsyncUserToken token = (AsyncUserToken)e.UserToken;

                int offset = 0; // token.BytesReceivedStatic;
                int bytesExpected = 0; // token.BytesExpected;

                token.BytesReceivedDynamic = e.BytesTransferred; // bytes received this time
                token.BytesReceivedStatic += e.BytesTransferred; // bytes accumulated in the buffer

                if (ReportReceivedProgress != null)
                    ReportReceivedProgress(e, token);

                bool hasCompletedMessage = true;
                while (hasCompletedMessage) 
                {
                    if (token.IsHead) // is head, read the prefix length
                    {
                        // incomplete prefix bytes
                        if (token.BytesReceivedStatic < 4)
                            break;

                        token.BytesExpected = BitConverter.ToInt32(e.Buffer, 0);

                        if (token.BytesExpected == 0)
                        {
                            // heartbeat message
                            // remove 4 bytes at head
                            Buffer.BlockCopy(e.Buffer, 4, e.Buffer, 0, token.BytesReceivedStatic - 4);

                            // continue to receive
                            break;
                        }

                        // expand the buffer size when the buffer is not large enough
                        if (token.BytesExpected > receiveBuffer.Length)
                        {
                            byte[] temp = new byte[token.BytesExpected * 2];
                            Buffer.BlockCopy(e.Buffer, 0, temp, 0, e.Buffer.Length);
                            receiveBuffer = temp;
                            e.SetBuffer(temp, 0, temp.Length);
                        }

                        token.IsHead = false;
                    }

                    // if bytes accumulated in the buffer is greater than or equals to bytes expected
                    hasCompletedMessage = token.BytesReceivedStatic >= token.BytesExpected;
                    //
                    if (hasCompletedMessage) // at least one complete message
                    {
                        // deliver the first message in the buffer
                        byte[] receivedData = new byte[token.BytesExpected - 4];
                        Buffer.BlockCopy(receiveBuffer, 4, receivedData, 0, receivedData.Length);
                        OnDataReceivedCompleted(this, receivedData);

                        token.IsHead = true; // reset the head flag

                        // valid bytes left in the buffer
                        int bytesLeftInBuffer = token.BytesReceivedStatic - token.BytesExpected;

                        // shift the incomplete data to the head of the buffer
                        // TODO: 
                        // FIXME: only do shift the last incomplete data
                        Buffer.BlockCopy(e.Buffer, token.BytesExpected, e.Buffer, 0, bytesLeftInBuffer);

                        // reset the bytes of valid data in the buffer
                        token.BytesReceivedStatic = bytesLeftInBuffer;
                    }
                } 

                // append the following data to the buffer from offset position
                offset = token.BytesReceivedStatic; // start position of the buffer

                bytesExpected =
                    //// only expected message length bytes
                    //token.BytesExpected - token.BytesReceivedStatic;
                    //// expected bytes as much as the buffer can hold
                    e.Buffer.Length - token.BytesReceivedStatic;

                e.SetBuffer(offset, bytesExpected);
                e.UserToken = token;

                if (!clientSocket.ReceiveAsync(e))
                    ProcessReceived(e);
            }
            else
            {
                CloseConnection();
            }
        }

        void ProcessConnected(SocketAsyncEventArgs e)
        {
            if (this.OnConnected == null)
                return;

            this.OnConnected(this);
        }

        void ProcessDisconnected(SocketAsyncEventArgs e)
        {
            if (this.OnDisconnected == null)
                return;

            this.OnDisconnected(this);
        }

        public bool Shutdown()
        {
            try
            {
                CloseConnection();
                return true;
            }
            catch { }

            return false;
        }

        internal void CloseConnection()
        {
            if (Interlocked.CompareExchange(ref onDisconnectedRaised, 1, 0) > 0)
                return;

            // close the socket associated with the client
            try
            {
                if (!closeGraceful)
                {
                    clientSocket.LingerState = new LingerOption(true, 0);
                }

                //clientSocket.Shutdown(SocketShutdown.Both);
            }
            catch { }

            try
            {
                this.OnDisconnected(this);
                clientSocket.Close(0);
            }
            catch { }

            
        }

        public void SendMessage(byte[] data)
        {
            SendMessage(data, 1);
        }

        public int SendMessage(byte[] data, int tries)
        {
            int count = 0;
            try
            {
                count = clientSocket.Send(data, 0, data.Length, SocketFlags.None);
            }
            catch (Exception)
            {
                if (tries-- > 0)
                    count = SendMessage(data, tries);
            }
            return count;
        }

        public int SendMessageWithPrefixLength(byte[] data)
        {
            int count = 0;
            try
            {
                byte[] message = new byte[data.Length + 4];
                byte[] prefixBytes = BitConverter.GetBytes(data.Length + 4);

                Buffer.BlockCopy(prefixBytes, 0, message, 0, prefixBytes.Length);
                Buffer.BlockCopy(data, 0, message, prefixBytes.Length, data.Length);

                count = SendMessage(message, 1);
            }
            catch (Exception ex)
            {
                ex.ToString();
                count = 0;
            }

            return count;
        }

        public bool SendMessageWithPrefixLengthAsync(byte[] data)
        {
            bool succeed = false;
            try
            {
                byte[] message = new byte[data.Length + 4];
                byte[] prefixBytes = BitConverter.GetBytes(data.Length + 4);

                Buffer.BlockCopy(prefixBytes, 0, message, 0, prefixBytes.Length);
                Buffer.BlockCopy(data, 0, message, prefixBytes.Length, data.Length);

                succeed = SendMessageAsync(message, false);
            }
            catch (Exception ex)
            {
                ex.ToString();
                succeed = false;
            }

            return succeed;
        }

        public bool SendMessageAsync(byte[] data, bool safe)
        {
            bool succeed = false;

            try
            {
                SocketAsyncEventArgs e = new SocketAsyncEventArgs();
                e.Completed += new EventHandler<SocketAsyncEventArgs>(e_Completed);
                if (safe)
                {
                    byte[] safeData = new byte[data.Length];
                    Buffer.BlockCopy(data, 0, safeData, 0, data.Length);
                    e.SetBuffer(safeData, 0, safeData.Length);
                }
                else
                {
                    e.SetBuffer(data, 0, data.Length);
                }

                e.UserToken = new AsyncUserToken(true, data.Length, 0, 0);

                clientSocket.SendAsync(e);
                succeed = true;
            }
            catch (Exception)
            {
                succeed = false;
            }

            return succeed;
        }
    }
}
