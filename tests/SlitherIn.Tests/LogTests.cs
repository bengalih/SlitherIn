using NUnit.Framework;
using SlitherIn.Core.Diagnostics;

namespace SlitherIn.Tests
{
    [TestFixture]
    public class LogTests
    {
        [Test]
        public void Parse_AcceptsTheThreeLevels()
        {
            Assert.That(Log.Parse("off"), Is.EqualTo(LogLevel.Off));
            Assert.That(Log.Parse("on"), Is.EqualTo(LogLevel.On));
            Assert.That(Log.Parse("debug"), Is.EqualTo(LogLevel.Debug));
        }

        [Test]
        public void Parse_Unknown_IsOff()
        {
            Assert.That(Log.Parse("loud"), Is.EqualTo(LogLevel.Off));
            Assert.That(Log.Parse(null), Is.EqualTo(LogLevel.Off));
        }
    }
}