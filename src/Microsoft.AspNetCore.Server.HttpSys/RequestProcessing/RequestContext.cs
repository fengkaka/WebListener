// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the Apache License, Version 2.0. See License.txt in the project root for license information.

using System;
using System.Diagnostics;
using System.IO;
using System.Net.WebSockets;
using System.Security.Authentication.ExtendedProtection;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace Microsoft.AspNetCore.Server.HttpSys
{
    internal sealed class RequestContext : IDisposable
    {
        private static readonly Action<object> AbortDelegate = Abort;

        private NativeRequestContext _memoryBlob;
        private CancellationTokenSource _requestAbortSource;
        private CancellationToken? _disconnectToken;
        private bool _disposed;

        internal RequestContext(HttpSysListener server, NativeRequestContext memoryBlob)
        {
            // TODO: Verbose log
            Server = server;
            _memoryBlob = memoryBlob;
            Request = new Request(this, _memoryBlob);
            Response = new Response(this);
        }

        internal HttpSysListener Server { get; }

        internal ILogger Logger => Server.Logger;

        public Request Request { get; }

        public Response Response { get; }

        public ClaimsPrincipal User => Request.User;

        public CancellationToken DisconnectToken
        {
            get
            {
                // Create a new token per request, but link it to a single connection token.
                // We need to be able to dispose of the registrations each request to prevent leaks.
                if (!_disconnectToken.HasValue)
                {
                    if (_disposed || Response.BodyIsFinished)
                    {
                        // We cannot register for disconnect notifications after the response has finished sending.
                        _disconnectToken = CancellationToken.None;
                    }
                    else
                    {
                        var connectionDisconnectToken = Server.DisconnectListener.GetTokenForConnection(Request.UConnectionId);

                        if (connectionDisconnectToken.CanBeCanceled)
                        {
                            _requestAbortSource = CancellationTokenSource.CreateLinkedTokenSource(connectionDisconnectToken);
                            _disconnectToken = _requestAbortSource.Token;
                        }
                        else
                        {
                            _disconnectToken = CancellationToken.None;
                        }
                    }
                }
                return _disconnectToken.Value;
            }
        }

        public unsafe Guid TraceIdentifier
        {
            get
            {
                // This is the base GUID used by HTTP.SYS for generating the activity ID.
                // HTTP.SYS overwrites the first 8 bytes of the base GUID with RequestId to generate ETW activity ID.
                var guid = new Guid(0xffcb4c93, 0xa57f, 0x453c, 0xb6, 0x3f, 0x84, 0x71, 0xc, 0x79, 0x67, 0xbb);
                *((ulong*)&guid) = Request.RequestId;
                return guid;
            }
        }

        public bool IsUpgradableRequest => Request.IsUpgradable;

        public Task<Stream> UpgradeAsync()
        {
            if (!IsUpgradableRequest)
            {
                throw new InvalidOperationException("This request cannot be upgraded, it is incompatible.");
            }
            if (Response.HasStarted)
            {
                throw new InvalidOperationException("This request cannot be upgraded, the response has already started.");
            }

            // Set the status code and reason phrase
            Response.StatusCode = Constants.Status101SwitchingProtocols;
            Response.ReasonPhrase = HttpReasonPhrase.Get(Constants.Status101SwitchingProtocols);

            Response.SendOpaqueUpgrade(); // TODO: Async
            Request.SwitchToOpaqueMode();
            Response.SwitchToOpaqueMode();
            var opaqueStream = new OpaqueStream(Request.Body, Response.Body);
            return Task.FromResult<Stream>(opaqueStream);
        }

        // Compare ValidateWebSocketRequest
        public bool IsWebSocketRequest
        {
            get
            {
                if (!WebSocketHelpers.AreWebSocketsSupported)
                {
                    return false;
                }

                if (!IsUpgradableRequest)
                {
                    return false;
                }

                if (Request.KnownMethod != HttpApi.HTTP_VERB.HttpVerbGET)
                {
                    return false;
                }

                // Connection: Upgrade (some odd clients send Upgrade,KeepAlive)
                var connection = Request.Headers[HttpKnownHeaderNames.Connection].ToString();
                if (connection == null || connection.IndexOf(HttpKnownHeaderNames.Upgrade, StringComparison.OrdinalIgnoreCase) < 0)
                {
                    return false;
                }

                // Upgrade: websocket
                var upgrade = Request.Headers[HttpKnownHeaderNames.Upgrade];
                if (!string.Equals(WebSocketHelpers.WebSocketUpgradeToken, upgrade, StringComparison.OrdinalIgnoreCase))
                {
                    return false;
                }

                // Sec-WebSocket-Version: 13
                var version = Request.Headers[HttpKnownHeaderNames.SecWebSocketVersion];
                if (!string.Equals(WebSocketHelpers.SupportedProtocolVersion, version, StringComparison.OrdinalIgnoreCase))
                {
                    return false;
                }

                // Sec-WebSocket-Key: {base64string}
                var key = Request.Headers[HttpKnownHeaderNames.SecWebSocketKey];
                if (!WebSocketHelpers.IsValidWebSocketKey(key))
                {
                    return false;
                }

                return true;
            }
        }

        // Compare IsWebSocketRequest()
        private void ValidateWebSocketRequest()
        {
            if (!WebSocketHelpers.AreWebSocketsSupported)
            {
                throw new NotSupportedException("WebSockets are not supported on this platform.");
            }

            if (!IsUpgradableRequest)
            {
                throw new InvalidOperationException("This request is not a valid upgrade request.");
            }

            if (Request.KnownMethod != HttpApi.HTTP_VERB.HttpVerbGET)
            {
                throw new InvalidOperationException("This request is not a valid upgrade request; invalid verb: " + Request.Method);
            }

            // Connection: Upgrade (some odd clients send Upgrade,KeepAlive)
            var connection = Request.Headers[HttpKnownHeaderNames.Connection].ToString();
            if (connection == null || connection.IndexOf(HttpKnownHeaderNames.Upgrade, StringComparison.OrdinalIgnoreCase) < 0)
            {
                throw new InvalidOperationException("The Connection header is invalid: " + connection);
            }

            // Upgrade: websocket
            var upgrade = Request.Headers[HttpKnownHeaderNames.Upgrade];
            if (!string.Equals(WebSocketHelpers.WebSocketUpgradeToken, upgrade, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("The Upgrade header is invalid: " + upgrade);
            }

            // Sec-WebSocket-Version: 13
            var version = Request.Headers[HttpKnownHeaderNames.SecWebSocketVersion];
            if (!string.Equals(WebSocketHelpers.SupportedProtocolVersion, version, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("The Sec-WebSocket-Version header is invalid or not supported: " + version);
            }

            // Sec-WebSocket-Key: {base64string}
            var key = Request.Headers[HttpKnownHeaderNames.SecWebSocketKey];
            if (!WebSocketHelpers.IsValidWebSocketKey(key))
            {
                throw new InvalidOperationException("The Sec-WebSocket-Key header is invalid: " + upgrade);
            }
        }

        public Task<WebSocket> AcceptWebSocketAsync()
        {
            return AcceptWebSocketAsync(null, WebSocketHelpers.DefaultReceiveBufferSize, WebSocketHelpers.DefaultKeepAliveInterval);
        }

        public Task<WebSocket> AcceptWebSocketAsync(string subProtocol)
        {
            return AcceptWebSocketAsync(subProtocol, WebSocketHelpers.DefaultReceiveBufferSize, WebSocketHelpers.DefaultKeepAliveInterval);
        }

        public Task<WebSocket> AcceptWebSocketAsync(string subProtocol, TimeSpan keepAliveInterval)
        {
            return AcceptWebSocketAsync(subProtocol, WebSocketHelpers.DefaultReceiveBufferSize, keepAliveInterval);
        }

        public Task<WebSocket> AcceptWebSocketAsync(string subProtocol, int receiveBufferSize, TimeSpan keepAliveInterval)
        {
            if (!IsUpgradableRequest)
            {
                throw new InvalidOperationException("This request cannot be upgraded.");
            }
            WebSocketHelpers.ValidateOptions(subProtocol, keepAliveInterval);

            return AcceptWebSocketAsyncCore(subProtocol, receiveBufferSize, keepAliveInterval);
        }

        private async Task<WebSocket> AcceptWebSocketAsyncCore(string subProtocol, int receiveBufferSize, TimeSpan keepAliveInterval)
        {
            ValidateWebSocketRequest();

            var subProtocols = Request.Headers.GetValues(HttpKnownHeaderNames.SecWebSocketProtocol);
            var shouldSendSecWebSocketProtocolHeader = WebSocketHelpers.ProcessWebSocketProtocolHeader(subProtocols, subProtocol);
            if (shouldSendSecWebSocketProtocolHeader)
            {
                Response.Headers[HttpKnownHeaderNames.SecWebSocketProtocol] = subProtocol;
            }

            // negotiate the websocket key return value
            var secWebSocketKey = Request.Headers[HttpKnownHeaderNames.SecWebSocketKey];
            var secWebSocketAccept = WebSocketHelpers.GetSecWebSocketAcceptString(secWebSocketKey);

            Response.Headers.Append(HttpKnownHeaderNames.Connection, HttpKnownHeaderNames.Upgrade);
            Response.Headers.Append(HttpKnownHeaderNames.Upgrade, WebSocketHelpers.WebSocketUpgradeToken);
            Response.Headers.Append(HttpKnownHeaderNames.SecWebSocketAccept, secWebSocketAccept);

            var opaqueStream = await UpgradeAsync();

            return WebSocketHelpers.CreateServerWebSocket(opaqueStream, subProtocol, receiveBufferSize, keepAliveInterval);
        }

        // TODO: Public when needed
        internal bool TryGetChannelBinding(ref ChannelBinding value)
        {
            if (!Request.IsHttps)
            {
                LogHelper.LogDebug(Logger, "TryGetChannelBinding", "Channel binding requires HTTPS.");
                return false;
            }

            value = ClientCertLoader.GetChannelBindingFromTls(Server.RequestQueue, Request.UConnectionId, Logger);

            Debug.Assert(value != null, "GetChannelBindingFromTls returned null even though OS supposedly supports Extended Protection");
            LogHelper.LogInfo(Logger, "Channel binding retrieved.");
            return value != null;
        }

        /// <summary>
        /// Flushes and completes the response.
        /// </summary>
        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }
            _disposed = true;

            // TODO: Verbose log
            try
            {
                _requestAbortSource?.Dispose();
                Response.Dispose();
            }
            catch
            {
                Abort();
            }
            finally
            {
                Request.Dispose();
            }
        }

        /// <summary>
        /// Forcibly terminate and dispose the request, closing the connection if necessary.
        /// </summary>
        public void Abort()
        {
            // May be called from Dispose() code path, don't check _disposed.
            // TODO: Verbose log
            _disposed = true;
            if (_requestAbortSource != null)
            {
                try
                {
                    _requestAbortSource.Cancel();
                }
                catch (ObjectDisposedException)
                {
                }
                catch (Exception ex)
                {
                    LogHelper.LogDebug(Logger, "Abort", ex);
                }
                _requestAbortSource.Dispose();
            }
            ForceCancelRequest();
            Request.Dispose();
            // Only Abort, Response.Dispose() tries a graceful flush
            Response.Abort();
        }

        private static void Abort(object state)
        {
            var context = (RequestContext)state;
            context.Abort();
        }

        internal CancellationTokenRegistration RegisterForCancellation(CancellationToken cancellationToken)
        {
            return cancellationToken.Register(AbortDelegate, this);
        }

        // The request is being aborted, but large writes may be in progress. Cancel them.
        internal void ForceCancelRequest()
        {
            try
            {
                var statusCode = HttpApi.HttpCancelHttpRequest(Server.RequestQueue.Handle,
                    Request.RequestId, IntPtr.Zero);

                // Either the connection has already dropped, or the last write is in progress.
                // The requestId becomes invalid as soon as the last Content-Length write starts.
                // The only way to cancel now is with CancelIoEx.
                if (statusCode == UnsafeNclNativeMethods.ErrorCodes.ERROR_CONNECTION_INVALID)
                {
                    Response.CancelLastWrite();
                }
            }
            catch (ObjectDisposedException)
            {
                // RequestQueueHandle may have been closed
            }
        }
    }
}
