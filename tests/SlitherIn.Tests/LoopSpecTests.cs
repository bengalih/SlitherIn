using NUnit.Framework;
using SlitherIn.Core.Config;
using SlitherIn.Core.Engine;

namespace SlitherIn.Tests
{
    /// <summary>Pure loop semantics. LoopRules is implemented (skeleton milestone 1)
    /// — these tests are real and green.</summary>
    [TestFixture]
    public class LoopSpecTests
    {
        [Test]
        public void Count_DefaultsToOne()
        {
            Assert.That(LoopRules.CountOrDefault(null), Is.EqualTo(1));
            Assert.That(LoopRules.CountOrDefault(new LoopSpec()), Is.EqualTo(1));
        }

        [Test]
        public void Count_MinusOne_IsInfinite()
        {
            Assert.That(LoopRules.IsInfinite(new LoopSpec { Count = -1 }), Is.True);
            Assert.That(LoopRules.IsInfinite(new LoopSpec { Count = 3 }), Is.False);
        }

        [Test]
        public void ContinueOnRelease_Omitted_DefaultsFalse()
        {
            // C-5: the flag governs HELD triggers only; its derived default is
            // false (release stops). Tap triggers never consult it.
            Assert.That(LoopRules.ContinueOnRelease(null), Is.False);
        }

        [Test]
        public void ContinueOnRelease_ExplicitTrue_OverridesDerived()
        {
            var spec = new LoopSpec { ContinueOnRelease = true };
            Assert.That(LoopRules.ContinueOnRelease(spec), Is.True);
        }

        [Test]
        public void ContinueOnRelease_ExplicitFalse_StaysFalse()
        {
            var spec = new LoopSpec { ContinueOnRelease = false };
            Assert.That(LoopRules.ContinueOnRelease(spec), Is.False);
        }
    }
}