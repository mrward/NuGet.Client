// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the Apache License, Version 2.0. See License.txt in the project root for license information.

using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Moq;
using NuGet.Configuration;
using Xunit;

namespace NuGet.Protocol.Core.v3.Tests
{
    public class HttpSourceAuthenticationHandlerTests
    {
        [Fact]
        public void Constructor_WithSourceCredentials_InitializesClientHandler()
        {
            var packageSource = new PackageSource("http://package.source.net", "source")
            {
                Credentials = new PackageSourceCredential("source", "user", "password", isPasswordClearText: true)
            };
            var clientHandler = new HttpClientHandler();
            var credentialService = Mock.Of<ICredentialService>();

            var handler = new HttpSourceAuthenticationHandler(packageSource, clientHandler, credentialService);

            Assert.NotNull(clientHandler.Credentials);

            var actualCredentials = clientHandler.Credentials.GetCredential(packageSource.SourceUri, "Basic");
            Assert.NotNull(actualCredentials);
            Assert.Equal("user", actualCredentials.UserName);
            Assert.Equal("password", actualCredentials.Password);
        }

        [Fact]
        public async Task SendAsync_WithUnauthenticatedSource_PassesThru()
        {
            var packageSource = new PackageSource("http://package.source.net");
            var clientHandler = new HttpClientHandler();

            var credentialService = new Mock<ICredentialService>(MockBehavior.Strict);
            var handler = new HttpSourceAuthenticationHandler(packageSource, clientHandler, credentialService.Object)
            {
                InnerHandler = GetLambdaMessageHandler(HttpStatusCode.OK)
            };

            var response = await SendAsync(handler);

            Assert.NotNull(response);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }

        [Fact]
        public async Task SendAsync_WithAcquiredCredentials_RetriesRequest()
        {
            var packageSource = new PackageSource("http://package.source.net");
            var clientHandler = new HttpClientHandler();

            var credentialService = Mock.Of<ICredentialService>();
            Mock.Get(credentialService)
                .Setup(
                    x => x.GetCredentialsAsync(
                        packageSource.SourceUri,
                        It.IsAny<IWebProxy>(),
                        CredentialRequestType.Unauthorized,
                        It.IsAny<string>(),
                        It.IsAny<CancellationToken>()))
                .Returns(() => Task.FromResult<ICredentials>(new NetworkCredential()));

            var handler = new HttpSourceAuthenticationHandler(packageSource, clientHandler, credentialService)
            {
                InnerHandler = GetLambdaMessageHandler(
                    HttpStatusCode.Unauthorized, HttpStatusCode.OK)
            };

            var response = await SendAsync(handler);

            Assert.NotNull(response);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);

            Mock.Get(credentialService)
                .Verify(
                    x => x.GetCredentialsAsync(
                        packageSource.SourceUri,
                        It.IsAny<IWebProxy>(),
                        CredentialRequestType.Unauthorized,
                        It.IsAny<string>(),
                        It.IsAny<CancellationToken>()),
                    Times.Once());
        }

        [Fact]
        public async Task SendAsync_WithWrongCredentials_StopsRetryingAfter3Times()
        {
            var packageSource = new PackageSource("http://package.source.net");
            var clientHandler = new HttpClientHandler();

            var credentialService = Mock.Of<ICredentialService>();
            Mock.Get(credentialService)
                .Setup(
                    x => x.GetCredentialsAsync(
                        packageSource.SourceUri,
                        It.IsAny<IWebProxy>(),
                        CredentialRequestType.Unauthorized,
                        It.IsAny<string>(),
                        It.IsAny<CancellationToken>()))
                .Returns(() => Task.FromResult<ICredentials>(new NetworkCredential()));

            var handler = new HttpSourceAuthenticationHandler(packageSource, clientHandler, credentialService);

            int retryCount = 0;
            var innerHandler = new LambdaMessageHandler(
                _ => { retryCount++; return new HttpResponseMessage(HttpStatusCode.Unauthorized); });
            handler.InnerHandler = innerHandler;

            var response = await SendAsync(handler);

            Assert.NotNull(response);
            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);

            Assert.Equal(HttpSourceAuthenticationHandler.MaxAuthRetries+1, retryCount);

            Mock.Get(credentialService)
                .Verify(
                    x => x.GetCredentialsAsync(
                        packageSource.SourceUri,
                        It.IsAny<IWebProxy>(),
                        CredentialRequestType.Unauthorized,
                        It.IsAny<string>(),
                        It.IsAny<CancellationToken>()),
                    Times.Exactly(HttpSourceAuthenticationHandler.MaxAuthRetries));
        }

        [Fact]
        public async Task SendAsync_WithMissingCredentials_Returns401()
        {
            var packageSource = new PackageSource("http://package.source.net");
            var clientHandler = new HttpClientHandler();

            var credentialService = Mock.Of<ICredentialService>();
            var handler = new HttpSourceAuthenticationHandler(packageSource, clientHandler, credentialService)
            {
                InnerHandler = GetLambdaMessageHandler(HttpStatusCode.Unauthorized)
            };

            var response = await SendAsync(handler);

            Assert.NotNull(response);
            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);

            Mock.Get(credentialService)
                .Verify(
                    x => x.GetCredentialsAsync(
                        packageSource.SourceUri,
                        It.IsAny<IWebProxy>(),
                        CredentialRequestType.Unauthorized,
                        It.IsAny<string>(),
                        It.IsAny<CancellationToken>()),
                    Times.Once());
        }

        private static LambdaMessageHandler GetLambdaMessageHandler(HttpStatusCode statusCode)
        {
            return new LambdaMessageHandler(
                _ => new HttpResponseMessage(statusCode));
        }

        private static LambdaMessageHandler GetLambdaMessageHandler(params HttpStatusCode[] statusCodes)
        {
            var responses = new Queue<HttpStatusCode>(statusCodes);
            return new LambdaMessageHandler(
                _ => new HttpResponseMessage(responses.Dequeue()));
        }

        private static async Task<HttpResponseMessage> SendAsync(HttpMessageHandler handler, HttpRequestMessage request = null)
        {
            using (var client = new HttpClient(handler))
            {
                return await client.SendAsync(request ?? new HttpRequestMessage(HttpMethod.Get, "http://foo"));
            }
        }
    }
}
