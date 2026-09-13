using NUnit.Framework;
using SlitherIn.Core.Engine;

namespace SlitherIn.Tests
{
    /// <summary>Cooperative cancellation token: request flag, idempotence, the
    /// reusable Never token, and the throw-on-cancel helper. Stage 3.</summary>
    [TestFixture]
    public class CancellationTests
    {
        [Test]
        public void Starts_Unrequested()
        {
            Assert.That(new Cancellation().IsRequested, Is.False);
        }

        [Test]
        public void Request_SetsFlag()
        {
            var c = new Cancellation();
            c.Request();
            Assert.That(c.IsRequested, Is.True);
        }

        [Test]
        public void Request_IsIdempotent()
        {
            var c = new Cancellation();
            c.Request();
            c.Request();
            Assert.That(c.IsRequested, Is.True);
        }

        [Test]
        public void Never_IgnoresRequest()
        {
            Cancellation.Never.Request();
            Assert.That(Cancellation.Never.IsRequested, Is.False);
        }

        [Test]
        public void ThrowIfCancelled_ThrowsOnlyWhenRequested()
        {
            var c = new Cancellation();
            Assert.DoesNotThrow(() => c.ThrowIfCancelled());
            c.Request();
            Assert.Throws<System.OperationCanceledException>(() => c.ThrowIfCancelled());
        }
    }
}