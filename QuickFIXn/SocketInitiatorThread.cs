﻿using System.Net.Sockets;
using System.Net;
using System.Threading;
using System.IO;
using System;
using System.Diagnostics;
using log4net;

namespace QuickFix
{
    /// <summary>
    /// Handles a connection with an acceptor.
    /// </summary>
    public class SocketInitiatorThread : IResponder
    {
        public Session Session { get { return session_; } }
        public Transport.SocketInitiator Initiator { get { return initiator_; } }

        public const int BUF_SIZE = 512;

        private Thread thread_ = null;
        private byte[] readBuffer_ = new byte[BUF_SIZE];
        private Parser parser_;
        protected Stream stream_;
        private Transport.SocketInitiator initiator_;
        private Session session_;
        private IPEndPoint socketEndPoint_;
        protected SocketSettings socketSettings_;
        private bool isDisconnectRequested_ = false;
        private log4net.ILog _log = LogManager.GetLogger("RollingFileQuickFixAppender");

        public SocketInitiatorThread(Transport.SocketInitiator initiator, Session session, IPEndPoint socketEndPoint, SocketSettings socketSettings)
        {
            _log.Info("initialising SocketInitiatorThread");
            isDisconnectRequested_ = false;
            initiator_ = initiator;
            session_ = session;
            socketEndPoint_ = socketEndPoint;
            parser_ = new Parser();
            session_ = session;
            socketSettings_ = socketSettings;
        }

        public void Start()
        {
            _log.Info("starting SocketInitiatorThread");
            isDisconnectRequested_ = false;
            thread_ = new Thread(new ParameterizedThreadStart(Transport.SocketInitiator.SocketInitiatorThreadStart));                        
            thread_.Start(this);
        }

        public void Join()
        {
            if (null == thread_)
                return;
            Disconnect();
            thread_.Join(5000);
            thread_ = null;
        }

        public void Connect()
        {
            Debug.Assert(stream_ == null);

            stream_ = SetupStream();
            session_.SetResponder(this);
        }

        /// <summary>
        /// Setup/Connect to the other party.
        /// Override this in order to setup other types of streams with other settings
        /// </summary>
        /// <returns>Stream representing the (network)connection to the other party</returns>
        protected virtual Stream SetupStream()
        {
            return QuickFix.Transport.StreamFactory.CreateClientStream(socketEndPoint_, socketSettings_, session_.Log);
        }


        public bool Read()
        {
            try
            {
                _log.Info("Read entered calling readsome");
                int bytesRead = ReadSome(readBuffer_, 1000);
                if (bytesRead > 0)
                    parser_.AddToStream(System.Text.Encoding.UTF8.GetString(readBuffer_, 0, bytesRead));
                else if (null != session_)
                {
                    _log.Info("Read calling sesson Next");
                    session_.Next();
                    _log.Info("Read called sesson Next");
                }
                else
                {
                    throw new QuickFIXException("Initiator timed out while reading socket");
                }
                _log.Info("Read calling sesson ProcessStream");
                ProcessStream();
                _log.Info("Read called sesson ProcessStream");
                return true;
            }
            catch (System.ObjectDisposedException e)
            {
                // this exception means socket_ is already closed when poll() is called
                if (isDisconnectRequested_ == false)
                {
                    // for lack of a better idea, do what the general exception does
                    if (null != session_)
                        session_.Disconnect(e.ToString());
                    else
                        Disconnect();
                }
                return false;                    
            }
            catch (System.Exception e)
            {
                if (null != session_)
                    session_.Disconnect(e.ToString());
                else
                    Disconnect();
            }
            return false;
        }

        /// <summary>
        /// Keep a handle to the current outstanding read request (if any)
        /// </summary>
        private IAsyncResult currentReadRequest_;
        /// <summary>
        /// Reads data from the network into the specified buffer.
        /// It will wait up to the specified number of milliseconds for data to arrive,
        /// if no data has arrived after the specified number of milliseconds then the function returns 0
        /// </summary>
        /// <param name="buffer">The buffer.</param>
        /// <param name="timeoutMilliseconds">The timeout milliseconds.</param>
        /// <returns>The number of bytes read into the buffer</returns>
        /// <exception cref="System.Net.Sockets.SocketException">On connection reset</exception>
        protected int ReadSome(byte[] buffer, int timeoutMilliseconds)
        {
            _log.Info("ReadSome entered");
            // NOTE: THIS FUNCTION IS EXACTLY THE SAME AS THE ONE IN SocketReader any changes here should 
            // also be performed there
            try
            {
                

                // Begin read if it is not already started
                if (currentReadRequest_ == null)
                    currentReadRequest_ = stream_.BeginRead(buffer, 0, buffer.Length, callback: null, state: null);

                // Wait for it to complete (given timeout)
                _log.Info("ReadSome waitone");
                currentReadRequest_.AsyncWaitHandle.WaitOne(timeoutMilliseconds);

                if (currentReadRequest_.IsCompleted)
                {
                    _log.InfoFormat("socketinitiatorthread: receiving, thread id: {0}", Thread.CurrentThread.ManagedThreadId);
                    // Make sure to set currentReadRequest_ to before retreiving result 
                    // so a new read can be started next time even if an exception is thrown
                    var request = currentReadRequest_;
                    currentReadRequest_ = null;

                    int bytesRead = stream_.EndRead(request);
                    if (0 == bytesRead)
                        throw new SocketException(System.Convert.ToInt32(SocketError.ConnectionReset));

                    return bytesRead;
                }
                else
                    return 0;
            }
            catch (System.IO.IOException ex) // Timeout
            {
                var inner = ex.InnerException as SocketException;
                if (inner != null && inner.SocketErrorCode == SocketError.TimedOut)
                {
                    _log.Info("ReadSome timed out.");
                    // Nothing read 
                    return 0;
                }
                else if (inner != null)
                {
                    throw inner; //rethrow SocketException part (which we have exception logic for)
                }
                else
                    throw; //rethrow original exception
            }
        }

        private void ProcessStream()
        {
            string msg;            
            while (parser_.ReadFixMessage(out msg))
            {
                _log.Info("Session next calling");
                session_.Next(msg);
                _log.Info("Session next called");
            }
        }

        #region Responder Members

        public bool Send(string data)
        {
            _log.InfoFormat("socketinitiatorthread: sending, thread id: {0}", Thread.CurrentThread.ManagedThreadId);
            byte[] rawData = System.Text.Encoding.UTF8.GetBytes(data);
            stream_.Write(rawData, 0, rawData.Length);
            return true;
        }

        public void Disconnect()
        {
            isDisconnectRequested_ = true;
            if (stream_ != null)
                stream_.Close();
        }

        #endregion
    }
}
