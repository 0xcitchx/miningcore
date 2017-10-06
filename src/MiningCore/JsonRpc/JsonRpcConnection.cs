﻿/* 
Copyright 2017 Coin Foundry (coinfoundry.org)
Authors: Oliver Weichhold (oliver@weichhold.com)

Permission is hereby granted, free of charge, to any person obtaining a copy of this software and 
associated documentation files (the "Software"), to deal in the Software without restriction, 
including without limitation the rights to use, copy, modify, merge, publish, distribute, sublicense, 
and/or sell copies of the Software, and to permit persons to whom the Software is furnished to do so, 
subject to the following conditions:

The above copyright notice and this permission notice shall be included in all copies or substantial 
portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR IMPLIED, INCLUDING BUT NOT 
LIMITED TO THE WARRANTIES OF MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. 
IN NO EVENT SHALL THE AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY, 
WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM, OUT OF OR IN CONNECTION WITH THE 
SOFTWARE OR THE USE OR OTHER DEALINGS IN THE SOFTWARE.
*/

using System;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Reactive;
using System.Reactive.Disposables;
using System.Reactive.Linq;
using System.Text;
using System.Threading.Tasks;
using NetUV.Core.Buffers;
using NetUV.Core.Handles;
using Newtonsoft.Json;
using NLog;
using Contract = MiningCore.Contracts.Contract;

// http://www.jsonrpc.org/specification
// https://github.com/Astn/JSON-RPC.NET

namespace MiningCore.JsonRpc
{
    public interface IJsonRpcConnection
    {
        IObservable<Timestamped<JsonRpcRequest>> Received { get; }
        string ConnectionId { get; }
        void Send(object payload);
    }

    public class JsonRpcConnection : IJsonRpcConnection
    {
        public JsonRpcConnection(JsonSerializerSettings serializerSettings)
        {
            Contract.RequiresNonNull(serializerSettings, nameof(serializerSettings));

            this.serializerSettings = serializerSettings;
        }

        private readonly ILogger logger = LogManager.GetCurrentClassLogger();

        private readonly JsonSerializerSettings serializerSettings;
        private TcpClient upstream;
        private const int MaxRequestLength = 8192;

        #region Implementation of IJsonRpcConnection

        public void Init(TcpClient tcp, string connectionId)
        {
            Contract.RequiresNonNull(tcp, nameof(tcp));

            upstream = tcp;
            ConnectionId = connectionId;

            var incomingLines = Observable.Create<string>(observer =>
            {
                var exit = false;

                Task.Factory.StartNew(() =>
                {
                    var stm = tcp.GetStream();
                    var buf = new byte[1024];
                    var sb = new StringBuilder();

                    // message loop
                    while (!exit)
                    {
                        try
                        {
                            var cb = stm.Read(buf, 0, buf.Length);

                            if(cb > 0)
                            {
                                var data = Encoding.UTF8.GetString(buf, 0, cb);

                                if (!string.IsNullOrEmpty(data))
                                {
                                    // flood-prevention check
                                    if (sb.Length + data.Length < MaxRequestLength)
                                    {
                                        sb.Append(data);

                                        // scan for lines and emit
                                        int index;
                                        while (sb.Length > 0 && (index = sb.ToString().IndexOf('\n')) != -1)
                                        {
                                            var line = sb.ToString(0, index).Trim();
                                            sb.Remove(0, index + 1);

                                            if (line.Length > 0)
                                                observer.OnNext(line);
                                        }
                                    }

                                    else
                                    {
                                        observer.OnError(new InvalidDataException($"[{ConnectionId}] Incoming message exceeds maximum length of {MaxRequestLength}"));
                                    }
                                }
                            }
                        }

                        catch (Exception ex)
                        {
                            break;
                        }
                    }

                    observer.OnCompleted();
                }, TaskCreationOptions.LongRunning);

                return Disposable.Create(() =>
                {
                    exit = true;
                    tcp.Close();
                });
            });

            Received = incomingLines
                .Select(line => new
                {
                    Json = line,
                    Request = JsonConvert.DeserializeObject<JsonRpcRequest>(line, serializerSettings)
                })
                .Do(x => logger.Trace(() => $"[{ConnectionId}] Received JsonRpc-Request: {x.Json}"))
                .Select(x => x.Request)
                .Timestamp()
                .Publish()
                .RefCount();
        }

        public IObservable<Timestamped<JsonRpcRequest>> Received { get; private set; }

        public void Send(object payload)
        {
            Contract.RequiresNonNull(payload, nameof(payload));

            var json = JsonConvert.SerializeObject(payload, serializerSettings);
            logger.Trace(() => $"[{ConnectionId}] Sending: {json}");

            SendInternal(Encoding.UTF8.GetBytes(json + '\n'));
        }

        public IPEndPoint RemoteEndPoint => (IPEndPoint) upstream?.Client.RemoteEndPoint;
        public string ConnectionId { get; private set; }

        #endregion

        private void SendInternal(byte[] data)
        {
            Contract.RequiresNonNull(data, nameof(data));

            try
            {
                upstream.Client.Send(data);
            }

            catch (SocketException )
            {
            }

            catch (ObjectDisposedException)
            {
            }
        }
    }
}
