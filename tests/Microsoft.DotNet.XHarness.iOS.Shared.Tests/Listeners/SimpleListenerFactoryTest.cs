﻿// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using Microsoft.DotNet.XHarness.iOS.Shared.Listeners;
using Microsoft.DotNet.XHarness.iOS.Shared.Logging;
using Moq;
using Xunit;

namespace Microsoft.DotNet.XHarness.iOS.Shared.Tests.Listeners
{
    public class SimpleListenerFactoryTest
    {
        private readonly Mock<ILog> _log;
        private readonly SimpleListenerFactory _factory;

        public SimpleListenerFactoryTest()
        {
            _log = new Mock<ILog>();
            _factory = new SimpleListenerFactory(Mock.Of<ITunnelBore> ());
        }

        [Fact]
        public void CreateNotWatchListener()
        {
            var (transport, listener, listenerTmpFile) = _factory.Create(RunMode.iOS, _log.Object, _log.Object, true, true, true, false);
            Assert.Equal(ListenerTransport.Tcp, transport);
            Assert.IsType<SimpleTcpListener>(listener);
            Assert.Null(listenerTmpFile);
        }

        [Fact]
        public void CreateWatchOSSimulator()
        {
            var logFullPath = "myfullpath.txt";
            _ = _log.Setup(l => l.FullPath).Returns(logFullPath);

            var (transport, listener, listenerTmpFile) = _factory.Create(RunMode.WatchOS, _log.Object, _log.Object, true, true, true, false);
            Assert.Equal(ListenerTransport.File, transport);
            Assert.IsType<SimpleFileListener>(listener);
            Assert.NotNull(listenerTmpFile);
            Assert.Equal(logFullPath + ".tmp", listenerTmpFile);

            _log.Verify(l => l.FullPath, Times.Once);

        }

        [Fact]
        public void CreateWatchOSDevice()
        {
            var (transport, listener, listenerTmpFile) = _factory.Create(RunMode.WatchOS, _log.Object, _log.Object, false, true, true, false);
            Assert.Equal(ListenerTransport.Http, transport);
            Assert.IsType<SimpleHttpListener>(listener);
            Assert.Null(listenerTmpFile);
        }
    }
}