using System;
using System.Collections.Generic;
using SlitherIn.Core.Config;

namespace SlitherIn.Input
{
    /// <summary>
    /// The engine seam — the ONLY way the dispatcher talks to the outside world for
    /// keys/mouse. Two implementations: <see cref="SendInputEngine"/> (always
    /// available, default) and <see cref="ViiperEngine"/> (opt-in, hard-fail when
    /// selected-but-unavailable). A game that blocks SendInput is a new engine
    /// class, never a change to the dispatcher.
    /// </summary>
    public interface IKeyEngine : IDisposable
    {
        /// <summary>Can be used RIGHT NOW. sendinput ↦ always true; viiper ↦ device
        /// found + connected. Checked at profile load (hard-fail rule).</summary>
        bool Available { get; }

        string Name { get; }

        void Initialize();

        /// <summary>
        /// Diff-based state push: set the set of chords currently held DOWN. Called
        /// repeatedly by the dispatcher; engines compute down/up transitions between
        /// calls (the old Viiper SetState model).
        /// </summary>
        void SetState(IReadOnlyList<Chord> pressed);

        /// <summary>Release everything pressed (abort/reload/switch/kill).</summary>
        void ReleaseAll();
    }
}