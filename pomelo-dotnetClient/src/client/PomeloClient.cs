﻿using SimpleJson;
using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Threading;

namespace Pomelo.DotNetClient
{
    /// <summary>
    /// network state enum
    /// </summary>
    public enum NetWorkState
    {
        [Description("initial state")]
        CLOSED,

        [Description("connecting server")]
        CONNECTING,

        [Description("server connected")]
        CONNECTED,

        [Description("disconnected with server")]
        DISCONNECTED,

        [Description("connect timeout")]
        TIMEOUT,

        [Description("netwrok error")]
        ERROR,

        [Description("netwrok kick")]
        KICK,
    }

    public class PomeloClient : IDisposable
    {
        /// <summary>
        /// netwrok changed event
        /// </summary>
        public event Action<NetWorkState> NetWorkStateChangedEvent;


        private NetWorkState netWorkState = NetWorkState.CLOSED;   //current network state

        private EventManager eventManager;
        private Socket socket;
        private Protocol protocol;
        private bool disposed = false;
        private uint reqId = 1;

        //private ManualResetEvent timeoutEvent = new ManualResetEvent(false);
        private int timeoutMSec = 8000;    //connect timeout count in millisecond

        public PomeloClient(int timeout_millisecond = 8000)
        {
            timeoutMSec = timeout_millisecond;
        }

        /// <summary>
        /// initialize pomelo client
        /// </summary>
        /// <param name="host">server name or server ip (www.xxx.com/127.0.0.1/::1/localhost etc.)</param>
        /// <param name="port">server port</param>
        /// <param name="callback">socket successfully connected callback(in network thread)</param>
        public void initClient(string host, int port, Action callback = null)
        {
            ManualResetEvent timeoutEvent = new ManualResetEvent(false);
            eventManager = new EventManager();
            NetWorkChanged(NetWorkState.CONNECTING);

            IPAddress ipAddress = null;

            try
            {
                // 解析服务器地址
                if (!IPAddress.TryParse(host, out ipAddress))
                {
                    IPAddress[] addresses = Dns.GetHostEntry(host).AddressList;
                    foreach (var item in addresses)
                    {
                        if (item.AddressFamily == AddressFamily.InterNetwork)
                        {
                            ipAddress = item;
                            break;
                        }
                    }
                }
            }
            catch (Exception e)
            {
                error();
                return;
            }

            if (ipAddress == null)
            {
                error();
                throw new Exception("can not parse host : " + host);
            }

            this.socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            this.socket.SetSocketOption(SocketOptionLevel.Tcp, SocketOptionName.NoDelay, true);
            Trace.TraceInformation("new Socket");
            IPEndPoint ie = new IPEndPoint(ipAddress, port);
            socket.BeginConnect(ie, new AsyncCallback((result) =>
            {
                try
                {
                    this.socket.EndConnect(result);
                    this.protocol = new Protocol(this, this.socket);
                    NetWorkChanged(NetWorkState.CONNECTED);
                }
                catch (SocketException e)
                {
                    if (netWorkState != NetWorkState.DISCONNECTED
                        && netWorkState != NetWorkState.TIMEOUT
                        && netWorkState != NetWorkState.ERROR
                        && netWorkState != NetWorkState.KICK)
                    {
                        error();
                    }
                }
                finally
                {
                    if (timeoutEvent != null)
                    {
                        timeoutEvent.Set();
                    }
                }

                if (callback != null)
                {
                    callback();
                }
            }), this.socket);

            timeoutEvent.WaitOne(timeoutMSec, false);
            if (netWorkState == NetWorkState.CONNECTING)
            {
                timeout();
            }
            timeoutEvent.Close();
            timeoutEvent = null;
        }

        /// <summary>
        /// 网络状态变化
        /// </summary>
        /// <param name="state"></param>
        private void NetWorkChanged(NetWorkState state)
        {
            netWorkState = state;

            if (NetWorkStateChangedEvent != null)
            {
                NetWorkStateChangedEvent(state);
            }
        }

        public bool connect()
        {
            return connect(null, null);
        }

        public bool connect(JsonObject user)
        {
            return connect(user, null);
        }

        public bool connect(Action<JsonObject> handshakeCallback)
        {
            return connect(null, handshakeCallback);
        }

        public bool connect(JsonObject user, Action<JsonObject> handshakeCallback)
        {
            ManualResetEvent timeoutEvent = new ManualResetEvent(false);
            try
            {
                protocol.start(user, (JsonObject json) =>
                {
                    timeoutEvent.Set();
                    try
                    {
                        if (handshakeCallback != null) { handshakeCallback(json); }
                    }
                    catch (SocketException e)
                    {
                        if (netWorkState != NetWorkState.DISCONNECTED
                            && netWorkState != NetWorkState.TIMEOUT
                            && netWorkState != NetWorkState.ERROR
                            && netWorkState != NetWorkState.KICK)
                        {
                            error();
                        }
                    }
                });

                timeoutEvent.WaitOne(timeoutMSec, false);
                if (!protocol.isWaking())
                {
                    timeout();
                }
                return true;
            }
            catch (Exception e)
            {
                Trace.TraceInformation(e.ToString());
            }
            timeoutEvent.Close();
            timeoutEvent = null;
            error();
            return false;
        }

        private JsonObject emptyMsg = new JsonObject();
        public void request(string route, Action<JsonObject> action)
        {
            this.request(route, emptyMsg, action);
        }

        public void request(string route, JsonObject msg, Action<JsonObject> action)
        {
            this.eventManager.AddCallBack(reqId, action);
            protocol.send(route, reqId, msg);

            if (reqId == int.MaxValue) { reqId = 1; } else { reqId++; }
        }

        public void notify(string route, JsonObject msg)
        {
            protocol.send(route, msg);
        }

        public void addOnEvent(string eventName, Action<JsonObject> action)
        {
            eventManager.AddOnEvent(eventName, action);
        }

        public void removeOnEvent(string eventName, Action<JsonObject> action)
        {
            eventManager.RemoveOnEvent(eventName, action);
        }

        internal void processMessage(Message msg)
        {
            if (msg.type == MessageType.MSG_RESPONSE)
            {
                //msg.data["__route"] = msg.route;
                //msg.data["__type"] = "resp";
                eventManager.InvokeCallBack(msg.id, msg.data);
            }
            else if (msg.type == MessageType.MSG_PUSH)
            {
                //msg.data["__route"] = msg.route;
                //msg.data["__type"] = "push";
                eventManager.InvokeOnEvent(msg.route, msg.data);
            }
        }

        public void disconnect()
        {
            Dispose();
            NetWorkChanged(NetWorkState.DISCONNECTED);
        }

        public void timeout()
        {
            Dispose();
            NetWorkChanged(NetWorkState.TIMEOUT);
        }

        public void Kick()
        {
            Dispose();
            NetWorkChanged(NetWorkState.KICK);
        }

        public void error()
        {
            Dispose();
            NetWorkChanged(NetWorkState.ERROR);
        }

        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        // The bulk of the clean-up code
        protected virtual void Dispose(bool disposing)
        {
            if (this.disposed)
                return;

            if (disposing)
            {
                // free managed resources
                if (this.protocol != null)
                {
                    this.protocol.close();
                }

                if (this.eventManager != null)
                {
                    this.eventManager.Dispose();
                }

                try
                {
                    this.socket.Shutdown(SocketShutdown.Both);
                    this.socket.Close();
                    this.socket = null;
                }
                catch (Exception)
                {
                    //todo : 有待确定这里是否会出现异常，这里是参考之前官方github上pull request。emptyMsg
                }

                this.disposed = true;
            }
        }
    }
}