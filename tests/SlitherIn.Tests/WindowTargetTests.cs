using NUnit.Framework;
using SlitherIn.Core.Config;
using SlitherIn.Core.Target;

namespace SlitherIn.Tests
{
    /// <summary>WindowTarget matching is implemented (skeleton milestone 1) — these
    /// tests are real and green.</summary>
    [TestFixture]
    public class WindowTargetTests
    {
        [Test]
        public void ExeOnly_MatchesThatProcess_AnythingTitle()
        {
            var t = WindowTarget.FromSpec(new WindowSpec { Exe = "dndclient64.exe" });
            Assert.That(t.Matches("dndclient64.exe", "Anything"), Is.True);
            Assert.That(t.Matches("notepad.exe", "Anything"), Is.False);
        }

        [Test]
        public void TitleWildcard_Matches() // "CharacterA - Sarlona"
        {
            var t = WindowTarget.FromSpec(new WindowSpec { Title = "CharacterA - *" });
            Assert.That(t.Matches("dndclient64.exe", "CharacterA - Sarlona"), Is.True);
            Assert.That(t.Matches("dndclient64.exe", "CharacterB - Sarlona"), Is.False);
        }

        [Test]
        public void ExeAndTitle_BothMustMatch()
        {
            var t = WindowTarget.FromSpec(new WindowSpec { Exe = "dndclient64.exe", Title = "* - Sarlona" });
            Assert.That(t.Matches("dndclient64.exe", "CharacterA - Sarlona"), Is.True);
            Assert.That(t.Matches("dndclient64.exe", "CharacterA - Khyber"), Is.False);
            Assert.That(t.Matches("notepad.exe", "CharacterA - Sarlona"), Is.False);
        }

        [Test]
        public void NoSpec_NeverMatches() // manual-only profile
        {
            var t = WindowTarget.FromSpec(null);
            Assert.That(t.HasAny, Is.False);
            Assert.That(t.Matches("dndclient64.exe", "CharacterA - Sarlona"), Is.False);
        }
    }
}