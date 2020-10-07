// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the Apache License, Version 2.0. See License.txt in the project root for license information.

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics.Tracing;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Security;
using System.Reflection;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.AspNetCore.Server.Kestrel.Core.Internal.Infrastructure;
using Microsoft.AspNetCore.Server.Kestrel.InMemory.FunctionalTests.TestTransport;
using Microsoft.AspNetCore.Testing;
using Microsoft.Extensions.Logging;
using Xunit;
using Xunit.Abstractions;

namespace Microsoft.AspNetCore.Server.Kestrel.InMemory.FunctionalTests
{
    public class EventSourceTests : LoggedTest
    {
        private static X509Certificate2 _x509Certificate2 = TestResources.GetTestCertificate();

        private TestEventListener _listener;// = new TestEventListener();

        public override void Initialize(TestContext context, MethodInfo methodInfo, object[] testMethodArguments, ITestOutputHelper testOutputHelper)
        {
            base.Initialize(context, methodInfo, testMethodArguments, testOutputHelper);

            _listener = new TestEventListener(Logger);
            _listener.EnableEvents(KestrelEventSource.Log, EventLevel.Verbose);
        }

        [Fact]
        public async Task Http1_EmitsStartAndStopEventsWithActivityIds()
        {
            int port;
            string connectionId = null;

            const int requestsToSend = 2;
            var requestIds = new string[requestsToSend];
            var requestsReceived = 0;

            await using (var server = new TestServer(async context =>
            {
                connectionId = context.Features.Get<IHttpConnectionFeature>().ConnectionId;
                requestIds[requestsReceived++] = context.TraceIdentifier;

                var upgradeFeature = context.Features.Get<IHttpUpgradeFeature>();

                if (upgradeFeature.IsUpgradableRequest)
                {
                    await upgradeFeature.UpgradeAsync();
                }
            },
            new TestServiceContext(LoggerFactory)))
            {
                port = server.Port;

                using var connection = server.CreateConnection();

                await connection.SendEmptyGet();
                await connection.Receive(
                    "HTTP/1.1 200 OK",
                    $"Date: {server.Context.DateHeaderValue}",
                    "Content-Length: 0",
                    "",
                    "");

                await connection.SendEmptyGetWithUpgrade();
                await connection.ReceiveEnd("HTTP/1.1 101 Switching Protocols",
                    "Connection: Upgrade",
                    $"Date: {server.Context.DateHeaderValue}",
                    "",
                    "");
            }

            Assert.NotNull(connectionId);
            Assert.Equal(2, requestsReceived);

            // Other tests executing in parallel may log events.
            var events = _listener.EventData.Where(e => e != null && GetProperty(e, "connectionId") == connectionId).ToList();
            var eventIndex = 0;

            var connectionQueuedStart = events[eventIndex++];
            Assert.Equal("ConnectionQueuedStart", connectionQueuedStart.EventName);
            Assert.Equal(6, connectionQueuedStart.EventId);
            Assert.All(new[] { "connectionId", "remoteEndPoint", "localEndPoint" }, p => Assert.Contains(p, connectionQueuedStart.PayloadNames));
            Assert.Equal($"127.0.0.1:{port}", GetProperty(connectionQueuedStart, "localEndPoint"));
            Assert.NotEqual(Guid.Empty, connectionQueuedStart.ActivityId);
            Assert.Equal(Guid.Empty, connectionQueuedStart.RelatedActivityId);

            var connectionQueuedStop = events[eventIndex++];
            Assert.Equal("ConnectionQueuedStop", connectionQueuedStop.EventName);
            Assert.Equal(7, connectionQueuedStop.EventId);
            Assert.All(new[] { "connectionId", "remoteEndPoint", "localEndPoint" }, p => Assert.Contains(p, connectionQueuedStop.PayloadNames));
            Assert.Equal($"127.0.0.1:{port}", GetProperty(connectionQueuedStop, "localEndPoint"));
            Assert.Equal(connectionQueuedStart.ActivityId, connectionQueuedStop.ActivityId);
            Assert.Equal(Guid.Empty, connectionQueuedStop.RelatedActivityId);

            var connectionStart = events[eventIndex++];
            Assert.Equal("ConnectionStart", connectionStart.EventName);
            Assert.Equal(1, connectionStart.EventId);
            Assert.All(new[] { "connectionId", "remoteEndPoint", "localEndPoint" }, p => Assert.Contains(p, connectionStart.PayloadNames));
            Assert.Equal($"127.0.0.1:{port}", GetProperty(connectionStart, "localEndPoint"));
            Assert.NotEqual(Guid.Empty, connectionStart.ActivityId);
            Assert.Equal(Guid.Empty, connectionStart.RelatedActivityId);

            var firstRequestStart = events[eventIndex++];
            Assert.Equal("RequestStart", firstRequestStart.EventName);
            Assert.Equal(3, firstRequestStart.EventId);
            Assert.All(new[] { "connectionId", "requestId" }, p => Assert.Contains(p, firstRequestStart.PayloadNames));
            Assert.Equal(requestIds[0], GetProperty(firstRequestStart, "requestId"));
            Assert.Same(KestrelEventSource.Log, firstRequestStart.EventSource);
            Assert.NotEqual(Guid.Empty, firstRequestStart.ActivityId);
            Assert.Equal(connectionStart.ActivityId, firstRequestStart.RelatedActivityId);

            var firstRequestStop = events[eventIndex++];
            Assert.Equal("RequestStop", firstRequestStop.EventName);
            Assert.Equal(4, firstRequestStop.EventId);
            Assert.All(new[] { "connectionId", "requestId" }, p => Assert.Contains(p, firstRequestStop.PayloadNames));
            Assert.Same(KestrelEventSource.Log, firstRequestStop.EventSource);
            Assert.Equal(requestIds[0], GetProperty(firstRequestStop, "requestId"));
            Assert.Equal(firstRequestStart.ActivityId, firstRequestStop.ActivityId);
            Assert.Equal(Guid.Empty, firstRequestStop.RelatedActivityId);

            var secondRequestStart = events[eventIndex++];
            Assert.Equal("RequestStart", secondRequestStart.EventName);
            Assert.Equal(3, secondRequestStart.EventId);
            Assert.All(new[] { "connectionId", "requestId" }, p => Assert.Contains(p, secondRequestStart.PayloadNames));
            Assert.Equal(requestIds[1], GetProperty(secondRequestStart, "requestId"));
            Assert.Same(KestrelEventSource.Log, secondRequestStart.EventSource);
            Assert.NotEqual(Guid.Empty, secondRequestStart.ActivityId);
            Assert.Equal(connectionStart.ActivityId, secondRequestStart.RelatedActivityId);

            var requestUpgradedStart = events[eventIndex++];
            Assert.Equal("RequestUpgradedStart", requestUpgradedStart.EventName);
            Assert.Equal(13, requestUpgradedStart.EventId);
            Assert.All(new[] { "connectionId", "requestId" }, p => Assert.Contains(p, requestUpgradedStart.PayloadNames));
            Assert.Equal(requestIds[1], GetProperty(requestUpgradedStart, "requestId"));
            Assert.Same(KestrelEventSource.Log, requestUpgradedStart.EventSource);
            Assert.NotEqual(Guid.Empty, requestUpgradedStart.ActivityId);
            Assert.Equal(secondRequestStart.ActivityId, requestUpgradedStart.RelatedActivityId);

            var requestUpgradedStop = events[eventIndex++];
            Assert.Equal("RequestUpgradedStop", requestUpgradedStop.EventName);
            Assert.Equal(14, requestUpgradedStop.EventId);
            Assert.All(new[] { "connectionId", "requestId" }, p => Assert.Contains(p, requestUpgradedStop.PayloadNames));
            Assert.Same(KestrelEventSource.Log, requestUpgradedStop.EventSource);
            Assert.Equal(requestIds[1], GetProperty(requestUpgradedStop, "requestId"));
            Assert.Equal(requestUpgradedStart.ActivityId, requestUpgradedStop.ActivityId);
            Assert.Equal(Guid.Empty, requestUpgradedStop.RelatedActivityId);

            var secondRequestStop = events[eventIndex++];
            Assert.Equal("RequestStop", secondRequestStop.EventName);
            Assert.Equal(4, secondRequestStop.EventId);
            Assert.All(new[] { "connectionId", "requestId" }, p => Assert.Contains(p, secondRequestStop.PayloadNames));
            Assert.Same(KestrelEventSource.Log, secondRequestStop.EventSource);
            Assert.Equal(requestIds[1], GetProperty(secondRequestStop, "requestId"));
            Assert.Equal(secondRequestStart.ActivityId, secondRequestStop.ActivityId);
            Assert.Equal(Guid.Empty, secondRequestStop.RelatedActivityId);

            var connectionStop = events[eventIndex++];
            Assert.Equal("ConnectionStop", connectionStop.EventName);
            Assert.Equal(2, connectionStop.EventId);
            Assert.All(new[] { "connectionId" }, p => Assert.Contains(p, connectionStop.PayloadNames));
            Assert.Same(KestrelEventSource.Log, connectionStop.EventSource);
            Assert.Equal(connectionStart.ActivityId, connectionStop.ActivityId);
            Assert.Equal(Guid.Empty, connectionStop.RelatedActivityId);

            Assert.Equal(eventIndex, events.Count);
        }

        [Fact]
        public async Task Http2_EmitsStartAndStopEventsWithActivityIds()
        {
            int port;
            string connectionId = null;

            const int requestsToSend = 2;
            var requestIds = new string[requestsToSend];
            var requestsReceived = 0;

            await using (var server = new TestServer(context =>
            {
                connectionId = context.Features.Get<IHttpConnectionFeature>().ConnectionId;
                requestIds[requestsReceived++] = context.TraceIdentifier;
                return Task.CompletedTask;
            },
            new TestServiceContext(LoggerFactory),
            listenOptions =>
            {
                listenOptions.UseHttps(_x509Certificate2);
                listenOptions.Protocols = HttpProtocols.Http2;
            }))
            {
                port = server.Port;

                using var connection = server.CreateConnection();

                using var socketsHandler = new SocketsHttpHandler()
                {
                    ConnectCallback = (_, _) =>
                    {
                        // This test should only require a single connection.
                        if (connectionId != null)
                        {
                            throw new InvalidOperationException();
                        }

                        return new ValueTask<Stream>(connection.Stream);
                    },
                    SslOptions = new SslClientAuthenticationOptions
                    {
                        RemoteCertificateValidationCallback = (_, _, _, _) => true
                    }
                };

                using var httpClient = new HttpClient(socketsHandler);

                for (int i = 0; i < requestsToSend; i++)
                {
                    using var httpRequestMessage = new HttpRequestMessage()
                    {
                        RequestUri = new Uri("https://localhost/"),
                        Version = new Version(2, 0),
                        VersionPolicy = HttpVersionPolicy.RequestVersionExact,
                    };

                    using var responseMessage = await httpClient.SendAsync(httpRequestMessage);
                    responseMessage.EnsureSuccessStatusCode();
                }
            }

            Assert.NotNull(connectionId);
            Assert.Equal(2, requestsReceived);

            // Other tests executing in parallel may log events.
            var events = _listener.EventData.Where(e => e != null && GetProperty(e, "connectionId") == connectionId).ToList();
            var eventIndex = 0;

            var connectionQueuedStart = events[eventIndex++];
            Assert.Equal("ConnectionQueuedStart", connectionQueuedStart.EventName);
            Assert.Equal(6, connectionQueuedStart.EventId);
            Assert.All(new[] { "connectionId", "remoteEndPoint", "localEndPoint" }, p => Assert.Contains(p, connectionQueuedStart.PayloadNames));
            Assert.Equal($"127.0.0.1:{port}", GetProperty(connectionQueuedStart, "localEndPoint"));
            Assert.NotEqual(Guid.Empty, connectionQueuedStart.ActivityId);
            Assert.Equal(Guid.Empty, connectionQueuedStart.RelatedActivityId);

            var connectionQueuedStop = events[eventIndex++];
            Assert.Equal("ConnectionQueuedStop", connectionQueuedStop.EventName);
            Assert.Equal(7, connectionQueuedStop.EventId);
            Assert.All(new[] { "connectionId", "remoteEndPoint", "localEndPoint" }, p => Assert.Contains(p, connectionQueuedStop.PayloadNames));
            Assert.Equal($"127.0.0.1:{port}", GetProperty(connectionQueuedStop, "localEndPoint"));
            Assert.Equal(connectionQueuedStart.ActivityId, connectionQueuedStop.ActivityId);
            Assert.Equal(Guid.Empty, connectionQueuedStop.RelatedActivityId);

            var connectionStart = events[eventIndex++];
            Assert.Equal("ConnectionStart", connectionStart.EventName);
            Assert.Equal(1, connectionStart.EventId);
            Assert.All(new[] { "connectionId", "remoteEndPoint", "localEndPoint" }, p => Assert.Contains(p, connectionStart.PayloadNames));
            Assert.Equal($"127.0.0.1:{port}", GetProperty(connectionStart, "localEndPoint"));
            Assert.NotEqual(Guid.Empty, connectionStart.ActivityId);
            Assert.Equal(Guid.Empty, connectionStart.RelatedActivityId);

            var tlsHandshakeStart = events[eventIndex++];
            Assert.Equal("TlsHandshakeStart", tlsHandshakeStart.EventName);
            Assert.Equal(8, tlsHandshakeStart.EventId);
            Assert.All(new[] { "connectionId" , "sslProtocols" }, p => Assert.Contains(p, tlsHandshakeStart.PayloadNames));
            Assert.Same(KestrelEventSource.Log, tlsHandshakeStart.EventSource);
            Assert.NotEqual(Guid.Empty, tlsHandshakeStart.ActivityId);
            Assert.Equal(connectionStart.ActivityId, tlsHandshakeStart.RelatedActivityId);

            var tlsHandshakeStop = events[eventIndex++];
            Assert.Equal("TlsHandshakeStop", tlsHandshakeStop.EventName);
            Assert.Equal(9, tlsHandshakeStop.EventId);
            Assert.All(new[] { "connectionId", "sslProtocols", "applicationProtocol", "hostName" }, p => Assert.Contains(p, tlsHandshakeStop.PayloadNames));
            Assert.Equal("h2", GetProperty(tlsHandshakeStop, "applicationProtocol"));
            Assert.Same(KestrelEventSource.Log, tlsHandshakeStop.EventSource);
            Assert.Equal(tlsHandshakeStart.ActivityId, tlsHandshakeStop.ActivityId);
            Assert.Equal(Guid.Empty, tlsHandshakeStop.RelatedActivityId);

            for (int i = 0; i < requestsToSend; i++)
            {
                var requestQueuedStart = events[eventIndex++];
                Assert.Equal("RequestQueuedStart", requestQueuedStart.EventName);
                Assert.Equal(11, requestQueuedStart.EventId);
                Assert.All(new[] { "connectionId", "requestId" }, p => Assert.Contains(p, requestQueuedStart.PayloadNames));
                Assert.Equal(requestIds[i], GetProperty(requestQueuedStart, "requestId"));
                Assert.Same(KestrelEventSource.Log, requestQueuedStart.EventSource);
                Assert.NotEqual(Guid.Empty, requestQueuedStart.ActivityId);
                Assert.Equal(connectionStart.ActivityId, requestQueuedStart.RelatedActivityId);

                var requestQueuedStop = events[eventIndex++];
                Assert.Equal("RequestQueuedStop", requestQueuedStop.EventName);
                Assert.Equal(12, requestQueuedStop.EventId);
                Assert.All(new[] { "connectionId", "requestId" }, p => Assert.Contains(p, requestQueuedStop.PayloadNames));
                Assert.Same(KestrelEventSource.Log, requestQueuedStop.EventSource);
                Assert.Equal(requestIds[i], GetProperty(requestQueuedStop, "requestId"));
                Assert.Equal(requestQueuedStop.ActivityId, requestQueuedStop.ActivityId);
                Assert.Equal(Guid.Empty, requestQueuedStop.RelatedActivityId);

                var requestStart = events[eventIndex++];
                Assert.Equal("RequestStart", requestStart.EventName);
                Assert.Equal(3, requestStart.EventId);
                Assert.All(new[] { "connectionId", "requestId" }, p => Assert.Contains(p, requestStart.PayloadNames));
                Assert.Equal(requestIds[i], GetProperty(requestStart, "requestId"));
                Assert.Same(KestrelEventSource.Log, requestStart.EventSource);
                Assert.NotEqual(Guid.Empty, requestStart.ActivityId);
                Assert.Equal(connectionStart.ActivityId, requestStart.RelatedActivityId);

                var requestStop = events[eventIndex++];
                Assert.Equal("RequestStop", requestStop.EventName);
                Assert.Equal(4, requestStop.EventId);
                Assert.All(new[] { "connectionId", "requestId" }, p => Assert.Contains(p, requestStop.PayloadNames));
                Assert.Same(KestrelEventSource.Log, requestStop.EventSource);
                Assert.Equal(requestIds[i], GetProperty(requestStop, "requestId"));
                Assert.Equal(requestStart.ActivityId, requestStop.ActivityId);
                Assert.Equal(Guid.Empty, requestStop.RelatedActivityId);
            }

            var connectionStop = events[eventIndex++];
            Assert.Equal("ConnectionStop", connectionStop.EventName);
            Assert.Equal(2, connectionStop.EventId);
            Assert.All(new[] { "connectionId" }, p => Assert.Contains(p, connectionStop.PayloadNames));
            Assert.Same(KestrelEventSource.Log, connectionStop.EventSource);
            Assert.Equal(connectionStart.ActivityId, connectionStop.ActivityId);
            Assert.Equal(Guid.Empty, connectionStop.RelatedActivityId);

            Assert.Equal(eventIndex, events.Count);
        }

        [Fact]
        public async Task TlsHandshakeFailure_EmitsStartAndStopEventsWithActivityIds()
        {
            int port;
            string connectionId = null;

            await using (var server = new TestServer(context => Task.CompletedTask, new TestServiceContext(LoggerFactory),
            listenOptions =>
            {
                listenOptions.Use(next =>
                {
                    return connectionContext =>
                    {
                        connectionId = connectionContext.ConnectionId;
                        return next(connectionContext);
                    };
                });

                listenOptions.UseHttps(_x509Certificate2);
            }))
            {
                port = server.Port;

                using var connection = server.CreateConnection();
                await using var sslStream = new SslStream(connection.Stream);

                var clientAuthOptions = new SslClientAuthenticationOptions
                {
                    TargetHost = "localhost",

                    // Only enabling SslProtocols.Ssl2 should cause a handshake failure on all platforms.
#pragma warning disable CS0618 // Type or member is obsolete
                    EnabledSslProtocols = SslProtocols.Ssl2,
#pragma warning restore CS0618 // Type or member is obsolete
                };

                using var handshakeCts = new CancellationTokenSource(TestConstants.DefaultTimeout);
                await Assert.ThrowsAnyAsync<Exception>(() => sslStream.AuthenticateAsClientAsync(clientAuthOptions, handshakeCts.Token));
            }

            Assert.NotNull(connectionId);

            // Other tests executing in parallel may log events.
            var events = _listener.EventData.Where(e => e != null && GetProperty(e, "connectionId") == connectionId).ToList();
            var eventIndex = 0;

            var connectionQueuedStart = events[eventIndex++];
            Assert.Equal("ConnectionQueuedStart", connectionQueuedStart.EventName);
            Assert.Equal(6, connectionQueuedStart.EventId);
            Assert.All(new[] { "connectionId", "remoteEndPoint", "localEndPoint" }, p => Assert.Contains(p, connectionQueuedStart.PayloadNames));
            Assert.Equal($"127.0.0.1:{port}", GetProperty(connectionQueuedStart, "localEndPoint"));
            Assert.NotEqual(Guid.Empty, connectionQueuedStart.ActivityId);
            Assert.Equal(Guid.Empty, connectionQueuedStart.RelatedActivityId);

            var connectionQueuedStop = events[eventIndex++];
            Assert.Equal("ConnectionQueuedStop", connectionQueuedStop.EventName);
            Assert.Equal(7, connectionQueuedStop.EventId);
            Assert.All(new[] { "connectionId", "remoteEndPoint", "localEndPoint" }, p => Assert.Contains(p, connectionQueuedStop.PayloadNames));
            Assert.Equal($"127.0.0.1:{port}", GetProperty(connectionQueuedStop, "localEndPoint"));
            Assert.Equal(connectionQueuedStart.ActivityId, connectionQueuedStop.ActivityId);
            Assert.Equal(Guid.Empty, connectionQueuedStop.RelatedActivityId);

            var connectionStart = events[eventIndex++];
            Assert.Equal("ConnectionStart", connectionStart.EventName);
            Assert.Equal(1, connectionStart.EventId);
            Assert.All(new[] { "connectionId", "remoteEndPoint", "localEndPoint" }, p => Assert.Contains(p, connectionStart.PayloadNames));
            Assert.Equal($"127.0.0.1:{port}", GetProperty(connectionStart, "localEndPoint"));
            Assert.NotEqual(Guid.Empty, connectionStart.ActivityId);
            Assert.Equal(Guid.Empty, connectionStart.RelatedActivityId);

            var tlsHandshakeStart = events[eventIndex++];
            Assert.Equal("TlsHandshakeStart", tlsHandshakeStart.EventName);
            Assert.Equal(8, tlsHandshakeStart.EventId);
            Assert.All(new[] { "connectionId", "sslProtocols" }, p => Assert.Contains(p, tlsHandshakeStart.PayloadNames));
            Assert.Same(KestrelEventSource.Log, tlsHandshakeStart.EventSource);
            Assert.NotEqual(Guid.Empty, tlsHandshakeStart.ActivityId);
            Assert.Equal(connectionStart.ActivityId, tlsHandshakeStart.RelatedActivityId);

            var tlsHandshakeFailed = events[eventIndex++];
            Assert.Equal("TlsHandshakeFailed", tlsHandshakeFailed.EventName);
            Assert.Equal(10, tlsHandshakeFailed.EventId);
            Assert.All(new[] { "connectionId" }, p => Assert.Contains(p, tlsHandshakeFailed.PayloadNames));
            Assert.Same(KestrelEventSource.Log, tlsHandshakeFailed.EventSource);
            Assert.NotEqual(tlsHandshakeStart.ActivityId, tlsHandshakeFailed.ActivityId);
            Assert.Equal(Guid.Empty, tlsHandshakeFailed.RelatedActivityId);

            var tlsHandshakeStop = events[eventIndex++];
            Assert.Equal("TlsHandshakeStop", tlsHandshakeStop.EventName);
            Assert.Equal(9, tlsHandshakeStop.EventId);
            Assert.All(new[] { "connectionId", "sslProtocols", "applicationProtocol", "hostName" }, p => Assert.Contains(p, tlsHandshakeStop.PayloadNames));
            Assert.Same(KestrelEventSource.Log, tlsHandshakeStop.EventSource);
            Assert.Equal(tlsHandshakeStart.ActivityId, tlsHandshakeStop.ActivityId);
            Assert.Equal(Guid.Empty, tlsHandshakeStop.RelatedActivityId);

            var connectionStop = events[eventIndex++];
            Assert.Equal("ConnectionStop", connectionStop.EventName);
            Assert.Equal(2, connectionStop.EventId);
            Assert.All(new[] { "connectionId" }, p => Assert.Contains(p, connectionStop.PayloadNames));
            Assert.Same(KestrelEventSource.Log, connectionStop.EventSource);
            Assert.Equal(connectionStart.ActivityId, connectionStop.ActivityId);
            Assert.Equal(Guid.Empty, connectionStop.RelatedActivityId);

            Assert.Equal(eventIndex, events.Count);
        }

        private string GetProperty(EventWrittenEventArgs data, string propName)
        {
            var index = data.PayloadNames.IndexOf(propName);
            return index >= 0 ? data.Payload[index] as string : null;
        }

        private class TestEventListener : EventListener
        {
            private readonly ConcurrentQueue<EventWrittenEventArgs> _events = new ConcurrentQueue<EventWrittenEventArgs>();
            private readonly ILogger _logger;
            private volatile bool _disposed;

            public TestEventListener(ILogger logger)
            {
                _logger = logger;
            }

            public IEnumerable<EventWrittenEventArgs> EventData => _events;

            protected override void OnEventSourceCreated(EventSource eventSource)
            {
                if (eventSource.Name == "System.Threading.Tasks.TplEventSource")
                {
                    // Enable TasksFlowActivityIds
                    EnableEvents(eventSource, EventLevel.Informational, (EventKeywords)0x80);
                }
            }

            protected override void OnEventWritten(EventWrittenEventArgs eventData)
            {
                if (!_disposed)
                {
                    _logger.LogInformation("{event}", JsonSerializer.Serialize(eventData, new JsonSerializerOptions
                    {
                        WriteIndented = true
                    }));

                    _events.Enqueue(eventData);
                }
            }

            public override void Dispose()
            {
                _disposed = true;
                base.Dispose();
            }
        }

        public override void Dispose()
        {
            _listener.Dispose();
            base.Dispose();
        }
    }
}
