﻿// Copyright (c) Microsoft Open Technologies, Inc. All rights reserved. See License.md in the project root for license information.

using System;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNet.SignalR.Client.Http;
using Microsoft.AspNet.SignalR.Client.Infrastructure;
using Microsoft.AspNet.SignalR.Infrastructure;

namespace Microsoft.AspNet.SignalR.Client.Transports
{
    public class LongPollingTransport : HttpBasedTransport
    {
        private PollingRequestHandler _requestHandler;
        private NegotiationResponse _negotiationResponse;

        /// <summary>
        /// The time to wait after a connection drops to try reconnecting.
        /// </summary>
        public TimeSpan ReconnectDelay { get; set; }

        /// <summary>
        /// The time to wait after an error happens to continue polling.
        /// </summary>
        public TimeSpan ErrorDelay { get; set; }

        public LongPollingTransport()
            : this(new DefaultHttpClient())
        {
        }

        public LongPollingTransport(IHttpClient httpClient)
            : base(httpClient, "longPolling")
        {
            ReconnectDelay = TimeSpan.FromSeconds(5);
            ErrorDelay = TimeSpan.FromSeconds(2);
        }

        /// <summary>
        /// Indicates whether or not the transport supports keep alive
        /// </summary>
        public override bool SupportsKeepAlive
        {
            get
            {
                // Don't check for keep alives if the server didn't send back the "LongPollDelay" as
                // part of the response to /negotiate. That indicates the server is running an older
                // version of SignalR that doesn't send long polling keep alives.
                return _negotiationResponse != null &&
                       _negotiationResponse.LongPollDelay.HasValue;
            }
        }

        public override Task<NegotiationResponse> Negotiate(IConnection connection, string connectionData)
        {
            return
                base.Negotiate(connection, connectionData)
                    .Then(negotiationResponse => _negotiationResponse = negotiationResponse);
        }

        protected override void OnStart(IConnection connection,
                                        string connectionData,
                                        CancellationToken disconnectToken,
                                        TransportInitializationHandler initializeHandler)
        {
            if (initializeHandler == null)
            {
                throw new ArgumentNullException("initializeHandler");
            }

            _requestHandler = new PollingRequestHandler(HttpClient);
            _requestHandler.OnKeepAlive += connection.MarkLastMessage;

            // If the transport fails to initialize we want to silently stop
            initializeHandler.OnFailure += () =>
            {
                _requestHandler.Stop();
            };

            PerformConnect(connection, connectionData, initializeHandler)
                .Then(() => 
                {
                    // Add additional actions to each of the PollingRequestHandler events
                    PollingSetup(connection, connectionData, disconnectToken);

                    _requestHandler.Start();
                });
        }

        [SuppressMessage("Microsoft.Maintainability", "CA1502:AvoidExcessiveComplexity", Justification = "We will refactor later.")]
        private void PollingSetup(IConnection connection,
                                  string data,
                                  CancellationToken disconnectToken)
        {
            // reconnectInvoker is created new on each poll
            var reconnectInvoker = new ThreadSafeInvoker();

            var disconnectRegistration = disconnectToken.SafeRegister(state =>
            {
                reconnectInvoker.Invoke();
                _requestHandler.Stop();
            }, null);

            _requestHandler.ResolveUrl = () =>
            {
                string url;

                if (IsReconnecting(connection))
                {
                    url = UrlBuilder.BuildReconnect(connection, Name, data);
                    connection.Trace(TraceLevels.Events, "LP Reconnect: {0}", url);
                }
                else
                {
                    url = UrlBuilder.BuildPoll(connection, Name, data);
                    connection.Trace(TraceLevels.Events, "LP Poll: {0}", url);
                }

                return url;
            };

            _requestHandler.PrepareRequest += req =>
            {
                connection.PrepareRequest(req);
            };

            _requestHandler.OnMessage += message =>
            {
                connection.Trace(TraceLevels.Messages, "LP: OnMessage({0})", message);

                var shouldReconnect = ProcessResponse(connection, message, () => { });

                if (IsReconnecting(connection))
                {
                    // If the timeout for the reconnect hasn't fired as yet just fire the 
                    // event here before any incoming messages are processed
                    TryReconnect(connection, reconnectInvoker);
                }

                if (shouldReconnect)
                {
                    // Transition into reconnecting state
                    connection.EnsureReconnecting();
                }
            };

            _requestHandler.OnError += exception =>
            {
                reconnectInvoker.Invoke();

                if (!TransportHelper.VerifyLastActive(connection))
                {
                    _requestHandler.Stop();
                }

                // Transition into reconnecting state
                connection.EnsureReconnecting();

                // Sometimes a connection might have been closed by the server before we get to write anything
                // so just try again and raise OnError.
                if (!ExceptionHelper.IsRequestAborted(exception) && !(exception is IOException))
                {
                    connection.OnError(exception);
                }
            };

            _requestHandler.OnPolling += () =>
            {
                // Capture the cleanup within a closure so it can persist through multiple requests
                TryDelayedReconnect(connection, reconnectInvoker);
            };

            _requestHandler.OnAfterPoll = exception =>
            {
                if (AbortHandler.TryCompleteAbort())
                {
                    // Abort() was called, so don't reconnect
                    _requestHandler.Stop();
                }
                else
                {
                    reconnectInvoker = new ThreadSafeInvoker();

                    if (exception != null)
                    {
                        // Delay polling by the error delay
                        return TaskAsyncHelper.Delay(ErrorDelay);
                    }
                }

                return TaskAsyncHelper.Empty;
            };

            _requestHandler.OnAbort += _ =>
            {
                disconnectRegistration.Dispose();

                // Complete any ongoing calls to Abort()
                // If someone calls Abort() later, have it no-op
                AbortHandler.CompleteAbort();
            };
        }

        private Task PerformConnect(IConnection connection, string connectionData, TransportInitializationHandler initializationHandler)
        {
            var startUrl = UrlBuilder.BuildConnect(connection, Name, connectionData);

            HttpClient.Initialize(connection);

            return HttpClient
                .Post(startUrl,
                    request =>
                    {
                        connection.PrepareRequest(request);
                        initializationHandler.OnFailure += request.Abort;
                    }, isLongRunning: false)
                .Then(response => response.ReadAsString())
                .Then(responseMessage => ProcessResponse(connection, responseMessage, initializationHandler.InitReceived))
                .ContinueWith(t =>
                {
                    if (t.IsFaulted || t.IsCanceled)
                    {
                        initializationHandler.Fail();
                    }
                }, TaskContinuationOptions.NotOnRanToCompletion);
        }

        private void TryDelayedReconnect(IConnection connection, ThreadSafeInvoker reconnectInvoker)
        {
            if (IsReconnecting(connection))
            {
                TaskAsyncHelper.Delay(ReconnectDelay).Then(() =>
                {
                    TryReconnect(connection, reconnectInvoker);
                });
            }
        }

        private static void TryReconnect(IConnection connection, ThreadSafeInvoker reconnectInvoker)
        {
            // Fire the reconnect event after the delay.
            reconnectInvoker.Invoke((conn) => FireReconnected(conn), connection);
        }

        private static void FireReconnected(IConnection connection)
        {
            // Mark the connection as connected
            if (connection.ChangeState(ConnectionState.Reconnecting, ConnectionState.Connected))
            {
                connection.OnReconnected();
            }
        }

        private static bool IsReconnecting(IConnection connection)
        {
            return connection.State == ConnectionState.Reconnecting;
        }

        public override void LostConnection(IConnection connection)
        {
            if (connection.EnsureReconnecting())
            {
                _requestHandler.LostConnection();
            }
        }
    }
}
